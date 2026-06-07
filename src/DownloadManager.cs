// =============================================================================
// DownloadManager.cs - File downloads with progress and cancellation
// C# 5 compatible (no string interpolation, no expression-bodied members)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace MSStoreDownloader
{
    public class DownloadProgressEventArgs : EventArgs
    {
        public Guid   TaskId        { get; private set; }
        public string FileName      { get; private set; }
        public long   BytesReceived { get; private set; }
        public long   TotalBytes    { get; private set; }
        public string SpeedDisplay  { get; private set; }

        public double ProgressPct
        {
            get { return TotalBytes > 0 ? (double)BytesReceived / TotalBytes * 100.0 : 0; }
        }

        public DownloadProgressEventArgs(Guid taskId, string fileName,
            long bytesReceived, long totalBytes, string speedDisplay)
        {
            TaskId        = taskId;
            FileName      = fileName;
            BytesReceived = bytesReceived;
            TotalBytes    = totalBytes;
            SpeedDisplay  = speedDisplay;
        }
    }

    public class DownloadCompletedEventArgs : EventArgs
    {
        public Guid   TaskId       { get; private set; }
        public string FileName     { get; private set; }
        public bool   Succeeded    { get; private set; }
        public bool   Cancelled    { get; private set; }
        public string ErrorMessage { get; private set; }

        public DownloadCompletedEventArgs(Guid taskId, string fileName,
            bool succeeded, bool cancelled, string errorMessage)
        {
            TaskId       = taskId;
            FileName     = fileName;
            Succeeded    = succeeded;
            Cancelled    = cancelled;
            ErrorMessage = errorMessage;
        }
    }

    public class DownloadManager
    {
        public event EventHandler<DownloadProgressEventArgs>  DownloadProgress;
        public event EventHandler<DownloadCompletedEventArgs> DownloadCompleted;

        private readonly Dictionary<Guid, CancellationTokenSource> _activeTasks =
            new Dictionary<Guid, CancellationTokenSource>();
        private readonly object _lock = new object();
        private readonly Logger _logger;

        private const int BufferSize            = 81920;
        private const int ProgressIntervalMs    = 250;
        private const int MaxConcurrentDownloads = 4;

        private int _activeDownloadCount;

        public int ActiveDownloadCount { get { return _activeDownloadCount; } }

        public DownloadManager(Logger logger)
        {
            if (logger == null) throw new ArgumentNullException("logger");
            _logger = logger;
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.DefaultConnectionLimit = MaxConcurrentDownloads + 2;
        }

        public Guid StartDownload(PackageInfo package, string destinationPath)
        {
            if (package == null)
                throw new ArgumentNullException("package");
            if (string.IsNullOrEmpty(destinationPath))
                throw new ArgumentException("Destination path must be provided.", "destinationPath");

            var cts    = new CancellationTokenSource();
            var taskId = Guid.NewGuid();

            lock (_lock) _activeTasks[taskId] = cts;

            _logger.Info("Starting download: " + package.FileName);
            _logger.Debug("  -> " + destinationPath);

            Interlocked.Increment(ref _activeDownloadCount);

            var capturedTaskId = taskId;
            var capturedCts    = cts;
            var capturedPkg    = package;
            var capturedDest   = destinationPath;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                DownloadFile(capturedTaskId, capturedPkg, capturedDest, capturedCts.Token);
                Interlocked.Decrement(ref _activeDownloadCount);
                lock (_lock) _activeTasks.Remove(capturedTaskId);
                capturedCts.Dispose();
            });

            return taskId;
        }

        public void CancelDownload(Guid taskId)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (!_activeTasks.TryGetValue(taskId, out cts)) return;
            }
            _logger.Info("Cancelling download " + taskId);
            cts.Cancel();
        }

        public void CancelAll()
        {
            List<CancellationTokenSource> list;
            lock (_lock)
            {
                list = new List<CancellationTokenSource>(_activeTasks.Values);
            }
            _logger.Warning("Cancelling " + list.Count + " active download(s).");
            foreach (var cts in list)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        // ── Core download ──────────────────────────────────────────────────────

        private void DownloadFile(Guid taskId, PackageInfo package,
                                   string destinationPath, CancellationToken ct)
        {
            string tempPath = destinationPath + ".tmp";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(package.DownloadUrl);
                request.Method            = "GET";
                request.Timeout           = 30000;
                request.ReadWriteTimeout  = 60000;
                request.UserAgent         = "Microsoft-Delivery-Optimization/10.0";
                request.AllowAutoRedirect = true;

                ct.Register(delegate { try { request.Abort(); } catch { } });

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    long totalBytes = response.ContentLength;
                    _logger.Debug("  Content-Length: " + FormatBytes(totalBytes));

                    using (var responseStream = response.GetResponseStream())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create,
                               FileAccess.Write, FileShare.None, BufferSize,
                               FileOptions.WriteThrough))
                    {
                        byte[] buffer        = new byte[BufferSize];
                        long   bytesReceived = 0;
                        int    bytesRead;
                        long   lastReportTime    = Environment.TickCount;
                        long   lastBytes         = 0;
                        double speedBytesPerSec  = 0;

                        while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            fileStream.Write(buffer, 0, bytesRead);
                            bytesReceived += bytesRead;

                            long now     = Environment.TickCount;
                            long elapsed = now - lastReportTime;

                            if (elapsed >= ProgressIntervalMs)
                            {
                                double instantSpeed =
                                    (bytesReceived - lastBytes) * 1000.0 / Math.Max(1, elapsed);
                                speedBytesPerSec = speedBytesPerSec == 0
                                    ? instantSpeed
                                    : 0.7 * speedBytesPerSec + 0.3 * instantSpeed;

                                lastReportTime = now;
                                lastBytes      = bytesReceived;

                                OnProgress(taskId, package.FileName,
                                           bytesReceived, totalBytes,
                                           FormatSpeed(speedBytesPerSec));
                            }
                        }

                        OnProgress(taskId, package.FileName,
                                   bytesReceived, Math.Max(totalBytes, bytesReceived), "0 B/s");
                    }
                }

                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(tempPath, destinationPath);

                _logger.Success("Downloaded: " + package.FileName);
                OnCompleted(taskId, package.FileName, true, false, null);
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("Download cancelled: " + package.FileName);
                TryDeleteTemp(tempPath);
                OnCompleted(taskId, package.FileName, false, true, "Cancelled.");
            }
            catch (WebException wex)
            {
                if (wex.Status == WebExceptionStatus.RequestCanceled)
                {
                    _logger.Warning("Download aborted: " + package.FileName);
                    TryDeleteTemp(tempPath);
                    OnCompleted(taskId, package.FileName, false, true, "Cancelled.");
                }
                else
                {
                    string msg = "Download failed: " + wex.Message;
                    _logger.Error(msg);
                    TryDeleteTemp(tempPath);
                    OnCompleted(taskId, package.FileName, false, false, msg);
                }
            }
            catch (Exception ex)
            {
                string msg = "Download failed: " + ex.Message;
                _logger.Error(msg);
                TryDeleteTemp(tempPath);
                OnCompleted(taskId, package.FileName, false, false, msg);
            }
        }

        private void OnProgress(Guid taskId, string fileName,
                                  long bytesReceived, long totalBytes, string speed)
        {
            var h = DownloadProgress;
            if (h != null)
                h(this, new DownloadProgressEventArgs(taskId, fileName, bytesReceived, totalBytes, speed));
        }

        private void OnCompleted(Guid taskId, string fileName,
                                   bool succeeded, bool cancelled, string error)
        {
            var h = DownloadCompleted;
            if (h != null)
                h(this, new DownloadCompletedEventArgs(taskId, fileName,
                          succeeded, cancelled, error ?? ""));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 0)           return "Unknown";
            if (bytes >= 1073741824L) return string.Format("{0:F2} GB", bytes / 1073741824.0);
            if (bytes >= 1048576L)    return string.Format("{0:F1} MB", bytes / 1048576.0);
            if (bytes >= 1024L)       return string.Format("{0:F1} KB", bytes / 1024.0);
            return bytes + " B";
        }

        private static string FormatSpeed(double bps)
        {
            if (bps >= 1073741824.0) return string.Format("{0:F2} GB/s", bps / 1073741824.0);
            if (bps >= 1048576.0)    return string.Format("{0:F1} MB/s", bps / 1048576.0);
            if (bps >= 1024.0)       return string.Format("{0:F1} KB/s", bps / 1024.0);
            return string.Format("{0:F0} B/s", bps);
        }

        private static void TryDeleteTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
