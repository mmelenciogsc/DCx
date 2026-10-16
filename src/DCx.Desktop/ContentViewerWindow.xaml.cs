using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DCx.Core.Content;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class ContentViewerWindow : Window
{
    private const int MaximumDisplayedTextCharacters = 2_000_000;
    private readonly LocalContentAdmissionPolicy _contentPolicy = new();
    private readonly YouTubeUrlPolicy _youTubePolicy = new();

    public ContentViewerWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<Uri>? YouTubeNavigationRequested;

    private void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Supported files|*.txt;*.csv;*.pdf;*.docx;*.xlsx;*.pptx;*.png;*.jpg;*.jpeg;*.wav;*.mp3;*.mp4|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            FilePathBox.Text = dialog.FileName;
            OpenLocalFile(dialog.FileName);
        }
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e) => OpenLocalFile(FilePathBox.Text);

    private void OpenLocalFile(string path)
    {
        MediaViewer.Stop();
        MediaViewer.Source = null;
        ImageViewer.Source = null;
        DocumentText.Clear();
        var admission = _contentPolicy.Evaluate(path);
        if (!admission.IsAllowed || admission.CanonicalPath is null || admission.Kind is null)
        {
            SetStatus($"File blocked. {admission.Reason}");
            FilePathBox.Focus();
            return;
        }

        switch (admission.Kind)
        {
            case ContentKind.Text:
            case ContentKind.Csv:
                using (var reader = new StreamReader(admission.CanonicalPath, detectEncodingFromByteOrderMarks: true))
                {
                    var buffer = new char[MaximumDisplayedTextCharacters];
                    var read = reader.ReadBlock(buffer, 0, buffer.Length);
                    DocumentText.Text = new string(buffer, 0, read);
                    if (!reader.EndOfStream)
                    {
                        DocumentText.AppendText(Environment.NewLine + "[Display truncated at 2,000,000 characters.]");
                    }
                }

                ContentTabs.SelectedIndex = 0;
                DocumentText.Focus();
                break;
            case ContentKind.Image:
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(admission.CanonicalPath);
                bitmap.EndInit();
                bitmap.Freeze();
                ImageViewer.Source = bitmap;
                ContentTabs.SelectedIndex = 1;
                break;
            case ContentKind.Audio:
            case ContentKind.Video:
                MediaViewer.Source = new Uri(admission.CanonicalPath);
                ContentTabs.SelectedIndex = 2;
                MediaViewer.Focus();
                break;
            default:
                SetStatus($"{admission.Kind} passed admission. Its structured viewer/export remains under P07 implementation.");
                return;
        }

        SetStatus($"Opened {Path.GetFileName(admission.CanonicalPath)} as {admission.Kind}. Media is paused until Play is activated.");
    }

    private void OnListFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetFullPath(FolderPathBox.Text);
            var directory = new DirectoryInfo(path);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                SetStatus("Folder blocked because it does not exist or is a reparse point.");
                return;
            }

            var items = directory.EnumerateFileSystemInfos()
                .OrderBy(item => item is FileInfo)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(500)
                .Select(item => item is DirectoryInfo ? $"Folder: {item.Name}" : $"File: {item.Name}")
                .ToArray();
            FolderItems.ItemsSource = items;
            ContentTabs.SelectedIndex = 3;
            FolderItems.Focus();
            SetStatus($"Listed {items.Length} items from {directory.Name}. Paths outside this folder were not read.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            SetStatus("Folder could not be listed safely.");
        }
    }

    private void OnOpenYouTubeClick(object sender, RoutedEventArgs e)
    {
        var decision = _youTubePolicy.Evaluate(YouTubeUrlBox.Text);
        if (!decision.IsAllowed || decision.CanonicalUri is null)
        {
            SetStatus($"YouTube URL blocked. {decision.Reason}");
            YouTubeUrlBox.Focus();
            return;
        }

        YouTubeNavigationRequested?.Invoke(this, decision.CanonicalUri);
        SetStatus("Validated YouTube video opened in the shared DCx browser. Playback and site availability remain subject to YouTube.");
    }

    private void OnMediaPlayClick(object sender, RoutedEventArgs e)
    {
        if (MediaViewer.Source is null) return;
        MediaViewer.Play();
        SetStatus("Media playing.");
    }

    private void OnMediaPauseClick(object sender, RoutedEventArgs e)
    {
        if (MediaViewer.Source is null) return;
        MediaViewer.Pause();
        SetStatus("Media paused.");
    }

    private void OnMediaStopClick(object sender, RoutedEventArgs e)
    {
        if (MediaViewer.Source is null) return;
        MediaViewer.Stop();
        SetStatus("Media stopped.");
    }

    private void SetStatus(string message) => ContentStatusText.Text = message;
}
