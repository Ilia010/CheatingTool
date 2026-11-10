using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Playwright;

namespace CheatingTool;

/// <summary>
/// Запасной путь: работает через настоящий Chrome, залогиненный в Gemini.
/// Профиль сохраняется между запусками, вход нужен только один раз.
/// </summary>
internal sealed class GeminiBrowser : IAsyncDisposable
{
    private readonly AppConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private uint _chromePid;

    public GeminiBrowser(AppConfig cfg)
    {
        _cfg = cfg;
    }

    private static bool DebugMode => Env.Flag("DEBUG");

    private static bool SkipAttach => Env.Flag("SKIP_ATTACH");

    private const string MarkerName = ".imported";

    public string ProfileDir
    {
        get
        {
            var dir = _cfg.Browser.UserDataDir;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Paths.DataDir, "chrome-profile");
            return dir;
        }
    }

    /// <summary>Папка «User Data» обычного Chrome пользователя.</summary>
    public string SourceRoot
    {
        get
        {
            var dir = _cfg.Browser.ChromeProfileSource;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
            return dir;
        }
    }

    public string SourceProfileName
    {
        get
        {
            var configured = _cfg.Browser.ChromeProfileName;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            // Берём профиль, который последним использовался в обычном Chrome.
            try
            {
                var localState = Path.Combine(SourceRoot, "Local State");
                if (File.Exists(localState))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(localState));
                    var name = node?["profile"]?["last_used"]?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(name) &&
                        Directory.Exists(Path.Combine(SourceRoot, name)))
                    {
                        return name;
                    }
                }
            }
            catch { }

            return "Default";
        }
    }

    public string SourceProfileDir => Path.Combine(SourceRoot, SourceProfileName);

    public string SourceCookies => Path.Combine(SourceProfileDir, "Network", "Cookies");

    private string MarkerPath => Path.Combine(ProfileDir, MarkerName);

    public bool HasChromeLogin => File.Exists(SourceCookies);

    /// <summary>Занят ли профиль запущенным Chrome (тогда куки-база недоступна для чтения).</summary>
    public bool ChromeHoldsProfile => HasChromeLogin && !CanRead(SourceCookies);

    /// <summary>Нужно ли один раз перенести вход из обычного Chrome.</summary>
    public bool ProfileNeedsImport
    {
        get
        {
            if (!_cfg.Browser.ImportChromeProfile) return false;
            if (File.Exists(MarkerPath)) return false;
            if (HasGoogleLogin(ProfileDir)) return false;
            return HasChromeLogin && File.Exists(Path.Combine(SourceRoot, "Local State"));
        }
    }

    private static bool HasGoogleLogin(string profileRoot)
    {
        var file = Path.Combine(profileRoot, "Default", "Network", "Cookies");
        if (!File.Exists(file)) return false;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 32 * 1024 * 1024) return false;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var text = System.Text.Encoding.UTF8.GetString(memory.ToArray());

            return text.Contains("__Secure-1PSID", StringComparison.Ordinal) ||
                   text.Contains("__Secure-3PSID", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanRead(string file)
    {
        if (!File.Exists(file)) return false;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Закрывает окна Chrome (аккуратно, через WM_CLOSE — вкладки восстановятся).</summary>
    public static int CloseChromeWindows()
    {
        int closed = 0;

        foreach (var process in Process.GetProcessesByName("chrome"))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                    closed++;
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        return closed;
    }

    /// <summary>Добивает фоновые процессы Chrome, если после закрытия окон профиль всё ещё занят.</summary>
    public static int KillChromeProcesses()
    {
        int killed = 0;

        foreach (var process in Process.GetProcessesByName("chrome"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    /// <summary>Переносит состояние входа из обычного Chrome в профиль программы. Chrome должен быть закрыт.</summary>
    public void ImportProfileFromChrome()
    {
        var source = SourceProfileDir;
        var cookies = SourceCookies;

        if (!File.Exists(cookies))
            throw new GeminiException($"в профиле Chrome «{SourceProfileName}» нет входа в Google — войди в Chrome один раз");

        if (!CanRead(cookies))
            throw new GeminiException("Chrome запущен. Полностью закрой Chrome и повтори перенос входа.");

        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(ProfileDir)) Directory.Delete(ProfileDir, true);
                    break;
                }
                catch when (attempt < 4)
                {
                    Thread.Sleep(300);
                }
            }
        }
        catch (Exception ex)
        {
            throw new GeminiException("не удалось очистить профиль программы: " + ex.Message);
        }

        Directory.CreateDirectory(ProfileDir);

        int copied = 0;
        copied += CopyFile(Path.Combine(SourceRoot, "Local State"), Path.Combine(ProfileDir, "Local State"));

        foreach (var relative in new[]
                 {
                     "Preferences",
                     "Secure Preferences",
                     "Network\\Cookies",
                     "Network\\Cookies-wal",
                     "Network\\Cookies-journal",
                 })
        {
            copied += CopyFile(Path.Combine(source, relative), Path.Combine(ProfileDir, "Default", relative));
        }

        foreach (var relative in new[] { "Local Storage", "Session Storage" })
        {
            copied += CopyDirectory(Path.Combine(source, relative), Path.Combine(ProfileDir, "Default", relative));
        }

        PatchLocalState(Path.Combine(ProfileDir, "Local State"));

        if (!File.Exists(Path.Combine(ProfileDir, "Default", "Network", "Cookies")))
            throw new GeminiException("не удалось скопировать куки Chrome. Закрой Chrome полностью и повтори.");

        try { File.WriteAllText(MarkerPath, DateTime.Now.ToString("o")); } catch { }
        Log.Write($"импорт входа из Chrome: профиль {SourceProfileName}, скопировано объектов: {copied}");
    }

    private static int CopyFile(string source, string destination)
    {
        try
        {
            if (!File.Exists(source)) return 0;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Write($"импорт: {Path.GetFileName(source)}: {ex.Message}");
            return 0;
        }
    }

    private static int CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return 0;

        int copied = 0;
        try
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source))
                copied += CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));

            foreach (var dir in Directory.EnumerateDirectories(source))
                copied += CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
        catch (Exception ex)
        {
            Log.Write("импорт папки " + Path.GetFileName(source) + ": " + ex.Message);
        }

        return copied;
    }

    private void PatchLocalState(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            if (node is not System.Text.Json.Nodes.JsonObject root) return;
            if (root["profile"] is not System.Text.Json.Nodes.JsonObject profile) return;

            // В профиле программы вход всегда лежит в папке Default.
            const string destination = "Default";
            profile["last_used"] = destination;

            if (profile["info_cache"] is System.Text.Json.Nodes.JsonObject info && !info.ContainsKey(destination))
            {
                info[destination] = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = "Default",
                    ["is_using_default_name"] = true,
                };
            }

            File.WriteAllText(path, root.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Write("импорт: Local State: " + ex.Message);
        }
    }

    /// <summary>Один раз переносит вход из Chrome, если он ещё не перенесён. Бросает понятную ошибку, если Chrome открыт.</summary>
    public async Task EnsureProfileReadyAsync()
    {
        if (!ProfileNeedsImport) return;

        await _gate.WaitAsync();
        try
        {
            if (!ProfileNeedsImport) return;

            await CloseBrowserAsync();

            if (!HasChromeLogin)
                throw new GeminiException($"в профиле Chrome «{SourceProfileName}» нет входа в Google. Войди в Chrome или используй трей → «Войти в Gemini…».");

            if (ChromeHoldsProfile)
                throw new GeminiException("Нужно один раз перенести вход из Chrome. Полностью закрой Chrome и нажми F8 ещё раз.");

            ImportProfileFromChrome();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CloseOwnBrowserAsync() => CloseBrowserAsync();

    /// <summary>Открывает обычный Chrome (без автоматизации) на том же профиле — чтобы спокойно войти в Google.</summary>
    public void OpenLoginWindow()
    {
        try
        {
            Task.Run(async () => await CloseBrowserAsync()).Wait(5000);

            var chrome = FindChrome();
            if (chrome == null) throw new InvalidOperationException("Chrome не найден");

            Directory.CreateDirectory(ProfileDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = chrome,
                Arguments = $"--user-data-dir=\"{ProfileDir}\" --profile-directory=Default --no-first-run --no-default-browser-check --window-position=120,120 --window-size=1200,900 https://gemini.google.com/app",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Write("login: " + ex.Message);
            throw;
        }
    }

    private static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string> AskAsync(byte[] jpeg, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var tempFile = Path.Combine(Path.GetTempPath(), $"cheatingtool_{Guid.NewGuid():N}.jpg");

        try
        {
            await File.WriteAllBytesAsync(tempFile, jpeg, ct);
            var page = await EnsurePageAsync();
            await NewChatAsync(page);
            await MinimizeAsync(page);
            if (!SkipAttach)
            {
                try
                {
                    await AttachAsync(page, tempFile);
                }
                catch (GeminiException)
                {
                    // Иногда Chrome открывает Gemini ещё не готовым к вложению.
                    // Один раз перезагружаем страницу и пробуем снова.
                    Dbg("attach failed, retrying");
                    await page.ReloadAsync(new PageReloadOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60_000,
                    });
                    await page.WaitForTimeoutAsync(1500);
                    await NewChatAsync(page);
                    await AttachAsync(page, tempFile);
                }
            }

            int before = 0;
            try { before = await page.Locator("model-response").CountAsync(); } catch { }
            Dbg($"replies before submit: {before}");

            await SubmitAsync(page);
            await VerifyUserMessageAsync(page);

            var answer = await ReadAnswerAsync(page, ct, before);
            Dbg("raw answer: " + answer);
            await DebugShotAsync(page, "dbg_after.png");
            return answer;
        }
        finally
        {
            await MinimizeAsync(_page);
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            _gate.Release();
        }
    }

    /// <summary>Заранее открывает Gemini в скрытом Chrome, чтобы первый F8 не ждал запуска.</summary>
    public async Task PreloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsurePageAsync();
            HideOwnChrome();
            Log.Write("preload: Chrome готов");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IPage> EnsurePageAsync()
    {
        if (_playwright == null)
        {
            try
            {
                _playwright = await Playwright.CreateAsync();
            }
            catch (Exception ex)
            {
                throw new GeminiException("Playwright не запустился: " + ex.Message);
            }
        }

        if (_context == null)
        {
            Directory.CreateDirectory(ProfileDir);
            var args = new List<string>
            {
                "--disable-background-timer-throttling",
                "--disable-renderer-backgrounding",
                "--disable-backgrounding-occluded-windows",
                "--disable-blink-features=AutomationControlled",
                "--profile-directory=Default",
                "--no-first-run",
                "--no-default-browser-check",
            };

            if (HideWindowEnabled)
            {
                // Окно Chrome живёт за пределами экрана: его не видно и оно не попадает в снимок.
                args.Add("--window-position=-32000,-32000");
                args.Add("--window-size=1024,768");
            }

            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = "chrome",
                Headless = _cfg.Browser.Headless,
                ViewportSize = ViewportSize.NoViewport,
                Locale = "ru-RU",
                Args = args.ToArray(),
            };

            var launchStarted = DateTime.Now;
            try
            {
                _context = await _playwright.Chromium.LaunchPersistentContextAsync(ProfileDir, options);
            }
            catch (Exception ex)
            {
                throw new GeminiException("Chrome не запустился: " + ex.Message);
            }

            if (HideWindowEnabled)
            {
                _chromePid = await FindChromePidAsync(launchStarted);
                HideOwnChrome();
            }
        }

        _page = _context.Pages.FirstOrDefault(p => !p.IsClosed && p.Url.Contains("gemini.google.com"))
                ?? _context.Pages.FirstOrDefault(p => !p.IsClosed);
        if (_page == null || _page.IsClosed)
            _page = await _context.NewPageAsync();

        if (!_page.Url.Contains("gemini.google.com"))
        {
            await _page.GotoAsync("https://gemini.google.com/app", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 90_000,
            });
        }

        if (await FindEditorAsync(_page, 25_000) == null)
            throw new GeminiException("Gemini не открылся. Трей → «Войти в Gemini…», войди в аккаунт, потом F8.");

        if (!SkipAttach)
        {
            try
            {
                var login = _page.GetByText("Войти", new PageGetByTextOptions { Exact = true }).First;
                if (await login.CountAsync() > 0 && await login.IsVisibleAsync())
                    throw new GeminiException(File.Exists(MarkerPath)
                        ? "Вход в Gemini не подхватился. Трей → «Войти в Gemini…», войди один раз — дальше Chrome будет открываться уже с входом."
                        : "Нужен вход. Трей → «Перенести вход из Chrome…» (Chrome предварительно закрой) или «Войти в Gemini…», потом F8.");
            }
            catch (GeminiException) { throw; }
            catch { }
        }

        return _page;
    }

    private static async Task NewChatAsync(IPage page)
    {
        try
        {
            var button = page.Locator("button[aria-label*='New chat' i], button[aria-label*='Новый чат' i]").First;
            if (await button.CountAsync() > 0 && await button.IsVisibleAsync())
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = 4000 });
                await page.WaitForTimeoutAsync(400);
                return;
            }
        }
        catch { }

        if (!page.Url.TrimEnd('/').EndsWith("/app", StringComparison.OrdinalIgnoreCase))
        {
            await page.GotoAsync("https://gemini.google.com/app", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });
        }
    }

    private async Task AttachAsync(IPage page, string file)
    {
        if (DebugMode)
        {
            Dbg("url: " + page.Url);
            try
            {
                var labels = await page.Locator("button").EvaluateAllAsync<string[]>(
                    "els => els.map(e => (e.getAttribute('aria-label') || '') + '|' + (e.textContent || '').trim()).filter(s => s.length > 1)");
                Dbg("buttons: " + string.Join(" ;; ", labels.Take(50)));
            }
            catch (Exception ex) { Dbg("buttons fail: " + ex.Message); }
        }

        bool attached = await TryInputFileAsync(page, file);
        if (attached && await WaitForPreviewAsync(page, 5000))
        {
            Dbg("attach: file input");
        }
        else
        {
            // Клипборд — самый быстрый и стабильный путь в веб-версии Gemini.
            attached = await TryClipboardImageAsync(page, file) && await WaitForPreviewAsync(page, 2000);
            if (attached)
            {
                Dbg("attach: clipboard");
            }
            else
            {
                // Резервный путь: меню загрузки. Ждём недолго, иначе он зря тормозит.
                attached = await TryChooserAsync(page, file);
                if (attached) await WaitForPreviewAsync(page, 4000);
            }
        }

        int previews = await AttachmentCountAsync(page);
        Dbg($"attached={attached} previews={previews}");
        await DebugShotAsync(page, "dbg_upload.png");

        if (previews == 0)
            throw new GeminiException(File.Exists(MarkerPath)
                ? "Gemini не принял картинку. Нажми F8 ещё раз; если повторяется — трей → «Войти в Gemini…»."
                : "Gemini не принял картинку. Трей → «Перенести вход из Chrome…» или «Войти в Gemini…», потом F8.");
    }

    private async Task<bool> TryInputFileAsync(IPage page, string file)
    {
        try
        {
            var input = page.Locator("input[type='file']").First;
            if (await input.CountAsync() == 0) return false;

            await input.SetInputFilesAsync(file, new LocatorSetInputFilesOptions { Timeout = 20_000 });
            Dbg("file input ok");
            return true;
        }
        catch (Exception ex)
        {
            Dbg("file input: " + ex.Message);
            return false;
        }
    }

    private static async Task<bool> WaitForPreviewAsync(IPage page, int timeoutMs)
    {
        for (int waited = 0; waited < timeoutMs; waited += 150)
        {
            if (await AttachmentCountAsync(page) > 0) return true;
            await page.WaitForTimeoutAsync(150);
        }

        return false;
    }

    private async Task<bool> TryChooserAsync(IPage page, string file)
    {
        string[] attachButtons =
        {
            "button[aria-label*='Загрузка и инструменты' i]",
            "button[aria-label*='Upload and tools' i]",
            "button[aria-label*='Add files' i]",
            "button[aria-label*='Добавить файл' i]",
            "button[aria-label*='Upload' i]",
            "button[aria-label*='Загрузить' i]",
        };

        foreach (var selector in attachButtons)
        {
            try
            {
                var button = page.Locator(selector).First;
                if (await button.CountAsync() == 0 || !await button.IsVisibleAsync()) continue;

                var chooserTask = page.WaitForFileChooserAsync(new PageWaitForFileChooserOptions { Timeout = 1500 });
                await button.ClickAsync(new LocatorClickOptions { Timeout = 4000 });

                await ClickUploadMenuItemAsync(page);

                var chooser = await chooserTask;
                await chooser.SetFilesAsync(file);
                Dbg("chooser ok: " + selector);
                return true;
            }
            catch (Exception ex)
            {
                Dbg("chooser: " + ex.Message);
                try { await page.Keyboard.PressAsync("Escape"); } catch { }
            }
        }

        return false;
    }

    private static async Task ClickUploadMenuItemAsync(IPage page)
    {
        string[] menuItems =
        {
            "Загрузить файлы",
            "Загрузить файл",
            "Загрузить фото",
            "Загрузить изображение",
            "Загрузить с компьютера",
            "Загрузить",
            "Upload files",
            "Upload file",
            "Upload image",
            "Upload from computer",
        };

        foreach (var text in menuItems)
        {
            try
            {
                var item = page.GetByText(text, new PageGetByTextOptions { Exact = false }).First;
                if (await item.CountAsync() > 0 && await item.IsVisibleAsync())
                {
                    await item.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                    return;
                }
            }
            catch { }
        }
    }

    private async Task<bool> TryClipboardImageAsync(IPage page, string file)
    {
        try
        {
            using var image = new Bitmap(file);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetImage(image);
                    break;
                }
                catch (Exception ex)
                {
                    Dbg("clipboard image retry: " + ex.Message);
                    await Task.Delay(200);
                }
            }

            var editor = await FindEditorAsync(page, 10_000);
            if (editor == null) return false;

            await editor.ClickAsync();
            await page.WaitForTimeoutAsync(150);
            await page.Keyboard.PressAsync("Control+V");
            Dbg("clipboard image pasted");
            return true;
        }
        catch (Exception ex)
        {
            Dbg("clipboard image: " + ex.Message);
            return false;
        }
    }

    private static async Task<int> AttachmentCountAsync(IPage page)
    {
        try
        {
            int count = await page.Locator("img[src^='blob:'], img[src^='data:image']").CountAsync();
            if (count > 0) return count;

            return await page.Locator("[data-test-id*='attachment'], .attachment-container, file-preview, [data-test-id*='image']").CountAsync();
        }
        catch
        {
            return 0;
        }
    }

    private static ILocator SendButton(IPage page) =>
        page.Locator("button[aria-label*='Send' i], button[aria-label*='Отправить' i], button.send-button");

    private async Task SubmitAsync(IPage page)
    {
        var editor = await FindEditorAsync(page, 15_000)
                     ?? throw new GeminiException("не нашёл поле ввода Gemini");

        await editor.ClickAsync();
        await PastePromptAsync(page, editor);
        await page.WaitForTimeoutAsync(150);

        if (DebugMode)
        {
            try
            {
                var typed = await editor.InnerTextAsync();
                var oneLine = typed.Replace("\n", " ");
                Dbg("editor: " + (oneLine.Length > 90 ? oneLine[..90] : oneLine));
            }
            catch { }
        }

        await DebugShotAsync(page, "dbg_before_send.png");
        await page.Keyboard.PressAsync("Enter");

        // Ждём, пока сообщение реально отправится (поле очистится или появится «Stop»).
        for (int i = 0; i < 15; i++)
        {
            await page.WaitForTimeoutAsync(200);

            if (await IsBusyAsync(page)) return;

            string left = "";
            try { left = (await editor.InnerTextAsync()).Trim(); } catch { }
            if (left.Length == 0) return;
        }

        try
        {
            var send = SendButton(page);
            if (await send.CountAsync() > 0 && await send.First.IsEnabledAsync())
                await send.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
        }
        catch { }
    }

    private async Task PastePromptAsync(IPage page, ILocator editor)
    {
        await editor.ClickAsync();

        string probe = _cfg.Prompt.Trim();
        probe = probe.Length > 12 ? probe[..12] : probe;

        // 1. Прямая вставка через Playwright.
        try
        {
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.PressAsync("Backspace");
            await page.Keyboard.InsertTextAsync(_cfg.Prompt);
            await page.WaitForTimeoutAsync(250);

            var typed = await editor.InnerTextAsync();
            if (typed.Contains(probe, StringComparison.Ordinal))
            {
                Dbg("prompt: insertText");
                return;
            }

            Dbg("prompt: insertText mismatch");
        }
        catch (Exception ex)
        {
            Dbg("insertText: " + ex.Message);
        }

        // 2. Через буфер обмена (страховка от проблем с кодировкой).
        try
        {
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.PressAsync("Backspace");

            string? previous = null;
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsText())
                    previous = System.Windows.Forms.Clipboard.GetText();
            }
            catch { }

            bool copied = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(_cfg.Prompt);
                    copied = true;
                    break;
                }
                catch (Exception ex)
                {
                    Dbg("clipboard text retry: " + ex.Message);
                    await Task.Delay(200);
                }
            }

            if (!copied) throw new InvalidOperationException("буфер обмена занят");

            await page.Keyboard.PressAsync("Control+V");
            await page.WaitForTimeoutAsync(500);

            if (previous != null)
            {
                try { System.Windows.Forms.Clipboard.SetText(previous); } catch { }
            }

            Dbg("prompt: clipboard");
            return;
        }
        catch (Exception ex)
        {
            Dbg("paste prompt: " + ex.Message);
        }

        // 3. Последний шанс.
        await page.Keyboard.InsertTextAsync(_cfg.Prompt);
    }

    private static async Task<string> ReadAnswerAsync(IPage page, CancellationToken ct, int baselineCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        string last = "";
        int stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await page.WaitForTimeoutAsync(400);

            int count = 0;
            try { count = await page.Locator("model-response").CountAsync(); } catch { }

            // Ждём именно новую реплику: иначе можно прочитать ответ из прошлого чата.
            if (count <= baselineCount) continue;

            string current = "";
            try { current = (await GetLastTextAsync(page)).Trim(); } catch { }

            bool busy = await IsBusyAsync(page);
            bool finished = !busy && await IsAnswerFinishedAsync(page);

            // Gemini печатает ответ потоком и иногда делает паузы между строками.
            // Поэтому ждём не 0,8 с, а 2 с неизменного текста (или кнопки «ответ готов»).
            if (current.Length > 0 && current == last && !busy)
            {
                stable++;
                if (finished || stable >= 5) break;
            }
            else
            {
                stable = 0;
                last = current;
            }
        }

        if (last.Length == 0) throw new GeminiException("Gemini не ответил (таймаут). Проверь окно Chrome.");
        if (LooksLikeError(last)) throw new GeminiException("Gemini: " + Short(last));

        return GeminiApi.Clean(last);
    }

    /// <summary>Признак, что ответ дописан: под ним появились кнопки оценки/копирования.</summary>
    private static async Task<bool> IsAnswerFinishedAsync(IPage page)
    {
        try
        {
            var root = page.Locator("model-response").Last;
            var actions = root.Locator(
                "button[aria-label*='Good response' i], button[aria-label*='Bad response' i], " +
                "button[aria-label*='Хороший ответ' i], button[aria-label*='Плохой ответ' i], " +
                "button[aria-label*='Copy' i], button[aria-label*='Копировать' i]");
            return await actions.CountAsync() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Пишет в лог, реально ли в отправленном сообщении оказалась картинка.</summary>
    private static async Task VerifyUserMessageAsync(IPage page)
    {
        try
        {
            var query = page.Locator("user-query").Last;
            if (await query.CountAsync() == 0) return;

            int images = await query.Locator("img").CountAsync();
            var text = "";
            try { text = (await query.InnerTextAsync()).Replace("\r", " ").Replace("\n", " ").Trim(); } catch { }
            if (text.Length > 70) text = text[..70];

            Log.Write($"отправлено: картинок={images}, текст={text}");
        }
        catch (Exception ex)
        {
            Dbg("verify query: " + ex.Message);
        }
    }

    private static async Task<string> GetLastTextAsync(IPage page)
    {
        var root = page.Locator("model-response").Last;

        var content = root.Locator("message-content").Last;
        if (await content.CountAsync() > 0) return await content.InnerTextAsync();

        var markdown = root.Locator(".markdown").Last;
        if (await markdown.CountAsync() > 0) return await markdown.InnerTextAsync();

        return await root.InnerTextAsync();
    }

    private static async Task<bool> IsBusyAsync(IPage page)
    {
        try
        {
            var stop = page.Locator("button[aria-label*='Stop' i], button[aria-label*='Остановить' i]").First;
            return await stop.CountAsync() > 0 && await stop.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ILocator?> FindEditorAsync(IPage page, int timeoutMs)
    {
        string[] selectors =
        {
            "rich-textarea div[contenteditable='true']",
            "div[contenteditable='true'][role='textbox']",
            "div[contenteditable='true']",
            "textarea",
        };

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var selector in selectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync() && await locator.IsEditableAsync())
                        return locator;
                }
                catch { }
            }
            await page.WaitForTimeoutAsync(400);
        }

        return null;
    }

    private static bool LooksLikeError(string text) =>
        text.Contains("Something went wrong", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Что-то пошло не так", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Произошла ошибка", StringComparison.OrdinalIgnoreCase);

    private static string Short(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    private static void Dbg(string message)
    {
        if (DebugMode) Log.Write("dbg: " + message);
    }

    private static async Task DebugShotAsync(IPage page, string name)
    {
        if (!DebugMode) return;
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(Log.DirectoryPath, name),
                FullPage = false,
            });
        }
        catch (Exception ex)
        {
            Log.Write("dbg shot: " + ex.Message);
        }
    }

    /// <summary>Сворачивает окно своего Chrome, чтобы оно не попало в скриншот.</summary>
    public async Task PrepareForCaptureAsync()
    {
        if (_context == null || _cfg.Browser.Headless) return;

        try
        {
            var page = _page ?? _context.Pages.FirstOrDefault(p => !p.IsClosed);
            await MinimizeAsync(page);
            await Task.Delay(HideWindowEnabled ? 80 : 350);
        }
        catch (Exception ex)
        {
            Dbg("prepare capture: " + ex.Message);
        }
    }

    private bool HideWindowEnabled => _cfg.Browser.HideWindow && !_cfg.Browser.Headless;

    /// <summary>Находит процесс нашего Chrome по времени запуска (у него есть главное окно).</summary>
    private static async Task<uint> FindChromePidAsync(DateTime launchedAt)
    {
        var earliest = launchedAt.AddSeconds(-2);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            foreach (var process in Process.GetProcessesByName("chrome"))
            {
                uint id = 0;
                try
                {
                    if (process.StartTime >= earliest && process.MainWindowHandle != IntPtr.Zero)
                        id = (uint)process.Id;
                }
                catch { }
                finally
                {
                    process.Dispose();
                }

                if (id != 0) return id;
            }

            await Task.Delay(150);
        }

        return 0;
    }

    /// <summary>Уводит окно нашего Chrome за пределы экрана и убирает его из панели задач.</summary>
    private void HideOwnChrome()
    {
        if (!HideWindowEnabled || _chromePid == 0) return;
        ChromeWindow.HideOffScreen(_chromePid);
    }

    private async Task MinimizeAsync(IPage? page)
    {
        if (_cfg.Browser.Headless) return;

        if (HideWindowEnabled)
        {
            HideOwnChrome();
            return;
        }

        if (!_cfg.Browser.MinimizeWindow || page == null || _context == null) return;

        try
        {
            var session = await _context.NewCDPSessionAsync(page);
            var info = await session.SendAsync("Browser.getWindowForTarget");
            if (info is null) return;
            int windowId = info.Value.GetProperty("windowId").GetInt32();

            await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "minimized" },
            });

            await session.DetachAsync();
        }
        catch (Exception ex)
        {
            Dbg("minimize: " + ex.Message);
        }
    }

    private async Task CloseBrowserAsync()
    {
        try
        {
            if (_context != null)
            {
                await _context.CloseAsync();
                _context = null;
                _page = null;
                _chromePid = 0;
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseBrowserAsync(); } catch { }
        try { _playwright?.Dispose(); } catch { }
        try { _gate.Dispose(); } catch { }
    }
}
