using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GlyphLatch.Windows;

internal sealed record SetRow(SetSpec Set, bool Active)
{
    public string Label => Active ? $"{Set.Name}  •" : Set.Name;
}

internal sealed class FontRow(string key, string title, string subtitle, FontEntry[] entries,
    bool isGroup, bool indent, string disclosure, bool canEnable, bool canDisable, string detail) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Subtitle { get; private set; } = subtitle;
    public FontEntry[] Entries { get; } = entries;
    public bool IsGroup { get; } = isGroup;
    public bool Indent { get; } = indent;
    public string Disclosure { get; private set; } = disclosure;
    public bool CanEnable { get; private set; } = canEnable;
    public bool CanDisable { get; private set; } = canDisable;
    public string Detail { get; private set; } = detail;
    public bool HasAction => CanEnable || CanDisable;
    public bool IsPartial => CanEnable && CanDisable;
    public bool IsFullyActive => CanDisable && !CanEnable;
    public string ActivationHint => IsPartial ? $"Включить остальные начертания · {Title}"
        : CanDisable ? $"Выключить · {Title}" : CanEnable ? $"Включить · {Title}" : Detail;
    public FontEntry SampleFont { get; } = entries.OrderByDescending(font => font.Available)
        .ThenByDescending(font => font.Face is "Regular" or "Обычный" or "Normal").First();
    public bool HasGlyphs => SampleFont is { Available: true, Font: not null };
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateState(FontRow next)
    {
        if ((Subtitle, Disclosure, CanEnable, CanDisable, Detail) ==
            (next.Subtitle, next.Disclosure, next.CanEnable, next.CanDisable, next.Detail)) return;
        (Subtitle, Disclosure, CanEnable, CanDisable, Detail) =
            (next.Subtitle, next.Disclosure, next.CanEnable, next.CanDisable, next.Detail);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public partial class MainWindow : Window
{
    private readonly LibraryStore store;
    private readonly LibraryState library;
    private readonly FontActivation activation;
    private FontCatalog catalog;
    private readonly HashSet<string> expanded = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> foldedInSearch = new(StringComparer.CurrentCultureIgnoreCase);
    internal FontPreviewCache PreviewCache { get; }
    public static readonly DependencyProperty CatalogSampleProperty =
        DependencyProperty.Register(nameof(CatalogSample), typeof(string), typeof(MainWindow), new PropertyMetadata(""));
    public static readonly DependencyProperty CatalogSampleSizeProperty =
        DependencyProperty.Register(nameof(CatalogSampleSize), typeof(double), typeof(MainWindow), new PropertyMetadata(42d));
    public static readonly DependencyProperty CatalogSampleHeightProperty =
        DependencyProperty.Register(nameof(CatalogSampleHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(65d));
    public string CatalogSample => (string)GetValue(CatalogSampleProperty);
    public double CatalogSampleSize => (double)GetValue(CatalogSampleSizeProperty);
    public double CatalogSampleHeight => (double)GetValue(CatalogSampleHeightProperty);
    private static readonly (string Key, string Name, string Text)[] Samples =
    [
        ("latin", "Латинская фраза", "The quick brown fox jumps over the lazy dog"),
        ("cyrillic", "Кириллическая фраза", "Съешь ещё этих мягких французских булок"),
        ("digits", "Цифры", "0123456789"),
        ("latin-alphabet", "Латинский алфавит", "ABCDEFGHIJKLMNOPQRSTUVWXYZ  abcdefghijklmnopqrstuvwxyz"),
        ("cyrillic-alphabet", "Кириллический алфавит", "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ  абвгдеёжзийклмнопрстуфхцчшщъыьэюя"),
        ("mixed", "Буквы, цифры и знаки", "Aa Бб 0123 & @ % ! ? + − ₽")
    ];
    private readonly Dictionary<string, FileSystemWatcher> watchers = new();
    private readonly HashSet<string> changed = new();
    private readonly DispatcherTimer searchTimer;
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer watchTimer;
    private readonly DispatcherTimer retrySources;
    private CancellationTokenSource? scan;
    private TaskCompletionSource? activationFinished;
    private TrayIcon? tray;
    private bool ready;
    private bool working;
    private bool exiting;
    private bool quitRequested;
    private bool canClose;
    private bool changingNavigation;
    private bool restoringFontSelection;
    private string? folderScope;
    private string? setScope;
    private bool onlyActive;
    private string? selectedFont;
    private string? recovery;

    public MainWindow() : this(new LibraryStore()) { }

    internal MainWindow(LibraryStore store)
    {
        this.store = store;
        library = store.Load();
        library.PreviewMode ??= Samples.FirstOrDefault(preset => preset.Text == library.Specimen).Key ?? "custom";
        PreviewCache = new FontPreviewCache(store.PreviewDirectory);
        catalog = new FontCatalog(store.LoadCache(), library.Folders, library.HiddenFonts,
            library.HiddenFolders, library.FolderAliases);
        activation = new FontActivation(store);
        searchTimer = Timer(70, () => { searchTimer!.Stop(); RenderFonts(); });
        saveTimer = Timer(600, () => { saveTimer!.Stop(); SaveSettings(); });
        watchTimer = Timer(500, () =>
        {
            watchTimer!.Stop();
            if (scan != null)
            {
                watchTimer.Start();
                return;
            }
            _ = ScanChangedSources();
        });
        retrySources = Timer(12000, () =>
        {
            if (scan != null) return;
            foreach (var source in catalog.Cache.Folders.Where(folder => folder.Source == folder.Id))
                if (source.Unavailable == Directory.Exists(source.Path)) changed.Add(source.Source);
            changed.UnionWith(catalog.Cache.Folders.Where(folder => folder.Unavailable && Directory.Exists(folder.Path))
                .Select(folder => folder.Source));
            bool restoredLink = catalog.HasUnavailableSources && catalog.Cache.Files.Any(file =>
                file.Source == null && file.Issue == FontCatalog.Unavailable && File.Exists(file.Path));
            if (changed.Count > 0 || restoredLink) _ = ScanChangedSources();
        });
        LoadInterfaceFonts();
        ApplyThemeResources();
        InitializeComponent();
        string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        VersionLabel.Text = $"Версия {version}";
        SampleText.Text = library.Specimen;
        SampleSize.Value = library.Size;
        SidebarColumn.Width = new GridLength(Math.Clamp(library.SidebarWidth, 207, 465));
        InitializeSandbox();
        SizeChanged += (_, _) => UpdateSidebarLimit();
        ThemeButton.IsChecked = library.Dark;
        Glyphs.CloseRequested += (_, _) => CloseGlyphs();
        SourceInitialized += (_, _) => ApplyTitleBar();
        ready = true;
        UpdateSample();
        RenderNavigation();
        RenderFonts();
    }

    private DispatcherTimer Timer(double delay, Action onTick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(delay)
        };
        timer.Tick += (_, _) => onTick();
        return timer;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowLoaded;
        SetupTray();
        SyncWatchers();
        retrySources.Start();
        await RunActivation(_ => { recovery = activation.ReadRecovery(); return recovery ?? ""; });
        await RefreshCatalog();
    }

    private async Task RefreshCatalog(HashSet<string>? sources = null)
    {
        if (exiting) return;
        scan?.Cancel();
        using var cancellation = new CancellationTokenSource();
        scan = cancellation;
        var specs = library.Folders.ToArray();
        var links = library.Links.ToArray();
        var previous = catalog.Cache;
        var hiddenFonts = library.HiddenFonts.ToArray();
        var hiddenFolders = library.HiddenFolders.ToArray();
        var aliases = new Dictionary<string, string>(library.FolderAliases);
        CancelScanButton.Visibility = Visibility.Visible;
        ScanIndicator.Visibility = Visibility.Visible;
        Report("Читаю папки и метаданные шрифтов…");
        try
        {
            var next = await Task.Run(() => FontCatalog.Scan(specs, links, previous, cancellation.Token, sources,
                hiddenFonts, hiddenFolders, aliases), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (scan != cancellation) return;
            catalog = next;
            if (folderScope != null && FindFolder(folderScope) == null) folderScope = null;
            if (!working)
            {
                RenderNavigation();
                RenderFonts();
            }
            if (sandboxOpen) RefreshSandboxFonts();
            SyncWatchers();
            int skipped = catalog.Cache.Files.Count(file => file.Issue != null);
            int duplicates = catalog.Entries.Sum(font => Math.Max(0,
                font.Locations.Select(location => location.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() - 1));
            Report(string.Join(" ", new[] { skipped > 0 ? $"Файлов с ошибками или без доступа: {skipped}." : "",
                duplicates > 0 ? $"Объединено одинаковых копий: {duplicates}." : "" }.Where(text => text.Length > 0)));
            await Task.Run(() => store.SaveCache(next.Cache));
        }
        catch (OperationCanceledException)
        {
            if (scan == cancellation) Report("Обновление остановлено. Его можно запустить снова.");
        }
        catch (Exception error)
        {
            if (scan == cancellation) Report($"Не удалось обновить библиотеку. {error.Message}");
        }
        finally
        {
            if (scan == cancellation)
            {
                scan = null;
                CancelScanButton.Visibility = Visibility.Collapsed;
                ScanIndicator.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SyncWatchers()
    {
        var sources = library.Folders.Where(folder => folder.Source != null).ToDictionary(folder => folder.Id);
        foreach (string id in watchers.Keys.Where(id => !sources.ContainsKey(id) || !Directory.Exists(sources[id].Source)).ToArray())
        {
            watchers[id].Dispose();
            watchers.Remove(id);
        }
        foreach (var source in sources.Values)
        {
            if (watchers.ContainsKey(source.Id) || !Directory.Exists(source.Source)) continue;
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(source.Source!)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                void MarkChanged(object sender, FileSystemEventArgs eventArgs) => QueueSource(source.Id, false);
                watcher.Changed += MarkChanged;
                watcher.Created += MarkChanged;
                watcher.Deleted += MarkChanged;
                watcher.Renamed += MarkChanged;
                watcher.Error += (_, _) => QueueSource(source.Id, true);
                watcher.EnableRaisingEvents = true;
                watchers.Add(source.Id, watcher);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                System.Diagnostics.Trace.TraceWarning(error.Message);
            }
        }
    }

    private void QueueSource(string id, bool reset)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (exiting) return;
            if (reset && watchers.Remove(id, out var watcher)) watcher.Dispose();
            changed.Add(id);
            watchTimer.Stop();
            watchTimer.Start();
        });
    }

    private Task ScanChangedSources()
    {
        var sources = new HashSet<string>(changed);
        changed.Clear();
        return RefreshCatalog(sources);
    }

    private Dictionary<string, FontEntry> FontMap()
    {
        var map = catalog.Entries.ToDictionary(font => font.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var font in activation.ActiveFiles)
            map.TryAdd(font.Hash, new FontEntry(font.Hash, font, font.Path, File.Exists(font.Path), null,
                [new IndexedPath(null, font.Path, font, null, null)]));
        return map;
    }

    private IEnumerable<FontEntry> FontsInScope()
    {
        var entries = catalog.Entries;
        if (onlyActive) return FontMap().Values.Where(font => activation.IsActive(font.Id));
        if (setScope != null)
        {
            var set = CurrentSet();
            if (set == null) return [];
            var members = set.Fonts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return entries.Where(font => members.Contains(font.Id));
        }
        if (folderScope == null) return entries;
        var folder = FindFolder(folderScope);
        if (folder == null) return [];
        var descendants = Descendants(folder).Select(node => node.Id).ToHashSet();
        return entries.Where(font => font.Locations.Any(location => location.Folder != null && descendants.Contains(location.Folder)));
    }

    private void RenderFonts()
    {
        if (!ready || working) return;
        var scoped = FontsInScope().OrderBy(font => font.Family, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(font => font.Face, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var families = scoped.GroupBy(font => font.Family, StringComparer.CurrentCultureIgnoreCase);
        string query = SearchBox.Text.Trim();
        bool Cyrillic(FontEntry font) => CyrillicFilter.IsChecked != true || font.Font?.Cyrillic == true;
        bool Matches(FontEntry font) => Cyrillic(font) && (query.Length == 0 ||
            font.Family.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            font.Face.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            $"{font.Family} {font.Face}".Contains(query, StringComparison.CurrentCultureIgnoreCase));

        var rows = new List<FontRow>();
        int count = 0;
        int groups = 0;
        foreach (var family in families)
        {
            var visible = family.Where(Matches).ToArray();
            if (visible.Length == 0) continue;
            count += visible.Length;
            groups++;
            var available = family.ToArray();
            bool group = available.Length > 1;
            if (group)
            {
                string key = $"family:{family.Key}";
                int active = available.Count(font => activation.IsActive(font.Id));
                string status = active == available.Length ? " · Включено в Windows"
                    : active > 0 ? $" · Частично включено {active}/{available.Length}" : "";
                bool folded = query.Length == 0 ? !expanded.Contains(key) : foldedInSearch.Contains(key);
                rows.Add(ToRow(key, family.Key, $"{available.Length} начерт.{status}", available, true, false, folded ? "›" : "⌄"));
                if (folded) continue;
            }
            foreach (var font in visible)
            {
                string metadata = font.Font is { } file ? $"{file.Format}{(file.Variable ? " Variable" : "")}" : "";
                string description = string.Join(" · ", new[] { group ? "" : font.Face, metadata,
                    StateLabel(activation.GetStatus(font), compact: true) }.Where(value => value.Length > 0));
                rows.Add(ToRow(font.Id, group ? font.Face : font.Family, description, [font], false, group, ""));
            }
        }
        var retained = selectedFont == null ? [] : FontList.SelectedItems.Cast<FontRow>().Select(row => row.Key).ToHashSet();
        var scroll = CatalogScroll();
        double offset = retained.Count > 0 ? scroll?.VerticalOffset ?? 0 : 0;
        restoringFontSelection = true;
        if (FontList.ItemsSource is List<FontRow> existing && existing.Count == rows.Count &&
            existing.Zip(rows).All(pair => pair.First.Key == pair.Second.Key && pair.First.Title == pair.Second.Title &&
                pair.First.IsGroup == pair.Second.IsGroup && pair.First.Indent == pair.Second.Indent &&
                pair.First.Entries.SequenceEqual(pair.Second.Entries, ReferenceEqualityComparer.Instance)))
        {
            for (int i = 0; i < rows.Count; i++) existing[i].UpdateState(rows[i]);
            rows = existing;
        }
        else
        {
            FontList.ItemsSource = rows;
        }
        FontList.SelectedItem = rows.FirstOrDefault(row => row.Key == selectedFont)
            ?? rows.FirstOrDefault(row => row.Entries.Any(font => font.Available)) ?? rows.FirstOrDefault();
        foreach (var row in rows)
            if (retained.Contains(row.Key) && !FontList.SelectedItems.Contains(row)) FontList.SelectedItems.Add(row);
        if (offset > 0 && rows.Any(row => retained.Contains(row.Key))) scroll?.ScrollToVerticalOffset(offset);
        selectedFont = (FontList.SelectedItem as FontRow)?.Key;
        restoringFontSelection = false;
        FontCount.Text = $"{groups} гарн. · {count} нач.";
        EmptyListText.Text = scoped.Length == 0 ? "В этом разделе пока нет шрифтов." : "По заданным условиям ничего не найдено.";
        if (catalog.Entries.Count == 0 && folderScope == null && setScope == null && !onlyActive)
            EmptyListText.Text = "Добавьте шрифты или перетащите папку из Проводника.";
        EmptyImportButton.Visibility = scoped.Length == 0 && !onlyActive ? Visibility.Visible : Visibility.Collapsed;
        EmptyList.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveBadge.Text = activation.ActiveCount > 0 ? activation.ActiveCount.ToString() : "";
        RetryButton.Visibility = activation.HasUnresolved ? Visibility.Visible : Visibility.Collapsed;
        DisableAllButton.Visibility = activation.ActiveCount > 0 || activation.SelectedSet != null ? Visibility.Visible : Visibility.Collapsed;
        RenderDetails();
    }

    private FontRow ToRow(string key, string title, string description, FontEntry[] entries,
        bool isGroup, bool indent, string disclosure) =>
        new(key, title, description, entries, isGroup, indent, disclosure,
            entries.Any(font => font.Available && !activation.IsActive(font.Id) && activation.GetStatus(font).State != FontState.Installed),
            entries.Any(font => activation.IsActive(font.Id)),
            string.Join(Environment.NewLine, entries.Select(font => activation.GetStatus(font).Detail)
                .Where(detail => detail.Length != 0).Distinct().DefaultIfEmpty(description)));

    private ScrollViewer? CatalogScroll()
    {
        return FontList.Template?.FindName("PART_ScrollViewer", FontList) as ScrollViewer
            ?? (VisualTreeHelper.GetChildrenCount(FontList) > 0 ? VisualTreeHelper.GetChild(FontList, 0) as ScrollViewer : null);
    }

    private FontEntry[] Selection() => FontList.SelectedItems.Cast<FontRow>().SelectMany(row => row.Entries)
        .DistinctBy(font => font.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    private FontEntry? PreviewEntry => (FontList.SelectedItem as FontRow)?.SampleFont;

    private void RenderDetails()
    {
        if (!ready || working) return;
        var selected = Selection();
        bool canEnable = selected.Any(font => font.Available && !activation.IsActive(font.Id)
            && activation.GetStatus(font).State != FontState.Installed);
        bool canDisable = selected.Any(font => activation.IsActive(font.Id));
        DetailAction.Content = canEnable ? "Включить" : canDisable ? "Выключить" : "Включить";
        DetailAction.CommandParameter = canEnable ? "on" : "off";
        DetailAction.IsEnabled = canEnable || canDisable;
        bool batch = FontList.SelectedItems.Count > 1;
        DetailAction.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
        DetailOff.IsEnabled = canDisable;
        DetailOff.Visibility = batch && canEnable && canDisable ? Visibility.Visible : Visibility.Collapsed;
        AddToSetButton.IsEnabled = selected.Any(font => font.Font != null);
        if (Glyphs.Visibility == Visibility.Visible)
        {
            if (PreviewEntry is { } entry) _ = Glyphs.ShowFont(entry, PreviewCache);
            else CloseGlyphs();
        }
    }

    private void OpenRowGlyphs(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FontRow { HasGlyphs: true } row }) return;
        if (!ReferenceEquals(FontList.SelectedItem, row)) FontList.SelectedItem = row;
        OpenGlyphs(row.SampleFont);
    }

    private void OpenGlyphs(FontEntry entry)
    {
        GlyphColumn.Width = new GridLength(299);
        GlyphGap.Width = new GridLength(10);
        Glyphs.Visibility = Visibility.Visible;
        UpdateSidebarLimit();
        _ = Glyphs.ShowFont(entry, PreviewCache);
    }

    private void CloseGlyphs()
    {
        Glyphs.Clear();
        Glyphs.Visibility = Visibility.Collapsed;
        GlyphColumn.Width = new GridLength(0);
        UpdateSidebarLimit();
        GlyphGap.Width = new GridLength(0);
    }

    private static string StateLabel(ActivationStatus status, bool compact = false) => status.State switch
    {
        FontState.On => "Включено в Windows",
        FontState.Installed => "Установлено в Windows",
        FontState.Unavailable => "Файл недоступен",
        FontState.Conflict => "Конфликт",
        FontState.Error => "Ошибка",
        _ => compact ? "" : "Выключено"
    };

    private async Task RunActivation(Func<IReadOnlyDictionary<string, FontEntry>, string> work)
    {
        if (working) return;
        var map = FontMap();
        working = true;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activationFinished = finished;
        DetailAction.IsEnabled = DetailOff.IsEnabled = AddToSetButton.IsEnabled = false;
        UseSetButton.IsEnabled = RetryButton.IsEnabled = DisableAllButton.IsEnabled = false;
        Report("Выполняю операцию…");
        string message;
        try
        {
            message = await Task.Run(() => work(map));
        }
        catch (Exception error)
        {
            message = error.Message;
        }
        finally
        {
            working = false;
            activationFinished = null;
            UseSetButton.IsEnabled = RetryButton.IsEnabled = DisableAllButton.IsEnabled = true;
            finished.TrySetResult();
        }
        RenderNavigation();
        RenderFonts();
        Report(message);
        if (quitRequested)
        {
            quitRequested = false;
            _ = Quit();
        }
    }

    private void Report(string message)
    {
        FooterStatus.Text = !string.IsNullOrWhiteSpace(message) ? message : recovery ?? "Шрифты остаются в исходных папках.";
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        searchTimer.Stop();
        searchTimer.Start();
    }
    private void FilterChanged(object sender, RoutedEventArgs e) => RenderFonts();
    private void ClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (sandboxOpen)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Navigate(null, null, false);
                SearchBox.Focus();
                e.Handled = true;
            }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) ImportFiles(sender, e);
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O) ImportFolder(sender, e);
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            e.Handled = true;
            if (searchTimer.IsEnabled)
            {
                searchTimer.Stop();
                RenderFonts();
            }
            if (!DetailAction.IsEnabled) return;
            bool enable = Equals(DetailAction.CommandParameter, "on");
            string[] ids = Selection().Select(font => font.Id).ToArray();
            await RunActivation(map => activation.SetManual(ids, enable, map));
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin)
        {
            searchTimer.Stop();
            RenderFonts();
            FontList.Focus();
            if (FontList.ItemContainerGenerator.ContainerFromItem(FontList.SelectedItem) is ListBoxItem item) item.Focus();
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
            SearchBox.Clear();
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F4 && PreviewEntry is { Available: true } entry)
            OpenGlyphs(entry);
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape && Glyphs.Visibility == Visibility.Visible)
            CloseGlyphs();
        else return;
        e.Handled = true;
    }

    private void FontSelected(object sender, SelectionChangedEventArgs e)
    {
        if (restoringFontSelection) return;
        selectedFont = (FontList.SelectedItem as FontRow)?.Key;
        RenderDetails();
    }
    private void ToggleGroup(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FontRow row }) ToggleGroup(row);
    }

    private void FontRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return;
        for (var node = e.OriginalSource as DependencyObject; node != null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is not ListBoxItem item) continue;
            if (item.DataContext is FontRow { IsGroup: true } row) ToggleGroup(row);
            break;
        }
    }

    private void ToggleGroup(FontRow row)
    {
        if (working) return;
        var toggled = string.IsNullOrWhiteSpace(SearchBox.Text) ? expanded : foldedInSearch;
        if (!toggled.Add(row.Key)) toggled.Remove(row.Key);
        selectedFont = row.Key;
        RenderFonts();
    }
    private async void RowActivation(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FontRow row })
            await ApplyRowActivation(row, row.CanEnable);
    }

    private async Task ApplyRowActivation(FontRow row, bool enable)
    {
        if (working) return;
        selectedFont = row.Key;
        if (!FontList.SelectedItems.Contains(row)) FontList.SelectedItem = row;
        string[] ids = row.Entries.Select(font => font.Id).ToArray();
        await RunActivation(map => activation.SetManual(ids, enable, map));
    }
    private async void DetailActivation(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            bool enable = Equals(button.CommandParameter, "on");
            string[] ids = Selection().Select(font => font.Id).ToArray();
            await RunActivation(map => activation.SetManual(ids, enable, map));
        }
    }
    private async void RetryClick(object sender, RoutedEventArgs e) => await RunActivation(activation.Retry);
    private async void DisableAllClick(object sender, RoutedEventArgs e) => await RunActivation(activation.DisableAll);
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshCatalog();
    private void CancelScan(object sender, RoutedEventArgs e) => scan?.Cancel();
    private void SampleChanged(object sender, TextChangedEventArgs e) => UpdateSample();
    private void SampleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSample();
    private void SamplePreset(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string key }) return;
        library.PreviewMode = key;
        UpdateSample();
        if (key == "custom") SampleText.Focus();
    }

    private void MoreSamples(object sender, RoutedEventArgs e)
    {
        var menu = MakeMenu();
        foreach (var preset in Samples.Skip(3))
            AddMenu(menu, preset.Name, () => { library.PreviewMode = preset.Key; UpdateSample(); });
        OpenMenu(menu, (Button)sender);
    }

    private void UpdateSample()
    {
        if (!ready) return;
        library.Specimen = SampleText.Text;
        library.Size = SampleSize.Value;
        string mode = library.PreviewMode ?? "latin";
        var preset = Samples.FirstOrDefault(candidate => candidate.Key == mode);
        bool custom = mode == "custom";
        LatinPreset.IsChecked = mode == "latin";
        CyrillicPreset.IsChecked = mode == "cyrillic";
        DigitsPreset.IsChecked = mode == "digits";
        CustomPreset.IsChecked = custom;
        SampleText.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        MorePresets.Content = !custom && mode is not ("latin" or "cyrillic" or "digits") ? $"{preset.Name} ▾" : "Ещё ▾";
        SetValue(CatalogSampleProperty, custom ? SampleText.Text : preset.Text ?? Samples[0].Text);
        SetValue(CatalogSampleSizeProperty, SampleSize.Value);
        SetValue(CatalogSampleHeightProperty, Math.Ceiling(SampleSize.Value * 1.36 + 4));
        SizeLabel.Text = $"{SampleSize.Value:0} px";
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SaveSettings()
    {
        try { store.Save(library); }
        catch (Exception error) { Report($"Не удалось сохранить библиотеку. {error.Message}"); }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static void LoadInterfaceFonts()
    {
        string assembly = typeof(MainWindow).Assembly.GetName().Name!;
        var root = new Uri($"pack://application:,,,/{assembly};component/Assets/Fonts/");
        Application.Current.Resources["InterfaceFont"] = new FontFamily(root, "./#Medeo Sans, Segoe UI");
        Application.Current.Resources["InterfaceCodeFont"] = new FontFamily(root, "./#Medeo Mono, Consolas");
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        library.Dark = ThemeButton.IsChecked == true;
        ApplyThemeResources();
        ApplyTitleBar();
        RenderNavigation();
        RenderDetails();
        SaveSettings();
    }

    private void ApplyThemeResources()
    {
        (string Key, string Light, string Dark)[] palette =
        [
            ("Canvas", "#F4F4F4", "#111111"), ("Rail", "#EDEDED", "#171717"), ("Panel", "#FFFFFF", "#1C1C1C"),
            ("Ink", "#111111", "#F5F5F5"), ("Muted", "#595959", "#B7B7B7"), ("Line", "#D9D9D9", "#383838"),
            ("Hover", "#F0F0F0", "#282828"), ("Selected", "#E9E9E9", "#303030"),
            ("SelectedLine", "#C4C4C4", "#505050"), ("Focus", "#555555", "#CCCCCC"), ("LogoDot", "#B5B5B5", "#777777"),
            ("LampOff", "#8E8E8E", "#737373"), ("LampOn", "#168644", "#3AC975")
        ];
        foreach (var item in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(library.Dark ? item.Dark : item.Light));
            brush.Freeze();
            Application.Current.Resources[item.Key] = brush;
        }
        Application.Current.Resources[SystemColors.HighlightBrushKey] = Application.Current.Resources["SelectedLine"];
        Application.Current.Resources[SystemColors.HighlightTextBrushKey] = Application.Current.Resources["Ink"];
        Application.Current.Resources[SystemColors.MenuBrushKey] = Application.Current.Resources["Panel"];
        Application.Current.Resources[SystemColors.MenuTextBrushKey] = Application.Current.Resources["Ink"];
    }

    private void ApplyTitleBar()
    {
        IntPtr window = new WindowInteropHelper(this).Handle;
        int dark = library.Dark ? 1 : 0;
        if (window != IntPtr.Zero) _ = DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void SetupTray()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(handle) is not HwndSource source) return;
        string assembly = typeof(MainWindow).Assembly.GetName().Name!;
        var resource = Application.GetResourceStream(new Uri($"/{assembly};component/Assets/glyphlatch.ico", UriKind.Relative));
        using var stream = resource.Stream;
        using var temporary = new System.Drawing.Icon(stream);
        tray = new TrayIcon(source, (System.Drawing.Icon)temporary.Clone(), PopulateTrayMenu, ShowFromTray);
        if (!tray.IsRegistered) store.Diagnostic("Не удалось зарегистрировать значок GlyphLatch в трее.");
    }

    private void PopulateTrayMenu(Forms.ContextMenuStrip menu)
    {
        var colors = new TrayMenuColors(library.Dark);
        menu.Renderer = new TrayMenuRenderer(colors);
        menu.ForeColor = colors.Text;
        foreach (var old in menu.Items.OfType<Forms.ToolStripItem>().ToArray()) old.Dispose();
        menu.Items.Clear();
        menu.Items.Add("Открыть GlyphLatch", null, (_, _) => ShowFromTray());
        var sets = new Forms.ToolStripMenuItem("Наборы") { Enabled = !working && library.Sets.Count > 0 };
        sets.DropDown.ForeColor = colors.Text;
        sets.DropDown.Renderer = menu.Renderer;
        sets.DropDownItems.Add("Выключить набор", null, (_, _) => _ = RunActivation(map => activation.SelectSet(null, map)));
        foreach (var set in library.Sets)
        {
            var item = new Forms.ToolStripMenuItem(set.Name) { Checked = set.Id == activation.SelectedSet };
            item.Click += (_, _) => _ = RunActivation(map => activation.SelectSet(set, map));
            sets.DropDownItems.Add(item);
        }
        menu.Items.Add(sets);
        menu.Items.Add("Выключить все шрифты", null, (_, _) => _ = RunActivation(activation.DisableAll)).Enabled
            = !working && (activation.ActiveCount > 0 || activation.SelectedSet != null);
        menu.Items.Add("Выйти из GlyphLatch", null, (_, _) => _ = Quit());
    }

    internal void ShowFromTray()
    {
        if (canClose) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (canClose) return;
        e.Cancel = true;
        if (tray?.IsRegistered == true) Hide();
        else _ = Dispatcher.BeginInvoke(() => _ = Quit());
    }

    private async void QuitClick(object sender, RoutedEventArgs e) => await Quit();

    private async Task Quit()
    {
        if (exiting) return;
        if (working)
        {
            quitRequested = true;
            Report("GlyphLatch завершится после текущей операции.");
            return;
        }
        exiting = true;
        scan?.Cancel();
        await RunActivation(activation.DisableAll);
        if (activation.ActiveCount != 0)
        {
            ShowFromTray();
            if (MessageBox.Show(this, "Windows не сняла часть регистраций. Выйти всё равно?", "GlyphLatch",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                exiting = false;
                return;
            }
        }
        SaveSettings();
        searchTimer.Stop();
        saveTimer.Stop();
        watchTimer.Stop();
        retrySources.Stop();
        foreach (var watcher in watchers.Values) watcher.Dispose();
        tray?.Dispose();
        canClose = true;
        Close();
    }
}
