using System.Windows;

namespace DCx.NativeFixture;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FixtureInput.Focus();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        ResultText.Text = string.IsNullOrWhiteSpace(FixtureInput.Text)
            ? "Nothing applied."
            : $"Applied {FixtureInput.Text.Length} characters.";
        FixtureInput.Focus();
        FixtureInput.SelectAll();
    }
}
