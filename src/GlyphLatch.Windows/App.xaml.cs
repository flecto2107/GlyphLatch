using System;
using System.Threading;
using System.Windows;

namespace GlyphLatch.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private RegisteredWaitHandle? showListener;
    private const string ShowSignalName = @"Local\GlyphLatch.ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mutex = new Mutex(true, @"Local\GlyphLatch.Windows", out bool created);
        if (!created)
        {
            mutex.Dispose();
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                using (signal) signal.Set();
            }
            else
            {
                MessageBox.Show("Предыдущая версия GlyphLatch работает в трее. Чтобы перейти на эту сборку, выберите «Выйти» в меню старой версии.",
                    "GlyphLatch", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        instance = mutex;
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            showListener = ThreadPool.RegisterWaitForSingleObject(showSignal,
                (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Не удалось открыть GlyphLatch. {error.Message}", "GlyphLatch",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showListener?.Unregister(null);
        showSignal?.Dispose();
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
