using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using DCx.Core.Content;
using DCx.Core.Documents;
using DCx.Desktop.Media;
using Microsoft.Win32;

namespace DCx.Desktop;

public partial class ContentViewerWindow : Window
{
    private const int MaximumDisplayedTextCharacters = 2_000_000;
    private readonly LocalContentAdmissionPolicy _contentPolicy = new();
    private readonly YouTubeUrlPolicy _youTubePolicy = new();
    private readonly OfficeTextExtractor _officeTextExtractor = new();
    private readonly MediaFrameDescriptionService _frameDescriptionService = new();
    private CancellationTokenSource? _frameDescriptionCancellation;

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
        DescribeFrameButton.IsEnabled = false;
        FrameDescriptionText.Text = string.Empty;
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
                DescribeFrameButton.IsEnabled = admission.Kind == ContentKind.Video;
                ContentTabs.SelectedIndex = 2;
                MediaViewer.Focus();
                break;
            case ContentKind.Word:
            case ContentKind.Spreadsheet:
            case ContentKind.Presentation:
                DocumentText.Text = string.Join(Environment.NewLine, _officeTextExtractor.Extract(admission.CanonicalPath, admission.Kind.Value));
                ContentTabs.SelectedIndex = 0;
                DocumentText.Focus();
                break;
            case ContentKind.Pdf:
                var wizard = new ScannedPdfWizardWindow(admission.CanonicalPath) { Owner = this };
                wizard.Show();
                SetStatus("PDF admitted. The accessibility wizard opened; no editable export exists until OCR and geometry validation pass.");
                return;
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

    private async void OnDescribeFrameClick(object sender, RoutedEventArgs e)
    {
        if (MediaViewer.Source is null) return;
        var model = Environment.GetEnvironmentVariable("DCX_VISION_MODEL");
        var modelDigest = Environment.GetEnvironmentVariable("DCX_VISION_MODEL_SHA256");
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(modelDigest))
        {
            SetStatus("No digest-pinned local vision model is configured. Complete P06 model selection first.");
            return;
        }

        MediaViewer.Pause();
        DescribeFrameButton.IsEnabled = false;
        _frameDescriptionCancellation?.Cancel();
        _frameDescriptionCancellation?.Dispose();
        _frameDescriptionCancellation = new CancellationTokenSource();
        SetStatus("Media paused. Local vision model is describing the current frame.");
        try
        {
            var result = await _frameDescriptionService.DescribeAsync(
                MediaViewer.Source.LocalPath,
                MediaViewer.Position,
                model,
                modelDigest,
                _frameDescriptionCancellation.Token);
            FrameDescriptionText.Text = result.Text;
            SetStatus($"Frame description complete. {result.PromptTokens} prompt tokens and {result.OutputTokens} output tokens; video remains paused.");
            FrameDescriptionText.Focus();
        }
        catch (Exception exception)
        {
            SetStatus($"Frame description failed safely. {exception.Message}");
        }
        finally
        {
            DescribeFrameButton.IsEnabled = MediaViewer.Source is not null;
        }
    }

    private void SetStatus(string message) => ContentStatusText.Text = message;
}
