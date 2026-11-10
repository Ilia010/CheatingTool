using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CheatingTool;

internal sealed class AppConfig
{
    public const string DefaultPrompt =
        "Ты — помощник, который решает задания по скриншоту.\n\n" +
        "На изображении могут быть вопросы теста, варианты ответов или открытые задания — включая математику, уравнения, геометрию, проценты, физику, графики и таблицы.\n" +
        "Выведи ТОЛЬКО ответы, по одной строке на каждый вопрос, в формате:\n" +
        "1 — פריז\n" +
        "2 — 1850\n\n" +
        "Правила:\n" +
        "- Все ответы пиши на иврите (עברית), даже если задание на другом языке. Числа, формулы, химические символы и единицы измерения оставляй в обычном виде.\n" +
        "- Если у вопроса есть варианты ответов — выведи сам текст правильного варианта точно как в задании (число, слово или короткую фразу), а не его букву. Так ответ нельзя перепутать.\n" +
        "- Если вопрос открытый — дай максимально короткий ответ (до 6 слов).\n" +
        "- Математику, уравнения, геометрию, проценты и физику решай точно, по шагам, и проверяй результат обратной подстановкой.\n" +
        "- В геометрии используй теоремы (Пифагора, подобия, суммы углов, площади и объёма) и проверяй, что найденная величина — именно та, о которой спрашивают (длина, площадь, угол, объём).\n" +
        "- Если заданий несколько — отвечай на каждое, сохраняя их номера.\n" +
        "- Никаких заголовков, markdown, пояснений, вступлений и итогов.\n" +
        "- Не пиши сам вопрос, только номер и ответ.\n" +
        "- Если на изображении нет вопросов — выведи «—».\n" +
        "- Язык ответа — иврит.";

    // Промт версии 1: заменяется на новый, чтобы старые установки получили иврит.
    private const string OldDefaultPromptV1 =
        "Ты — помощник, который решает задания по скриншоту.\n\n" +
        "На изображении могут быть вопросы теста, варианты ответов или открытые задания.\n" +
        "Выведи ТОЛЬКО ответы, по одной строке на каждый вопрос, в формате:\n" +
        "1 — B\n" +
        "2 — 1850\n\n" +
        "Правила:\n" +
        "- Если у вопроса есть варианты ответов — укажи только букву правильного варианта (или сам вариант, если букв нет).\n" +
        "- Если вопрос открытый — дай максимально короткий ответ (до 6 слов).\n" +
        "- Никаких заголовков, markdown, пояснений, вступлений и итогов.\n" +
        "- Не пиши сам вопрос, только номер и ответ.\n" +
        "- Если на изображении нет вопросов — выведи «—».\n" +
        "- Язык ответа — язык заданий.";

    // Промт версии 2 (буквы вариантов): тоже заменяется — буквы оказались ненадёжными.
    private const string OldDefaultPromptV2 =
        "Ты — помощник, который решает задания по скриншоту.\n\n" +
        "На изображении могут быть вопросы теста, варианты ответов или открытые задания — включая математику, уравнения, геометрию, проценты, физику, графики и таблицы.\n" +
        "Выведи ТОЛЬКО ответы, по одной строке на каждый вопрос, в формате:\n" +
        "1 — B\n" +
        "2 — 1850\n\n" +
        "Правила:\n" +
        "- Все ответы пиши на иврите (עברית), даже если задание на другом языке. Числа, формулы, химические символы и единицы измерения оставляй в обычном виде.\n" +
        "- Если у вопроса есть варианты ответов — укажи только букву правильного варианта (или сам текст варианта, если букв нет). Буквы вариантов — латинские, как в задании.\n" +
        "- Если вопрос открытый — дай максимально короткий ответ (до 6 слов).\n" +
        "- Математику, уравнения, геометрию, проценты и физику решай точно, по шагам, и проверяй результат обратной подстановкой.\n" +
        "- В геометрии используй теоремы (Пифагора, подобия, суммы углов, площади и объёма) и проверяй, что найденная величина — именно та, о которой спрашивают (длина, площадь, угол, объём).\n" +
        "- Если заданий несколько — отвечай на каждое, сохраняя их номера.\n" +
        "- Никаких заголовков, markdown, пояснений, вступлений и итогов.\n" +
        "- Не пиши сам вопрос, только номер и ответ.\n" +
        "- Если на изображении нет вопросов — выведи «—».\n" +
        "- Язык ответа — иврит.";

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = DefaultPrompt;
    public CaptureConfig Capture { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();
    public BrowserConfig Browser { get; set; } = new();
    public bool ShowStartupHint { get; set; } = true;

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath => Path.Combine(Paths.DataDir, "appsettings.json");

    public static AppConfig Load()
    {
        var path = DefaultPath;
        AppConfig cfg;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();

                bool needsUpdate = !json.Contains("\"HideWindow\"", StringComparison.Ordinal) ||
                                   !json.Contains("\"Preload\"", StringComparison.Ordinal);
                if (string.IsNullOrWhiteSpace(cfg.Prompt) || cfg.Prompt == OldDefaultPromptV1 || cfg.Prompt == OldDefaultPromptV2)
                {
                    cfg.Prompt = DefaultPrompt;
                    needsUpdate = true;
                }

                // Сразу сохраняем новые поля и промт, чтобы они были видны в настройках.
                if (needsUpdate)
                {
                    try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, JsonOpts), new UTF8Encoding(true)); }
                    catch (Exception ex) { Log.Write("конфиг: " + ex.Message); }
                }
            }
            else
            {
                cfg = new AppConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // BOM, чтобы конфиг корректно читался и PowerShell 5.1, и Блокнотом.
                File.WriteAllText(path, JsonSerializer.Serialize(cfg, JsonOpts), new UTF8Encoding(true));
            }
        }
        catch (Exception ex)
        {
            Log.Write("конфиг: " + ex.Message);
            cfg = new AppConfig();
        }

        cfg.Normalize();
        cfg.FilePath = path;
        return cfg;
    }

    private void Normalize()
    {
        Capture ??= new CaptureConfig();
        Overlay ??= new OverlayConfig();
        Browser ??= new BrowserConfig();

        Capture.MaxWidth = Math.Clamp(Capture.MaxWidth, 640, 3840);
        Capture.JpegQuality = Math.Clamp(Capture.JpegQuality, 50, 100);
        if (Capture.Monitor != "primary") Capture.Monitor = "cursor";

        Overlay.Width = Math.Clamp(Overlay.Width, 260, 1400);
        Overlay.FontSize = Math.Clamp(Overlay.FontSize, 10.0, 28.0);
        Overlay.Opacity = Math.Clamp(Overlay.Opacity, 0.25, 1.0);
        Overlay.Margin = Math.Clamp(Overlay.Margin, 0, 400);
        Overlay.MaxLines = Math.Clamp(Overlay.MaxLines, 3, 60);

        if (string.IsNullOrWhiteSpace(ApiKey)) ApiKey = "";
        else ApiKey = ApiKey.Trim();

        if (string.IsNullOrWhiteSpace(Prompt) || Prompt == OldDefaultPromptV1 || Prompt == OldDefaultPromptV2) Prompt = DefaultPrompt;
    }
}

internal sealed class CaptureConfig
{
    /// <summary>cursor — монитор под курсором, primary — основной.</summary>
    public string Monitor { get; set; } = "cursor";
    public int MaxWidth { get; set; } = 1920;
    public int JpegQuality { get; set; } = 88;
}

internal sealed class OverlayConfig
{
    public double Width { get; set; } = 520;
    public double FontSize { get; set; } = 13.5;
    public double Opacity { get; set; } = 0.8;
    public double Margin { get; set; } = 18;
    public int MaxLines { get; set; } = 20;
}

internal sealed class BrowserConfig
{
    /// <summary>Использовать Chrome, если API-ключа нет или API не ответил.</summary>
    public bool FallbackToBrowser { get; set; } = true;
    public bool Headless { get; set; } = false;
    /// <summary>Сворачивать окно Chrome после запроса, чтобы оно не мешало.</summary>
    public bool MinimizeWindow { get; set; } = true;
    /// <summary>Держать окно Chrome за пределами экрана и вне панели задач / Alt+Tab.</summary>
    public bool HideWindow { get; set; } = true;
    /// <summary>Заранее запускать Chrome в фоне, чтобы первый F8 ответил быстрее.</summary>
    public bool Preload { get; set; } = true;
    /// <summary>Один раз перенести вход из обычного Chrome (нужно, чтобы Chrome был закрыт).</summary>
    public bool ImportChromeProfile { get; set; } = true;
    /// <summary>Пусто = %LOCALAPPDATA%\Google\Chrome\User Data</summary>
    public string ChromeProfileSource { get; set; } = "";
    /// <summary>Пусто = Default</summary>
    public string ChromeProfileName { get; set; } = "";
    /// <summary>Пусто = %LOCALAPPDATA%\CheatingTool\chrome-profile</summary>
    public string UserDataDir { get; set; } = "";
}
