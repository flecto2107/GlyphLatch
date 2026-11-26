using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GlyphLatch.Windows;

internal enum SandboxSample { Custom, Latin, Cyrillic }

internal sealed record SandboxSpec(string Text, double Size, string? FontId)
{
    public string? LatinText { get; init; }
    public string? CyrillicText { get; init; }
    public string? CompareFontId { get; init; }
}

public partial class MainWindow
{
    private static readonly string[] LatinSandbox =
    [
        "The city in detail",
        "Typography for a new project",
        "Every typeface has a character of its own. See how different styles work together in a headline and a paragraph."
    ];
    private static readonly string[] CyrillicSandbox =
    [
        "Город в деталях",
        "Типографика для нового проекта",
        "У каждой гарнитуры свой характер. Посмотрите, как разные начертания работают вместе в заголовке и в абзаце."
    ];
    private static readonly string[] SandboxRoles = ["Заголовок", "Подзаголовок", "Основной текст"];

    private sealed record SandboxBlock(Button FontButton, TextBlock FontLabel, FontPreview Preview, TextBlock UnstyledText);
    private readonly List<SandboxBlock>[] sandboxBlocks = [[], []];
    private Grid sandboxSelectors = null!;
    private Grid sandboxPages = null!;
    private TextBlock sandboxLabelA = null!;
    private TextBlock sandboxStatus = null!;
    private ToggleButton sandboxCompare = null!;
    private readonly Dictionary<SandboxSample, ToggleButton> sandboxModes = [];
    private bool sandboxOpen;

    private void SelectSandbox(object sender, RoutedEventArgs e)
    {
        if (working) return;
        folderScope = null;
        setScope = null;
        onlyActive = false;
        SetSandboxVisibility(true);
        RenderNavigation();
    }

    private void SetSandboxVisibility(bool visible)
    {
        if (sandboxOpen == visible) return;
        sandboxOpen = visible;
        if (!visible) CloseSandboxPicker();
        CatalogWorkspace.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        SandboxWorkspace.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarLimit();
        if (visible) RefreshSandboxFonts();
    }

    private void SidebarResized(object sender, DragCompletedEventArgs e)
    {
        library.SidebarWidth = SidebarColumn.ActualWidth;
        SaveSettings();
    }

    private void UpdateSidebarLimit()
    {
        if (ActualWidth <= 0) return;
        double reserved = !sandboxOpen && Glyphs.Visibility == Visibility.Visible ? 769 : 630;
        SidebarColumn.MaxWidth = Math.Clamp(ActualWidth - reserved, SidebarColumn.MinWidth, 465);
    }

    private void InitializeSandbox()
    {
        var workspace = new DockPanel();
        SandboxWorkspace.Children.Add(workspace);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        workspace.Children.Add(header);
        header.Children.Add(new TextBlock
        {
            Text = "Песочница", FontSize = 32, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10)
        });
        header.Children.Add(BuildSandboxToolbar());
        sandboxStatus = new TextBlock
        {
            Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0)
        };
        header.Children.Add(sandboxStatus);
        sandboxSelectors = SandboxColumns();
        sandboxSelectors.HorizontalAlignment = HorizontalAlignment.Left;
        sandboxSelectors.Margin = new Thickness(0, 0, 0, 9);
        header.Children.Add(sandboxSelectors);
        sandboxPages = SandboxColumns();
        sandboxLabelA = new TextBlock { Text = "A", Style = (Style)FindResource("Caption") };
        AddSandboxPage(0, sandboxLabelA);
        AddSandboxPage(1, new TextBlock { Text = "B", Style = (Style)FindResource("Caption") });
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = sandboxPages
        };
        workspace.Children.Add(scroll);
        sandboxSelectors.SetBinding(WidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth)) { Source = scroll });
        InitializeSandboxPicker();
        SetSandboxComparisonLayout();
        UpdateSandboxSamples();
    }

    private StackPanel BuildSandboxToolbar()
    {
        var toolbar = new StackPanel();
        var actions = new DockPanel();
        toolbar.Children.Add(actions);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(right);
        var sizes = new ToggleButton
        {
            Content = "Размеры", Style = (Style)FindResource("Segment"), Margin = new Thickness(0, 0, 4, 0)
        };
        right.Children.Add(sizes);
        sandboxCompare = new ToggleButton
        {
            Content = "Сравнить A/B", Style = (Style)FindResource("Segment")
        };
        sandboxCompare.Click += (_, _) => ToggleSandboxComparison();
        right.Children.Add(sandboxCompare);

        var samples = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(samples);
        foreach (var (mode, label) in new[]
        {
            (SandboxSample.Latin, "Латиница"), (SandboxSample.Cyrillic, "Кириллица"), (SandboxSample.Custom, "Свой текст")
        })
        {
            var option = new ToggleButton { Content = label, Style = (Style)FindResource("Segment") };
            sandboxModes.Add(mode, option);
            samples.Children.Add(option);
            option.Click += (_, _) =>
            {
                library.SandboxMode = mode;
                UpdateSandboxSamples();
                QueueSandboxSave();
            };
        }

        var sizeOptions = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        sizes.Click += (_, _) => sizeOptions.Visibility = sizes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        toolbar.Children.Add(sizeOptions);
        for (int index = 0; index < SandboxRoles.Length; index++) sizeOptions.Children.Add(BuildSandboxSize(index));
        return toolbar;
    }

    private FrameworkElement BuildSandboxSize(int index)
    {
        var row = new DockPanel { Margin = new Thickness(3, 0, 12, 0) };
        var size = new Slider
        {
            Minimum = 12, Maximum = 100, Value = Math.Clamp(library.Sandbox[index].Size, 12, 100), TickFrequency = 1,
            IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        };
        AutomationProperties.SetName(size, $"Размер · {SandboxRoles[index]}");
        var sizeLabel = new TextBlock
        {
            Text = $"{size.Value:0} px", Width = 42, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Caption")
        };
        var role = new TextBlock { Text = SandboxRoles[index], Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(sizeLabel, Dock.Right);
        row.Children.Add(sizeLabel);
        row.Children.Add(role);
        row.Children.Add(size);
        size.ValueChanged += (_, _) =>
        {
            library.Sandbox[index] = library.Sandbox[index] with { Size = size.Value };
            sizeLabel.Text = $"{size.Value:0} px";
            foreach (var group in sandboxBlocks) group[index].Preview.Size = size.Value;
            QueueSandboxSave();
        };
        return row;
    }

    private static Grid SandboxColumns()
    {
        var grid = new Grid();
        for (int column = 0; column < 3; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        return grid;
    }

    private void AddSandboxPage(int side, TextBlock letter)
    {
        var heading = new StackPanel();
        letter.FontWeight = FontWeights.SemiBold;
        letter.Margin = new Thickness(2, 0, 0, 5);
        heading.Children.Add(letter);
        var selectors = new UniformGrid { Columns = 3 };
        heading.Children.Add(selectors);
        var composition = new StackPanel();
        var paper = new Border
        {
            Child = composition, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 19, 20, 23), MinHeight = 235, VerticalAlignment = VerticalAlignment.Top
        };
        paper.SetResourceReference(Border.BackgroundProperty, "Panel");
        paper.SetResourceReference(Border.BorderBrushProperty, "Line");
        for (int index = 0; index < SandboxRoles.Length; index++)
        {
            var role = new StackPanel { Margin = new Thickness(index == 0 ? 0 : 5, 0, 0, 0) };
            role.Children.Add(new TextBlock
            {
                Text = SandboxRoles[index], Style = (Style)FindResource("Caption"), Margin = new Thickness(2, 0, 0, 4),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var block = BuildSandboxBlock(side, index, composition);
            role.Children.Add(block.FontButton);
            selectors.Children.Add(role);
            sandboxBlocks[side].Add(block);
        }
        Grid.SetColumn(heading, side * 2);
        Grid.SetColumn(paper, side * 2);
        sandboxSelectors.Children.Add(heading);
        sandboxPages.Children.Add(paper);
    }

    private SandboxBlock BuildSandboxBlock(int side, int index, StackPanel composition)
    {
        string role = SandboxRoles[index];
        string variant = side == 0 ? "A" : "B";
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var buttonContent = new DockPanel();
        var arrow = new TextBlock { Text = "⌄", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(arrow, Dock.Right);
        buttonContent.Children.Add(arrow);
        buttonContent.Children.Add(label);
        var fontButton = new Button
        {
            Content = buttonContent, MinWidth = 0, MinHeight = 32, FontSize = 12,
            Padding = new Thickness(6, 3, 6, 3), HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(fontButton, $"Выбрать шрифт · {role} · {variant}");
        fontButton.Click += (_, _) => OpenSandboxPicker(fontButton, side, index);

        var preview = new FontPreview { Size = Math.Clamp(library.Sandbox[index].Size, 12, 100), Wrap = true, MinHeight = 24 };
        preview.SetResourceReference(FontPreview.InkProperty, "Ink");
        preview.SetResourceReference(FontPreview.MutedProperty, "Muted");
        var unstyled = new TextBlock
        {
            Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5)
        };
        var display = new Grid();
        display.Children.Add(preview);
        display.Children.Add(unstyled);
        var editButton = new Button
        {
            Content = display, Style = (Style)FindResource("SandboxText"), ToolTip = "Редактировать текст"
        };
        AutomationProperties.SetName(editButton, $"Редактировать · {role} · {variant}");
        var editor = new TextBox
        {
            Visibility = Visibility.Collapsed, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1500,
            MinHeight = 39, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "Завершить редактирование · Ctrl+Enter"
        };
        AutomationProperties.SetName(editor, $"{role} · {variant}");
        var surface = new Grid { Margin = new Thickness(0, 0, 0, index == 0 ? 9 : index == 1 ? 12 : 0) };
        surface.Children.Add(editButton);
        surface.Children.Add(editor);
        composition.Children.Add(surface);
        SandboxSample? editMode = null;
        editButton.Click += (_, _) =>
        {
            editor.Text = SandboxText(index);
            editMode = library.SandboxMode;
            editor.MinHeight = Math.Clamp(editButton.ActualHeight, 39, editor.MaxHeight);
            editor.Visibility = Visibility.Visible;
            editButton.Visibility = Visibility.Collapsed;
            editor.Focus();
            editor.CaretIndex = editor.Text.Length;
        };
        editor.LostKeyboardFocus += (_, _) => EndEdit();
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape && (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control)) return;
            e.Handled = true;
            EndEdit();
            editButton.Focus();
        };
        editor.TextChanged += (_, _) =>
        {
            if (editMode == null) return;
            var spec = library.Sandbox[index];
            library.Sandbox[index] = editMode switch
            {
                SandboxSample.Latin => spec with { LatinText = editor.Text },
                SandboxSample.Cyrillic => spec with { CyrillicText = editor.Text },
                _ => spec with { Text = editor.Text }
            };
            UpdateSandboxSample(index);
            QueueSandboxSave();
        };
        return new SandboxBlock(fontButton, label, preview, unstyled);

        void EndEdit()
        {
            editMode = null;
            editButton.Visibility = Visibility.Visible;
            editor.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleSandboxComparison()
    {
        library.SandboxCompare = sandboxCompare.IsChecked == true;
        if (library.SandboxCompare && !library.SandboxComparisonSeeded)
        {
            for (int index = 0; index < SandboxRoles.Length; index++)
            {
                var spec = library.Sandbox[index];
                library.Sandbox[index] = spec with
                {
                    CompareFontId = spec.FontId ?? (sandboxBlocks[0][index].Preview.Source as FontEntry)?.Id
                };
            }
            library.SandboxComparisonSeeded = true;
        }
        CloseSandboxPicker();
        SetSandboxComparisonLayout();
        UpdateSandboxFaces();
        QueueSandboxSave();
    }

    private void SetSandboxComparisonLayout()
    {
        bool compare = library.SandboxCompare;
        sandboxCompare.IsChecked = compare;
        sandboxLabelA.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        foreach (var columns in new[] { sandboxSelectors, sandboxPages })
        {
            ((FrameworkElement)columns.Children[0]).MaxWidth = compare ? double.PositiveInfinity : 830;
            columns.Children[1].Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
            columns.ColumnDefinitions[1].Width = new GridLength(compare ? 14 : 0);
            columns.ColumnDefinitions[2].Width = compare ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
    }

    private string SandboxText(int index)
    {
        var spec = library.Sandbox[index];
        return library.SandboxMode switch
        {
            SandboxSample.Latin => spec.LatinText ?? LatinSandbox[index],
            SandboxSample.Cyrillic => spec.CyrillicText ?? CyrillicSandbox[index],
            _ => spec.Text
        };
    }

    private void UpdateSandboxSamples()
    {
        foreach (var (mode, button) in sandboxModes) button.IsChecked = mode == library.SandboxMode;
        for (int index = 0; index < SandboxRoles.Length; index++) UpdateSandboxSample(index);
    }

    private void UpdateSandboxSample(int index)
    {
        string sample = SandboxText(index);
        foreach (var group in sandboxBlocks)
        {
            var block = group[index];
            block.Preview.Sample = sample;
            block.UnstyledText.Text = string.IsNullOrEmpty(sample) ? "Нажмите, чтобы добавить текст" : sample;
            UpdateSandboxTextVisibility(block);
        }
    }

    private static void UpdateSandboxTextVisibility(SandboxBlock block)
    {
        bool typedPreview = block.Preview.Source != null && !string.IsNullOrEmpty(block.Preview.Sample);
        block.Preview.Visibility = typedPreview ? Visibility.Visible : Visibility.Collapsed;
        block.UnstyledText.Visibility = typedPreview ? Visibility.Collapsed : Visibility.Visible;
    }

    private void QueueSandboxSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }
}
