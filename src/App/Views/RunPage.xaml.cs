using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using StartSet.App.ViewModels;
using StartSet.Infrastructure.Gui;

namespace StartSet.App.Views;

public sealed partial class RunPage : Page
{
    private readonly RunViewModel _vm;
    private readonly List<Button> _runButtons = [];

    public RunPage()
    {
        InitializeComponent();

        _vm = new RunViewModel(DispatcherQueue.GetForCurrentThread());
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.OutputLines.CollectionChanged += (_, _) => ScrollToBottom();

        BuildRunCards();
    }

    // ── Run cards ───────────────────────────────────────────────

    private void BuildRunCards()
    {
        var column = 0;
        foreach (var mode in RunModes.All)
        {
            var privileged = !RunModes.UsesTrigger(mode);
            var button = new Button
            {
                Tag = mode,
                Style = privileged ? null : (Style)Application.Current.Resources["AccentButtonStyle"],
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = privileged && !_vm.IsElevated ? "" : "", FontSize = 14 },
                        new TextBlock { Text = "Run" }
                    }
                }
            };
            if (privileged && !_vm.IsElevated)
                ToolTipService.SetToolTip(button, "Asks for administrator approval");
            button.Click += RunButton_Click;
            _runButtons.Add(button);

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = RunModes.Title(mode),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            panel.Children.Add(new TextBlock
            {
                Text = RunModes.Description(mode),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.WrapWholeWords,
                MinHeight = 34
            });
            panel.Children.Add(button);

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Child = panel
            };
            Grid.SetColumn(card, column++);
            RunCards.Children.Add(card);
        }
    }

    // ── Button Handlers ─────────────────────────────────────────

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RunMode mode })
            await _vm.RunAsync(mode);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _vm.Stop();

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _vm.Clear();

    private void DebugToggle_Changed(object sender, RoutedEventArgs e)
        => _vm.ShowDebug = DebugToggle.IsChecked ?? false;

    // ── UI State Sync ────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RunViewModel.IsRunning):
                UpdateRunningState();
                break;
            case nameof(RunViewModel.LastExitCode):
                UpdateStatusIndicator();
                UpdateResultBanner();
                break;
            case nameof(RunViewModel.FilteredLines):
                UpdateConsoleItems();
                break;
            case nameof(RunViewModel.PayloadCount):
            case nameof(RunViewModel.CurrentItemName):
                UpdateStepInfo();
                break;
        }
    }

    private void UpdateRunningState()
    {
        foreach (var button in _runButtons)
            button.IsEnabled = !_vm.IsRunning;
        StopButton.IsEnabled = _vm.IsRunning;
        RunningProgress.IsActive = _vm.IsRunning;
        RunningLabel.Visibility = _vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = _vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;

        if (_vm.IsRunning)
        {
            RunningLabel.Text = $"{RunModes.Title(_vm.CurrentMode)}...";
            ClearButton.Visibility = Visibility.Collapsed;
            StepProgress.IsIndeterminate = true;
            StepItemLabel.Text = "Starting...";
            StepCountLabel.Text = "";
            ResultBanner.IsOpen = false;
        }
        else
        {
            ClearButton.Visibility = _vm.OutputLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateStatusIndicator()
    {
        if (_vm.LastExitCode is null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StatusPanel.Visibility = Visibility.Visible;

        if (_vm.LastExitCode == 0)
        {
            StatusIcon.Glyph = "";
            StatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
            StatusText.Text = "Completed successfully";
            StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
        }
        else
        {
            StatusIcon.Glyph = "";
            StatusIcon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            StatusText.Text = $"Failed (exit code {_vm.LastExitCode})";
            StatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
        }
    }

    private void UpdateConsoleItems()
    {
        EmptyState.Visibility = _vm.OutputLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConsoleOutput.Blocks.Clear();
        foreach (var line in _vm.FilteredLines)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run { Text = line.Text });
            if (BrushForLevel(line.Level) is { } brush)
                paragraph.Foreground = brush;
            paragraph.Margin = new Thickness(0, 1, 0, 1);
            ConsoleOutput.Blocks.Add(paragraph);
        }
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
            ConsoleScroller.ChangeView(null, ConsoleScroller.ScrollableHeight, null));
    }

    private void UpdateStepInfo()
    {
        if (!string.IsNullOrEmpty(_vm.CurrentItemName))
            StepItemLabel.Text = _vm.CurrentItemName;
        if (_vm.PayloadCount > 0)
            StepCountLabel.Text = $"Payload {_vm.PayloadCount}";
    }

    private void UpdateResultBanner()
    {
        if (_vm.LastExitCode is null)
        {
            ResultBanner.IsOpen = false;
            return;
        }

        ResultBanner.IsOpen = true;
        if (_vm.LastExitCode == 0)
        {
            ResultBanner.Severity = InfoBarSeverity.Success;
            ResultBanner.Title = "Completed successfully";
            ResultBanner.Message = $"{_vm.PayloadCount} payload{(_vm.PayloadCount == 1 ? "" : "s")} run";
        }
        else
        {
            ResultBanner.Severity = InfoBarSeverity.Error;
            ResultBanner.Title = $"Failed with exit code {_vm.LastExitCode}";
            ResultBanner.Message = _vm.ErrorCount > 0
                ? $"{_vm.ErrorCount} error{(_vm.ErrorCount == 1 ? "" : "s")} during {_vm.PayloadCount} payload{(_vm.PayloadCount == 1 ? "" : "s")}"
                : "Check the console output for details";
        }
    }

    private static Brush? BrushForLevel(RunViewModel.LogLevel level) => level switch
    {
        RunViewModel.LogLevel.Error   => new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        RunViewModel.LogLevel.Warning => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        RunViewModel.LogLevel.Success => new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen),
        RunViewModel.LogLevel.Debug   => new SolidColorBrush(Windows.UI.Color.FromArgb(204, 128, 128, 128)),
        _ => null,
    };
}
