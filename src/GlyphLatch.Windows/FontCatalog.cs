using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace GlyphLatch.Windows;

internal sealed record IndexedPath(string? Folder, string Path, FontFile? Font, string? Issue, string? Source);
internal sealed record DiskFolder(string Id, string Parent, string Name, string Path, string Source, bool Unavailable);
internal sealed record CatalogCache(List<IndexedPath> Files, List<DiskFolder> Folders)
{
    public CatalogCache() : this([], []) { }
}

internal sealed record FolderNode(string Id, string Name, string? Source, bool Unavailable, bool Editable)
{
    public string Label => Unavailable ? $"{Name} · недоступна" : Name;
    public List<FolderNode> Children { get; } = [];
}

internal sealed record FontEntry(string Id, FontFile? Font, string Path, bool Available, string? Issue, IReadOnlyList<IndexedPath> Locations)
{
    public string Family => Font?.Family ?? System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Face => Font?.Face ?? Issue ?? "Файл недоступен";
}

internal sealed class FontCatalog
{
    internal const string Unavailable = "Файл недоступен";
    internal CatalogCache Cache { get; }
    internal List<FontEntry> Entries { get; }
    internal List<FolderNode> Folders { get; }
    internal bool HasUnavailableSources => Cache.Folders.Any(f => f.Unavailable) || Cache.Files.Any(f => f.Issue == Unavailable);

    internal FontCatalog(CatalogCache cache, IReadOnlyList<FolderSpec> specs,
        IReadOnlyCollection<string>? hiddenFonts = null, IReadOnlyCollection<string>? hiddenFolders = null,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        Cache = cache;
        var nodes = specs.ToDictionary(folder => folder.Id,
            folder => new FolderNode(folder.Id, folder.Name, folder.Source, false, true));
        foreach (var disk in cache.Folders)
        {
            if (nodes.TryGetValue(disk.Id, out var saved))
                nodes[disk.Id] = saved with { Unavailable = disk.Unavailable };
            else
                nodes.Add(disk.Id, new FolderNode(disk.Id, aliases?.GetValueOrDefault(disk.Id) ?? disk.Name,
                    disk.Path, disk.Unavailable, false));
        }

        Folders = [];
        foreach (var spec in specs.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (spec.Parent != null && nodes.TryGetValue(spec.Parent, out var parent))
                parent.Children.Add(nodes[spec.Id]);
            else
                Folders.Add(nodes[spec.Id]);
        }
        foreach (var disk in cache.Folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (disk.Id != disk.Source && nodes.TryGetValue(disk.Parent, out var parent))
                parent.Children.Add(nodes[disk.Id]);
        }
        var excludedFolders = new HashSet<string>(hiddenFolders ?? []);
        var visibleFolders = new HashSet<string>();
        void KeepVisible(List<FolderNode> folders)
        {
            folders.RemoveAll(folder => excludedFolders.Contains(folder.Id));
            foreach (var folder in folders)
                if (visibleFolders.Add(folder.Id)) KeepVisible(folder.Children);
        }
        KeepVisible(Folders);
        var excludedFonts = new HashSet<string>(hiddenFonts ?? [], StringComparer.OrdinalIgnoreCase);
        Entries = cache.Files
            .Where(file => FontFile.Supports(file.Path) && (file.Folder == null || visibleFolders.Contains(file.Folder)))
            .GroupBy(Identity, StringComparer.OrdinalIgnoreCase)
            .Where(group => !excludedFonts.Contains(group.Key))
            .Select(group =>
            {
                var preferred = group.FirstOrDefault(file => file.Issue == null) ?? group.First();
                return new FontEntry(group.Key, preferred.Font, preferred.Path, preferred.Issue == null,
                    preferred.Issue, group.ToArray());
            })
            .OrderBy(font => font.Family, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(font => font.Face, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static string Identity(IndexedPath file) => file.Font?.Hash ?? $"path:{file.Path}";

    internal static FontCatalog Scan(FolderSpec[] folders, FontLink[] links, CatalogCache previous, CancellationToken token,
        HashSet<string>? changedSources = null, IReadOnlyCollection<string>? hiddenFonts = null,
        IReadOnlyCollection<string>? hiddenFolders = null, IReadOnlyDictionary<string, string>? aliases = null)
    {
        Dictionary<string, FontFile> oldFiles = previous.Files.Where(file => file.Font != null)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(file => file.Path, file => file.Font!, StringComparer.OrdinalIgnoreCase);
        var cache = new CatalogCache();

        IndexedPath Index(string? folder, string path, string? source)
        {
            token.ThrowIfCancellationRequested();
            oldFiles.TryGetValue(path, out var old);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return new IndexedPath(folder, path, old, Unavailable, source);
                var font = old?.Length == info.Length && old.Modified == info.LastWriteTimeUtc.Ticks
                    ? old : FontFile.Load(info);
                oldFiles[path] = font;
                return new IndexedPath(folder, path, font, null, source);
            }
            catch (Exception error) when (error is FileFormatException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                string issue = error is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
                    ? Unavailable : "Ошибка чтения файла";
                return new IndexedPath(folder, path, old, issue, source);
            }
        }

        foreach (var link in links.Where(link => FontFile.Supports(link.Path)))
            cache.Files.Add(Index(link.Folder, link.Path, null));

        var previousFiles = previous.Files.Where(file => file.Source != null).ToLookup(file => file.Source!);
        var previousFolders = previous.Folders.ToLookup(folder => folder.Source);
        foreach (var spec in folders.Where(folder => folder.Source != null))
        {
            token.ThrowIfCancellationRequested();
            if (changedSources != null && !changedSources.Contains(spec.Id) && previousFolders.Contains(spec.Id))
            {
                cache.Files.AddRange(previousFiles[spec.Id].Where(file => FontFile.Supports(file.Path)));
                cache.Folders.AddRange(previousFolders[spec.Id]);
                continue;
            }

            string root = spec.Source!;
            var pending = new Stack<(string Path, string Id, string Parent, string Name)>();
            pending.Push((root, spec.Id, spec.Parent ?? "", spec.Name));
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new DirectoryInfo(directory.Path);
                    var children = info.GetFileSystemInfos();
                    cache.Folders.Add(new DiskFolder(directory.Id, directory.Parent, directory.Name, directory.Path, spec.Id, false));
                    foreach (var child in children)
                    {
                        token.ThrowIfCancellationRequested();
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (child is DirectoryInfo)
                            pending.Push((child.FullName, $"{spec.Id}|{child.FullName}", directory.Id, child.Name));
                        else if (FontFile.Supports(child.FullName))
                            cache.Files.Add(Index(directory.Id, child.FullName, spec.Id));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    string prefix = Path.TrimEndingDirectorySeparator(directory.Path) + Path.DirectorySeparatorChar;
                    cache.Folders.RemoveAll(folder => folder.Id == directory.Id);
                    cache.Folders.Add(new DiskFolder(directory.Id, directory.Parent, directory.Name, directory.Path, spec.Id, true));
                    cache.Folders.AddRange(previousFolders[spec.Id]
                        .Where(folder => folder.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(folder => folder with { Unavailable = true }));
                    var knownPaths = cache.Files.Where(file => file.Source == spec.Id)
                        .Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    cache.Files.AddRange(previousFiles[spec.Id]
                        .Where(file => file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !knownPaths.Contains(file.Path))
                        .Select(file => file with { Issue = Unavailable }));
                }
            }
        }
        return new FontCatalog(cache, folders, hiddenFonts, hiddenFolders, aliases);
    }
}
