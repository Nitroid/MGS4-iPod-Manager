using System;
using System.Diagnostics;
using System.Windows;

namespace iPodManager
{
    public partial class MainWindow
    {
        private void NitroidLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://x.com/Nitroid",
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                AppLog.Error("Could not open the external link.", exception);
                ShowError("Could not open the link. Check your browser settings and try again.");
            }

            e.Handled = true;
        }
    }
}
