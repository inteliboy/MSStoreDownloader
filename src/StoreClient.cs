// =============================================================================
// StoreClient.cs - Microsoft Store package retrieval via store.rg-adguard.net
// C# 5 compatible (no string interpolation, no expression-bodied members)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;

namespace MSStoreDownloader
{
    public class StoreClient
    {
        private const string RgAdguardApiUrl   = "https://store.rg-adguard.net/api/GetFiles";
        private const string BrowserUserAgent  = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
        private const int    HttpTimeoutSeconds = 45;

        private static readonly HashSet<string> PackageExtensions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ".appx", ".appxbundle", ".msix", ".msixbundle", ".eappx", ".eappxbundle"
        };

        // Known framework/runtime package name prefixes that are always dependencies,
        // never main application packages. Matched against the extracted package
        // identity name (everything before the first _version_ segment), not the
        // full filename, to avoid false positives like "AppRuntime" in an app name.
        private static readonly string[] DependencyPrefixes = new string[]
        {
            "Microsoft.VCLibs",
            "Microsoft.NET.",
            "Microsoft.UI.Xaml",
            "Microsoft.WindowsAppRuntime",
            "Microsoft.DesktopAppInstaller",
            "Microsoft.DirectX",
            "Microsoft.Xbox.Identity",
            "Microsoft.Services.Store",
            "Microsoft.Advertising",
        };

        private readonly Logger _logger;

        public StoreClient(Logger logger)
        {
            if (logger == null) throw new ArgumentNullException("logger");
            _logger = logger;
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        }

        // ── Public ─────────────────────────────────────────────────────────────

        public string ResolveProductId(string input, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                _logger.Error("Input is empty.");
                return null;
            }

            input = input.Trim();

            if (Regex.IsMatch(input, @"^[A-Za-z0-9]{9,20}$"))
            {
                _logger.Info("Input recognised as Product ID: " + input.ToUpperInvariant());
                return input.ToUpperInvariant();
            }

            string[] patterns = new string[]
            {
                @"[/=]([A-Za-z0-9]{9,20})(?:[/?#]|$)",
                @"detail[/\\]([A-Za-z0-9]{9,20})",
                @"productid[/=]([A-Za-z0-9]{9,20})"
            };

            foreach (string pattern in patterns)
            {
                Match m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string id = m.Groups[1].Value.ToUpperInvariant();
                    _logger.Info("Extracted Product ID from URL: " + id);
                    return id;
                }
            }

            _logger.Error(
                "Could not parse a Product ID from the provided input.\n" +
                "Expected a Microsoft Store URL or a product ID like '9NBLGGH4NNS1'.");
            return null;
        }

        public StoreQueryResult GetPackages(string productId, string ring, CancellationToken ct)
        {
            StoreQueryResult result = new StoreQueryResult();
            result.ProductId = productId;

            try
            {
                _logger.Info("Querying store.rg-adguard.net for product: " + productId);

                string html = PostToRgAdguard(productId, ring, ct);

                if (ct.IsCancellationRequested)
                {
                    result.ErrorMessage = "Cancelled.";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(html))
                {
                    result.ErrorMessage = "Empty response from server.";
                    _logger.Error(result.ErrorMessage);
                    return result;
                }

                if (html.Contains("The product was not found") ||
                    html.Contains("ProductNotFound") ||
                    html.Contains("no packages"))
                {
                    result.ErrorMessage =
                        "Product '" + productId + "' was not found in the Microsoft Store.\n" +
                        "Verify the Product ID or URL and try again.";
                    _logger.Warning(result.ErrorMessage);
                    return result;
                }

                List<PackageInfo> packages = ParsePackageTable(html, productId);
                result.Packages  = packages;
                result.Succeeded = packages.Count > 0;

                if (!result.Succeeded)
                {
                    result.ErrorMessage =
                        "The server responded but no package links were found.\n" +
                        "The product may not have publicly downloadable packages, " +
                        "or the page format may have changed.";
                    _logger.Warning(result.ErrorMessage);
                }
                else
                {
                    _logger.Success("Found " + packages.Count + " package(s) for product " + productId + ".");
                }
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "Request was cancelled.";
                _logger.Warning(result.ErrorMessage);
            }
            catch (WebException wex)
            {
                result.ErrorMessage = BuildWebExceptionMessage(wex);
                _logger.Error(result.ErrorMessage);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = "Unexpected error: " + ex.Message;
                _logger.Error(result.ErrorMessage, ex);
            }

            return result;
        }

        /// <summary>
        /// For each non-dependency package in the result, fetch the first 256 KB
        /// of the remote file to read AppxManifest.xml and populate
        /// PackageInfo.ManifestDeps.  Runs the fetches sequentially to avoid
        /// hammering the CDN; typically takes 1-3 seconds per package.
        /// Progress is reported via the progressCallback (packageIndex, total).
        /// </summary>
        public void FetchManifests(StoreQueryResult result,
                                   Action<int, int> progressCallback,
                                   CancellationToken ct)
        {
            List<PackageInfo> mains = result.MainPackages;
            int total = mains.Count;
            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) break;
                PackageInfo pkg = mains[i];
                if (progressCallback != null)
                    progressCallback(i + 1, total);
                _logger.Info("Reading manifest " + (i + 1) + "/" + total + ": " + pkg.FileName);
                pkg.ManifestDeps = ManifestReader.ReadDependencies(pkg, _logger, ct);
            }
        }

        /// <summary>
        /// For packages whose FileSize is suspiciously small (rg-adguard placeholder),
        /// issue a HEAD request to get the real Content-Length.
        /// Runs sequentially; typically very fast (no body transferred).
        /// </summary>
        public void FetchMissingSizes(StoreQueryResult result,
                                      Action<int, int> progressCallback,
                                      CancellationToken ct)
        {
            // Threshold: anything <= 1024 bytes is almost certainly a placeholder
            const long PlaceholderThreshold = 1024;

            List<PackageInfo> needSize = new List<PackageInfo>();
            foreach (PackageInfo p in result.Packages)
                if (p.FileSize <= PlaceholderThreshold && !string.IsNullOrEmpty(p.DownloadUrl))
                    needSize.Add(p);

            int total = needSize.Count;
            if (total == 0) return;

            _logger.Debug("Fetching real sizes for " + total + " package(s) via HEAD...");

            for (int i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) break;
                PackageInfo pkg = needSize[i];
                if (progressCallback != null) progressCallback(i + 1, total);
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(pkg.DownloadUrl);
                    req.Method            = "HEAD";
                    req.Timeout           = 10000;
                    req.UserAgent         = "Microsoft-Delivery-Optimization/10.0";
                    req.AllowAutoRedirect = true;
                    ct.Register(delegate { try { req.Abort(); } catch { } });

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if (resp.ContentLength > 0)
                        {
                            pkg.FileSize = resp.ContentLength;
                            _logger.Debug("  " + pkg.FileName + ": " + pkg.FileSizeDisplay);
                        }
                    }
                }
                catch { /* best-effort */ }
            }
        }

        // ── Private ────────────────────────────────────────────────────────────

        private string PostToRgAdguard(string productId, string ring, CancellationToken ct)
        {
            string postData =
                "type=ProductId" +
                "&url=" + Uri.EscapeDataString(productId) +
                "&ring=" + Uri.EscapeDataString(ring ?? "RP") +
                "&lang=en-US";

            byte[] postBytes = Encoding.UTF8.GetBytes(postData);

            // First do a GET to obtain a session cookie, then POST with it.
            // rg-adguard checks Referer and cookies to block non-browser clients.
            CookieContainer cookies = new CookieContainer();

            HttpWebRequest getReq = (HttpWebRequest)WebRequest.Create("https://store.rg-adguard.net/");
            getReq.Method           = "GET";
            getReq.Timeout          = HttpTimeoutSeconds * 1000;
            getReq.ReadWriteTimeout = HttpTimeoutSeconds * 1000;
            getReq.CookieContainer  = cookies;
            getReq.UserAgent        = BrowserUserAgent;
            getReq.Accept           = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
            getReq.Headers["Accept-Language"]  = "en-US,en;q=0.9";
            getReq.Headers["Accept-Encoding"]  = "identity";
            getReq.Headers["Upgrade-Insecure-Requests"] = "1";
            getReq.Headers["Sec-Fetch-Dest"]   = "document";
            getReq.Headers["Sec-Fetch-Mode"]   = "navigate";
            getReq.Headers["Sec-Fetch-Site"]   = "none";
            getReq.Headers["Sec-Fetch-User"]   = "?1";
            getReq.KeepAlive        = true;

            ct.Register(delegate { try { getReq.Abort(); } catch { } });

            _logger.Debug("GET https://store.rg-adguard.net/ (obtaining session cookie)");
            try
            {
                using (HttpWebResponse getResp = (HttpWebResponse)getReq.GetResponse())
                {
                    // Drain the body so the connection is reused
                    using (StreamReader dr = new StreamReader(getResp.GetResponseStream(), Encoding.UTF8))
                        dr.ReadToEnd();
                    _logger.Debug("GET status: " + (int)getResp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Pre-flight GET failed (" + ex.Message + "), continuing anyway.");
            }

            ct.ThrowIfCancellationRequested();

            // Now POST with the session cookie and Referer
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(RgAdguardApiUrl);
            request.Method           = "POST";
            request.ContentType      = "application/x-www-form-urlencoded";
            request.ContentLength    = postBytes.Length;
            request.Timeout          = HttpTimeoutSeconds * 1000;
            request.ReadWriteTimeout = HttpTimeoutSeconds * 1000;
            request.CookieContainer  = cookies;
            request.UserAgent        = BrowserUserAgent;
            request.Accept           = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
            request.Referer          = "https://store.rg-adguard.net/";
            request.KeepAlive        = true;
            request.Headers["Accept-Language"]  = "en-US,en;q=0.9";
            request.Headers["Accept-Encoding"]  = "identity";
            request.Headers["Origin"]           = "https://store.rg-adguard.net";
            request.Headers["Sec-Fetch-Dest"]   = "document";
            request.Headers["Sec-Fetch-Mode"]   = "navigate";
            request.Headers["Sec-Fetch-Site"]   = "same-origin";
            request.Headers["Sec-Fetch-User"]   = "?1";
            request.Headers["Upgrade-Insecure-Requests"] = "1";

            ct.Register(delegate { try { request.Abort(); } catch { } });

            using (Stream stream = request.GetRequestStream())
                stream.Write(postBytes, 0, postBytes.Length);

            ct.ThrowIfCancellationRequested();

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                _logger.Debug("POST status: " + (int)response.StatusCode + " from rg-adguard");
                return reader.ReadToEnd();
            }
        }

        private List<PackageInfo> ParsePackageTable(string html, string productId)
        {
            List<PackageInfo> packages = new List<PackageInfo>();

            Regex rowRegex  = new Regex(@"<tr[^>]*>(.*?)</tr>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            Regex linkRegex = new Regex(
                @"<a\s+[^>]*href=""([^""]+)""[^>]*>\s*([^<]+\.(?:appx|appxbundle|msix|msixbundle|eappx|eappxbundle))\s*</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Regex cellRegex = new Regex(@"<td[^>]*>\s*(.*?)\s*</td>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match rowMatch in rowRegex.Matches(html))
            {
                string rowHtml = rowMatch.Groups[1].Value;

                Match linkMatch = linkRegex.Match(rowHtml);
                if (!linkMatch.Success) continue;

                string downloadUrl = HttpUtility.HtmlDecode(linkMatch.Groups[1].Value.Trim());
                string fileName    = linkMatch.Groups[2].Value.Trim();

                if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(fileName))
                    continue;

                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (!PackageExtensions.Contains(ext)) continue;

                List<string> cells = new List<string>();
                foreach (Match cellMatch in cellRegex.Matches(rowHtml))
                {
                    string cellText = Regex.Replace(cellMatch.Groups[1].Value, @"<[^>]+>", "").Trim();
                    cellText = HttpUtility.HtmlDecode(cellText);
                    if (!string.IsNullOrWhiteSpace(cellText))
                        cells.Add(cellText);
                }

                PackageInfo pkg = new PackageInfo();
                pkg.FileName     = fileName;
                pkg.DownloadUrl  = downloadUrl;
                pkg.PackageType  = DeterminePackageType(ext);
                pkg.Architecture = DetectArchitecture(fileName);
                pkg.Version      = ExtractVersion(fileName, cells);
                pkg.FileSize     = ExtractFileSize(cells);
                pkg.IsDependency = IsDependencyPackage(fileName);
                pkg.ContentId    = productId;

                packages.Add(pkg);
                _logger.Debug("  + " + pkg.FileName + " (" + pkg.Architecture + ", " + pkg.Version + ", " + pkg.FileSizeDisplay + ")");
            }

            packages.Sort(delegate(PackageInfo a, PackageInfo b)
            {
                int d = a.IsDependency.CompareTo(b.IsDependency);
                if (d != 0) return d;
                int c = string.Compare(a.Architecture, b.Architecture, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            // Mark encrypted packages as blocked when a plain equivalent exists.
            // Encrypted (.eappx/.eappxbundle) packages share the same identity name,
            // version and architecture as their plain counterparts — they are only
            // needed on devices without the matching decryption license key.
            // Prefer plain variants for general use.
            HashSet<string> plainKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageInfo p in packages)
            {
                string ext = Path.GetExtension(p.FileName).ToLowerInvariant();
                bool isEncrypted = ext == ".eappx" || ext == ".eappxbundle";
                if (!isEncrypted)
                {
                    // Key = identity name + arch + version (without extension)
                    string key = ExtractPkgName(p.FileName) + "|" +
                                 p.Architecture + "|" + p.Version;
                    plainKeys.Add(key);
                }
            }
            foreach (PackageInfo p in packages)
            {
                string ext = Path.GetExtension(p.FileName).ToLowerInvariant();
                bool isEncrypted = ext == ".eappx" || ext == ".eappxbundle";
                if (isEncrypted)
                {
                    string key = ExtractPkgName(p.FileName) + "|" +
                                 p.Architecture + "|" + p.Version;
                    if (plainKeys.Contains(key))
                        p.IsBlocked = true;   // plain equivalent exists — deprioritise
                }
            }

            return packages;
        }

        private static string DeterminePackageType(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".appx":        return "APPX";
                case ".appxbundle":  return "APPX Bundle";
                case ".msix":        return "MSIX";
                case ".msixbundle":  return "MSIX Bundle";
                case ".eappx":       return "Encrypted APPX";
                case ".eappxbundle": return "Encrypted APPX Bundle";
                default:             return extension.TrimStart('.').ToUpperInvariant();
            }
        }

        private static string DetectArchitecture(string fileName)
        {
            if (Regex.IsMatch(fileName, @"[_\-.](?:x64|amd64)[_\-.]", RegexOptions.IgnoreCase)) return "x64";
            if (Regex.IsMatch(fileName, @"[_\-.]x86[_\-.]",           RegexOptions.IgnoreCase)) return "x86";
            if (Regex.IsMatch(fileName, @"[_\-.]arm64[_\-.]",         RegexOptions.IgnoreCase)) return "arm64";
            if (Regex.IsMatch(fileName, @"[_\-.]arm[_\-.]",           RegexOptions.IgnoreCase)) return "arm";
            if (Regex.IsMatch(fileName, @"[_\-.]neutral[_\-.]",       RegexOptions.IgnoreCase)) return "neutral";
            if (Regex.IsMatch(fileName, @"\bx64\b",                   RegexOptions.IgnoreCase)) return "x64";
            if (Regex.IsMatch(fileName, @"\bx86\b",                   RegexOptions.IgnoreCase)) return "x86";
            if (Regex.IsMatch(fileName, @"\barm64\b",                 RegexOptions.IgnoreCase)) return "arm64";
            if (Regex.IsMatch(fileName, @"\barm\b",                   RegexOptions.IgnoreCase)) return "arm";
            if (Regex.IsMatch(fileName, @"\bneutral\b",               RegexOptions.IgnoreCase)) return "neutral";
            return "Unknown";
        }

        private static string ExtractVersion(string fileName, List<string> cells)
        {
            // Store filenames follow the pattern:
            //   Name_Major.Minor.Build.Revision_Arch__Hash.ext
            // Extract the version segment between the first and second underscore
            // that looks like a full 4-part version number.
            Regex fullVersion = new Regex(@"_(\d+\.\d+\.\d+\.\d+)_");
            Match mf = fullVersion.Match(fileName);
            if (mf.Success) return mf.Groups[1].Value;

            // Fall back to any 3-or-4-part dotted number in the cell text
            Regex cellVersion = new Regex(@"(\d+\.\d+\.\d+(?:\.\d+)?)");
            foreach (string cell in cells)
            {
                Match m = cellVersion.Match(cell);
                if (m.Success) return m.Groups[1].Value;
            }

            // Last resort: any dotted number in filename
            Regex anyVersion = new Regex(@"(\d+\.\d+(?:\.\d+)*)");
            Match m2 = anyVersion.Match(fileName);
            return m2.Success ? m2.Groups[1].Value : "";
        }

        private static long ExtractFileSize(List<string> cells)
        {
            Regex sizeRegex = new Regex(@"([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            foreach (string cell in cells)
            {
                Match m = sizeRegex.Match(cell);
                if (!m.Success) continue;

                double num;
                if (!double.TryParse(
                    m.Groups[1].Value.Replace(",", "."),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out num)) continue;

                switch (m.Groups[2].Value.ToUpperInvariant())
                {
                    case "B":  return (long)num;
                    case "KB": return (long)(num * 1024);
                    case "MB": return (long)(num * 1048576);
                    case "GB": return (long)(num * 1073741824L);
                    case "TB": return (long)(num * 1099511627776L);
                }
            }
            return 0;
        }

        private static bool IsDependencyPackage(string fileName)
        {
            // Extract the package identity name (before the first _digits_ segment)
            // so we match on the name alone, not on version numbers or hash suffixes.
            string pkgName = ExtractPkgName(fileName);

            foreach (string prefix in DependencyPrefixes)
                if (pkgName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Extract the package identity name from a Store filename.
        /// e.g. "Microsoft.VCLibs.140.00_14.0.33519.0_x64__hash.appx"
        ///   -> "Microsoft.VCLibs.140.00"
        /// </summary>
        private static string ExtractPkgName(string fileName)
        {
            // Remove extension
            string name = fileName;
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);

            // Split on underscore; stop at first segment that starts with a digit
            string[] parts = name.Split('_');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string part in parts)
            {
                if (part.Length > 0 && char.IsDigit(part[0])) break;
                if (part == "~") break;
                if (sb.Length > 0) sb.Append('_');
                sb.Append(part);
            }
            return sb.ToString();
        }

        private static string BuildWebExceptionMessage(WebException wex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Network error: ");
            sb.Append(wex.Message);

            HttpWebResponse httpResp = wex.Response as HttpWebResponse;
            if (httpResp != null)
            {
                sb.Append("\nHTTP status: " + (int)httpResp.StatusCode + " " + httpResp.StatusDescription);
                try
                {
                    using (StreamReader r = new StreamReader(httpResp.GetResponseStream()))
                    {
                        string body = r.ReadToEnd();
                        if (!string.IsNullOrWhiteSpace(body) && body.Length < 500)
                            sb.Append("\nResponse: " + body);
                    }
                }
                catch { }
            }

            switch (wex.Status)
            {
                case WebExceptionStatus.Timeout:
                    sb.Append("\n\nThe request timed out. Check your internet connection.");
                    break;
                case WebExceptionStatus.NameResolutionFailure:
                    sb.Append("\n\nDNS resolution failed. Check your internet connection.");
                    break;
                case WebExceptionStatus.ConnectFailure:
                    sb.Append("\n\nCould not connect to the server.");
                    break;
            }

            return sb.ToString();
        }
    }
}
