using GitHubTray_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace GitHubTray_App.Controls;

/// <summary>
/// Resolves semantic brushes by key so presentation follows the system's light, dark
/// and high contrast themes instead of a hardcoded palette. A missing key must never
/// crash a card, so lookups fall back to a transparent brush.
/// </summary>
internal static class ThemeBrushes
{
    public static Brush Get(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>Status color for a check outcome, shared by the rollup chip and detail rows.</summary>
    public static Brush Tone(StatusTone tone) => Get(ToneKey(tone, "TextFillColorSecondaryBrush"));

    /// <summary>
    /// Fill for one run of the check bar. Neutral and skipped outcomes take the track
    /// color rather than a status color, so a completion never reads as a success.
    /// </summary>
    public static Brush Segment(StatusTone tone) => Get(ToneKey(tone, TrackKey));

    public const string TrackKey = "ControlStrongFillColorDisabledBrush";

    private static string ToneKey(StatusTone tone, string neutral) => tone switch
    {
        StatusTone.Success => "SystemFillColorSuccessBrush",
        StatusTone.Failure => "SystemFillColorCriticalBrush",
        StatusTone.Caution => "SystemFillColorCautionBrush",
        StatusTone.Progress => "SystemFillColorAttentionBrush",
        _ => neutral
    };
}
