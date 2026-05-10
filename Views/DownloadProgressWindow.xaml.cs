using System.Windows;
using VmbLauncher.Services;

namespace VmbLauncher.Views;

public partial class DownloadProgressWindow : Window
{
    public event EventHandler? Cancelled;

    public DownloadProgressWindow(string title)
    {
        InitializeComponent();
        TbTitle.Text = title;
    }

    public void Update(DownloadProgress p)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Update(p)); return; }
        TbStatus.Text = p.Message;
        if (p.Fraction is double f)
        {
            PbProgress.IsIndeterminate = false;
            PbProgress.Value = Math.Clamp(f * 100.0, 0.0, 100.0);
        }
        else
        {
            PbProgress.IsIndeterminate = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        BtnCancel.IsEnabled = false;
        TbStatus.Text = "Cancelling...";
        Cancelled?.Invoke(this, EventArgs.Empty);
    }
}
