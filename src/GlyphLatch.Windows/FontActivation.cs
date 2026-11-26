using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using GlyphLatch.Win32;
using Microsoft.Data.Sqlite;

namespace GlyphLatch.Windows;

internal enum FontState { Off, On, Installed, Unavailable, Conflict, Error }
internal sealed record ActivationStatus(FontState State, string Detail)
{
    internal bool Failed => State is FontState.Unavailable or FontState.Conflict or FontState.Error;
}

internal sealed class FontActivation(LibraryStore store)
{
    private readonly Dictionary<string, FontFile> registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActivationStatus> results = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> manual = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> setFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int session = GetSessionId();
    private readonly long boot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Environment.TickCount64 / 1000;

    internal string? SelectedSet { get; private set; }
    internal int ActiveCount => registered.Count;
    internal IEnumerable<FontFile> ActiveFiles => registered.Values;
    internal bool HasUnresolved => results.Values.Any(result => result.Failed);
    internal bool IsActive(string id) => registered.ContainsKey(id);
    internal bool IsHeldBySet(string id) => setFonts.Contains(id);

    internal ActivationStatus GetStatus(FontEntry entry)
    {
        if (results.TryGetValue(entry.Id, out var status)) return status;
        if (entry.Available) return new(FontState.Off, "");
        return new(entry.Issue == FontCatalog.Unavailable ? FontState.Unavailable : FontState.Error, entry.Issue ?? "");
    }

    internal string? ReadRecovery()
    {
        int uncertain = 0;
        foreach (var row in store.ReadRegistrations())
        {
            if (row.State == "removed" || row.Session != session ||
                !long.TryParse(row.Boot, CultureInfo.InvariantCulture, out long previous) || Math.Abs(boot - previous) > 120)
            {
                store.ForgetRegistration(row.Path);
                continue;
            }

            // A surviving family name does not prove that a resource at this path belongs to us.
            try
            {
                var file = FontFile.Load(new FileInfo(row.Path));
                if (file.Hash == row.Identity && !file.Families.Any(ExistsInWindows))
                {
                    store.ForgetRegistration(row.Path);
                    continue;
                }
            }
            catch (Exception error) when (IsFontError(error))
            {
                Trace.TraceWarning(error.Message);
            }
            uncertain++;
        }
        return uncertain == 0 ? null :
            $"После аварийного выхода осталось записей для проверки: {uncertain}. Выйдите из сеанса Windows, если шрифты по-прежнему видны.";
    }

    internal string SetManual(string[] ids, bool enabled, IReadOnlyDictionary<string, FontEntry> entries)
    {
        if (enabled) manual.UnionWith(ids);
        else manual.ExceptWith(ids);
        return Reconcile(ids, entries);
    }

    internal string SelectSet(SetSpec? set, IReadOnlyDictionary<string, FontEntry> entries)
    {
        var ids = setFonts.Concat(set?.Fonts ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SelectedSet = set?.Id;
        setFonts = new HashSet<string>(set?.Fonts ?? [], StringComparer.OrdinalIgnoreCase);
        return Reconcile(ids, entries);
    }

    internal string ForgetFonts(string[] ids, IReadOnlyDictionary<string, FontEntry> entries)
    {
        manual.ExceptWith(ids);
        setFonts.ExceptWith(ids);
        return Reconcile(ids, entries);
    }

    internal string DisableAll(IReadOnlyDictionary<string, FontEntry> entries)
    {
        string[] ids = registered.Keys.Concat(manual).Concat(setFonts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        manual.Clear();
        setFonts.Clear();
        SelectedSet = null;
        return Reconcile(ids, entries);
    }

    internal string Retry(IReadOnlyDictionary<string, FontEntry> entries)
    {
        return Reconcile(results.Where(pair => pair.Value.Failed).Select(pair => pair.Key).ToArray(), entries);
    }

    private string Reconcile(string[] ids, IReadOnlyDictionary<string, FontEntry> entries)
    {
        bool changed = false;
        int held = 0;
        foreach (string id in ids)
        {
            try
            {
                if (manual.Contains(id) || setFonts.Contains(id))
                {
                    if (registered.ContainsKey(id))
                    {
                        results[id] = new(FontState.On, setFonts.Contains(id) ? "Начертание удерживает активный набор." : "");
                        if (setFonts.Contains(id)) held++;
                    }
                    else
                    {
                        entries.TryGetValue(id, out var entry);
                        results[id] = Add(id, entry, ref changed);
                    }
                }
                else if (registered.TryGetValue(id, out var font))
                {
                    if (NativeFonts.RemoveFontResource(font.Path, 0, IntPtr.Zero))
                    {
                        registered.Remove(id);
                        changed = true;
                        StoreResult(font, "removed");
                        results.Remove(id);
                    }
                    else
                    {
                        results[id] = new(FontState.Error, "Windows не сняла временную регистрацию. Повторите выключение.");
                    }
                }
                else
                {
                    results.Remove(id);
                }
            }
            catch (Exception error) when (IsFontError(error) || error is SqliteException or Win32Exception)
            {
                results[id] = new(FontState.Error, error.Message);
            }
        }

        int failures = ids.Count(id => results.TryGetValue(id, out var status) && status.Failed);
        string detail = failures > 0 ? $"Обработано {ids.Length - failures} из {ids.Length}. Ошибок: {failures}." : "";
        if (held > 0) detail += $" Активный набор удерживает {held}.";
        if (changed && !NativeFonts.NotifyFontChange())
        {
            store.Diagnostic($"WM_FONTCHANGE failed for session {session}.");
            detail += " Windows не подтвердила уведомление редакторов.";
        }
        return detail.Trim();
    }

    private ActivationStatus Add(string id, FontEntry? entry, ref bool changed)
    {
        if (entry is not { Available: true, Font: not null })
            return new(FontState.Unavailable, entry?.Issue ?? "Файл не найден в библиотеке.");

        foreach (var location in entry.Locations.Where(location => location.Issue == null))
        {
            if (!File.Exists(location.Path)) continue;
            FileStream source;
            try { source = File.OpenRead(location.Path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            using var lockedSource = source;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(source)), id, StringComparison.Ordinal)) continue;
            FontFile font = entry.Font with { Path = location.Path };
            ActivationStatus? collision = CheckIdentity(font);
            if (collision != null) return collision;

            store.LogRegistration(font.Path, BootKey, session, font.Hash, "pending");
            if (NativeFonts.AddFontResource(font.Path, 0, IntPtr.Zero) == 0)
            {
                StoreResult(font, "removed");
                return new(FontState.Error, "Windows не приняла файл шрифта.");
            }

            registered[id] = font;
            changed = true;
            StoreResult(font, "registered");
            return new(FontState.On, setFonts.Contains(id) ? "Начертание удерживает активный набор." : "При необходимости переоткройте список шрифтов в редакторе.");
        }
        return new(FontState.Unavailable, "Исходный файл перемещён или изменился. Обновите библиотеку.");
    }

    private ActivationStatus? CheckIdentity(FontFile font)
    {
        if (font.FullNames.Length == 0 || font.PostScriptNames.Length == 0)
            return new(FontState.Conflict, "Файл не содержит полного имени и имени PostScript для проверки.");
        if (registered.Values.Any(other => Matches(font, other.FullNames, other.PostScriptNames)))
            return new(FontState.Conflict, "Начертание с таким именем уже включено из другого файла.");

        bool gdiMatch = false;
        bool uncertain = false;
        foreach (string family in font.Families)
        {
            foreach (var table in NativeFonts.ReadGdiNameTables(family))
            {
                if (table == null)
                {
                    uncertain = true;
                    continue;
                }
                var names = FontFile.DecodeNames(table);
                gdiMatch |= Matches(font, names.Full, names.PostScript);
            }
        }

        bool directWrite = font.Families.Any(NativeFonts.ExistsInDirectWrite);
        if (directWrite)
        {
            foreach (var family in Fonts.SystemFontFamilies.Where(family =>
                         family.FamilyNames.Values.Intersect(font.Families, StringComparer.OrdinalIgnoreCase).Any()))
            {
                foreach (var typeface in family.GetTypefaces())
                {
                    if (!typeface.TryGetGlyphTypeface(out var glyphs) || !glyphs.FontUri.IsFile) continue;
                    if (string.Equals(glyphs.FontUri.LocalPath, font.Path, StringComparison.OrdinalIgnoreCase))
                        return new(FontState.Installed, "Этот файл уже доступен Windows.");
                    if (gdiMatch) break;
                }
            }
        }

        if (gdiMatch || uncertain || directWrite)
            return new(FontState.Conflict, "Windows уже видит одноимённое начертание или семейство. Источник может отличаться.");

        return null;
    }

    private static bool ExistsInWindows(string family) =>
        (family.Length < 32 && NativeFonts.ExistsInGdi(family)) || NativeFonts.ExistsInDirectWrite(family);

    private static bool Matches(FontFile font, string[] full, string[] postScript) =>
        font.FullNames.Intersect(full, StringComparer.OrdinalIgnoreCase).Any() ||
        font.PostScriptNames.Intersect(postScript, StringComparer.OrdinalIgnoreCase).Any();

    private string BootKey => boot.ToString(CultureInfo.InvariantCulture);

    private void StoreResult(FontFile font, string state)
    {
        try
        {
            store.LogRegistration(font.Path, BootKey, session, font.Hash, state);
        }
        catch (SqliteException error)
        {
            store.Diagnostic($"Registration result for {font.Path}: {state} ({error.Message})");
        }
    }

    private static int GetSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static bool IsFontError(Exception error) => error is IOException or UnauthorizedAccessException or
        FileFormatException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException;
}
