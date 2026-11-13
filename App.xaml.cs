using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LastLogon;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ApplyThemeBrushes(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast))
                ApplyThemeBrushes(SystemParameters.HighContrast);
        };

        base.OnStartup(e);
    }

    // The brushes that change in a High Contrast theme, and the system brush each one becomes.
    // Their normal colours are the ones declared in App.xaml.
    private static readonly (string Key, Func<Brush> HighContrast)[] ThemeBrushes =
    {
        ("CaptionBrush", () => SystemColors.GrayTextBrush),
        ("ErrorBrush", () => SystemColors.HotTrackBrush),
        ("SubtleBorderBrush", () => SystemColors.ControlDarkBrush),
        ("PanelBrush", () => SystemColors.WindowBrush),
        ("FieldBorderBrush", () => SystemColors.ControlDarkBrush),
        ("FieldHoverBorderBrush", () => SystemColors.HighlightBrush),
        ("FieldFocusBorderBrush", () => SystemColors.HighlightBrush),

        // The row tint would hide the text on a High Contrast background. The Stale column says
        // the same thing there.
        ("StaleBrush", () => Brushes.Transparent),

        // The button keeps its face and only its border answers the pointer, so that its text
        // never lands on a colour the theme did not choose for text.
        ("ButtonBrush", () => SystemColors.ControlBrush),
        ("ButtonBorderBrush", () => SystemColors.ControlTextBrush),
        ("ButtonHoverBrush", () => SystemColors.ControlBrush),
        ("ButtonHoverBorderBrush", () => SystemColors.HighlightBrush),
        ("ButtonPressedBrush", () => SystemColors.ControlBrush),
        ("ButtonPressedBorderBrush", () => SystemColors.HighlightBrush),

        // A highlighted row is the theme's highlight with the theme's text on it.
        ("RowHoverBrush", () => SystemColors.HighlightBrush),
        ("RowSelectedBrush", () => SystemColors.HighlightBrush),
        ("RowSelectedBorderBrush", () => SystemColors.HighlightBrush),
        ("RowSelectedInactiveBrush", () => SystemColors.HighlightBrush),
        ("RowHighlightTextBrush", () => SystemColors.HighlightTextBrush),
    };

    private static Dictionary<string, object>? _normalBrushes;

    /// <summary>
    /// Sets the colours the windows pick for themselves. Normally they are the ones declared in
    /// App.xaml. In a High Contrast theme they come from the system, because the theme chooses
    /// the background and a fixed colour can fall below readable contrast on it.
    /// </summary>
    internal static void ApplyThemeBrushes(bool highContrast)
    {
        var resources = Current.Resources;
        _normalBrushes ??= ThemeBrushes.ToDictionary(item => item.Key, item => resources[item.Key]);

        foreach (var (key, system) in ThemeBrushes)
            resources[key] = highContrast ? system() : _normalBrushes[key];
    }

    /// <summary>
    /// Keeps a failure in one action from taking the whole application down. The operator is
    /// told what went wrong instead of the window disappearing.
    /// </summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "LastLogon",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
