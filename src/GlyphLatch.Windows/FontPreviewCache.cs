using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace GlyphLatch.Windows;

internal sealed class FontPreviewCache(string directory)
{
    private readonly LinkedList<(string Hash, GlyphTypeface Face)> recent = [];
    private readonly Dictionary<string, Task<GlyphTypeface?>> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim disk = new(1, 1);

    // Call on the window dispatcher. Disk work stays off the dispatcher.
    internal Task<GlyphTypeface?> GetAsync(FontEntry entry)
    {
        if (entry is not { Available: true, Font: { } font }) return Task.FromResult<GlyphTypeface?>(null);
        for (var node = recent.First; node != null; node = node.Next)
        {
            if (node.Value.Hash != font.Hash) continue;
            recent.Remove(node);
            recent.AddFirst(node);
            return Task.FromResult<GlyphTypeface?>(node.Value.Face);
        }
        if (pending.TryGetValue(font.Hash, out var load)) return load;
        var sources = new[] { font with { Path = entry.Path } }.Concat(entry.Locations
                .Where(location => location.Issue == null && location.Font?.Hash == font.Hash)
                .Select(location => location.Font! with { Path = location.Path }))
            .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        load = Load(sources);
        pending.Add(font.Hash, load);
        return load;
    }

    private async Task<GlyphTypeface?> Load(FontFile[] sources)
    {
        await Task.Yield();
        try
        {
            await disk.WaitAsync();
            try
            {
                var pinned = recent.Select(item => item.Hash).ToHashSet(StringComparer.Ordinal);
                var face = await Task.Run(() => ReadFace(sources, pinned));
                if (face != null)
                {
                    recent.AddFirst((sources[0].Hash, face));
                    if (recent.Count > 32) recent.RemoveLast();
                }
                return face;
            }
            finally { disk.Release(); }
        }
        catch (Exception error) when (IsFontError(error))
        {
            return null;
        }
        finally { pending.Remove(sources[0].Hash); }
    }

    private GlyphTypeface? ReadFace(FontFile[] sources, HashSet<string> pinned)
    {
        foreach (var source in sources)
        {
            try { return Open(Copy(source, pinned)); }
            catch (Exception error) when (IsFontError(error)) { }

            try
            {
                var original = new FileInfo(source.Path);
                if (original.Exists && original.Length == source.Length && original.LastWriteTimeUtc.Ticks == source.Modified)
                    return Open(source.Path);
            }
            catch (Exception error) when (IsFontError(error)) { }
        }
        return null;
    }

    private static GlyphTypeface Open(string path)
    {
        var face = new GlyphTypeface(new Uri(path));
        _ = face.CharacterToGlyphMap.Count;
        return face;
    }

    private string Copy(FontFile font, HashSet<string> pinned)
    {
        Directory.CreateDirectory(directory);
        // A separate directory per face avoids stale WPF font-directory indexes.
        string parent = Path.Combine(directory, font.Hash);
        string target = Path.Combine(parent, "specimen" + Path.GetExtension(font.Path).ToLowerInvariant());
        if (File.Exists(target))
        {
            if (new FileInfo(target).Length != font.Length) throw new IOException("Копия шрифта в кеше повреждена.");
            return target;
        }
        const long limit = 192L * 1024 * 1024;
        var copies = new DirectoryInfo(directory).EnumerateDirectories()
            .Where(folder => folder.Name.Length == 64 && folder.Name.All(Uri.IsHexDigit) &&
                (folder.Attributes & FileAttributes.ReparsePoint) == 0)
            .SelectMany(folder => folder.EnumerateFiles("specimen.*")).Where(file => FontFile.Supports(file.Name))
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        int count = copies.Length;
        long used = copies.Sum(file => file.Length);
        foreach (var copy in copies)
        {
            if (count < 128 && used + font.Length <= limit) break;
            var folder = copy.Directory!;
            if (pinned.Contains(folder.Name)) continue;
            try
            {
                long length = copy.Length;
                copy.Delete();
                count--;
                used -= length;
                folder.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (count >= 128 || used + font.Length > limit) throw new IOException("Хранилище образцов заполнено.");

        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = File.OpenRead(font.Path))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                source.CopyTo(output);
                output.Position = 0;
                if (Convert.ToHexString(SHA256.HashData(output)) != font.Hash)
                    throw new IOException("Исходный файл изменился.");
            }
            File.Move(temporary, target);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsFontError(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or FormatException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException or
        NotSupportedException or System.Runtime.InteropServices.COMException;
}
