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
    private AccessibleDocument? _document;
    private DocumentBlock[] _orderedBlocks = [];

    public ScannedPdfWizardWindow(string pdfPath)
    {
        _pdfPath = Path.GetFullPath(pdfPath);
        InitializeComponent();
        PdfPathText.Text = $"Source: {Path.GetFileName(_pdfPath)}";
        RepairRoleBox.ItemsSource = Enum.GetValues<DocumentBlockRole>();
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
        SetStatus(validation.IsValid
            ? $"Semantic repair applied to {replacement.Id}. Exports remain valid."
            : $"Repair requires correction. {string.Join(" ", validation.Errors)}");
    }

    private async void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var path = GetExportPath("html");
        await _exporter.ExportHtmlAsync(_document, path);
        SetStatus($"Semantic HTML exported: {Path.GetFileName(path)}.");
    }

    private void OnExportDocxClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var path = GetExportPath("docx");
        _exporter.ExportDocx(_document, path);
        SetStatus($"Structured DOCX exported: {Path.GetFileName(path)}.");
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

    private void SetStatus(string message) => WizardStatusText.Text = message;
}
