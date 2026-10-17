using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using DCx.Core.Content;
using DCx.Core.Documents;
using DCx.Core.LocalModels;
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
    private readonly LocalModelSelectionStore _localModelSelectionStore;
    private readonly WhisperBackendSelectionStore _whisperBackendSelectionStore;
    private CancellationTokenSource? _frameDescriptionCancellation;
    private CancellationTokenSource? _webVideoCancellation;
    private CancellationTokenSource? _transcriptionCancellation;
    private string? _currentMediaPath;

    public ContentViewerWindow(
        LocalModelSelectionStore localModelSelectionStore,
        WhisperBackendSelectionStore whisperBackendSelectionStore)
    {
        _localModelSelectionStore = localModelSelectionStore
            ?? throw new ArgumentNullException(nameof(localModelSelectionStore));
        _whisperBackendSelectionStore = whisperBackendSelectionStore
            ?? throw new ArgumentNullException(nameof(whisperBackendSelectionStore));
        InitializeComponent();
        Closed += (_, _) =>
        {
            _frameDescriptionCancellation?.Cancel();
            _frameDescriptionCancellation?.Dispose();
            _frameDescriptionCancellation = null;
            _webVideoCancellation?.Cancel();
            _webVideoCancellation?.Dispose();
            _webVideoCancellation = null;
            _transcriptionCancellation?.Cancel();
        };
    }

    public event EventHandler<Uri>? YouTubeNavigationRequested;
    public Func<string?, CancellationToken, Task<WebVideoFrameDescriptionResult>>?
        WebVideoFrameQuestionRequested { get; set; }
    public Func<CancellationToken, Task>? WebVideoResumeRequested { get; set; }

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
        var previousTranscription = _transcriptionCancellation;
        _transcriptionCancellation = null;
        previousTranscription?.Cancel();
        _currentMediaPath = null;
        MediaViewer.Stop();
        MediaViewer.Source = null;
        DescribeFrameButton.IsEnabled = false;
        UpdateTranscriptionControls();
        FrameDescriptionText.Text = string.Empty;
        MediaTranscriptText.Clear();
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
                _currentMediaPath = admission.CanonicalPath;
                MediaViewer.Source = new Uri(admission.CanonicalPath);
                DescribeFrameButton.IsEnabled = admission.Kind == ContentKind.Video;
                UpdateTranscriptionControls();
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

        var mediaNotice = admission.Kind is ContentKind.Audio or ContentKind.Video
            ? IsCurrentWav()
                ? " Media is paused until Play is activated. Approved local WAV transcription is available."
                : " Media is paused until Play is activated. Direct transcription currently supports admitted WAV files only."
            : string.Empty;
        SetStatus($"Opened {Path.GetFileName(admission.CanonicalPath)} as {admission.Kind}.{mediaNotice}");
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

    private async void OnTranscribeWavClick(object sender, RoutedEventArgs e)
    {
        if (!IsCurrentWav() || _currentMediaPath is null) return;
        MediaViewer.Pause();
        _transcriptionCancellation?.Cancel();
        var operation = new CancellationTokenSource();
        _transcriptionCancellation = operation;
        UpdateTranscriptionControls();
        MediaTranscriptText.Clear();
        SetStatus("Media paused. Checking the approved local Whisper registration.");
        try
        {
            var binding = (await _whisperBackendSelectionStore.GetStateAsync(operation.Token)).Backend;
            if (binding is null)
            {
                SetStatus("No local Whisper backend is registered. Open Local models, review the exact executable, model and two licenses, then explicitly approve registration.");
                return;
            }

            SetStatus("Media paused. The approved local Whisper backend is transcribing the selected WAV. Cancel transcription remains available.");
            var transcriber = binding.CreateTranscriber(TimeSpan.FromMinutes(10));
            var result = await transcriber.TranscribeWavAsync(_currentMediaPath, cancellationToken: operation.Token);
            MediaTranscriptText.Text = result.Text;
            var workingSet = result.PeakWorkingSetBytes is > 0
                ? $" Peak working set {result.PeakWorkingSetBytes.Value / 1024d / 1024d:0.0} MiB."
                : " Peak working set was unavailable for this process duration.";
            SetStatus($"WAV transcription complete in {result.Elapsed.TotalSeconds:0.###} seconds.{workingSet} Media remains paused.");
            MediaTranscriptText.Focus();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_transcriptionCancellation, operation))
                SetStatus("WAV transcription canceled. Media remains paused and no partial transcript was retained.");
        }
        catch (TimeoutException)
        {
            if (ReferenceEquals(_transcriptionCancellation, operation))
                SetStatus("WAV transcription exceeded its approved time limit and was stopped. Media remains paused.");
        }
        catch (ArgumentException)
        {
            if (ReferenceEquals(_transcriptionCancellation, operation))
                SetStatus("WAV transcription was blocked because a selected file or approved binding is invalid. Media remains paused.");
        }
        catch
        {
            if (ReferenceEquals(_transcriptionCancellation, operation))
                SetStatus("WAV transcription failed safely without exposing process diagnostics. Review the exact approved backend files. Media remains paused.");
        }
        finally
        {
            if (ReferenceEquals(_transcriptionCancellation, operation))
            {
                _transcriptionCancellation = null;
                UpdateTranscriptionControls();
            }
            operation.Dispose();
        }
    }

    private void OnCancelTranscriptionClick(object sender, RoutedEventArgs e)
    {
        if (_transcriptionCancellation is null) return;
        _transcriptionCancellation.Cancel();
        CancelTranscriptionButton.IsEnabled = false;
        SetStatus("Canceling local WAV transcription. Media remains paused.");
    }

    private async void OnDescribeFrameClick(object sender, RoutedEventArgs e)
    {
        if (MediaViewer.Source is null) return;
        MediaViewer.Pause();
        DescribeFrameButton.IsEnabled = false;
        _frameDescriptionCancellation?.Cancel();
        _frameDescriptionCancellation?.Dispose();
        _frameDescriptionCancellation = new CancellationTokenSource();
        SetStatus("Media paused. Checking the approved local vision-model selection.");
        try
        {
            var binding = (await _localModelSelectionStore.GetStateAsync(
                _frameDescriptionCancellation.Token)).Vision;
            if (binding is null)
            {
                SetStatus("No local vision model is selected. Open Local models, review an exact digest and license, then approve a vision model.");
                return;
            }

            SetStatus("Media paused. The approved local vision model is describing the current frame.");
            var result = await _frameDescriptionService.DescribeAsync(
                MediaViewer.Source.LocalPath,
                MediaViewer.Position,
                binding.Name,
                binding.Digest,
                binding.LicenseSha256,
                FrameQuestionBox.Text,
                _frameDescriptionCancellation.Token);
            FrameDescriptionText.Text = result.Text;
            SetStatus(string.IsNullOrWhiteSpace(FrameQuestionBox.Text)
                ? $"Frame description complete. {result.PromptTokens} prompt tokens and {result.OutputTokens} output tokens; video remains paused."
                : $"Frame answer complete. {result.PromptTokens} prompt tokens and {result.OutputTokens} output tokens; video remains paused for follow-up questions.");
            FrameDescriptionText.Focus();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Frame description canceled. The local video remains paused.");
        }
        catch (ArgumentException)
        {
            SetStatus("Frame description blocked because the question or selected model binding is invalid. The local video remains paused.");
        }
        catch
        {
            SetStatus("Frame description failed safely without exposing process or model diagnostics. The local video remains paused.");
        }
        finally
        {
            DescribeFrameButton.IsEnabled = MediaViewer.Source is not null;
        }
    }

    private async void OnAskWebVideoFrameClick(object sender, RoutedEventArgs e)
    {
        if (WebVideoFrameQuestionRequested is null)
        {
            SetStatus("Web video frame questions are unavailable because no shared browser handler is connected.");
            return;
        }

        _webVideoCancellation?.Cancel();
        _webVideoCancellation?.Dispose();
        _webVideoCancellation = new CancellationTokenSource();
        AskWebVideoFrameButton.IsEnabled = false;
        ResumeWebVideoButton.IsEnabled = false;
        SetStatus("Pausing the largest accessible video in the active browser tab and capturing only its visible video rectangle for the approved local model.");
        try
        {
            var result = await WebVideoFrameQuestionRequested(
                FrameQuestionBox.Text,
                _webVideoCancellation.Token);
            FrameDescriptionText.Text = result.Vision.Text;
            ContentTabs.SelectedIndex = 2;
            ResumeWebVideoButton.IsEnabled = true;
            SetStatus($"Web video frame answer complete at {result.CurrentTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. " +
                      $"{result.Vision.PromptTokens} prompt tokens and {result.Vision.OutputTokens} output tokens. " +
                      "The web video remains paused for follow-up questions.");
            FrameDescriptionText.Focus();
        }
        catch (OperationCanceledException)
        {
            ResumeWebVideoButton.IsEnabled = true;
            SetStatus("Web video frame request canceled. The video may remain paused; use Resume web video or the page's own controls.");
        }
        catch (WebVideoFrameException exception)
        {
            ResumeWebVideoButton.IsEnabled = exception.Code is not (
                "local_vision_model_not_selected" or
                "web_video_missing" or
                "web_video_question_invalid" or
                "web_video_page_unsupported" or
                "web_video_page_changed");
            SetStatus(WebVideoFailureMessage(exception.Code));
        }
        catch
        {
            ResumeWebVideoButton.IsEnabled = true;
            SetStatus("Web video frame request failed safely without exposing page or model diagnostics. The video may remain paused.");
        }
        finally
        {
            AskWebVideoFrameButton.IsEnabled = true;
        }
    }

    private async void OnResumeWebVideoClick(object sender, RoutedEventArgs e)
    {
        if (WebVideoResumeRequested is null) return;
        ResumeWebVideoButton.IsEnabled = false;
        try
        {
            await WebVideoResumeRequested(CancellationToken.None);
            SetStatus("Web video resumed after explicit user action.");
        }
        catch (WebVideoFrameException exception)
        {
            SetStatus(WebVideoFailureMessage(exception.Code));
        }
        catch
        {
            SetStatus("Web video resume failed safely. Use the page's own accessible playback control.");
        }
    }

    private static string WebVideoFailureMessage(string code) => code switch
    {
        "local_vision_model_not_selected" =>
            "No local vision model is selected. Open Local models, review an exact digest and license, then approve a vision model.",
        "web_video_missing" =>
            "No ready, visible HTML5 video was found in the active browser page. Embedded, live, protected, or provider-controlled players may not support this feature.",
        "web_video_question_invalid" =>
            "The frame question is outside the readable 500-character limit.",
        "web_video_page_changed" =>
            "The browser page changed, so DCx discarded the frame operation. Start a new request on the current page.",
        "web_video_capture_unavailable" or "web_video_capture_invalid" =>
            "The current web video cannot be captured safely. Protected or provider-controlled playback may remain unavailable.",
        "web_video_capture_storage_failed" =>
            "The paused web frame could not be held in private temporary storage and was not sent to the model.",
        "web_video_model_binding_invalid" =>
            "The approved local vision-model binding changed or is invalid. Review and approve the exact installed model again.",
        "web_video_model_failed" =>
            "The approved local vision model could not answer this web-frame question. The video remains paused.",
        "web_video_processing_failed" =>
            "The bounded web-frame processing pipeline failed closed. The video may remain paused.",
        "web_video_resume_blocked" or "web_video_resume_unavailable" =>
            "The page did not allow programmatic resume. Use the page's own accessible playback control.",
        _ => "The web video frame operation was blocked safely. The video may remain paused.",
    };

    private bool IsCurrentWav() =>
        _currentMediaPath is not null &&
        string.Equals(Path.GetExtension(_currentMediaPath), ".wav", StringComparison.OrdinalIgnoreCase);

    private void UpdateTranscriptionControls()
    {
        TranscribeWavButton.IsEnabled = _transcriptionCancellation is null && IsCurrentWav();
        CancelTranscriptionButton.IsEnabled = _transcriptionCancellation is not null;
    }

    private void SetStatus(string message) => ContentStatusText.Text = message;
}
