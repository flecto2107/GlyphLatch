using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlyphLatch.Win32;

namespace GlyphLatch.Probe;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length is 2 or 3 && args[0] == "query")
                return Query(args[1], args.Length == 3 ? args[2] : args[1]);

            if (args.Length is 2 or 3 && args[0] == "session")
                return RunSession(args[1], args.Length == 3 ? args[2] : null);

            Console.Error.WriteLine("Использование: session <путь> [файл-маркер] | query <семейство GDI> [семейство DirectWrite]");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static int Query(string gdiFamily, string directWriteFamily)
    {
        bool failed = false;
        try
        {
            Console.WriteLine($"GDI={NativeFonts.ExistsInGdi(gdiFamily)}");
        }
        catch (Exception error)
        {
            Console.WriteLine($"GDI=Error {error.Message}");
            failed = true;
        }

        try
        {
            Console.WriteLine($"DirectWrite={NativeFonts.ExistsInDirectWrite(directWriteFamily)}");
        }
        catch (Exception error)
        {
            Console.WriteLine($"DirectWrite=Error {error.Message}");
            failed = true;
        }

        return failed ? 1 : 0;
    }

    private static int RunSession(string file, string marker)
    {
        string path = Path.GetFullPath(file);
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл шрифта недоступен.", path);
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Укажите тестовый файл TTF или OTF.");

        string eventMarker = marker == null ? null : Path.GetFullPath(marker);
        var registration = new Registration(path, eventMarker);
        ConsoleCancelEventHandler cancel = (_, _) => registration.Close();
        Console.CancelKeyPress += cancel;
        try
        {
            using Process process = Process.GetCurrentProcess();
            Console.WriteLine($"READY pid={process.Id} session={process.SessionId}");
            Console.WriteLine("Команды: on, off, status, quit");
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                switch (line.Trim().ToLowerInvariant())
                {
                    case "on":
                        registration.Enable();
                        break;
                    case "off":
                        registration.Disable();
                        break;
                    case "status":
                        registration.Status();
                        break;
                    case "quit":
                        return registration.Close() ? 0 : 1;
                    default:
                        Console.WriteLine("ERROR command=unknown");
                        break;
                }
            }

            return registration.Close() ? 0 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            registration.Close();
        }
    }

    private sealed class Registration(string path, string eventMarker)
    {
        private readonly object gate = new();
        private bool enabled;
        private bool closed;

        public void Enable()
        {
            lock (gate)
            {
                if (closed || enabled)
                {
                    Console.WriteLine($"ON changed=False active={enabled}");
                    return;
                }

                int count = NativeFonts.AddFontResource(path, 0, IntPtr.Zero);
                if (count == 0)
                {
                    Console.WriteLine("ON changed=False active=False count=0");
                    return;
                }

                enabled = true;
                DateTimeOffset registeredAt = DateTimeOffset.UtcNow;
                Console.WriteLine($"ON changed=True active=True count={count} utc={registeredAt:O}");
                try
                {
                    if (eventMarker != null)
                        File.WriteAllText(eventMarker, registeredAt.ToString("O"));
                }
                catch (IOException error)
                {
                    Console.WriteLine($"MARKER error={error.Message}");
                }
                catch (UnauthorizedAccessException error)
                {
                    Console.WriteLine($"MARKER error={error.Message}");
                }
                Console.WriteLine($"WM_FONTCHANGE success={NativeFonts.NotifyFontChange()}");
            }
        }

        public void Disable()
        {
            lock (gate)
                Remove("OFF");
        }

        public void Status()
        {
            lock (gate)
                Console.WriteLine($"STATUS active={enabled}");
        }

        public bool Close()
        {
            lock (gate)
            {
                if (!closed)
                {
                    closed = true;
                    Remove("EXIT");
                }
                return !enabled;
            }
        }

        private void Remove(string action)
        {
            if (!enabled)
            {
                Console.WriteLine($"{action} changed=False active=False");
                return;
            }

            if (!NativeFonts.RemoveFontResource(path, 0, IntPtr.Zero))
            {
                Console.WriteLine($"{action} changed=False active=True error=remove_failed");
                return;
            }

            enabled = false;
            Console.WriteLine($"{action} changed=True active=False utc={DateTimeOffset.UtcNow:O}");
            Console.WriteLine($"WM_FONTCHANGE success={NativeFonts.NotifyFontChange()}");
        }
    }
}
