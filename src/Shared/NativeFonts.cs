using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GlyphLatch.Win32;

internal static class NativeFonts
{
    internal static bool ExistsInGdi(string family)
    {
        if (family.Length is 0 or >= 32)
            throw new ArgumentException("Имя семейства GDI должно содержать от 1 до 31 символа.");

        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            throw new Win32Exception();

        try
        {
            var filter = new LogFont { CharacterSet = 1, FaceName = family };
            bool found = false;
            EnumFontFamiliesEx(dc, ref filter, (font, _, _, _) =>
            {
                string name = Marshal.PtrToStructure<LogFont>(font).FaceName;
                found = string.Equals(name, family, StringComparison.OrdinalIgnoreCase);
                return found ? 0 : 1;
            }, IntPtr.Zero, 0);
            return found;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    internal static bool ExistsInDirectWrite(string family)
    {
        Guid factoryId = typeof(IDWriteFactory).GUID;
        Marshal.ThrowExceptionForHR(DWriteCreateFactory(1, ref factoryId, out IDWriteFactory factory));
        try
        {
            Marshal.ThrowExceptionForHR(factory.GetSystemFontCollection(out IDWriteFontCollection fonts, true));
            try
            {
                Marshal.ThrowExceptionForHR(fonts.FindFamilyName(family, out _, out bool exists));
                return exists;
            }
            finally
            {
                Marshal.ReleaseComObject(fonts);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    internal static IReadOnlyList<byte[]?> ReadGdiNameTables(string family)
    {
        var tables = new List<byte[]?>();
        if (family.Length is 0 or >= 32) return tables;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var seen = new HashSet<(string, int, byte)>();
            var filter = new LogFont { CharacterSet = 1, FaceName = family };
            EnumFontFamiliesEx(dc, ref filter, (pointer, _, _, _) =>
            {
                LogFont font = Marshal.PtrToStructure<LogFont>(pointer);
                if (string.Equals(font.FaceName, family, StringComparison.OrdinalIgnoreCase) &&
                    seen.Add((font.FaceName, font.Weight, font.Italic)))
                    tables.Add(ReadNameTable(dc, ref font));
                return 1;
            }, IntPtr.Zero, 0);
            return tables;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    private static byte[]? ReadNameTable(IntPtr dc, ref LogFont font)
    {
        IntPtr handle = CreateFontIndirect(ref font);
        if (handle == IntPtr.Zero) return null;
        IntPtr previous = SelectObject(dc, handle);
        try
        {
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
            const uint nameTag = 0x656D616E;
            uint size = GetFontData(dc, nameTag, 0, null, 0);
            if (size is 0 or > 4_000_000) return null;
            byte[] buffer = new byte[size];
            return GetFontData(dc, nameTag, 0, buffer, size) == size ? buffer : null;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            DeleteObject(handle);
        }
    }

    internal static bool NotifyFontChange()
    {
        const uint wmFontChange = 0x001D;
        const uint abortIfHung = 0x0002;
        IntPtr result = SendMessageTimeout(new IntPtr(0xffff), wmFontChange, IntPtr.Zero,
            IntPtr.Zero, abortIfHung, 500, out _);
        return result != IntPtr.Zero;
    }

    [DllImport("gdi32.dll", EntryPoint = "AddFontResourceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int AddFontResource(string path, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", EntryPoint = "RemoveFontResourceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern bool RemoveFontResource(string path, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", EntryPoint = "EnumFontFamiliesExW", ExactSpelling = true)]
    private static extern int EnumFontFamiliesEx(IntPtr dc, ref LogFont font,
        EnumFontCallback callback, IntPtr data, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontIndirectW", ExactSpelling = true)]
    private static extern IntPtr CreateFontIndirect(ref LogFont font);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern uint GetFontData(IntPtr dc, uint table, uint offset, [Out] byte[]? buffer, uint size);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IDWriteFactory factory);

    private delegate int EnumFontCallback(IntPtr font, IntPtr metrics, uint fontType, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig]
        int GetSystemFontCollection(out IDWriteFontCollection collection, [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport, Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig]
        uint GetFontFamilyCount();

        [PreserveSig]
        int GetFontFamily(uint index, out IntPtr family);

        [PreserveSig]
        int FindFamilyName([MarshalAs(UnmanagedType.LPWStr)] string familyName, out uint index,
            [MarshalAs(UnmanagedType.Bool)] out bool exists);
    }
}
