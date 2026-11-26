using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlyphLatch.Windows;

public partial class MainWindow
{
    private sealed record SandboxChoice(FontEntry Entry)
    {
        public string Label => $"{Entry.Family} · {Entry.Face}";
    }

    private Popup sandboxPicker = null!;
    private TextBox sandboxFontQuery = null!;
    private ListBox sandboxFontResults = null!;
    private TextBlock sandboxNoResults = null!;
    private SandboxChoice[] sandboxChoices = [];
    private Dictionary<string, SandboxChoice> sandboxChoicesById = new(StringComparer.OrdinalIgnoreCase);
    private SandboxChoice? sandboxDefault;
    private (Button Anchor, int Side, int Index)? sandboxPickerTarget;

    private void InitializeSandboxPicker()
    {
        var panel = new DockPanel { Width = 304, Margin = new Thickness(5) };
        sandboxFontQuery = new TextBox { MinHeight = 34, Margin = new Thickness(1, 1, 1, 5) };
        AutomationProperties.SetName(sandboxFontQuery, "Поиск начертания");
        var search = new Grid();
        search.Children.Add(sandboxFontQuery);
        var placeholder = new TextBlock
        {
            Text = "Поиск начертания", Style = (Style)FindResource("Caption"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 4), IsHitTestVisible = false
        };
        search.Children.Add(placeholder);
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);

        var results = new Grid { MaxHeight = 265 };
        sandboxFontResults = new ListBox { DisplayMemberPath = nameof(SandboxChoice.Label), IsTextSearchEnabled = false };
        AutomationProperties.SetName(sandboxFontResults, "Начертания из библиотеки");
        results.Children.Add(sandboxFontResults);
        sandboxNoResults = new TextBlock
        {
            Text = "Начертания не найдены.", Style = (Style)FindResource("Caption"), Margin = new Thickness(8),
            Visibility = Visibility.Collapsed
        };
        results.Children.Add(sandboxNoResults);
        panel.Children.Add(results);

        var frame = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        frame.SetResourceReference(Border.BackgroundProperty, "Panel");
        frame.SetResourceReference(Border.BorderBrushProperty, "SelectedLine");
        sandboxPicker = new Popup
        {
            AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom, VerticalOffset = 3, Child = frame
        };
        SandboxWorkspace.Children.Add(sandboxPicker);
        sandboxPicker.Closed += (_, _) =>
        {
            if (!sandboxPicker.IsOpen) sandboxPickerTarget = null;
        };
        sandboxFontQuery.TextChanged += (_, _) =>
        {
            placeholder.Visibility = sandboxFontQuery.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (sandboxPicker.IsOpen) FilterSandboxFonts();
        };
        sandboxFontResults.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            for (var node = e.OriginalSource as DependencyObject; node != null && node != sandboxFontResults;
                 node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is not ListBoxItem { DataContext: SandboxChoice choice }) continue;
                ChooseSandboxFont(choice);
                e.Handled = true;
                break;
            }
        };
        frame.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CloseSandboxPicker(restoreFocus: true);
                    break;
                case Key.Enter when sandboxFontResults.SelectedItem is SandboxChoice choice:
                    ChooseSandboxFont(choice);
                    break;
                case Key.Down when sandboxFontQuery.IsKeyboardFocusWithin && sandboxFontResults.SelectedIndex >= 0:
                    var selected = sandboxFontResults.SelectedItem;
                    sandboxFontResults.ScrollIntoView(selected);
                    sandboxFontResults.UpdateLayout();
                    (sandboxFontResults.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem)?.Focus();
                    break;
                default:
                    return;
            }
            e.Handled = true;
        };
    }

    private void RefreshSandboxFonts()
    {
        sandboxChoices = catalog.Entries.Where(entry => entry.Available && entry.Font != null)
            .Select(entry => new SandboxChoice(entry)).ToArray();
        sandboxChoicesById = sandboxChoices.ToDictionary(choice => choice.Entry.Id, StringComparer.OrdinalIgnoreCase);
        sandboxDefault = sandboxChoices.FirstOrDefault(choice => choice.Entry.Font!.Cyrillic &&
            choice.Entry.Face is "Regular" or "Обычный" or "Normal")
            ?? sandboxChoices.FirstOrDefault(choice => choice.Entry.Font!.Cyrillic) ?? sandboxChoices.FirstOrDefault();
        bool empty = sandboxChoices.Length == 0;
        sandboxStatus.Text = "Добавьте шрифты в библиотеку, чтобы примерить их на макете.";
        sandboxStatus.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) CloseSandboxPicker();
        UpdateSandboxFaces();
        if (sandboxPicker.IsOpen) FilterSandboxFonts();
    }

    private void UpdateSandboxFaces()
    {
        for (int side = 0; side < sandboxBlocks.Length; side++)
        {
            var group = sandboxBlocks[side];
            for (int index = 0; index < group.Count; index++)
            {
                var block = group[index];
                var spec = library.Sandbox[index];
                string? id = side == 0 ? spec.FontId : spec.CompareFontId;
                var choice = id == null ? sandboxDefault : sandboxChoicesById.GetValueOrDefault(id);
                block.Preview.Source = side == 0 || library.SandboxCompare ? choice?.Entry : null;
                block.FontLabel.Text = choice?.Label ?? (id != null ? "Шрифт недоступен" : "Выбрать шрифт");
                block.FontButton.ToolTip = block.FontLabel.Text;
                block.FontButton.IsEnabled = sandboxChoices.Length > 0;
                UpdateSandboxTextVisibility(block);
            }
        }
    }

    private void OpenSandboxPicker(Button anchor, int side, int index)
    {
        CloseSandboxPicker();
        sandboxPickerTarget = (anchor, side, index);
        sandboxFontQuery.Clear();
        sandboxPicker.PlacementTarget = anchor;
        FilterSandboxFonts();
        sandboxPicker.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!sandboxPicker.IsOpen) return;
            sandboxFontQuery.Focus();
        });
    }

    private void FilterSandboxFonts()
    {
        if (sandboxPickerTarget is not { } target) return;
        string[] words = sandboxFontQuery.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var results = words.Length == 0 ? sandboxChoices : sandboxChoices.Where(choice =>
            words.All(word => choice.Label.Contains(word, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        sandboxFontResults.ItemsSource = results;
        string? activeFontId = (sandboxBlocks[target.Side][target.Index].Preview.Source as FontEntry)?.Id;
        sandboxFontResults.SelectedItem = words.Length == 0
            ? results.FirstOrDefault(choice => choice.Entry.Id == activeFontId) ?? results.FirstOrDefault()
            : results.FirstOrDefault();
        if (sandboxFontResults.SelectedItem is { } selected) sandboxFontResults.ScrollIntoView(selected);
        sandboxNoResults.Visibility = results.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChooseSandboxFont(SandboxChoice choice)
    {
        if (sandboxPickerTarget is not { } target) return;
        var spec = library.Sandbox[target.Index];
        library.Sandbox[target.Index] = target.Side == 0
            ? spec with { FontId = choice.Entry.Id }
            : spec with { CompareFontId = choice.Entry.Id };
        UpdateSandboxFaces();
        CloseSandboxPicker(restoreFocus: true);
        QueueSandboxSave();
    }

    private void CloseSandboxPicker(bool restoreFocus = false)
    {
        var anchor = sandboxPickerTarget?.Anchor;
        sandboxPicker.IsOpen = false;
        sandboxPickerTarget = null;
        if (restoreFocus) anchor?.Focus();
    }
}
