using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using StartSet.App.ViewModels;

namespace StartSet.App.Views;

public sealed partial class LogsPage : Page
{
    private readonly LogsViewModel _vm = new();

    public LogsPage()
    {
        InitializeComponent();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Refresh();

        LogFileList.ItemsSource = _vm.LogFiles;
        LogFileList.SelectedItem = _vm.SelectedLog;
        UpdateLogContent();
    }

    // ── Event Handlers ───────────────────────────────────────────

    private void LogFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogFileList.SelectedItem is LogsViewModel.LogFile file)
            _vm.SelectedLog = file;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        => _vm.FilterText = FilterBox.Text;

    private void OpenEditor_Click(object sender, RoutedEventArgs e)
        => _vm.OpenInEditorCommand.Execute(null);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
        => _vm.OpenFolderCommand.Execute(null);

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _vm.Refresh();
        LogFileList.ItemsSource = null;
        LogFileList.ItemsSource = _vm.LogFiles;
        LogFileList.SelectedItem = _vm.SelectedLog;
    }

    // ── UI State ─────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.FilteredLines))
            UpdateLogContent();
    }

    private void UpdateLogContent()
    {
        EmptyState.Visibility = _vm.SelectedLog is null ? Visibility.Visible : Visibility.Collapsed;
        OpenEditorBtn.IsEnabled = _vm.SelectedLog is not null;

        LogOutput.Blocks.Clear();
        foreach (var line in _vm.FilteredLines)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run { Text = line.Text });
            paragraph.Foreground = BrushForColor(line.Color);
            paragraph.Margin = new Thickness(0, 1, 0, 1);
            LogOutput.Blocks.Add(paragraph);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static SolidColorBrush BrushForColor(LogsViewModel.LogLineColor color) => color switch
    {
        LogsViewModel.LogLineColor.Error   => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        LogsViewModel.LogLineColor.Warning => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        LogsViewModel.LogLineColor.Success => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
        LogsViewModel.LogLineColor.Debug   => new SolidColorBrush(Windows.UI.Color.FromArgb(204, 128, 128, 128)),
        LogsViewModel.LogLineColor.Header  => new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue),
        _ => (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"],
    };
}
