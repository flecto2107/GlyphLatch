using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace GlyphLatch.Windows;

public partial class MainWindow
{
    private string? footerBeforeDrop;
    private void SelectAll(object sender, RoutedEventArgs e) => Navigate(null, null, false);
    private void SelectActive(object sender, RoutedEventArgs e) => Navigate(null, null, true);

    private void Navigate(string? folder, string? set, bool active)
    {
        if (working) return;
        SetSandboxVisibility(false);
        folderScope = folder;
        setScope = set;
        onlyActive = active;
        selectedFont = null;
        RenderNavigation();
        RenderFonts();
    }

    private void FolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!changingNavigation && e.NewValue is FolderNode folder)
            Navigate(folder.Id, null, false);
    }

    private void SetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!changingNavigation && SetsList.SelectedItem is SetRow row)
            Navigate(null, row.Set.Id, false);
    }

    private void RenderNavigation()
    {
        if (!ready || working) return;
        changingNavigation = true;
        if (!ReferenceEquals(FoldersTree.ItemsSource, catalog.Folders)) FoldersTree.ItemsSource = catalog.Folders;
        var sets = library.Sets.Select(set => new SetRow(set, set.Id == activation.SelectedSet)).ToArray();
        SetsList.ItemsSource = sets;
        SetsList.SelectedItem = sets.FirstOrDefault(row => row.Set.Id == setScope);
        SelectTreeNode(FoldersTree, folderScope);
        changingNavigation = false;
        var folder = folderScope == null ? null : FindFolder(folderScope);
        var set = CurrentSet();
        PageTitle.Text = folder?.Name ?? set?.Name ?? (onlyActive ? "Активные" : "Библиотека");
        PageSubtitle.Text = folder?.Source ?? (folder != null ? "Папка в библиотеке" : set != null
            ? set.Id == activation.SelectedSet ? "Активный набор проекта" : "Набор проекта"
            : onlyActive ? "Временные регистрации GlyphLatch" : "Все шрифты");
        PageSubtitle.ToolTip = folder?.Source;
        UseSetButton.Visibility = set != null ? Visibility.Visible : Visibility.Collapsed;
        UseSetButton.Content = set != null && set.Id == activation.SelectedSet ? "Выключить набор" : "Включить набор";
        AllButton.Background = !sandboxOpen && folder == null && set == null && !onlyActive ? Brush("Selected") : Brushes.Transparent;
        ActiveButton.Background = !sandboxOpen && onlyActive ? Brush("Selected") : Brushes.Transparent;
        SandboxButton.Background = sandboxOpen ? Brush("Selected") : Brushes.Transparent;
    }

    private static void SelectTreeNode(ItemsControl items, string? id)
    {
        if (items is TreeView tree && tree.SelectedItem is FolderNode selected && selected.Id == id) return;
        for (int index = 0; index < items.Items.Count; index++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem child) continue;
            child.IsSelected = child.DataContext is FolderNode folder && folder.Id == id;
            if (child.HasItems) SelectTreeNode(child, id);
        }
    }

    private string? ImportParent => folderScope;

    private SetSpec? CurrentSet() => library.Sets.FirstOrDefault(set => set.Id == setScope);

    private async void ToggleCurrentSet(object sender, RoutedEventArgs e)
    {
        if (CurrentSet() is not { } set || working) return;
        var target = set.Id == activation.SelectedSet ? null : set;
        await RunActivation(map => activation.SelectSet(target, map));
    }

    private FolderNode? FindFolder(string id) => catalog.Folders.SelectMany(Descendants).FirstOrDefault(folder => folder.Id == id);

    private static IEnumerable<FolderNode> Descendants(FolderNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var node in Descendants(child))
                yield return node;
    }

    private async void NewFolder(object sender, RoutedEventArgs e)
    {
        if (working) return;
        string? name = AskName("Новая папка", "");
        if (name == null) return;
        string? parent = ImportParent;
        if (library.Folders.Any(folder => folder.Parent == parent &&
            string.Equals(folder.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Report("Папка с таким именем уже есть в этом разделе.");
            return;
        }
        string id = Guid.NewGuid().ToString("N");
        library.Folders.Add(new FolderSpec(id, name, parent, null));
        folderScope = id;
        setScope = null;
        onlyActive = false;
        SaveSettings();
        await RefreshCatalog([]);
    }

    private void NewSet(object sender, RoutedEventArgs e)
    {
        if (working) return;
        string? name = AskName("Новый набор", "");
        if (name == null) return;
        if (library.Sets.Any(set => string.Equals(set.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            Report("Набор с таким именем уже существует.");
            return;
        }
        var set = new SetSpec(Guid.NewGuid().ToString("N"), name, []);
        library.Sets.Add(set);
        SaveSettings();
        Navigate(null, set.Id, false);
    }

    private async void ImportFiles(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new OpenFileDialog
        {
            Title = "Добавить шрифты в библиотеку",
            Filter = "Шрифты TTF и OTF|*.ttf;*.otf",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true) await ImportPaths(dialog.FileNames, ImportParent, CurrentSet());
    }

    private async void ImportFolder(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new OpenFolderDialog { Title = "Импортировать папку со шрифтами", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ImportPaths(dialog.FolderNames, ImportParent, CurrentSet());
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (!working && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;
            var (folder, set) = DropDestination(e.OriginalSource as DependencyObject);
            string? name = set?.Name ?? (folder == null ? null : FindFolder(folder)?.Name);
            footerBeforeDrop ??= FooterStatus.Text;
            FooterStatus.Text = name == null ? "Добавить в библиотеку" : $"Добавить в «{name}»";
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (!new Rect(RenderSize).Contains(e.GetPosition(this))) RestoreDropHint();
    }

    private void RestoreDropHint()
    {
        if (footerBeforeDrop == null) return;
        FooterStatus.Text = footerBeforeDrop;
        footerBeforeDrop = null;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        RestoreDropHint();
        if (!working && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            var (folder, set) = DropDestination(e.OriginalSource as DependencyObject);
            await ImportPaths(paths, folder, set);
        }
    }

    private (string? Folder, SetSpec? Set) DropDestination(DependencyObject? target)
    {
        for (var node = target; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TreeViewItem { DataContext: FolderNode folder }) return (folder.Id, null);
            if (node is ListBoxItem { DataContext: SetRow row }) return (null, row.Set);
        }
        return (ImportParent, CurrentSet());
    }

    private async Task ImportPaths(string[] paths, string? parent, SetSpec? set)
    {
        var created = new HashSet<string>();
        var roots = new HashSet<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastFolder = null;
        int imports = 0;
        foreach (string raw in paths)
        {
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (Directory.Exists(path))
            {
                var existing = library.Folders.FirstOrDefault(folder => folder.Parent == parent &&
                    string.Equals(folder.Source, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    roots.Add(existing.Id);
                    lastFolder = existing.Id;
                    continue;
                }
                string id = Guid.NewGuid().ToString("N");
                string name = new DirectoryInfo(path).Name;
                library.Folders.Add(new FolderSpec(id, name, parent, path));
                created.Add(id);
                roots.Add(id);
                lastFolder = id;
                imports++;
            }
            else if (File.Exists(path) && FontFile.Supports(path))
            {
                files.Add(path);
                var identity = catalog.Cache.Files.FirstOrDefault(file =>
                    string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
                string id = identity == null ? $"path:{path}" : FontCatalog.Identity(identity);
                if (library.HiddenFonts.RemoveAll(hidden => string.Equals(hidden, id, StringComparison.OrdinalIgnoreCase)) > 0)
                    imports++;
                if (!library.Links.Any(link => link.Folder == parent && string.Equals(link.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    library.Links.Add(new FontLink(parent, path));
                    imports++;
                }
            }
        }
        if (set != null)
        {
            folderScope = null;
            setScope = set.Id;
            onlyActive = false;
        }
        else if (lastFolder != null)
        {
            folderScope = lastFolder;
            setScope = null;
            onlyActive = false;
        }
        else if (imports > 0)
        {
            folderScope = parent;
            setScope = null;
            onlyActive = false;
        }
        if (imports == 0 && lastFolder == null && (set == null || files.Count == 0))
        {
            Report("Новых файлов TTF, OTF или папок нет.");
            return;
        }
        SetSandboxVisibility(false);
        SaveSettings();
        await RefreshCatalog(created);
        if (set != null && library.Sets.Any(existing => existing.Id == set.Id))
        {
            var ids = catalog.Entries.Where(entry => entry.Font != null &&
                entry.Locations.Any(location => files.Contains(location.Path) || location.Source != null && roots.Contains(location.Source)))
                .Select(entry => entry.Id).ToArray();
            await ChangeSetMembership(set, ids, true);
        }
    }

    private void SetMenu(object sender, RoutedEventArgs e)
    {
        if (working || sender is not Button { Tag: SetRow row } button) return;
        var set = row.Set;
        bool active = activation.SelectedSet == set.Id;
        var menu = MakeMenu();
        AddMenu(menu, active ? "Выключить набор" : "Включить набор",
            async () => await RunActivation(map => activation.SelectSet(active ? null : set, map)));
        if (active && activation.HasUnresolved)
            AddMenu(menu, "Повторить ошибки", async () => await RunActivation(activation.Retry));
        AddMenu(menu, "Переименовать", () =>
        {
            string? name = AskName("Переименовать набор", set.Name);
            if (name == null) return;
            if (library.Sets.Any(other => other.Id != set.Id &&
                string.Equals(other.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                Report("Набор с таким именем уже существует.");
                return;
            }
            library.Sets[library.Sets.IndexOf(set)] = set with { Name = name };
            SaveSettings();
            RenderNavigation();
        });
        AddMenu(menu, "Удалить набор", async () =>
        {
            library.Sets.Remove(set);
            if (setScope == set.Id) setScope = null;
            SaveSettings();
            if (active) await RunActivation(map => activation.SelectSet(null, map));
            else
            {
                RenderNavigation();
                RenderFonts();
            }
        });
        OpenMenu(menu, button);
    }

    private void FolderMenu(object sender, RoutedEventArgs e)
    {
        if (working || sender is not Button { Tag: FolderNode node } button) return;
        var menu = MakeMenu();
        var descendants = Descendants(node).Select(child => child.Id).ToHashSet();
        string[] ids = catalog.Entries.Where(font => font.Locations.Any(location =>
                location.Folder != null && descendants.Contains(location.Folder)))
            .Select(font => font.Id).ToArray();
        if (ids.Length > 0)
        {
            AddMenu(menu, "Включить папку", async () => await RunActivation(map => activation.SetManual(ids, true, map)));
            AddMenu(menu, "Выключить папку", async () => await RunActivation(map => activation.SetManual(ids, false, map)));
        }
        AddMenu(menu, "Обновить", async () => await RefreshCatalog());
        AddMenu(menu, "Переименовать в библиотеке", async () =>
        {
            string? name = AskName("Переименовать папку", node.Name);
            if (name == null) return;
            int index = library.Folders.FindIndex(folder => folder.Id == node.Id);
            if (index >= 0) library.Folders[index] = library.Folders[index] with { Name = name };
            else library.FolderAliases[node.Id] = name;
            SaveSettings();
            await RefreshCatalog([]);
        });
        AddMenu(menu, "Убрать папку из библиотеки", async () =>
        {
            if (!node.Editable) library.HiddenFolders.Add(node.Id);
            library.Folders.RemoveAll(spec => descendants.Contains(spec.Id));
            library.Links.RemoveAll(link => link.Folder != null && descendants.Contains(link.Folder));
            foreach (string id in descendants) library.FolderAliases.Remove(id);
            if (folderScope != null && descendants.Contains(folderScope)) folderScope = null;
            SaveSettings();
            await RefreshCatalog([]);
        });
        OpenMenu(menu, button);
    }

    private void DetailsMenu(object sender, RoutedEventArgs e)
    {
        if (working || sender is not Button { Tag: FontRow row }) return;
        FontEntry font = row.SampleFont;
        var menu = MakeMenu();
        if (row.IsPartial)
            AddMenu(menu, "Выключить включённые начертания", async () => await ApplyRowActivation(row, false));
        var selectedSet = CurrentSet();
        if (selectedSet == null && activation.IsHeldBySet(font.Id))
            selectedSet = library.Sets.FirstOrDefault(set => set.Id == activation.SelectedSet);
        if (selectedSet is { } removeFrom && removeFrom.Fonts.Contains(font.Id))
            AddMenu(menu, $"Убрать из набора «{removeFrom.Name}»",
                async () => await ChangeSetMembership(removeFrom, [font.Id], false));

        if (row.Entries.Any(entry => catalog.Entries.Contains(entry)))
            AddMenu(menu, row.IsGroup ? "Убрать гарнитуру из библиотеки" : "Убрать из библиотеки",
                async () => await RemoveFonts(row.Entries));
        AddMenu(menu, "Скопировать путь", () =>
        {
            try { Clipboard.SetText(font.Path); }
            catch (System.Runtime.InteropServices.COMException) { Report("Буфер обмена занят. Повторите копирование."); }
        });
        OpenMenu(menu, (Button)sender);
    }

    private void QuickSetMenu(object sender, RoutedEventArgs e)
    {
        if (working) return;
        string[] ids = Selection().Where(font => font.Font != null).Select(font => font.Id).ToArray();
        if (ids.Length == 0) return;
        var menu = MakeMenu();
        foreach (var set in library.Sets)
        {
            var option = new MenuItem { Header = set.Name, IsEnabled = ids.Any(id => !set.Fonts.Contains(id)) };
            option.Click += async (_, _) => await ChangeSetMembership(set, ids, true);
            menu.Items.Add(option);
        }
        if (library.Sets.Count > 0) menu.Items.Add(new Separator());
        AddMenu(menu, "Создать набор из выбранных…", () =>
        {
            string? name = AskName("Новый набор", "");
            if (name == null) return;
            if (library.Sets.Any(set => string.Equals(set.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                Report("Набор с таким именем уже существует.");
                return;
            }
            var set = new SetSpec(Guid.NewGuid().ToString("N"), name, ids.ToList());
            library.Sets.Add(set);
            SaveSettings();
            RenderNavigation();
            Report($"В набор «{name}» добавлено: {ids.Length}.");
        });
        OpenMenu(menu, (Button)sender);
    }

    private async Task RemoveFonts(FontEntry[] entries)
    {
        if (working) return;
        string[] ids = entries.Select(font => font.Id).ToArray();
        var removed = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        library.HiddenFonts.AddRange(ids.Where(id => !library.HiddenFonts.Contains(id, StringComparer.OrdinalIgnoreCase)));
        foreach (var set in library.Sets) set.Fonts.RemoveAll(removed.Contains);
        SaveSettings();
        string result = "";
        await RunActivation(map => result = activation.ForgetFonts(ids, map));
        await RefreshCatalog([]);
        Report($"Убрано из библиотеки: {ids.Length}. {result}".TrimEnd());
    }

    private async Task ChangeSetMembership(SetSpec set, IReadOnlyCollection<string> ids, bool add)
    {
        while (activationFinished is { } operation) await operation.Task;
        if (exiting || library.Sets.FirstOrDefault(item => item.Id == set.Id) is not { } target) return;
        set = target;
        if (add)
        {
            var existing = set.Fonts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            set.Fonts.AddRange(ids.Where(existing.Add));
        }
        else
        {
            var remove = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            set.Fonts.RemoveAll(remove.Contains);
        }
        SaveSettings();
        if (activation.SelectedSet == set.Id)
        {
            await RunActivation(map => activation.SelectSet(set, map));
            return;
        }
        RenderFonts();
        if (add && ids.Count > 0) Report($"В наборе «{set.Name}»: {set.Fonts.Count} начерт.");
    }

    private ContextMenu MakeMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ActionMenu") };
        menu.Resources[typeof(MenuItem)] = FindResource("ActionMenuItem");
        menu.Resources[MenuItem.SeparatorStyleKey] = FindResource("ActionMenuSeparator");
        menu.Resources[typeof(ScrollBar)] = FindResource(typeof(ScrollBar));
        return menu;
    }

    private static void AddMenu(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void OpenMenu(ContextMenu menu, Button button)
    {
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private string? AskName(string title, string current)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 365, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Background = Brush("Panel"), Foreground = Brush("Ink"), FontFamily = FontFamily, ShowInTaskbar = false
        };
        string assembly = typeof(MainWindow).Assembly.GetName().Name!;
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/Controls.xaml", UriKind.Relative)
        });
        var body = new StackPanel { Margin = new Thickness(18) };
        var input = new TextBox { Text = current, MaxLength = 80, Padding = new Thickness(8),
            Background = Brush("Canvas"), Foreground = Brush("Ink"), BorderBrush = Brush("Line") };
        System.Windows.Automation.AutomationProperties.SetName(input, "Название");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 13, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(11, 5, 11, 5),
            Margin = new Thickness(0, 0, 7, 0) };
        var accept = new Button { Content = "Сохранить", IsDefault = true, IsEnabled = current.Trim().Length > 0,
            Padding = new Thickness(11, 5, 11, 5) };
        accept.Style = (Style)dialog.FindResource("Primary");
        input.TextChanged += (_, _) => accept.IsEnabled = input.Text.Trim().Length > 0;
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        body.Children.Add(input);
        body.Children.Add(actions);
        dialog.Content = body;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
