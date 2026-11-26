using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GlyphLatch.Windows;

internal sealed record FontFile(string Path, string Hash, string Family, string Face, string Format, bool Variable,
    bool Cyrillic, string[] Families, string[] FullNames, string[] PostScriptNames, long Length, long Modified)
{
    private const string Russian = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private const uint NameTag = 0x6E616D65;
    private const uint CmapTag = 0x636D6170;
    private const uint FvarTag = 0x66766172;

    internal static bool Supports(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf";

    internal static FontFile Load(FileInfo source)
    {
        using var stream = source.OpenRead();
        var tables = ReadDirectory(stream);
        var names = ReadNameRecords(ReadTable(stream, tables, NameTag));
        string family = Preferred(names, 16, 1) ?? System.IO.Path.GetFileNameWithoutExtension(source.Name);
        string face = Preferred(names, 17, 2) ?? "Regular";
        string[] families = names.Where(name => name.Id is 1 or 16 or 21).Select(name => name.Text)
            .Append(family).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool cyrillic = CoversRussian(ReadTable(stream, tables, CmapTag));
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        return new FontFile(source.FullName, hash, family, face, source.Extension[1..].ToUpperInvariant(),
            tables.ContainsKey(FvarTag), cyrillic, families, Values(names, 4), Values(names, 6),
            source.Length, source.LastWriteTimeUtc.Ticks);
    }

    internal static (string[] Full, string[] PostScript) DecodeNames(ReadOnlySpan<byte> table)
    {
        var names = ReadNameRecords(table);
        return (Values(names, 4), Values(names, 6));
    }

    private sealed record TypefaceName(int Id, int Platform, int Language, string Text);

    private static List<TypefaceName> ReadNameRecords(ReadOnlySpan<byte> table)
    {
        if (table.Length < 6) throw new FileFormatException("Таблица имён повреждена.");
        int count = U16(table, 2);
        int strings = U16(table, 4);
        if (strings < 6 + count * 12 || strings > table.Length) throw new FileFormatException("Таблица имён повреждена.");
        var names = new List<TypefaceName>();
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> record = table.Slice(6 + index * 12, 12);
            int platform = U16(record, 0);
            int id = U16(record, 6);
            if (id is not (1 or 2 or 4 or 6 or 16 or 17 or 21) || platform is not (0 or 1 or 3)) continue;
            int size = U16(record, 8);
            int start = strings + U16(record, 10);
            if (start + size > table.Length || (platform != 1 && size % 2 != 0)) continue;
            ReadOnlySpan<byte> value = table.Slice(start, size);
            string text = (platform == 1 ? Encoding.Latin1 : Encoding.BigEndianUnicode).GetString(value).Trim('\0', ' ');
            if (text.Length > 0) names.Add(new TypefaceName(id, platform, U16(record, 4), text));
        }
        return names;
    }

    private static string[] Values(List<TypefaceName> names, int id) => names
        .Where(name => name.Id == id).Select(name => name.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? Preferred(List<TypefaceName> names, int preferred, int fallback)
    {
        int locale = CultureInfo.CurrentUICulture.LCID;
        return names.Where(name => name.Id == preferred || name.Id == fallback)
            .OrderBy(name => name.Id != preferred)
            .ThenBy(name => (name.Platform, name.Language) switch
            {
                (3, _) when name.Language == locale => 0,
                (3, 0x0409) => 1,
                (0, _) => 2,
                (3, _) => 3,
                _ => 4
            })
            .FirstOrDefault()?.Text;
    }

    private static Dictionary<uint, (long Offset, uint Size)> ReadDirectory(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (U32(header, 0) is not (0x00010000 or 0x4F54544F or 0x74727565))
            throw new FileFormatException("Ожидается один шрифт OpenType или TrueType.");
        int count = U16(header, 4);
        if (count == 0 || 12L + count * 16 > stream.Length) throw new FileFormatException("Список таблиц повреждён.");
        Span<byte> entry = stackalloc byte[16];
        var tables = new Dictionary<uint, (long, uint)>();
        for (int index = 0; index < count; index++)
        {
            stream.ReadExactly(entry);
            tables.TryAdd(U32(entry, 0), (U32(entry, 8), U32(entry, 12)));
        }
        return tables;
    }

    private static byte[] ReadTable(Stream stream, Dictionary<uint, (long Offset, uint Size)> tables, uint tag)
    {
        if (!tables.TryGetValue(tag, out var table) || table.Size is 0 or > 4_000_000 ||
            table.Offset + table.Size > stream.Length)
            throw new FileFormatException("Обязательная таблица шрифта недоступна.");
        stream.Position = table.Offset;
        byte[] buffer = new byte[table.Size];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static bool CoversRussian(ReadOnlySpan<byte> table)
    {
        if (table.Length < 4 || 4 + U16(table, 2) * 8 > table.Length) return false;
        var subtables = new HashSet<int>();
        for (int index = 0; index < U16(table, 2); index++)
        {
            int record = 4 + index * 8;
            int platform = U16(table, record);
            int encoding = U16(table, record + 2);
            uint offset = U32(table, record + 4);
            if ((platform == 0 || platform == 3 && encoding is 1 or 10) && offset <= table.Length - 2)
                subtables.Add((int)offset);
        }
        foreach (char character in Russian)
        {
            bool exists = false;
            foreach (int offset in subtables)
            {
                var subtable = table[offset..];
                exists |= U16(subtable, 0) switch
                {
                    4 => MapBmp(subtable, character),
                    12 => MapGroups(subtable, character, constant: false),
                    13 => MapGroups(subtable, character, constant: true),
                    _ => false
                };
                if (exists) break;
            }
            if (!exists) return false;
        }
        return true;
    }

    private static bool MapBmp(ReadOnlySpan<byte> table, char character)
    {
        if (table.Length < 16) return false;
        int length = U16(table, 2);
        int segments = U16(table, 6) / 2;
        if (length > table.Length || length < 16 + segments * 8) return false;
        int startCodes = 16 + segments * 2;
        int deltas = startCodes + segments * 2;
        int offsets = deltas + segments * 2;
        for (int index = 0; index < segments; index++)
        {
            int entry = index * 2;
            if (character > U16(table, 14 + entry)) continue;
            int start = U16(table, startCodes + entry);
            if (character < start) return false;
            int delta = U16(table, deltas + entry);
            int range = U16(table, offsets + entry);
            if (range == 0) return (ushort)(character + delta) != 0;
            int address = offsets + entry + range + (character - start) * 2;
            return address <= length - 2 && U16(table, address) is var glyph && glyph != 0 && (ushort)(glyph + delta) != 0;
        }
        return false;
    }

    private static bool MapGroups(ReadOnlySpan<byte> table, char character, bool constant)
    {
        if (table.Length < 16) return false;
        uint length = U32(table, 4);
        uint count = U32(table, 12);
        if (length > table.Length || 16L + count * 12L > length) return false;
        for (int index = 0; index < count; index++)
        {
            var group = table.Slice(16 + index * 12, 12);
            uint start = U32(group, 0);
            if (character < start) return false;
            if (character <= U32(group, 4)) return U32(group, 8) + (constant ? 0 : character - start) != 0;
        }
        return false;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
}
