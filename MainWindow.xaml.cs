using System.ComponentModel;
using System.Windows;
using WwvDecoder.ViewModels;

namespace WwvDecoder;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Auto-scroll the log to the bottom when new text arrives,
        // unless the user has scrolled up to review earlier entries.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Ensure the TextBox receives keyboard focus on click so Ctrl+C works.
        LogTextBox.PreviewMouseDown += (_, _) => LogTextBox.Focus();

        // Clean up resources when the window closes
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LogText)) return;

        // Only auto-scroll when the caret is at the end (user hasn't scrolled up)
        if (LogTextBox.CaretIndex == LogTextBox.Text.Length || LogTextBox.Text.Length == 0)
            LogTextBox.ScrollToEnd();
    }
}
