using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace MSStoreDownloader
{
    internal sealed class WimDependency
    {
        public string Family;
        public Version MinimumVersion;
        public bool Optional;
    }

    internal sealed class WimPackageManifest
    {
        public readonly Dictionary<string, List<WimDependency>> Dependencies =
            new Dictionary<string, List<WimDependency>>(StringComparer.OrdinalIgnoreCase);
        public bool Framework;
        public bool Resource;
        public bool HasOtherPackageDependencies;

        public static WimPackageManifest Read(WimPackage package, string directory, ICollection<string> targetArchitectures)
        {
            var manifest = new WimPackageManifest();
            using (var file = File.OpenRead(Path.Combine(directory, package.FileName)))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                if (!package.Bundle)
                {
                    manifest.AddPackage(package, package.Architecture, archive);
                    return manifest;
                }

                XmlElement bundle = ReadManifest(archive, "AppxMetadata/AppxBundleManifest.xml");
                CheckIdentity(package, bundle, true);
                var applications = bundle.SelectNodes("./*[local-name()='Packages']/*[local-name()='Package']")
                    .Cast<XmlElement>()
                    .Where(element => element.GetAttribute("Type") == "application" ||
                        (element.GetAttribute("Type").Length == 0 && !element.HasAttribute("ResourceId")))
                    .ToList();

                var targets = applications.Where(element => targetArchitectures.Count == 0 ||
                    targetArchitectures.Contains(Architecture(element)) || Architecture(element) == "neutral").ToList();
                if (targets.Count == 0)
                    throw new InvalidDataException("The bundle has no application package for the WIM architectures.");

                foreach (XmlElement application in targets)
                {
                    string name = application.GetAttribute("FileName");
                    ZipArchiveEntry entry = archive.GetEntry(name);
                    if (entry == null)
                        throw new InvalidDataException("Missing bundle member " + name);

                    string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 81920, FileOptions.DeleteOnClose))
                    {
                        using (Stream source = entry.Open())
                            source.CopyTo(copy);
                        copy.Position = 0;
                        using (var inner = new ZipArchive(copy, ZipArchiveMode.Read, true))
                            manifest.AddPackage(package, Architecture(application), inner);
                    }
                }
            }
            return manifest;
        }

        private void AddPackage(WimPackage package, string arch, ZipArchive archive)
        {
            XmlElement manifest = ReadManifest(archive, "AppxManifest.xml");
            CheckIdentity(package, manifest, false);
            Framework |= ReadFlag(manifest, "Framework");
            Resource |= ReadFlag(manifest, "ResourcePackage");
            List<WimDependency> dependencies;
            if (!Dependencies.TryGetValue(arch, out dependencies))
            {
                dependencies = new List<WimDependency>();
                Dependencies.Add(arch, dependencies);
            }

            foreach (XmlElement dependency in manifest.SelectNodes("./*[local-name()='Dependencies']/*"))
            {
                if (dependency.LocalName != "PackageDependency")
                {
                    if (dependency.LocalName == "MainPackageDependency" ||
                        dependency.LocalName == "HostRuntimeDependency" ||
                        dependency.LocalName == "DriverDependency")
                        HasOtherPackageDependencies = true;
                    continue;
                }

                string name = dependency.GetAttribute("Name");
                string publisher = dependency.GetAttribute("Publisher");
                Version minimum;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(publisher) ||
                    !Version.TryParse(dependency.GetAttribute("MinVersion"), out minimum))
                    throw new InvalidDataException("Incomplete dependency declaration in " + package.FileName);

                dependencies.Add(new WimDependency
                {
                    Family = name + "_" + PublisherId(publisher),
                    MinimumVersion = minimum,
                    Optional = dependency.Attributes.Cast<XmlAttribute>().Any(attribute =>
                        attribute.LocalName == "Optional" && (attribute.Value == "true" || attribute.Value == "1"))
                });
            }
        }

        private static XmlElement ReadManifest(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name);
            if (entry == null)
                throw new InvalidDataException("Missing " + name);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024 };
            using (Stream content = entry.Open())
            using (XmlReader reader = XmlReader.Create(content, settings))
            {
                var document = new XmlDocument { XmlResolver = null };
                document.Load(reader);
                return document.DocumentElement;
            }
        }

        private static void CheckIdentity(WimPackage package, XmlElement manifest, bool outerBundle)
        {
            var identity = manifest.SelectSingleNode("./*[local-name()='Identity']") as XmlElement;
            if (identity == null || !string.Equals(identity.GetAttribute("Name"), package.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(PublisherId(identity.GetAttribute("Publisher")), package.PublisherId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest identity differs from the filename: " + package.FileName);

            if ((outerBundle || !package.Bundle) && identity.GetAttribute("Version") != package.Version.ToString())
                throw new InvalidDataException("Manifest version differs from the filename: " + package.FileName);
            if (!package.Bundle && !string.Equals(Architecture(identity), package.Architecture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest architecture differs from the filename: " + package.FileName);
        }

        private static string Architecture(XmlElement element)
        {
            string value = element.GetAttribute("Architecture");
            if (value.Length == 0) value = element.GetAttribute("ProcessorArchitecture");
            return value.Length == 0 ? "neutral" : value.ToLowerInvariant();
        }

        private static bool ReadFlag(XmlElement manifest, string name)
        {
            XmlNode property = manifest.SelectSingleNode("./*[local-name()='Properties']/*[local-name()='" + name + "']");
            return property != null && (property.InnerText.Trim() == "true" || property.InnerText.Trim() == "1");
        }

        private static string PublisherId(string publisher)
        {
            const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.Unicode.GetBytes(publisher));

            var result = new StringBuilder();
            for (int bit = 0; bit < 64; bit += 5)
            {
                int value = hash[bit / 8] << 8;
                if (bit / 8 < 7) value |= hash[bit / 8 + 1];
                result.Append(alphabet[(value >> (11 - bit % 8)) & 31]);
            }
            return result.ToString();
        }
    }
}
