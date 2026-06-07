// =============================================================================
// Logger.cs - Thread-safe logging with UI integration
// C# 5 compatible (no string interpolation)
// =============================================================================

using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MSStoreDownloader
{
    public enum LogLevel { Info, Success, Warning, Error, Debug }

    public class Logger
    {
        private readonly RichTextBox _outputBox;
        private readonly object      _lock = new object();
        private StreamWriter         _fileWriter;
        private bool                 _fileLoggingEnabled;

        /// <summary>When false, Debug-level messages are silently discarded.</summary>
        public bool DebugEnabled { get; set; }

        private static readonly System.Drawing.Color ColorInfo    = System.Drawing.Color.FromArgb(220, 220, 220);
        private static readonly System.Drawing.Color ColorSuccess = System.Drawing.Color.FromArgb( 80, 200, 120);
        private static readonly System.Drawing.Color ColorWarning = System.Drawing.Color.FromArgb(255, 200,  60);
        private static readonly System.Drawing.Color ColorError   = System.Drawing.Color.FromArgb(255,  80,  80);
        private static readonly System.Drawing.Color ColorDebug   = System.Drawing.Color.FromArgb(150, 150, 255);
        private static readonly System.Drawing.Color ColorTime    = System.Drawing.Color.FromArgb(120, 120, 120);

        public Logger(RichTextBox outputBox)
        {
            if (outputBox == null) throw new ArgumentNullException("outputBox");
            _outputBox = outputBox;
        }

        public void EnableFileLogging(string path)
        {
            try
            {
                _fileWriter = new StreamWriter(path, true, Encoding.UTF8);
                _fileWriter.AutoFlush = true;
                _fileLoggingEnabled = true;
                Log(LogLevel.Info, "File logging started: " + path);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, "Could not enable file logging: " + ex.Message);
            }
        }

        public void DisableFileLogging()
        {
            _fileLoggingEnabled = false;
            if (_fileWriter != null) { _fileWriter.Close(); _fileWriter = null; }
        }

        public void Info(string message)    { Log(LogLevel.Info,    message); }
        public void Success(string message) { Log(LogLevel.Success, message); }
        public void Warning(string message) { Log(LogLevel.Warning, message); }
        public void Error(string message)   { Log(LogLevel.Error,   message); }
        public void Debug(string message)   { Log(LogLevel.Debug,   message); }
        public void Error(string message, Exception ex) { Log(LogLevel.Error, message + ": " + ex.Message); }

        public void Log(LogLevel level, string message)
        {
            if (level == LogLevel.Debug && !DebugEnabled) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string prefix    = GetPrefix(level);

            if (_fileLoggingEnabled && _fileWriter != null)
            {
                try { _fileWriter.WriteLine("[" + timestamp + "] " + prefix + " " + message); }
                catch { }
            }

            if (_outputBox.InvokeRequired)
                _outputBox.BeginInvoke(new Action<string, string, LogLevel, string>(AppendToBox),
                                       timestamp, prefix, level, message);
            else
                AppendToBox(timestamp, prefix, level, message);
        }

        private void AppendToBox(string timestamp, string prefix, LogLevel level, string message)
        {
            lock (_lock)
            {
                try
                {
                    _outputBox.SuspendLayout();
                    AppendText("[" + timestamp + "] ", ColorTime);
                    AppendText(prefix + " ", GetColor(level));
                    AppendText(message + Environment.NewLine, ColorInfo);
                    _outputBox.SelectionStart = _outputBox.TextLength;
                    _outputBox.ScrollToCaret();
                    TrimBuffer();
                }
                finally { _outputBox.ResumeLayout(); }
            }
        }

        private void AppendText(string text, System.Drawing.Color color)
        {
            _outputBox.SelectionStart  = _outputBox.TextLength;
            _outputBox.SelectionLength = 0;
            _outputBox.SelectionColor  = color;
            _outputBox.AppendText(text);
        }

        private void TrimBuffer()
        {
            if (_outputBox.Lines.Length > 2200)
            {
                int removeChars = 0;
                for (int i = 0; i < 200 && i < _outputBox.Lines.Length; i++)
                    removeChars += _outputBox.Lines[i].Length + 1;
                _outputBox.SelectionStart  = 0;
                _outputBox.SelectionLength = removeChars;
                _outputBox.SelectedText    = string.Empty;
            }
        }

        private static string GetPrefix(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Info:    return "[INFO]   ";
                case LogLevel.Success: return "[OK]     ";
                case LogLevel.Warning: return "[WARN]   ";
                case LogLevel.Error:   return "[ERROR]  ";
                case LogLevel.Debug:   return "[DEBUG]  ";
                default:               return "[LOG]    ";
            }
        }

        private static System.Drawing.Color GetColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Info:    return ColorInfo;
                case LogLevel.Success: return ColorSuccess;
                case LogLevel.Warning: return ColorWarning;
                case LogLevel.Error:   return ColorError;
                case LogLevel.Debug:   return ColorDebug;
                default:               return ColorInfo;
            }
        }

        public void Clear()
        {
            if (_outputBox.InvokeRequired)
                _outputBox.BeginInvoke(new Action(_outputBox.Clear));
            else
                _outputBox.Clear();
        }
    }
}
