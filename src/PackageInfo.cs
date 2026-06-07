// =============================================================================
// PackageInfo.cs - Data models for Microsoft Store packages
// C# 5 compatible (no string interpolation, no auto-property initializers)
// =============================================================================

using System;
using System.Collections.Generic;

namespace MSStoreDownloader
{
    /// <summary>
    /// A single dependency declaration read from AppxManifest.xml.
    /// </summary>
    public class ManifestDependency
    {
        public string Name       { get; set; }
        public string MinVersion { get; set; }
        public string Publisher  { get; set; }

        public ManifestDependency()
        {
            Name       = "";
            MinVersion = "";
            Publisher  = "";
        }

        public override string ToString()
        {
            return Name + " >= " + MinVersion;
        }
    }

    public class PackageInfo
    {
        public string PackageIdentityName { get; set; }
        public string PublisherName       { get; set; }
        public string Version             { get; set; }
        public string Architecture        { get; set; }
        public string ContentId           { get; set; }
        public string FileName            { get; set; }
        public string PackageType         { get; set; }
        public string DownloadUrl         { get; set; }
        public long   FileSize            { get; set; }
        public string Sha256              { get; set; }
        public DateTime ExpiresOn         { get; set; }
        public bool IsDependency          { get; set; }
        public bool IsBlocked             { get; set; }

        /// <summary>
        /// Dependencies declared in AppxManifest.xml.
        /// Populated by ManifestReader after the store query completes.
        /// </summary>
        public List<ManifestDependency> ManifestDeps { get; set; }

        public PackageInfo()
        {
            PackageIdentityName = "";
            PublisherName       = "";
            Version             = "";
            Architecture        = "";
            ContentId           = "";
            FileName            = "";
            PackageType         = "";
            DownloadUrl         = "";
            Sha256              = "";
            ExpiresOn           = DateTime.MaxValue;
            ManifestDeps        = new List<ManifestDependency>();
        }

        public string FileSizeDisplay
        {
            get
            {
                if (FileSize <= 0)               return "Unknown";
                if (FileSize >= 1073741824L)     return string.Format("{0:F2} GB", FileSize / 1073741824.0);
                if (FileSize >= 1048576L)        return string.Format("{0:F1} MB", FileSize / 1048576.0);
                if (FileSize >= 1024L)           return string.Format("{0:F1} KB", FileSize / 1024.0);
                return FileSize + " B";
            }
        }

        public override string ToString()
        {
            return FileName + " (" + Architecture + ", " + Version + ")";
        }
    }

    public class StoreQueryResult
    {
        public bool              Succeeded    { get; set; }
        public string            ErrorMessage { get; set; }
        public string            ProductId    { get; set; }
        public string            ProductName  { get; set; }
        public List<PackageInfo> Packages     { get; set; }

        public StoreQueryResult()
        {
            ErrorMessage = "";
            ProductId    = "";
            ProductName  = "";
            Packages     = new List<PackageInfo>();
        }

        public List<PackageInfo> MainPackages
        {
            get { return Packages.FindAll(delegate(PackageInfo p) { return !p.IsDependency; }); }
        }

        public List<PackageInfo> Dependencies
        {
            get { return Packages.FindAll(delegate(PackageInfo p) { return p.IsDependency; }); }
        }
    }

    public class DownloadTask
    {
        public Guid        Id            { get; private set; }
        public PackageInfo Package       { get; set; }
        public string      Destination   { get; set; }
        public long        BytesReceived { get; set; }
        public long        TotalBytes    { get; set; }
        public bool        IsComplete    { get; set; }
        public bool        IsCancelled   { get; set; }
        public bool        IsFailed      { get; set; }
        public string      ErrorMessage  { get; set; }

        public DownloadTask()
        {
            Id           = Guid.NewGuid();
            Destination  = "";
            ErrorMessage = "";
        }

        public double Progress
        {
            get { return TotalBytes > 0 ? (double)BytesReceived / TotalBytes * 100.0 : 0; }
        }
    }
}
