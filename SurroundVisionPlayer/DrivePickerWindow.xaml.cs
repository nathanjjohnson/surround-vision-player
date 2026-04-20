using System.IO;
using System.Windows;
using System.Windows.Media;
using SurroundVisionPlayer.Logic;

namespace SurroundVisionPlayer;

public partial class DrivePickerWindow : Window
{
    /// <summary>The located SVR folder; valid only when DialogResult == true.</summary>
    public string? SelectedFolder { get; private set; }

    /// <summary>Optional initial path to pre-populate the dialog.</summary>
    public string? InitialPath { get; init; }

    public DrivePickerWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(InitialPath))
            Validate(InitialPath);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // Use the modern WPF OpenFolderDialog (requires .NET 8+)
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Thumb Drive Root Folder",
            InitialDirectory = InitialPath ?? Environment.GetFolderPath(
                Environment.SpecialFolder.Desktop)
        };

        if (dlg.ShowDialog(this) == true)
            Validate(dlg.FolderName);
    }

    private void Validate(string root)
    {
        PathLabel.Text = root;
        PathLabel.Foreground = Brushes.LightGray;

        var svr = RecordingScanner.FindSvrFolder(root);
        if (svr is not null)
        {
            var recordings = RecordingScanner.Scan(svr);
            var sessions   = SessionGrouper.Group([.. recordings.Keys]);
            StatusLabel.Text =
                $"✔  Found {recordings.Count} clips in {sessions.Count} trip(s).\n" +
                $"   …\\{RecordingScanner.SvrSubPath}";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            OkButton.IsEnabled = true;
            SelectedFolder = svr;
        }
        else
        {
            StatusLabel.Text =
                "✘  No dashcam recordings found.\n" +
                @"   Make sure you selected the drive root (e.g. D:\).";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0x45, 0x60));
            OkButton.IsEnabled = false;
            SelectedFolder = null;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not null)
            DialogResult = true;
    }
}
