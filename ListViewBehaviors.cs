using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace LastLogon;

/// <summary>
/// Marks a list row whose last activity is older than the stale threshold. The row is tinted and
/// the "Days ago" cell says "(stale)", so the mark does not depend on seeing a colour.
/// </summary>
public static class Stale
{
    public static readonly DependencyProperty IsStaleProperty =
        DependencyProperty.RegisterAttached("IsStale", typeof(bool), typeof(Stale), new PropertyMetadata(false));

    public static bool GetIsStale(DependencyObject element) => (bool)element.GetValue(IsStaleProperty);

    public static void SetIsStale(DependencyObject element, bool value) => element.SetValue(IsStaleProperty, value);
}

/// <summary>
/// Lets the last column of a GridView take the width the other columns leave. A GridView cannot
/// size a column by "the rest", so without this the last column is a fixed width: long text is cut
/// off beside unused space, and the table does not grow with the window.
/// </summary>
public static class FillColumn
{
    /// <summary>The leftover width the last column keeps even in a narrow list.</summary>
    private const double Slack = 8;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(FillColumn), new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty MinWidthProperty =
        DependencyProperty.RegisterAttached("MinWidth", typeof(double), typeof(FillColumn), new PropertyMetadata(120.0));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetMinWidth(DependencyObject element) => (double)element.GetValue(MinWidthProperty);

    public static void SetMinWidth(DependencyObject element, double value) => element.SetValue(MinWidthProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list) return;

        list.Loaded -= OnLoaded;
        list.SizeChanged -= OnSizeChanged;
        list.RemoveHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
        if (!(bool)e.NewValue) return;

        list.Loaded += OnLoaded;
        list.SizeChanged += OnSizeChanged;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Fit((ListView)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Fit((ListView)sender);

    // A vertical scroll bar appearing or disappearing changes the room the columns have.
    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) Fit((ListView)sender);
    }

    private static void Fit(ListView list)
    {
        if (list.View is not GridView grid || grid.Columns.Count < 2) return;
        if (FindViewer(list) is not { ViewportWidth: > 0 } viewer) return;

        var others = 0.0;
        for (var i = 0; i < grid.Columns.Count - 1; i++)
        {
            var column = grid.Columns[i];
            others += double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
        }

        var last = grid.Columns[^1];
        var width = Math.Max(GetMinWidth(list), Math.Floor(viewer.ViewportWidth - others - Slack));
        if (double.IsNaN(last.Width) || Math.Abs(last.Width - width) >= 1) last.Width = width;
    }

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindViewer(child) is { } nested) return nested;
        }

        return null;
    }
}

/// <summary>
/// Click-to-sort for the columns of a ListView that name a property in <see cref="SortByProperty"/>.
/// A second click on the same column reverses the order, and the sorted column shows an arrow.
/// </summary>
public static class Sorting
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(Sorting), new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SortByProperty =
        DependencyProperty.RegisterAttached("SortBy", typeof(string), typeof(Sorting));

    // The header text of a column without its arrow, remembered the first time it is seen.
    private static readonly ConditionalWeakTable<GridViewColumn, string> Titles = new();

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static string? GetSortBy(DependencyObject element) => (string?)element.GetValue(SortByProperty);

    public static void SetSortBy(DependencyObject element, string? value) => element.SetValue(SortByProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list) return;

        list.RemoveHandler(GridViewColumnHeader.ClickEvent, (RoutedEventHandler)OnHeaderClick);
        if ((bool)e.NewValue) list.AddHandler(GridViewColumnHeader.ClickEvent, (RoutedEventHandler)OnHeaderClick);
    }

    private static void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column, Role: GridViewColumnHeaderRole.Normal }) return;
        if (GetSortBy(column) is not { Length: > 0 } property) return;

        var list = (ListView)sender;
        if (list.ItemsSource is null) return;

        var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
        var direction = ListSortDirection.Ascending;
        if (view.SortDescriptions.Count > 0
            && view.SortDescriptions[0].PropertyName == property
            && view.SortDescriptions[0].Direction == ListSortDirection.Ascending)
        {
            direction = ListSortDirection.Descending;
        }

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(property, direction));
        }

        ShowArrow(list, column, direction);
    }

    /// <summary>Returns the list to the order its rows arrive in and removes the arrow.</summary>
    public static void Reset(ListView list)
    {
        if (list.ItemsSource is not null)
            CollectionViewSource.GetDefaultView(list.ItemsSource).SortDescriptions.Clear();

        ShowArrow(list, null, ListSortDirection.Ascending);
    }

    private static void ShowArrow(ListView list, GridViewColumn? sorted, ListSortDirection direction)
    {
        if (list.View is not GridView grid) return;

        foreach (var column in grid.Columns)
        {
            if (GetSortBy(column) is null) continue;

            var title = Titles.GetValue(column, c => c.Header as string ?? "");
            column.Header = ReferenceEquals(column, sorted) ? WithArrow(title, direction) : title;
        }
    }

    /// <summary>
    /// The heading with its arrow: a small grey triangle after the text. A triangle set in the font
    /// of the text is as tall as a capital letter and heavier than the heading it marks.
    /// </summary>
    private static TextBlock WithArrow(string title, ListSortDirection direction)
    {
        var arrow = new Run(direction == ListSortDirection.Ascending ? "▲" : "▼") { FontSize = 8 };
        arrow.SetResourceReference(TextElement.ForegroundProperty, "CaptionBrush");

        var heading = new TextBlock();
        heading.Inlines.Add(new Run(title + " "));
        heading.Inlines.Add(arrow);
        return heading;
    }
}
