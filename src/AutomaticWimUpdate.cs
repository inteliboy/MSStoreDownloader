using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MSStoreDownloader
{
    internal sealed class AutomaticWimUpdate
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        private readonly Func<string, IEnumerable<PackageInfo>> _getPackages;
        private readonly Action<PackageInfo, string> _download;
        private readonly Action<string> _log;
        private readonly Dictionary<string, WimPackageManifest> _manifests =
            new Dictionary<string, WimPackageManifest>(Names);
        private readonly Dictionary<string, Candidate> _available = new Dictionary<string, Candidate>(Names);
        private readonly HashSet<string> _queriedFamilies = new HashSet<string>(Names);
        private readonly HashSet<string> _required = new HashSet<string>(Names);
        private readonly HashSet<string> _visited = new HashSet<string>(Names);
        private readonly WimUpdatePlan _plan = new WimUpdatePlan();
        private string _downloads;
        private HashSet<string> _architectures;
        private List<WimPackage> _existing;

        private sealed class Candidate
        {
            public WimPackage Package;
            public PackageInfo Download;
        }

        public AutomaticWimUpdate(Func<string, IEnumerable<PackageInfo>> getPackages,
            Action<PackageInfo, string> download, Action<string> log)
        {
            _getPackages = getPackages;
            _download = download;
            _log = log;
        }

        public static WimUpdatePlan Run(string input, string ring, StoreClient store, Logger logger)
        {
            var downloader = new DownloadManager(logger);
            var feed = new StoreWimFeed(ring, logger.Info);
            var updater = new AutomaticWimUpdate(family => GetStoreOffers(feed, store, logger, family, ring),
                (package, path) => feed.Download(package, path, downloader), logger.Info);
            return PackageWim.CreateUpdated(input, updater.Plan, logger.Info);
        }

        private static IEnumerable<PackageInfo> GetStoreOffers(StoreWimFeed feed, StoreClient store, Logger logger,
            string family, string ring)
        {
            IEnumerable<PackageInfo> offers;
            try
            {
                offers = feed.GetPackages(family);
                if (offers.Any(offer => Names.Equals(WimPackage.Parse(offer.FileName).Family, family)))
                    return offers;
            }
            catch (Exception ex)
            {
                logger.Warning("Microsoft delivery lookup failed for " + family + ": " + ex.Message);
                offers = Enumerable.Empty<PackageInfo>();
            }
            return offers.Concat(store.GetPackagesByFamily(family, ring, CancellationToken.None).Packages);
        }

        public WimUpdatePlan Plan(IList<WimPackage> packages, bool unsupported,
            string source, string downloads)
        {
            _downloads = downloads;
            _existing = packages.ToList();
            _plan.PruningSkipped = unsupported;
            _architectures = new HashSet<string>(_existing
                .Where(package => package.Architecture != "neutral").Select(package => package.Architecture), Names);
            foreach (WimPackage package in _existing)
            {
                if (package.Encrypted)
                {
                    PreserveUnknown(package, "encrypted package");
                    continue;
                }

                try
                {
                    _manifests.Add(package.FileName, WimPackageManifest.Read(package, source, _architectures));
                }
                catch (IOException ex) { PreserveUnknown(package, ex.Message); }
                catch (InvalidDataException ex) { PreserveUnknown(package, ex.Message); }
                catch (System.Xml.XmlException ex) { PreserveUnknown(package, ex.Message); }
            }

            var applications = _existing.Where(package => _manifests.ContainsKey(package.FileName)
                && !_manifests[package.FileName].Framework && !_manifests[package.FileName].Resource)
                .GroupBy(package => new { Family = package.Family.ToLowerInvariant(),
                    package.Architecture, package.Bundle, Resource = package.Resource.ToLowerInvariant() })
                .ToList();
            if (applications.Count == 0)
                throw new InvalidDataException("No application manifests could be read from the WIM.");

            _log("Checking " + applications.Count + " application variant(s) against the Store...");
            foreach (string family in applications.Select(group => group.First().Family).Distinct(Names))
                Query(family);

            var roots = new List<WimPackage>();
            int resolved = 0;
            foreach (var group in applications)
            {
                WimPackage old = group.OrderByDescending(package => package.Version).First();
                Candidate best = _available.Values.Where(candidate => Names.Equals(old.Family, candidate.Package.Family)
                    && old.Architecture == candidate.Package.Architecture && old.Bundle == candidate.Package.Bundle
                    && Names.Equals(old.Resource, candidate.Package.Resource))
                    .OrderByDescending(candidate => candidate.Package.Version).FirstOrDefault();
                if (best == null)
                {
                    _plan.SkippedApps++;
                    _log("Keeping " + old.FileName + " because the Store did not return a matching variant.");
                    foreach (WimPackage original in group) roots.Add(original);
                    continue;
                }

                resolved++;
                if (best.Package.Version <= old.Version)
                {
                    roots.Add(old);
                    foreach (WimPackage original in group.Where(package => package != old))
                        _plan.Remove.Add(original.FileName);
                    continue;
                }

                WimPackageManifest manifest = Fetch(best);
                if (manifest.Framework || manifest.Resource ||
                    !Targets(old).All(arch => manifest.Dependencies.ContainsKey(arch) || manifest.Dependencies.ContainsKey("neutral")))
                    throw new InvalidDataException("The Store update does not cover the existing app architectures: " + best.Package.FileName);

                AddToArchive(best.Package);
                roots.Add(best.Package);
                foreach (WimPackage original in group) _plan.Remove.Add(original.FileName);
            }

            if (resolved == 0)
                throw new IOException("The Store did not return a matching package for any app in this WIM. See the output log for details.");

            foreach (WimPackage app in roots)
            {
                foreach (string architecture in Targets(app))
                    ResolveDependencies(app, architecture);
            }
            foreach (WimPackage resource in _existing.Where(package => _manifests.ContainsKey(package.FileName)
                && _manifests[package.FileName].Resource))
            {
                // A separately archived resource can also keep a framework in use.
                foreach (string architecture in Targets(resource))
                    ResolveDependencies(resource, architecture);
            }

            if (!_plan.PruningSkipped)
            {
                foreach (WimPackage obsolete in _existing.Where(package => _manifests.ContainsKey(package.FileName)
                    && _manifests[package.FileName].Framework && !_required.Contains(package.FileName)))
                {
                    _log("Removing unused framework " + obsolete.FileName);
                    _plan.Remove.Add(obsolete.FileName);
                }
            }
            else
            {
                _log("Keeping existing frameworks because some package dependencies could not be fully inspected.");
            }

            return _plan;
        }

        private IEnumerable<string> Targets(WimPackage package)
        {
            foreach (string architecture in _manifests[package.FileName].Dependencies.Keys)
            {
                if (architecture == "neutral" && _architectures.Count > 0)
                {
                    foreach (string target in _architectures) yield return target;
                }
                else
                {
                    yield return architecture;
                }
            }
        }

        private void ResolveDependencies(WimPackage package, string architecture)
        {
            if (!_visited.Add(package.FileName + "|" + architecture)) return;
            WimPackageManifest manifest = _manifests[package.FileName];
            _plan.PruningSkipped |= manifest.HasOtherPackageDependencies;

            foreach (var entry in manifest.Dependencies.Where(entry => entry.Key == architecture || entry.Key == "neutral"))
            {
                foreach (WimDependency dependency in entry.Value)
                {
                    WimPackage match = FindDependency(dependency, architecture);
                    if (match == null)
                    {
                        Query(dependency.Family);
                        match = FindDependency(dependency, architecture);
                    }
                    if (match == null)
                    {
                        if (dependency.Optional) continue;
                        throw new InvalidDataException(package.FileName + " requires " + dependency.Family +
                            " " + dependency.MinimumVersion + " or newer for " + architecture +
                            ", but no matching framework was found in the WIM or Store results.");
                    }
                    _required.Add(match.FileName);
                    ResolveDependencies(match, architecture);
                }
            }
        }

        private WimPackage FindDependency(WimDependency dependency, string architecture)
        {
            Func<WimPackage, bool> matches = package => Names.Equals(package.Family, dependency.Family)
                && (package.Architecture == architecture || package.Architecture == "neutral")
                && package.Version >= dependency.MinimumVersion && !package.Bundle
                && string.IsNullOrEmpty(package.Resource);

            var candidates = _existing.Where(package => matches(package) && _manifests.ContainsKey(package.FileName)
                    && _manifests[package.FileName].Framework)
                .Select(package => new Candidate { Package = package })
                .Concat(_available.Values.Where(candidate => matches(candidate.Package)))
                .OrderByDescending(candidate => candidate.Package.Version)
                .ThenByDescending(candidate => candidate.Package.Architecture == architecture)
                .ThenBy(candidate => candidate.Download != null);

            foreach (Candidate candidate in candidates)
            {
                if (candidate.Download == null) return candidate.Package;
                if (Fetch(candidate).Framework)
                {
                    AddToArchive(candidate.Package);
                    return candidate.Package;
                }
                _log("Ignoring a non-framework candidate for " + dependency.Family);
            }
            return null;
        }

        private void Query(string family)
        {
            if (!_queriedFamilies.Add(family)) return;
            foreach (PackageInfo offer in _getPackages(family))
            {
                WimPackage package = WimPackage.Parse(offer.FileName);
                if (package == null || package.Encrypted || _available.ContainsKey(package.FileName))
                    continue;
                _available.Add(package.FileName, new Candidate { Package = package, Download = offer });
            }
        }

        private WimPackageManifest Fetch(Candidate candidate)
        {
            string file = candidate.Package.FileName;
            WimPackageManifest manifest;
            if (_manifests.TryGetValue(file, out manifest)) return manifest;
            _log("Downloading " + file);
            _download(candidate.Download, Path.Combine(_downloads, file));
            manifest = WimPackageManifest.Read(candidate.Package, _downloads, _architectures);
            _manifests.Add(file, manifest);
            return manifest;
        }

        private void AddToArchive(WimPackage package)
        {
            if (!_existing.Any(existing => Names.Equals(existing.FileName, package.FileName))
                && !_plan.Add.Contains(package.FileName, Names))
                _plan.Add.Add(package.FileName);
        }

        private void PreserveUnknown(WimPackage package, string reason)
        {
            _plan.PruningSkipped = true;
            _log("Keeping " + package.FileName + " without pruning frameworks: " + reason);
        }
    }
}
