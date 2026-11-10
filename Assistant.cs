using System;
using System.Threading;
using System.Threading.Tasks;

namespace CheatingTool;

internal sealed class Assistant : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly OverlayWindow _overlay;
    private readonly GeminiApi _api;
    private readonly GeminiBrowser _browser;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _preloadLock = new();

    private Task? _preloadTask;
    private volatile bool _preloading;

    public Assistant(AppConfig cfg, OverlayWindow overlay)
    {
        _cfg = cfg;
        _overlay = overlay;
        _api = new GeminiApi(cfg);
        _browser = new GeminiBrowser(cfg);
    }

    public async Task RunAsync()
    {
        // F8 во время предзагрузки не теряется: дожидаемся её и продолжаем.
        if (_preloading && _preloadTask is { } preload)
        {
            _overlay.ShowBusy("готовлю Gemini");
            try { await preload; } catch { }
        }

        if (!_gate.Wait(0)) return;

        try
        {
            _overlay.ShowBusy("анализирую");

            // Если окно Chrome программы осталось на экране — убираем его до снимка.
            await _browser.PrepareForCaptureAsync();

            byte[] jpeg;
            try
            {
                jpeg = ScreenCapture.CaptureJpeg(_cfg.Capture);
                Log.Write($"скриншот: {jpeg.Length / 1024} КБ");
                try { System.IO.File.WriteAllBytes(LastScreenPath, jpeg); } catch { }

                if (Env.Flag("DEBUG"))
                {
                    try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(Log.DirectoryPath, "dbg_screen.jpg"), jpeg); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                _overlay.ShowError("скриншот: " + ex.Message);
                return;
            }

            string? answer = null;
            Exception? lastError = null;

            if (_api.HasKey)
            {
                try
                {
                    var started = DateTime.UtcNow;
                    answer = await _api.AskAsync(jpeg, CancellationToken.None);
                    Log.Write($"API: {answer.Length} символов за {(DateTime.UtcNow - started).TotalSeconds:0.0} с");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    Log.Write("API: " + ex.Message);
                }
            }

            if (answer == null && _cfg.Browser.FallbackToBrowser)
            {
                try
                {
                    _overlay.ShowBusy("Gemini думает");
                    var started = DateTime.UtcNow;
                    await _browser.EnsureProfileReadyAsync();
                    answer = await _browser.AskAsync(jpeg, CancellationToken.None);
                    Log.Write($"браузер: {answer.Length} символов за {(DateTime.UtcNow - started).TotalSeconds:0.0} с");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    Log.Write("браузер: " + ex.Message);
                }
            }

            if (answer == null && !_cfg.Browser.FallbackToBrowser && !_api.HasKey)
                lastError = new InvalidOperationException("нет API-ключа, а Chrome отключён в настройках");

            if (answer != null)
                _overlay.ShowText(answer);
            else
                _overlay.ShowError(lastError?.Message ?? "нет доступного источника ответа");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Стоит ли греть Chrome заранее: он нужен и вход уже настроен.</summary>
    public bool CanPreload => _cfg.Browser.Preload
                              && _cfg.Browser.FallbackToBrowser
                              && !_api.HasKey
                              && !_browser.ProfileNeedsImport;

    /// <summary>Заранее поднимает скрытый Chrome и открывает Gemini, чтобы первый F8 был быстрым.</summary>
    public Task PreloadAsync()
    {
        if (!CanPreload) return Task.CompletedTask;

        lock (_preloadLock)
        {
            if (_preloadTask != null) return _preloadTask;

            _preloading = true;
            _preloadTask = Task.Run(async () =>
            {
                try
                {
                    Log.Write("preload: старт");
                    await _browser.PreloadAsync();
                }
                catch (Exception ex)
                {
                    Log.Write("preload: " + ex.Message);
                }
                finally
                {
                    _preloading = false;
                }
            });

            return _preloadTask;
        }
    }

    public void OpenLogin()
    {
        try
        {
            _browser.OpenLoginWindow();
            _overlay.ShowStatus("войди в Gemini в окне Chrome, потом F8");
        }
        catch (Exception ex)
        {
            _overlay.ShowError(ex.Message);
        }
    }

    public bool BrowserNeedsImport => _browser.ProfileNeedsImport;

    /// <summary>Снимок, который ушёл в последний запрос (для проверки «что видит Gemini»).</summary>
    public static string LastScreenPath => System.IO.Path.Combine(Log.DirectoryPath, "last-screen.jpg");

    /// <summary>Переносит вход из обычного Chrome. При необходимости предлагает закрыть Chrome.</summary>
    public async Task<bool> ImportChromeLoginAsync(bool interactive = true)
    {
        if (!_gate.Wait(0))
        {
            _overlay.ShowStatus("подожди, идёт анализ…");
            return false;
        }

        try
        {
            if (_preloadTask is { IsCompleted: false } preload) await preload;

            if (!_browser.HasChromeLogin)
            {
                var message = $"в профиле Chrome «{_browser.SourceProfileName}» нет входа в Google — войди в Chrome один раз";
                if (interactive) _overlay.ShowError(message);
                Log.Write("импорт: " + message);
                return false;
            }

            // Свой Chrome (профиль программы) тоже держит папку профиля.
            await _browser.CloseOwnBrowserAsync();

            if (_browser.ChromeHoldsProfile)
            {
                if (interactive)
                {
                    var choice = System.Windows.MessageBox.Show(
                        "Чтобы перенести вход, нужно на время закрыть Chrome.\n" +
                        "Вкладки восстановятся при следующем запуске.\n\nЗакрыть Chrome сейчас?",
                        "CheatingTool — перенос входа",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Question);

                    if (choice != System.Windows.MessageBoxResult.Yes)
                    {
                        _overlay.ShowStatus("перенос отменён");
                        return false;
                    }
                }
                else
                {
                    var message = "Chrome запущен — полностью закрой его и повтори перенос входа";
                    Log.Write("импорт: " + message);
                    return false;
                }

                await _browser.CloseOwnBrowserAsync();

                _overlay.ShowBusy("закрываю Chrome");
                GeminiBrowser.CloseChromeWindows();

                for (int i = 0; i < 40 && _browser.ChromeHoldsProfile; i++)
                    await Task.Delay(250);

                if (_browser.ChromeHoldsProfile)
                {
                    // Chrome часто остаётся висеть в фоне (фоновые приложения) и держит куки.
                    Log.Write("импорт: Chrome остался в фоне, завершаю процессы");
                    GeminiBrowser.KillChromeProcesses();

                    for (int i = 0; i < 20 && _browser.ChromeHoldsProfile; i++)
                        await Task.Delay(250);
                }
            }

            _overlay.ShowBusy("переношу вход из Chrome");
            await Task.Run(() => _browser.ImportProfileFromChrome());
            Log.Write("импорт входа из Chrome: готово");
            _overlay.ShowText("✓ вход из Chrome перенесён\nнажми F8");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("импорт: " + ex.Message);
            if (interactive) _overlay.ShowError(ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try { _api.Dispose(); } catch { }
        try { Task.Run(async () => await _browser.DisposeAsync()).Wait(4000); } catch { }
        try { _gate.Dispose(); } catch { }
    }
}
