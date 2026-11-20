using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace StartSet.App.Converters;

/// <summary>The dot beside a Services row's state, from theme brushes only.</summary>
public static class ServiceStateBrush
{
    public static Brush For(string state) => (Brush)Application.Current.Resources[state switch
    {
        "Running" => "SystemFillColorSuccessBrush",
        "Ready" => "SystemFillColorAttentionBrush",
        _ => "SystemFillColorNeutralBrush"
    }];
}
