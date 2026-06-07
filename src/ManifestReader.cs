// =============================================================================
// ManifestReader.cs
// Reads dependency declarations from remote APPX/MSIX/bundle packages without
// downloading the whole file.
//
// For .appx / .msix:
//   1. HEAD to get file size
//   2. Fetch tail (64 KB) to find ZIP End-of-Central-Directory
//   3. Fetch Central Directory
//   4. Locate AppxManifest.xml, fetch + decompress it
//   5. Parse <PackageDependency> elements
//
// For .appxbundle / .msixbundle:
//   The bundle manifest (AppxMetadata/AppxBundleManifest.xml) only lists
//   contained sub-packages — it does NOT carry PackageDependency elements.
//   The real dependencies are declared in each sub-package's own
//   AppxManifest.xml.  Algorithm:
//
//   1. Read AppxBundleManifest.xml from the bundle (as above)
//   2. Parse it to find the offset + size of each <Package> entry
//      whose Type="application" (skip resource/scale/language packages)
//   3. For the first application sub-package found, fetch its local file
//      header from inside the bundle, which is itself a ZIP stream
//   4. Walk the inner ZIP to find AppxManifest.xml and decompress it
//   5. Parse <PackageDependency> elements — these are the true deps
//
// C# 5 compatible.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MSStoreDownloader
{
    public static class ManifestReader
    {
        private const int  TailBytes       = 65536;   // 64 KB for EOCD search
        private const int  TimeoutMs       = 30000;

        private const string AppxManifest        = "AppxManifest.xml";
        private const string BundleManifestPath  = "AppxMetadata/AppxBundleManifest.xml";
        private const string BundleManifestFlat  = "AppxBundleManifest.xml";

        // ── Public entry point ─────────────────────────────────────────────────

        public static List<ManifestDependency> ReadDependencies(
            PackageInfo package, Logger logger, CancellationToken ct)
        {
            if (package == null || string.IsNullOrEmpty(package.DownloadUrl))
                return new List<ManifestDependency>();

            try
            {
                string ext = Path.GetExtension(package.FileName).ToLowerInvariant();
                bool isBundle = ext == ".appxbundle" || ext == ".msixbundle" || ext == ".eappxbundle";

                logger.Debug("Fetching manifest for: " + package.FileName);

                if (isBundle)
                    return ReadBundleDeps(package.DownloadUrl, package.FileName, logger, ct);
                else
                    return ReadSinglePackageDeps(package.DownloadUrl, package.FileName, logger, ct);
            }
            catch (OperationCanceledException)
            {
                logger.Debug("Manifest fetch cancelled.");
                return new List<ManifestDependency>();
            }
            catch (Exception ex)
            {
                logger.Warning("Manifest read failed for " + package.FileName + ": " + ex.Message);
                return new List<ManifestDependency>();
            }
        }

        // ── Single APPX/MSIX ───────────────────────────────────────────────────

        private static List<ManifestDependency> ReadSinglePackageDeps(
            string url, string fileName, Logger logger, CancellationToken ct)
        {
            string xml = FetchZipEntry(url, AppxManifest, null, logger, ct);
            if (xml == null)
            {
                logger.Debug("  AppxManifest.xml not found.");
                return new List<ManifestDependency>();
            }
            List<ManifestDependency> deps = ParsePackageDeps(xml);
            logger.Debug("  Manifest: " + deps.Count + " dependency declaration(s).");
            foreach (ManifestDependency d in deps) logger.Debug("    -> " + d);
            return deps;
        }

        // ── Bundle ─────────────────────────────────────────────────────────────

        private static List<ManifestDependency> ReadBundleDeps(
            string url, string fileName, Logger logger, CancellationToken ct)
        {
            // Step 1: read the bundle manifest to find sub-package offsets
            string bundleXml = FetchZipEntry(url, BundleManifestPath, BundleManifestFlat, logger, ct);
            if (bundleXml == null)
            {
                logger.Debug("  AppxBundleManifest.xml not found; using filename inference.");
                return InferBundleDeps(fileName, logger);
            }

            logger.Debug("  Bundle manifest found. Parsing sub-packages...");

            // Step 2: parse sub-package entries from the bundle manifest
            List<BundlePackageEntry> subPackages = ParseBundleManifest(bundleXml, logger);
            if (subPackages.Count == 0)
            {
                logger.Debug("  No application sub-packages found in bundle manifest.");
                return InferBundleDepsFromXml(bundleXml, logger);
            }

            // Step 3: for each application sub-package, read its AppxManifest.xml
            // The sub-package is stored as a raw ZIP stream inside the bundle.
            // We locate it via the bundle's central directory, then treat the
            // compressed sub-package data as a nested ZIP.
            // We only need ONE sub-package's manifest — they all declare the same
            // framework dependencies. Pick the first application sub-package.
            List<ManifestDependency> allDeps = new List<ManifestDependency>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BundlePackageEntry sub in subPackages)
            {
                if (ct.IsCancellationRequested) break;
                logger.Debug("  Reading sub-package manifest: " + sub.FileName);

                List<ManifestDependency> subDeps =
                    ReadSubPackageDeps(url, sub.FileName, logger, ct);

                foreach (ManifestDependency d in subDeps)
                {
                    string key = d.Name + "|" + d.MinVersion;
                    if (!seen.Contains(key))
                    {
                        seen.Add(key);
                        allDeps.Add(d);
                    }
                }

                // One sub-package is enough if it has real deps
                if (allDeps.Count > 0) break;
            }

            if (allDeps.Count > 0)
            {
                logger.Debug("  Bundle deps from sub-package manifest: " + allDeps.Count);
                foreach (ManifestDependency d in allDeps) logger.Debug("    -> " + d);
            }
            else
            {
                logger.Debug("  No deps found in sub-packages; using XML inference.");
                allDeps = InferBundleDepsFromXml(bundleXml, logger);
            }

            return allDeps;
        }

        /// <summary>
        /// Read the AppxManifest.xml from inside a sub-package (.appx/.msix) that
        /// is itself stored inside the outer bundle.
        ///
        /// The sub-package is stored as a compressed or stored entry in the bundle
        /// ZIP.  We:
        ///   1. Locate the sub-package entry in the bundle's central directory
        ///   2. Fetch its local file header to find where data starts
        ///   3. Fetch up to 512 KB of the compressed sub-package data
        ///   4. If the sub-package is stored (method=0), it is itself a ZIP —
        ///      walk its local headers to find AppxManifest.xml
        ///   5. If the sub-package is deflated, we can't easily re-inflate a
        ///      partial stream and then re-parse it as ZIP, so we fall back to
        ///      fetching the range that corresponds to the sub-package's own tail
        ///      and walking its central directory.
        /// </summary>
        private static List<ManifestDependency> ReadSubPackageDeps(
            string bundleUrl, string subFileName, Logger logger, CancellationToken ct)
        {
            try
            {
                // Get bundle file size (needed to compute offsets)
                long bundleSize = GetFileSize(bundleUrl, logger, ct);
                if (bundleSize <= 0) return new List<ManifestDependency>();

                // Find the sub-package entry in the bundle's central directory
                byte[] tail = FetchRange(bundleUrl, Math.Max(0, bundleSize - TailBytes),
                                          bundleSize - 1, logger, ct);
                if (tail == null) return new List<ManifestDependency>();

                long cdOffset, cdSize;
                if (!FindEOCD(tail, bundleSize - TailBytes, bundleSize, out cdOffset, out cdSize, logger))
                    return new List<ManifestDependency>();

                byte[] cd = FetchCentralDirectory(bundleUrl, cdOffset, cdSize,
                                                   tail, bundleSize - TailBytes, logger, ct);
                if (cd == null) return new List<ManifestDependency>();

                CdEntry subEntry = FindCdEntry(cd, subFileName, null, logger);
                if (subEntry == null)
                {
                    logger.Debug("    Sub-package not found in bundle central directory.");
                    return new List<ManifestDependency>();
                }

                logger.Debug("    Sub-package entry: offset=" + subEntry.LocalHeaderOffset +
                             " compSize=" + subEntry.CompressedSize +
                             " method=" + subEntry.CompressionMethod);

                // Fetch the local header of the sub-package to find the data start
                byte[] localHdr = FetchRange(bundleUrl,
                    subEntry.LocalHeaderOffset,
                    subEntry.LocalHeaderOffset + 30 + 65535 + 65535 - 1,
                    logger, ct);
                if (localHdr == null || localHdr.Length < 30) return new List<ManifestDependency>();

                int nameLen  = ReadUInt16(localHdr, 26);
                int extraLen = ReadUInt16(localHdr, 28);
                long dataStart = subEntry.LocalHeaderOffset + 30 + nameLen + extraLen;

                if (subEntry.CompressionMethod == 0)
                {
                    // Sub-package is STORED (uncompressed) inside the bundle.
                    // Treat the sub-package data range as a ZIP and walk its
                    // local file headers linearly to find AppxManifest.xml.
                    // Fetch the first 512 KB of the sub-package data.
                    const int scanBytes = 524288;
                    byte[] subData = FetchRange(bundleUrl,
                        dataStart, dataStart + scanBytes - 1, logger, ct);
                    if (subData == null) return new List<ManifestDependency>();

                    string xml = ExtractEntryLinear(subData, AppxManifest, logger);
                    if (xml != null)
                        return ParsePackageDeps(xml);

                    // Try via the sub-package's own central directory
                    // (the tail of the sub-package, within the bundle)
                    long subSize = subEntry.UncompressedSize;
                    if (subSize > 0)
                    {
                        long subTailStart = dataStart + Math.Max(0, subSize - TailBytes);
                        byte[] subTail = FetchRange(bundleUrl,
                            subTailStart, dataStart + subSize - 1, logger, ct);
                        if (subTail != null)
                        {
                            long subCdOff, subCdSz;
                            // Adjust: tailStart relative to sub-package data start
                            if (FindEOCD(subTail, subTailStart - dataStart, subSize,
                                          out subCdOff, out subCdSz, logger))
                            {
                                // subCdOff is relative to sub-package; make it absolute in bundle
                                long absCdOff = dataStart + subCdOff;
                                byte[] subCd = FetchRange(bundleUrl,
                                    absCdOff, absCdOff + subCdSz - 1, logger, ct);
                                if (subCd != null)
                                {
                                    CdEntry mfEntry = FindCdEntry(subCd, AppxManifest, null, logger);
                                    if (mfEntry != null)
                                    {
                                        long absEntryOff = dataStart + mfEntry.LocalHeaderOffset;
                                        byte[] entryData = FetchRange(bundleUrl,
                                            absEntryOff,
                                            absEntryOff + 30 + 65535 + mfEntry.CompressedSize - 1,
                                            logger, ct);
                                        if (entryData != null)
                                        {
                                            xml = DecompressLocalEntry(entryData, mfEntry, logger);
                                            if (xml != null) return ParsePackageDeps(xml);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else if (subEntry.CompressionMethod == 8)
                {
                    // Sub-package is DEFLATED in the bundle — we can't seek inside
                    // a deflate stream.  Instead, treat the remote URL as if the
                    // sub-package were a standalone file by using HTTP ranges to
                    // simulate a random-access ZIP read starting from dataStart.
                    // Fetch the sub-package's tail to find its own central directory.
                    long subSize = subEntry.UncompressedSize > 0
                        ? subEntry.UncompressedSize
                        : subEntry.CompressedSize;
                    if (subSize <= 0) return new List<ManifestDependency>();

                    // We can't decompress partial deflate; use linear scan of first 512 KB
                    logger.Debug("    Sub-package is deflated in bundle — linear scan of data.");
                    const int scanBytes = 524288;
                    byte[] subData = FetchRange(bundleUrl,
                        dataStart, dataStart + scanBytes - 1, logger, ct);
                    if (subData == null) return new List<ManifestDependency>();

                    // Try to decompress as deflate stream and then scan as ZIP
                    // (only works if AppxManifest.xml is near the start)
                    try
                    {
                        using (MemoryStream  ms = new MemoryStream(subData))
                        using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                        {
                            MemoryStream decompressed = new MemoryStream();
                            byte[] buf = new byte[32768];
                            int    read;
                            long   total = 0;
                            while ((read = ds.Read(buf, 0, buf.Length)) > 0)
                            {
                                decompressed.Write(buf, 0, read);
                                total += read;
                                if (total >= scanBytes * 4) break;
                            }
                            byte[] inner = decompressed.ToArray();
                            string xml = ExtractEntryLinear(inner, AppxManifest, logger);
                            if (xml != null) return ParsePackageDeps(xml);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Debug("    Deflate sub-package scan failed: " + ex.Message);
                    }
                }

                return new List<ManifestDependency>();
            }
            catch (Exception ex)
            {
                logger.Debug("    Sub-package manifest read failed: " + ex.Message);
                return new List<ManifestDependency>();
            }
        }

        // ── Bundle manifest XML parsing ────────────────────────────────────────

        private class BundlePackageEntry
        {
            public string FileName;
            public string Type;      // "application", "resource"
        }

        /// <summary>
        /// Parse AppxBundleManifest.xml to extract sub-package filenames.
        /// Only "application" type packages carry AppxManifest.xml with deps.
        ///
        /// Relevant XML:
        ///   &lt;Package Type="application" FileName="App_x64.appx" ... /&gt;
        ///   &lt;Package Type="resource"    FileName="App.language-en.appx" ... /&gt;
        /// </summary>
        private static List<BundlePackageEntry> ParseBundleManifest(string xml, Logger logger)
        {
            List<BundlePackageEntry> result = new List<BundlePackageEntry>();

            // Match both self-closing and non-self-closing Package elements,
            // including multi-line attribute lists (Singleline mode).
            Regex pkgRegex  = new Regex(@"<Package\s([^>]+?)(?:/>|>)",
                                 RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Regex attrType  = new Regex(@"\bType\s*=""([^""]+)""",        RegexOptions.IgnoreCase);
            Regex attrFile  = new Regex(@"\bFileName\s*=""([^""]+)""",    RegexOptions.IgnoreCase);
            Regex attrResId = new Regex(@"\bResourceId\s*=""([^""]*)"" ", RegexOptions.IgnoreCase);
            Regex attrArch  = new Regex(@"\bArchitecture\s*=""([^""]+)""",RegexOptions.IgnoreCase);

            foreach (Match m in pkgRegex.Matches(xml))
            {
                string attrs = m.Groups[1].Value;

                Match mFile = attrFile.Match(attrs);
                if (!mFile.Success) continue;
                string file = mFile.Groups[1].Value;

                // Schema v4+ (2016): Type="application" or Type="resource"
                // Schema v1  (2013): no Type attribute; infer from ResourceId
                //   empty/absent ResourceId = application, non-empty = resource
                Match mType  = attrType.Match(attrs);
                Match mResId = attrResId.Match(attrs);
                Match mArch  = attrArch.Match(attrs);

                string type;
                if (mType.Success)
                {
                    type = mType.Groups[1].Value;
                }
                else
                {
                    bool hasResId = mResId.Success && mResId.Groups[1].Value.Length > 0;
                    type = hasResId ? "resource" : "application";
                }

                if (!string.Equals(type, "application", StringComparison.OrdinalIgnoreCase))
                    continue;

                string arch = mArch.Success ? mArch.Groups[1].Value : "unknown";
                BundlePackageEntry e = new BundlePackageEntry();
                e.FileName = file;
                e.Type     = type;
                result.Add(e);
                logger.Debug("    Sub-package (" + arch + "): " + file);
            }

            // Prefer arch-specific sub-packages first (neutral/AnyCPU last)
            // as they carry the authoritative PackageDependency declarations
            result.Sort(delegate(BundlePackageEntry a, BundlePackageEntry b)
            {
                bool aN = a.FileName.IndexOf("neutral", StringComparison.OrdinalIgnoreCase) >= 0
                       || a.FileName.IndexOf("AnyCPU",  StringComparison.OrdinalIgnoreCase) >= 0;
                bool bN = b.FileName.IndexOf("neutral", StringComparison.OrdinalIgnoreCase) >= 0
                       || b.FileName.IndexOf("AnyCPU",  StringComparison.OrdinalIgnoreCase) >= 0;
                if (aN == bN) return 0;
                return aN ? 1 : -1;
            });

            return result;
        }

        // ── Generic ZIP central-directory approach ─────────────────────────────

        /// <summary>
        /// Locate a named entry in a remote ZIP file using its central directory.
        /// Returns the decompressed text content, or null.
        /// </summary>
        private static string FetchZipEntry(string url,
                                             string name1, string name2,
                                             Logger logger, CancellationToken ct)
        {
            long fileSize = GetFileSize(url, logger, ct);
            if (fileSize <= 0)
            {
                logger.Debug("  HEAD failed; trying linear scan.");
                return FetchZipEntryLinear(url, name1, name2, logger, ct);
            }

            logger.Debug("  File size: " + FormatBytes(fileSize));

            byte[] tail = FetchRange(url, Math.Max(0, fileSize - TailBytes),
                                      fileSize - 1, logger, ct);
            if (tail == null) return null;

            long cdOffset, cdSize;
            if (!FindEOCD(tail, fileSize - TailBytes, fileSize, out cdOffset, out cdSize, logger))
            {
                logger.Debug("  EOCD not found; linear scan.");
                return FetchZipEntryLinear(url, name1, name2, logger, ct);
            }

            logger.Debug("  Central directory: offset=" + cdOffset + " size=" + cdSize);

            byte[] cd = FetchCentralDirectory(url, cdOffset, cdSize,
                                               tail, fileSize - TailBytes, logger, ct);
            if (cd == null) return null;

            CdEntry entry = FindCdEntry(cd, name1, name2, logger);
            if (entry == null) return null;

            logger.Debug("  Found entry '" + entry.Name + "' at offset " +
                         entry.LocalHeaderOffset + " comp=" + entry.CompressedSize +
                         " method=" + entry.CompressionMethod);

            long fetchEnd = entry.LocalHeaderOffset + 30 + 65535 + 65535 + entry.CompressedSize;
            if (fetchEnd >= fileSize) fetchEnd = fileSize - 1;

            byte[] localData = FetchRange(url, entry.LocalHeaderOffset, fetchEnd, logger, ct);
            if (localData == null) return null;

            return DecompressLocalEntry(localData, entry, logger);
        }

        private static byte[] FetchCentralDirectory(string url, long cdOffset, long cdSize,
                                                      byte[] tail, long tailStart,
                                                      Logger logger, CancellationToken ct)
        {
            if (cdOffset >= tailStart && cdOffset + cdSize <= tailStart + tail.Length)
            {
                int lo = (int)(cdOffset - tailStart);
                byte[] cd = new byte[(int)cdSize];
                Array.Copy(tail, lo, cd, 0, (int)cdSize);
                return cd;
            }
            return FetchRange(url, cdOffset, cdOffset + cdSize - 1, logger, ct);
        }

        // ── EOCD parsing ───────────────────────────────────────────────────────

        private static bool FindEOCD(byte[] data, long dataStartOffset, long fileSize,
                                      out long cdOffset, out long cdSize, Logger logger)
        {
            cdOffset = 0;
            cdSize   = 0;

            // Search backwards for EOCD signature 0x06054b50
            for (int i = data.Length - 4; i >= 0; i--)
            {
                if (data[i] == 0x50 && data[i+1] == 0x4B &&
                    data[i+2] == 0x05 && data[i+3] == 0x06)
                {
                    if (i + 22 > data.Length) continue;

                    uint cdSzU  = ReadUInt32(data, i + 12);
                    uint cdOffU = ReadUInt32(data, i + 16);

                    if (cdSzU == 0xFFFFFFFF || cdOffU == 0xFFFFFFFF)
                        return FindEOCD64(data, i, dataStartOffset, out cdOffset, out cdSize, logger);

                    cdOffset = cdOffU;
                    cdSize   = cdSzU;
                    return true;
                }
            }
            return false;
        }

        private static bool FindEOCD64(byte[] data, int eocdPos, long dataStartOffset,
                                        out long cdOffset, out long cdSize, Logger logger)
        {
            cdOffset = 0;
            cdSize   = 0;

            // ZIP64 EOCD locator: signature 0x07064b50, 20 bytes before regular EOCD
            int locPos = eocdPos - 20;
            if (locPos < 0 || locPos + 20 > data.Length) return false;

            if (data[locPos] != 0x50 || data[locPos+1] != 0x4B ||
                data[locPos+2] != 0x06 || data[locPos+3] != 0x07)
                return false;

            long eocd64Off = ReadInt64(data, locPos + 8);
            logger.Debug("  ZIP64 EOCD at offset " + eocd64Off);

            long localOff = eocd64Off - dataStartOffset;
            if (localOff < 0 || localOff + 56 > data.Length)
            {
                logger.Debug("  ZIP64 EOCD not in fetched tail.");
                return false;
            }

            cdSize   = ReadInt64(data, (int)localOff + 40);
            cdOffset = ReadInt64(data, (int)localOff + 48);
            return true;
        }

        // ── Central directory walking ──────────────────────────────────────────

        private class CdEntry
        {
            public string Name;
            public int    CompressionMethod;
            public long   CompressedSize;
            public long   UncompressedSize;
            public long   LocalHeaderOffset;
        }

        private static CdEntry FindCdEntry(byte[] cd, string name1, string name2, Logger logger)
        {
            int pos = 0;
            while (pos + 46 <= cd.Length)
            {
                if (cd[pos] != 0x50 || cd[pos+1] != 0x4B ||
                    cd[pos+2] != 0x01 || cd[pos+3] != 0x02)
                    break;

                int  comp      = ReadUInt16(cd, pos + 10);
                uint compSzU   = ReadUInt32(cd, pos + 20);
                uint uncompSzU = ReadUInt32(cd, pos + 24);
                int  nameLen   = ReadUInt16(cd, pos + 28);
                int  extraLen  = ReadUInt16(cd, pos + 30);
                int  cmtLen    = ReadUInt16(cd, pos + 32);
                uint localOffU = ReadUInt32(cd, pos + 42);

                if (pos + 46 + nameLen > cd.Length) break;

                string entryName = Encoding.UTF8.GetString(cd, pos + 46, nameLen);

                long compSz   = compSzU;
                long uncompSz = uncompSzU;
                long localOff = localOffU;

                if (compSzU == 0xFFFFFFFF || uncompSzU == 0xFFFFFFFF || localOffU == 0xFFFFFFFF)
                    ParseZip64Extra(cd, pos + 46 + nameLen, extraLen,
                                    ref compSz, ref uncompSz, ref localOff,
                                    compSzU, uncompSzU, localOffU);

                bool match =
                    string.Equals(entryName, name1, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(name2) &&
                     string.Equals(entryName, name2, StringComparison.OrdinalIgnoreCase));

                if (match)
                {
                    CdEntry e = new CdEntry();
                    e.Name              = entryName;
                    e.CompressionMethod = comp;
                    e.CompressedSize    = compSz;
                    e.UncompressedSize  = uncompSz;
                    e.LocalHeaderOffset = localOff;
                    return e;
                }

                pos += 46 + nameLen + extraLen + cmtLen;
            }
            return null;
        }

        private static void ParseZip64Extra(byte[] d, int start, int extraLen,
                                             ref long compSz, ref long uncompSz, ref long localOff,
                                             uint origComp, uint origUncomp, uint origLocal)
        {
            int pos = start;
            int end = start + extraLen;
            while (pos + 4 <= end)
            {
                int hdr  = ReadUInt16(d, pos);
                int dSz  = ReadUInt16(d, pos + 2);
                pos += 4;
                if (hdr == 0x0001)
                {
                    int fp = pos;
                    if (origUncomp == 0xFFFFFFFF && fp + 8 <= end) { uncompSz = ReadInt64(d, fp); fp += 8; }
                    if (origComp  == 0xFFFFFFFF && fp + 8 <= end) { compSz   = ReadInt64(d, fp); fp += 8; }
                    if (origLocal == 0xFFFFFFFF && fp + 8 <= end) { localOff = ReadInt64(d, fp); }
                    break;
                }
                pos += dSz;
            }
        }

        // ── Local entry decompression ──────────────────────────────────────────

        private static string DecompressLocalEntry(byte[] data, CdEntry entry, Logger logger)
        {
            if (data.Length < 30) return null;
            if (data[0] != 0x50 || data[1] != 0x4B || data[2] != 0x03 || data[3] != 0x04)
            {
                logger.Debug("  Local header signature mismatch.");
                return null;
            }

            int nameLen   = ReadUInt16(data, 26);
            int extraLen  = ReadUInt16(data, 28);
            int dataStart = 30 + nameLen + extraLen;
            if (dataStart >= data.Length) return null;

            long avail = data.Length - dataStart;
            long sz    = Math.Min(entry.CompressedSize, avail);
            if (sz <= 0) return null;

            byte[] compressed = new byte[sz];
            Array.Copy(data, dataStart, compressed, 0, (int)sz);

            if (entry.CompressionMethod == 0)
                return Encoding.UTF8.GetString(compressed);

            if (entry.CompressionMethod == 8)
            {
                try
                {
                    using (MemoryStream  ms = new MemoryStream(compressed))
                    using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                    using (StreamReader  sr = new StreamReader(ds, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (Exception ex)
                {
                    logger.Debug("  Decompression failed: " + ex.Message);
                    return null;
                }
            }

            logger.Debug("  Unsupported compression method: " + entry.CompressionMethod);
            return null;
        }

        // ── Linear scan fallback ───────────────────────────────────────────────

        private static string FetchZipEntryLinear(string url, string n1, string n2,
                                                   Logger logger, CancellationToken ct)
        {
            const int scan = 524288;
            logger.Debug("  Linear scan of first " + scan / 1024 + " KB.");
            byte[] data = FetchRange(url, 0, scan - 1, logger, ct);
            return data != null ? ExtractEntryLinear(data, n1, n2 != null ? n2 : "", logger) : null;
        }

        private static string ExtractEntryLinear(byte[] data, string name1, string name2, Logger logger)
        {
            return ExtractEntryLinear(data, name1, logger) ??
                   (string.IsNullOrEmpty(name2) ? null : ExtractEntryLinear(data, name2, logger));
        }

        private static string ExtractEntryLinear(byte[] data, string targetName, Logger logger)
        {
            int pos = 0;
            while (pos + 30 < data.Length)
            {
                if (data[pos] != 0x50 || data[pos+1] != 0x4B ||
                    data[pos+2] != 0x03 || data[pos+3] != 0x04)
                { pos++; continue; }

                int  flags   = ReadUInt16(data, pos + 6);
                int  comp    = ReadUInt16(data, pos + 8);
                uint compSz  = ReadUInt32(data, pos + 18);
                int  nameLen = ReadUInt16(data, pos + 26);
                int  extLen  = ReadUInt16(data, pos + 28);

                if (pos + 30 + nameLen > data.Length) break;
                string name = Encoding.UTF8.GetString(data, pos + 30, nameLen);
                int dStart  = pos + 30 + nameLen + extLen;

                if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (compSz == 0 && (flags & 0x08) != 0)
                    {
                        compSz = 0;
                        for (int i = dStart; i + 4 < data.Length; i++)
                            if (data[i] == 0x50 && data[i+1] == 0x4B &&
                                data[i+2] == 0x03 && data[i+3] == 0x04)
                            { compSz = (uint)(i - dStart); break; }
                    }
                    uint avail = (uint)(data.Length - dStart);
                    uint rsz   = Math.Min(compSz, avail);
                    if (rsz == 0) return null;

                    byte[] eb = new byte[rsz];
                    Array.Copy(data, dStart, eb, 0, (int)rsz);

                    if (comp == 0) return Encoding.UTF8.GetString(eb);
                    if (comp == 8)
                    {
                        try
                        {
                            using (MemoryStream  ms = new MemoryStream(eb))
                            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                            using (StreamReader  sr = new StreamReader(ds, Encoding.UTF8))
                                return sr.ReadToEnd();
                        }
                        catch { return null; }
                    }
                    return null;
                }

                pos = dStart + (int)(compSz > 0 ? compSz : 0);
                if (compSz == 0) pos = dStart;
            }
            return null;
        }

        // ── XML parsing ────────────────────────────────────────────────────────

        private static List<ManifestDependency> ParsePackageDeps(string xml)
        {
            List<ManifestDependency> result = new List<ManifestDependency>();
            Regex rx   = new Regex(@"<PackageDependency\s([^/]*?)/>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Regex rn   = new Regex(@"\bName\s*=\s*""([^""]+)""",       RegexOptions.IgnoreCase);
            Regex rv   = new Regex(@"\bMinVersion\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            Regex rp   = new Regex(@"\bPublisher\s*=\s*""([^""]+)""",  RegexOptions.IgnoreCase);

            foreach (Match m in rx.Matches(xml))
            {
                string a  = m.Groups[1].Value;
                Match  mn = rn.Match(a);
                if (!mn.Success) continue;

                ManifestDependency d = new ManifestDependency();
                d.Name = mn.Groups[1].Value;
                Match mv = rv.Match(a); if (mv.Success) d.MinVersion = mv.Groups[1].Value;
                Match mp = rp.Match(a); if (mp.Success) d.Publisher  = mp.Groups[1].Value;
                result.Add(d);
            }
            return result;
        }

        // ── Inference fallbacks ────────────────────────────────────────────────

        private static List<ManifestDependency> InferBundleDepsFromXml(string xml, Logger logger)
        {
            List<ManifestDependency> r = new List<ManifestDependency>();
            bool war    = xml.IndexOf("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) >= 0
                       || xml.IndexOf("WinAppSDK",         StringComparison.OrdinalIgnoreCase) >= 0;
            bool xaml   = xml.IndexOf("UI.Xaml",           StringComparison.OrdinalIgnoreCase) >= 0;
            string warV = "";
            Match  wm   = new Regex(@"WindowsAppRuntime[._](\d+\.\d+)",
                              RegexOptions.IgnoreCase).Match(xml);
            if (wm.Success) warV = wm.Groups[1].Value;

            logger.Debug("  XML inference: WAR=" + war + " UIXaml=" + xaml);
            AddDep(r, "Microsoft.VCLibs.140.00",            "14.0.0.0");
            AddDep(r, "Microsoft.VCLibs.140.00.UWPDesktop", "14.0.0.0");
            if (war) { string n = "Microsoft.WindowsAppRuntime"; if (warV != "") n += "." + warV; AddDep(r, n, "0.0.0.0"); }
            if (xaml) AddDep(r, "Microsoft.UI.Xaml", "0.0.0.0");
            return r;
        }

        private static List<ManifestDependency> InferBundleDeps(string fileName, Logger logger)
        {
            logger.Debug("  Filename inference: " + fileName);
            List<ManifestDependency> r = new List<ManifestDependency>();
            AddDep(r, "Microsoft.VCLibs.140.00",            "14.0.0.0");
            AddDep(r, "Microsoft.VCLibs.140.00.UWPDesktop", "14.0.0.0");
            return r;
        }

        private static void AddDep(List<ManifestDependency> l, string name, string minVer)
        {
            ManifestDependency d = new ManifestDependency();
            d.Name = name; d.MinVersion = minVer;
            l.Add(d);
        }

        // ── HTTP helpers ───────────────────────────────────────────────────────

        private static long GetFileSize(string url, Logger logger, CancellationToken ct)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD"; req.Timeout = TimeoutMs;
                req.UserAgent = "Microsoft-Delivery-Optimization/10.0";
                req.AllowAutoRedirect = true;
                ct.Register(delegate { try { req.Abort(); } catch { } });
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                    return r.ContentLength;
            }
            catch { return -1; }
        }

        private static byte[] FetchRange(string url, long from, long to,
                                          Logger logger, CancellationToken ct)
        {
            if (from > to) return new byte[0];
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET"; req.Timeout = TimeoutMs; req.ReadWriteTimeout = TimeoutMs;
            req.UserAgent = "Microsoft-Delivery-Optimization/10.0";
            req.AddRange(from, to); req.AllowAutoRedirect = true;
            ct.Register(delegate { try { req.Abort(); } catch { } });

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            {
                MemoryStream ms  = new MemoryStream();
                byte[]       buf = new byte[32768];
                int          rd;
                long         tot = 0, lim = to - from + 1;
                while ((rd = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int take = (int)Math.Min(rd, lim - tot);
                    ms.Write(buf, 0, take);
                    tot += take;
                    if (tot >= lim) break;
                }
                return ms.ToArray();
            }
        }

        // ── Binary helpers ─────────────────────────────────────────────────────

        private static int  ReadUInt16(byte[] d, int o) { return d[o] | (d[o+1] << 8); }
        private static uint ReadUInt32(byte[] d, int o) { return (uint)(d[o]|(d[o+1]<<8)|(d[o+2]<<16)|(d[o+3]<<24)); }
        private static long ReadInt64(byte[] d, int o)
        {
            return (long)((ulong)d[o]|(ulong)d[o+1]<<8|(ulong)d[o+2]<<16|(ulong)d[o+3]<<24|
                          (ulong)d[o+4]<<32|(ulong)d[o+5]<<40|(ulong)d[o+6]<<48|(ulong)d[o+7]<<56);
        }
        private static string FormatBytes(long b)
        {
            if (b >= 1073741824L) return string.Format("{0:F2} GB", b/1073741824.0);
            if (b >= 1048576L)    return string.Format("{0:F1} MB", b/1048576.0);
            if (b >= 1024L)       return string.Format("{0:F1} KB", b/1024.0);
            return b + " B";
        }
    }
}
