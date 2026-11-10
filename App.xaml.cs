using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CheatingTool;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppConfig _cfg = null!;
    private OverlayWindow _overlay = null!;
    private Hotkeys _hotkeys = null!;
    private TrayIcon _tray = null!;
    private Assistant _assistant = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, "CheatingTool-single-instance-9f31", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        Log.Init();
        Log.Write("--- старт ---");

        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write(args.ExceptionObject as Exception ?? new Exception("неизвестное исключение"));

        _cfg = AppConfig.Load();
        Log.Write("конфиг: " + _cfg.FilePath);
        Log.Write("источник: " + (_cfg.ApiKey.Length > 0 ? "API" : "Chrome"));

        _overlay = new OverlayWindow(_cfg);
        _assistant = new Assistant(_cfg, _overlay);

        _hotkeys = new Hotkeys();
        _hotkeys.Analyze += () => _ = _assistant.RunAsync();
        _hotkeys.ToggleOverlay += () => _overlay.Toggle();
        _hotkeys.CopyAnswer += () => _overlay.CopyLastToClipboard();
        _hotkeys.Exit += Shutdown;

        _tray = new TrayIcon(
            analyze: () => _ = _assistant.RunAsync(),
            toggle: () => _overlay.Toggle(),
            login: () => _assistant.OpenLogin(),
            importChrome: () => _ = _assistant.ImportChromeLoginAsync(),
            lastScreen: OpenLastScreen,
            settings: OpenSettings,
            restart: Restart,
            exit: Shutdown);

        if (Array.IndexOf(e.Args, "--demo") >= 0)
        {
            _overlay.ShowText("1 — B\n2 — 1850\n3 — Пётр I\n4 — 2/3\n5 — C\n6 — 12,5\n7 — D\n8 — Париж");
            return;
        }

        if (Array.IndexOf(e.Args, "--import-chrome") >= 0)
        {
            _ = ImportChromeAndExitAsync();
            return;
        }

        if (!_hotkeys.AnalyzeOk)
            _overlay.ShowError("F8 занята другой программой");

        if (_cfg.ShowStartupHint)
        {
            var hint = _assistant.BrowserNeedsImport
                ? "вход из Chrome: трей → «Перенести вход из Chrome…» (один раз)"
                : "F8 — анализ · Ctrl+Alt+H — скрыть";

            _overlay.ShowStatus(hint);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_overlay.IsHiddenByUser && _overlay.CurrentText == hint) _overlay.Hide();
            };
            timer.Start();
        }

        if (_assistant.CanPreload)
        {
            // Даём приложению спокойно стартовать и греем скрытый Chrome в фоне.
            var preloadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            preloadTimer.Tick += (_, _) =>
            {
                preloadTimer.Stop();
                _ = _assistant.PreloadAsync();
            };
            preloadTimer.Start();
        }
    }

    private async Task ImportChromeAndExitAsync()
    {
        bool ok = await _assistant.ImportChromeLoginAsync(interactive: false);
        Shutdown(ok ? 0 : 1);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)    {
        Log.Write(e.Exception);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Write(e.Exception);
        e.SetObserved();
    }

    private void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + _cfg.FilePath + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void OpenLastScreen()
    {
        try
        {
            var path = Assistant.LastScreenPath;
            if (!File.Exists(path))
            {
                _overlay.ShowStatus("последнего скриншота пока нет");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }
    }

    private void Restart()
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write(ex);
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hotkeys?.Dispose(); } catch { }
        try { _assistant?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        Log.Write("--- выход ---");
        base.OnExit(e);
    }
}
