// =============================================================================
// PackageGridRow.cs - View-model bridging PackageInfo and DataGridView
// C# 5 compatible
// =============================================================================

using System;

namespace MSStoreDownloader
{
    public class PackageGridRow
    {
        public PackageInfo Package { get; private set; }

        public string FileName     { get { return Package.FileName;     } }
        public string PackageType  { get { return Package.PackageType;  } }
        public string Version      { get { return Package.Version;      } }
        public string Architecture { get { return Package.Architecture; } }
        public string Size         { get { return Package.FileSizeDisplay; } }
        public string DownloadUrl  { get { return Package.DownloadUrl;  } }
        public string Kind         { get { return Package.IsDependency ? "Dependency" : "Main"; } }

        public Guid?  DownloadTaskId { get; set; }
        public string DownloadStatus { get; set; }
        public double ProgressPct    { get; set; }
        public string SpeedDisplay   { get; set; }

        public PackageGridRow(PackageInfo package)
        {
            if (package == null) throw new ArgumentNullException("package");
            Package        = package;
            DownloadStatus = "Ready";
            SpeedDisplay   = "";
        }
    }
}
