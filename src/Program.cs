// =============================================================================
// Program.cs - Application entry point
// C# 5 compatible
// =============================================================================

using System;
using System.Windows.Forms;

namespace MSStoreDownloader
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException +=
                new System.Threading.ThreadExceptionEventHandler(OnThreadException);

            AppDomain.CurrentDomain.UnhandledException +=
                new UnhandledExceptionEventHandler(OnUnhandledException);

            Application.Run(new MainForm());
        }

        private static void OnThreadException(object sender,
            System.Threading.ThreadExceptionEventArgs e)
        {
            MessageBox.Show(
                "An unexpected error occurred:\n\n" + e.Exception.Message,
                "Unhandled Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private static void OnUnhandledException(object sender,
            UnhandledExceptionEventArgs e)
        {
            string msg = e.ExceptionObject != null
                ? e.ExceptionObject.ToString()
                : "Unknown fatal error.";
            MessageBox.Show(
                "A fatal error occurred:\n\n" + msg,
                "Fatal Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
