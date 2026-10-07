using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StartSet.App.ViewModels;
using StartSet.Infrastructure.Configuration;

namespace StartSet.App.Views;

/// <summary>
/// The Prefs tab. The cards are built from the setting list rather than written out in XAML,
/// so a setting added to StartSetPreferences appears here without anyone remembering to.
/// </summary>
public sealed partial class PrefsPage : Page
{
    public PrefsViewModel ViewModel { get; } = new();

    public PrefsPage()
    {
        InitializeComponent();

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "StartSet.png");
        if (System.IO.File.Exists(iconPath))
            AppIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        ViewModel.SaveReported += (message, failed) => DispatcherQueue.TryEnqueue(() =>
        {
            SaveStatus.Text = message;
            SaveStatus.Foreground = failed
                ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            SaveStatus.Visibility = Visibility.Visible;
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
        BuildCards();
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        // Once the elevated copy has started (UAC accepted), this read-only instance closes.
        // A cancelled UAC prompt returns false and the tab simply stays read-only.
        if (ViewModel.TryRelaunchElevated())
            Application.Current.Exit();
    }

    // ── Card construction ───────────────────────────────────────

    private void BuildCards()
    {
        Cards.Children.Clear();
        foreach (var group in SettingDescriptions.Groups)
        {
            var items = ViewModel.Items.Where(i => i.Description.Group == group).ToList();
            if (items.Count > 0)
                Cards.Children.Add(Card(group, items));
        }
    }

    private Border Card(string title, List<SettingItem> items)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 2),
            Children =
            {
                new FontIcon
                {
                    Glyph = GlyphFor(title),
                    FontSize = 20,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = title,
                    Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });

        foreach (var item in items)
            panel.Children.Add(Row(item));

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            Child = panel
        };
    }

    private Grid Row(SettingItem item)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var label = item.Description.Unit is { } unit ? $"{item.Description.Label} ({unit})" : item.Description.Label;
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.WrapWholeWords });
        if (!string.IsNullOrEmpty(item.Description.Help))
        {
            text.Children.Add(new TextBlock
            {
                Text = item.Description.Help,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }
        text.Children.Add(SourceCaption(item));
        grid.Children.Add(text);

        var control = Editor(item);
        control.IsEnabled = item.CanEdit;
        control.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(control, item.IsManaged
            ? $"Set by policy (HKLM\\SOFTWARE\\Policies\\StartSet\\{item.Name})"
            : item.Name);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>The value's source, with a lock when policy sets it.</summary>
    private static StackPanel SourceCaption(SettingItem item)
    {
        var secondary = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        if (item.IsManaged)
        {
            caption.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        caption.Children.Add(new TextBlock
        {
            Text = item.SourceText,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = item.IsManaged ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : secondary
        });
        return caption;
    }

    private Control Editor(SettingItem item)
    {
        switch (item.Kind)
        {
            case SettingKind.Toggle:
            {
                var toggle = new ToggleSwitch { IsOn = item.BoolValue, OnContent = "On", OffContent = "Off", HorizontalAlignment = HorizontalAlignment.Right };
                toggle.Toggled += (_, _) => ViewModel.QueueSave(item, toggle.IsOn);
                return toggle;
            }
            case SettingKind.Number:
            {
                var number = new NumberBox
                {
                    Value = item.NumberValue,
                    Minimum = 0,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    PlaceholderText = item.Description.Unit ?? string.Empty
                };
                number.ValueChanged += (_, args) => ViewModel.QueueSave(item, args.NewValue);
                return number;
            }
            case SettingKind.Choice:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var level in PrefsViewModel.LogLevels)
                    combo.Items.Add(level.Length == 0 ? "(not set)" : level);
                var index = Array.FindIndex(PrefsViewModel.LogLevels, l => string.Equals(l, item.TextValue, StringComparison.OrdinalIgnoreCase));
                combo.SelectedIndex = Math.Max(0, index);
                combo.SelectionChanged += (_, _) => ViewModel.QueueSave(item, PrefsViewModel.LogLevels[Math.Max(0, combo.SelectedIndex)]);
                return combo;
            }
            case SettingKind.List:
            {
                var box = new TextBox
                {
                    Text = item.TextValue,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 64,
                    MaxHeight = 140,
                    PlaceholderText = "None"
                };
                ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                box.TextChanged += (_, _) => ViewModel.QueueSave(item, box.Text);
                return box;
            }
            default:
            {
                var box = new TextBox { Text = item.TextValue };
                box.TextChanged += (_, _) => ViewModel.QueueSave(item, box.Text);
                return box;
            }
        }
    }

    private static string GlyphFor(string group) => group switch
    {
        SettingDescriptions.Network => "",
        SettingDescriptions.Login => "",
        SettingDescriptions.Logging => "",
        _ => ""
    };
}
