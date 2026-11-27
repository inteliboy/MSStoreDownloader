using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace MSStoreDownloader
{
    // Catalog lookup and Windows Update delivery for Store package families.
    // Wire protocol reference: MECMwithShawn/Windows-Store-Offline-Environments.
    internal sealed class StoreWimFeed
    {
        private const string Endpoint = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
        private const string Protocol = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";
        private const string DeviceAttributes = "E:BranchReadinessLevel=CB&App=WU_STORE&OSVersion=10.0.22631.0&DeviceFamily=Windows.Desktop";
        private static readonly XNamespace Service = Protocol;
        private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
        private static readonly XNamespace Addressing = "http://www.w3.org/2005/08/addressing";
        private static readonly XNamespace Security = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        private static readonly XNamespace Utility = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        private static readonly XNamespace Ticket = "http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization";

        private readonly string _ring;
        private readonly Action<string> _log;
        private readonly Dictionary<PackageInfo, Delivery> _deliveries = new Dictionary<PackageInfo, Delivery>();
        private XElement _cookie;

        private sealed class Delivery
        {
            public string UpdateId;
            public string Revision;
            public string Digest;
        }

        private sealed class Update
        {
            public XElement Identity;
            public readonly List<XElement> Files = new List<XElement>();
        }

        public StoreWimFeed(string ring, Action<string> log)
        {
            _ring = ring == "Slow" ? "WIS" : ring == "Fast" ? "WIF" : ring;
            _log = log;
        }

        public IEnumerable<PackageInfo> GetPackages(string family)
        {
            _log("Looking up " + family + " in the Microsoft Store catalog...");
            string category = LookupCategory(family);
            if (category == null) return new PackageInfo[0];

            if (_cookie == null)
            {
                XElement cookieResult = SoapRequest("GetCookie", Node("GetCookie",
                    Node("lastChange", "2015-10-21T17:01:07.1472913Z"),
                    Node("currentTime", Timestamp(DateTime.UtcNow)), Node("protocolVersion", "1.81")));
                XElement encrypted = Descendants(cookieResult, "EncryptedData").FirstOrDefault();
                XElement expires = Descendants(cookieResult, "Expiration").FirstOrDefault();
                if (encrypted == null || expires == null)
                    throw new InvalidDataException("Microsoft delivery did not return a session cookie.");
                _cookie = Node("cookie", Node("Expiration", expires.Value), Node("EncryptedData", encrypted.Value));
            }

            // Windows Update category IDs for the baseline Store client applicability rules.
            int[] installed = { 1, 2, 3, 11, 19, 2359974, 5169044, 8788830, 23110993, 23110995,
                59830006, 59830007, 59830008, 60484010, 62450018, 62450019, 62450020,
                104433538, 104900364, 105489019, 117765322, 129905029, 130040031, 132387090,
                132393049, 133399034, 138537048 };
            XElement sync = SoapRequest("SyncUpdates", Node("SyncUpdates", _cookie,
                Node("parameters", Node("ExpressQuery", false),
                    Node("InstalledNonLeafUpdateIDs", installed.Select(id => Node("int", id))),
                    Node("OtherCachedUpdateIDs"), Node("SkipSoftwareSync", false),
                    Node("NeedTwoGroupOutOfScopeUpdates", true),
                    Node("FilterAppCategoryIds", Node("CategoryIdentifier", Node("Id", category))),
                    Node("TreatAppCategoryIdsAsInstalled", true), Node("AlsoPerformRegularSync", false),
                    Node("ComputerSpec"),
                    Node("ExtendedUpdateInfoParameters",
                        Node("XmlUpdateFragmentTypes", new[] { "Extended", "LocalizedProperties", "Eula", "Published", "Core" }
                            .Select(value => Node("XmlUpdateFragmentType", value))),
                        Node("Locales", Node("string", "en-US"), Node("string", "en"))),
                    Node("ClientPreferredLanguages", Node("string", "en-US")),
                    Node("ProductsParameters", Node("SyncCurrentVersionOnly", false),
                        Node("DeviceAttributes", GetDeviceAttributes() + "&AttrDataVer=130&InstallLanguage=en-US&OSUILocale=en-US&InstallationType=Client"),
                        Node("CallerAttributes", "E:Interactive=1&IsSeeker=1&Acquisition=1&SheddingAware=1&Id=Acquisition%3BMicrosoft.WindowsStore_8wekyb3d8bbwe"),
                        Node("Products")))));

            var updates = new Dictionary<string, Update>();
            foreach (XElement record in sync.Descendants())
            {
                XElement id = Child(record, "ID");
                XElement fragment = Child(record, "Xml");
                if (id == null || fragment == null || string.IsNullOrWhiteSpace(fragment.Value)) continue;

                XElement metadata;
                try { metadata = ReadXml(new StringReader("<metadata>" + fragment.Value + "</metadata>")); }
                catch (XmlException) { continue; }

                Update update;
                if (!updates.TryGetValue(id.Value, out update))
                {
                    update = new Update();
                    updates.Add(id.Value, update);
                }
                XElement identity = Descendants(metadata, "UpdateIdentity").FirstOrDefault();
                if (identity != null) update.Identity = identity;
                update.Files.AddRange(Descendants(metadata, "File"));
            }

            var packages = new List<PackageInfo>();
            foreach (Update update in updates.Values.Where(value => value.Identity != null))
            {
                string updateId = (string)update.Identity.Attribute("UpdateID");
                string revision = (string)update.Identity.Attribute("RevisionNumber");
                if (string.IsNullOrEmpty(updateId) || string.IsNullOrEmpty(revision)) continue;
                foreach (XElement file in update.Files)
                {
                    string originalName = (string)file.Attribute("FileName") ?? "";
                    string identifier = (string)file.Attribute("InstallerSpecificIdentifier");
                    string name = string.IsNullOrEmpty(identifier) ? originalName : identifier + Path.GetExtension(originalName);
                    WimPackage identity = WimPackage.Parse(name);
                    string digest = (string)file.Attribute("Digest");
                    if (identity == null || identity.Encrypted || string.IsNullOrEmpty(digest)) continue;
                    long size;
                    long.TryParse((string)file.Attribute("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out size);
                    var package = new PackageInfo { FileName = name, FileSize = size };
                    _deliveries.Add(package, new Delivery { UpdateId = updateId, Revision = revision, Digest = digest });
                    packages.Add(package);
                }
            }
            return packages;
        }

        public void Download(PackageInfo package, string path, DownloadManager manager)
        {
            Delivery delivery;
            if (!_deliveries.TryGetValue(package, out delivery))
            {
                manager.DownloadForArchive(package, path);
                return;
            }

            XElement result = SoapRequest("GetExtendedUpdateInfo2", Node("GetExtendedUpdateInfo2",
                Node("updateIDs", Node("UpdateIdentity", Node("UpdateID", delivery.UpdateId),
                    Node("RevisionNumber", delivery.Revision))),
                Node("infoTypes", Node("XmlUpdateFragmentType", "FileUrl")),
                Node("deviceAttributes", GetDeviceAttributes())));
            Uri url = Descendants(result, "FileLocation")
                .Where(location => (string)Child(location, "FileDigest") == delivery.Digest)
                .Select(location => GetDownloadUri((string)Child(location, "Url"))).Where(uri => uri != null)
                .OrderByDescending(uri => uri.Scheme == "https").FirstOrDefault();
            if (url == null)
                throw new IOException("No Microsoft download URL was returned for " + package.FileName);

            package.DownloadUrl = url.AbsoluteUri;
            manager.DownloadForArchive(package, path);
            byte[] expected = Convert.FromBase64String(delivery.Digest);
            using (var content = File.OpenRead(path))
            using (HashAlgorithm hash = expected.Length == 20 ? (HashAlgorithm)SHA1.Create() : SHA256.Create())
            {
                if ((package.FileSize > 0 && package.FileSize != content.Length) ||
                    !hash.ComputeHash(content).SequenceEqual(expected))
                    throw new InvalidDataException("The download did not match the Store digest: " + package.FileName);
            }
        }

        private static Uri GetDownloadUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return null;
            return uri.Host.EndsWith(".delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".update.microsoft.com", StringComparison.OrdinalIgnoreCase) ? uri : null;
        }

        private static string LookupCategory(string family)
        {
            string url = "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup" +
                "?alternateId=PackageFamilyName&fieldsTemplate=details&market=US&languages=en-us&value=" + Uri.EscapeDataString(family);
            using (WebResponse response = Request(url).GetResponse())
            using (var stream = new StreamReader(response.GetResponseStream()))
            {
                var parser = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var document = parser.DeserializeObject(stream.ReadToEnd()) as Dictionary<string, object>;
                foreach (object product in Items(document, "Products"))
                foreach (object availability in Items(product, "DisplaySkuAvailabilities"))
                {
                    object data = Field(Field(Field(availability, "Sku"), "Properties"), "FulfillmentData");
                    Guid category;
                    if (string.Equals(Field(data, "PackageFamilyName") as string, family, StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(Field(data, "WuCategoryId") as string, out category))
                        return category.ToString();
                }
            }
            return null;
        }

        private static object Field(object item, string name)
        {
            var fields = item as Dictionary<string, object>;
            object value;
            return fields != null && fields.TryGetValue(name, out value) ? value : null;
        }

        private static object[] Items(object item, string name)
        {
            return Field(item, name) as object[] ?? new object[0];
        }

        private XElement SoapRequest(string action, XElement payload)
        {
            DateTime now = DateTime.UtcNow;
            var envelope = new XElement(Soap + "Envelope", new XElement(Soap + "Header",
                new XElement(Addressing + "Action", new XAttribute(Soap + "mustUnderstand", "1"), Protocol + "/" + action),
                new XElement(Addressing + "MessageID", "urn:uuid:" + Guid.NewGuid()),
                new XElement(Addressing + "To", new XAttribute(Soap + "mustUnderstand", "1"), Endpoint),
                new XElement(Security + "Security", new XAttribute(Soap + "mustUnderstand", "1"),
                    new XElement(Utility + "Timestamp", new XElement(Utility + "Created", Timestamp(now)),
                        new XElement(Utility + "Expires", Timestamp(now.AddMinutes(5)))),
                    new XElement(Ticket + "WindowsUpdateTicketsToken", new XAttribute(Utility + "id", "ClientMSA"),
                        new XElement("TicketType", new XAttribute("Name", "AAD"), new XAttribute("Version", "1.0"),
                            new XAttribute("Policy", "MBI_SSL"))))),
                new XElement(Soap + "Body", payload));

            byte[] bytes = Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
            var request = Request(Endpoint);
            request.Method = "POST";
            request.ContentType = "application/soap+xml; charset=utf-8";
            request.ContentLength = bytes.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (WebResponse response = request.GetResponse())
            using (var stream = new StreamReader(response.GetResponseStream()))
                return ReadXml(stream);
        }

        private string GetDeviceAttributes()
        {
            return DeviceAttributes + "&CurrentBranch=" + _ring + "&FlightRing=" + _ring;
        }

        private static HttpWebRequest Request(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }

        private static string Timestamp(DateTime value)
        {
            return value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static XElement ReadXml(TextReader content)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024 };
            using (XmlReader reader = XmlReader.Create(content, settings)) return XElement.Load(reader);
        }

        private static XElement Node(string name, params object[] children)
        {
            return new XElement(Service + name, children);
        }

        private static XElement Child(XElement parent, string name)
        {
            return parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);
        }

        private static IEnumerable<XElement> Descendants(XElement parent, string name)
        {
            return parent.Descendants().Where(element => element.Name.LocalName == name);
        }
    }
}
