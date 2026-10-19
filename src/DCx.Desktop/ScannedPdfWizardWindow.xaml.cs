using System.IO;
using System.Windows;
using DCx.Core.Documents;
using DCx.Desktop.Documents;

namespace DCx.Desktop;

public partial class ScannedPdfWizardWindow : Window
{
    private readonly string _pdfPath;
    private readonly WindowsOcrService _ocrService = new();
    private readonly AccessibleDocumentValidator _validator = new();
    private readonly AccessibleDocumentExporter _exporter = new();
    private WebView2AccessiblePdfExporter? _pdfExporter;
    private AccessibleDocument? _document;
    private DocumentBlock[] _orderedBlocks = [];

    public ScannedPdfWizardWindow(string pdfPath)
    {
        _pdfPath = Path.GetFullPath(pdfPath);
        InitializeComponent();
        PdfPathText.Text = $"Source: {Path.GetFileName(_pdfPath)}";
        RepairRoleBox.ItemsSource = Enum.GetValues<DocumentBlockRole>();
        Closed += (_, _) =>
        {
            _pdfExporter?.Dispose();
            _pdfExporter = null;
        };
    }

    private async void OnRecognizeClick(object sender, RoutedEventArgs e)
    {
        RecognizePdfButton.IsEnabled = false;
        SetStatus("Windows OCR is processing the scanned PDF.");
        try
        {
            _document = await _ocrService.RecognizePdfAsync(_pdfPath);
            var validation = _validator.Validate(_document);
            _orderedBlocks = _document.Blocks.OrderBy(block => block.PageNumber).ThenBy(block => block.ReadingOrder).ToArray();
            OcrBlocksList.ItemsSource = _orderedBlocks
                .Select(block => $"Page {block.PageNumber}, {block.Role}: {block.Text}")
                .ToArray();
            ExportHtmlButton.IsEnabled = validation.IsValid;
            ExportDocxButton.IsEnabled = validation.IsValid;
            ExportPdfButton.IsEnabled = validation.IsValid;
            SetStatus(validation.IsValid
                ? $"OCR complete. {_document.Blocks.Count} ordered blocks passed semantic and geometry validation. Review before exporting."
                : $"OCR requires correction. {string.Join(" ", validation.Errors)}");
            OcrBlocksList.Focus();
        }
        catch (Exception exception)
        {
            SetStatus($"OCR failed safely. {exception.Message}");
            RecognizePdfButton.IsEnabled = true;
        }
    }

    private void OnBlockSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var index = OcrBlocksList.SelectedIndex;
        ApplyRepairButton.IsEnabled = index >= 0 && index < _orderedBlocks.Length;
        if (!ApplyRepairButton.IsEnabled) return;
        var block = _orderedBlocks[index];
        RepairRoleBox.SelectedItem = block.Role;
        RepairLabelBox.Text = block.AccessibleLabel ?? string.Empty;
    }

    private void OnApplyRepairClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || OcrBlocksList.SelectedIndex is < 0 || RepairRoleBox.SelectedItem is not DocumentBlockRole role) return;
        var selected = _orderedBlocks[OcrBlocksList.SelectedIndex];
        var replacement = selected with
        {
            Role = role,
            AccessibleLabel = string.IsNullOrWhiteSpace(RepairLabelBox.Text) ? null : RepairLabelBox.Text.Trim(),
            IsEditable = role == DocumentBlockRole.FormField,
        };
        var blocks = _document.Blocks.Select(block => block.Id == replacement.Id ? replacement : block).ToArray();
        _document = _document with { Blocks = blocks };
        _orderedBlocks = blocks.OrderBy(block => block.PageNumber).ThenBy(block => block.ReadingOrder).ToArray();
        OcrBlocksList.ItemsSource = _orderedBlocks.Select(block => $"Page {block.PageNumber}, {block.Role}: {block.Text}").ToArray();
        var validation = _validator.Validate(_document);
        ExportHtmlButton.IsEnabled = validation.IsValid;
        ExportDocxButton.IsEnabled = validation.IsValid;
        ExportPdfButton.IsEnabled = validation.IsValid;
        SetStatus(validation.IsValid
            ? $"Semantic repair applied to {replacement.Id}. Exports remain valid."
            : $"Repair requires correction. {string.Join(" ", validation.Errors)}");
    }

    private async void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var path = GetExportPath("html");
        SetExportActionsEnabled(false);
        SetStatus("Preparing semantic HTML export. An existing export will be replaced only after the new file is complete.");
        try
        {
            await _exporter.ExportHtmlAsync(_document, path);
            SetStatus($"Semantic HTML exported atomically: {Path.GetFileName(path)}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetStatus("Semantic HTML export failed safely. An existing export was not replaced.");
        }
        finally
        {
            SetExportActionsEnabled(_validator.Validate(_document).IsValid);
        }
    }

    private async void OnExportDocxClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var path = GetExportPath("docx");
        var document = _document;
        SetExportActionsEnabled(false);
        SetStatus("Preparing structured DOCX export. An existing export will be replaced only after the new file is complete.");
        try
        {
            await Task.Run(() => _exporter.ExportDocx(document, path));
            SetStatus($"Structured DOCX exported atomically: {Path.GetFileName(path)}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetStatus("Structured DOCX export failed safely. An existing export was not replaced.");
        }
        finally
        {
            SetExportActionsEnabled(_validator.Validate(_document).IsValid);
        }
    }

    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var path = GetExportPath("pdf");
        SetExportActionsEnabled(false);
        SetStatus("Preparing a flattened accessible PDF from reviewed text and geometry. The output is not an editable form.");
        try
        {
            _pdfExporter ??= new WebView2AccessiblePdfExporter(PdfExportWebView);
            await _pdfExporter.ExportFlattenedAsync(_document, path);
            SetStatus($"Flattened accessible PDF exported atomically: {Path.GetFileName(path)}. Human assistive-technology review is still required.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.Runtime.InteropServices.COMException)
        {
            SetStatus("Flattened PDF export failed safely. An existing export was not replaced.");
        }
        finally
        {
            SetExportActionsEnabled(_validator.Validate(_document).IsValid);
        }
    }

    private string GetExportPath(string extension)
    {
        var configured = Environment.GetEnvironmentVariable("DCX_EXPORT_ROOT");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DCx Exports")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{Path.GetFileNameWithoutExtension(_pdfPath)}-accessible.{extension}");
    }

    private void SetExportActionsEnabled(bool enabled)
    {
        ExportHtmlButton.IsEnabled = enabled;
        ExportDocxButton.IsEnabled = enabled;
        ExportPdfButton.IsEnabled = enabled;
    }

    private void SetStatus(string message) => WizardStatusText.Text = message;
}
