using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GlyphLatch.Windows;

internal sealed class TrayMenuColors(bool dark) : Forms.ProfessionalColorTable
{
    private Color Surface => dark ? Color.FromArgb(28, 28, 28) : Color.White;
    internal Color Hover => dark ? Color.FromArgb(65, 65, 65) : Color.FromArgb(226, 226, 226);
    public Color Text => dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(17, 17, 17);
    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemBorder => Hover;
    public override Color MenuBorder => Hover;
    public override Color CheckBackground => Hover;
}

internal sealed class TrayMenuRenderer(TrayMenuColors colors) : Forms.ToolStripProfessionalRenderer(colors)
{
    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !e.Item.Selected && !e.Item.Pressed) return;
        using var fill = new SolidBrush(colors.Hover);
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        bounds.Inflate(-2, -1);
        if (bounds.Width > 0 && bounds.Height > 0) e.Graphics.FillRectangle(fill, bounds);
    }
}

internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private const int MouseMove = 0x0200;
    private const int ContextMenu = 0x007B;
    private const int Select = 0x0400;
    private const int KeySelect = 0x0401;
    private readonly HwndSource source;
    private readonly Icon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly DispatcherTimer timer;
    private readonly Action<Forms.ContextMenuStrip> populate;
    private readonly Action open;
    private readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyData data;
    private long hoverStarted;
    private long leftAt;
    private bool hoverMenu;
    private bool waitForLeave;

    internal TrayIcon(HwndSource source, Icon icon, Action<Forms.ContextMenuStrip> populate, Action open)
    {
        this.source = source;
        this.icon = icon;
        this.populate = populate;
        this.open = open;
        data = new NotifyData
        {
            Size = (uint)Marshal.SizeOf<NotifyData>(), Window = source.Handle, Id = 1,
            Flags = 0x01 | 0x02 | 0x04, Message = CallbackMessage, Icon = icon.Handle,
            Tip = "GlyphLatch", Info = "", InfoTitle = ""
        };
        timer = new DispatcherTimer(DispatcherPriority.Background, source.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => CheckPointer(Environment.TickCount64, Forms.Cursor.Position);
        menu.Closed += (_, _) =>
        {
            if (hoverMenu)
            {
                hoverMenu = false;
                waitForLeave = true;
                timer.Start();
            }
        };
        source.AddHook(WindowMessage);
        Register();
    }

    internal bool IsRegistered { get; private set; }

    private void Register()
    {
        IsRegistered = ShellNotifyIcon(0, ref data);
        if (!IsRegistered) return;
        data.Version = 4;
        if (!ShellNotifyIcon(4, ref data))
        {
            ShellNotifyIcon(2, ref data);
            IsRegistered = false;
        }
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == taskbarCreated)
        {
            Register();
            return IntPtr.Zero;
        }
        if (message != CallbackMessage) return IntPtr.Zero;
        handled = true;
        switch ((int)(lparam.ToInt64() & 0xffff))
        {
            case MouseMove:
                if (!timer.IsEnabled && !menu.Visible)
                {
                    hoverStarted = Environment.TickCount64;
                    timer.Start();
                }
                break;
            case ContextMenu:
                ShowMenu(fromHover: false);
                break;
            case Select:
            case KeySelect:
                menu.Close();
                open();
                break;
        }
        return IntPtr.Zero;
    }

    private void CheckPointer(long now, Point cursor)
    {
        bool overIcon = IconBounds().Contains(cursor);
        if (hoverMenu && menu.Visible)
        {
            if (overIcon || IsOverMenu(menu, cursor))
            {
                leftAt = 0;
                return;
            }
            if (leftAt == 0) leftAt = now;
            if (now - leftAt >= 320) menu.Close();
            return;
        }

        if (!overIcon)
        {
            waitForLeave = false;
            timer.Stop();
            return;
        }
        if (!waitForLeave && now - hoverStarted >= 420) ShowMenu(fromHover: true);
    }

    private void ShowMenu(bool fromHover)
    {
        if (menu.Visible)
        {
            if (!fromHover)
            {
                hoverMenu = false;
                timer.Stop();
            }
            return;
        }

        var bounds = IconBounds();
        if (fromHover && bounds.IsEmpty)
        {
            timer.Stop();
            return;
        }
        populate(menu);
        Point cursor = Forms.Cursor.Position;
        Rectangle workArea = Forms.Screen.FromPoint(bounds.IsEmpty ? cursor : bounds.Location).WorkingArea;
        Size size = menu.GetPreferredSize(Size.Empty);
        int x = bounds.IsEmpty ? cursor.X : bounds.Right - size.Width;
        int y = bounds.IsEmpty ? cursor.Y : bounds.Top - size.Height;
        if (y < workArea.Top && !bounds.IsEmpty) y = bounds.Bottom;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        leftAt = 0;
        hoverMenu = fromHover;
        if (!fromHover) timer.Stop();
        menu.Show(new Point(x, y));
    }

    private Rectangle IconBounds()
    {
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = data.Window, Id = data.Id };
        return ShellNotifyIconGetRect(ref identifier, out var rect) == 0
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : Rectangle.Empty;
    }

    private static bool IsOverMenu(Forms.ToolStripDropDown popup, Point cursor)
    {
        if (popup.Bounds.Contains(cursor)) return true;
        foreach (var item in popup.Items)
            if (item is Forms.ToolStripDropDownItem { HasDropDownItems: true } submenu &&
                submenu.DropDown.Visible && IsOverMenu(submenu.DropDown, cursor)) return true;
        return false;
    }

    public void Dispose()
    {
        timer.Stop();
        hoverMenu = false;
        menu.Dispose();
        source.RemoveHook(WindowMessage);
        if (IsRegistered) ShellNotifyIcon(2, ref data);
        icon.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Message;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyData data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect", ExactSpelling = true)]
    private static extern int ShellNotifyIconGetRect(ref IconIdentifier identifier, out NativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterWindowMessage(string name);
}
