using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CheatingTool;

internal sealed class GeminiException : Exception
{
    public GeminiException(string message) : base(message) { }
}

internal sealed class GeminiApi : IDisposable
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly AppConfig _cfg;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(80) };
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private string? _model;

    public GeminiApi(AppConfig cfg)
    {
        _cfg = cfg;
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    public async Task<string> AskAsync(byte[] jpeg, CancellationToken ct)
    {
        var model = await ResolveModelAsync(ct);
        bool thinking = model.Contains("2.5", StringComparison.Ordinal) || model.Contains("3", StringComparison.Ordinal);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(model));
            request.Content = new StringContent(BuildBody(jpeg, thinking), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return Clean(ExtractText(body));

            var error = ExtractError(body);
            if (attempt == 0 && thinking && LooksLikeThinkingUnsupported(error))
            {
                Log.Write("Gemini API: thinkingConfig не поддержан, повтор без него");
                thinking = false;
                continue;
            }

            throw new GeminiException(error);
        }

        throw new GeminiException("Gemini API: неизвестная ошибка");
    }

    private string BuildUrl(string model) =>
        $"{BaseUrl}/models/{model}:generateContent?key={Uri.EscapeDataString(_cfg.ApiKey)}";

    private string BuildBody(byte[] jpeg, bool thinking)
    {
        var generation = new Dictionary<string, object?>
        {
            ["temperature"] = 0.15,
            ["topP"] = 0.9,
            ["maxOutputTokens"] = 2048,
        };

        if (thinking)
            generation["thinkingConfig"] = new Dictionary<string, object?> { ["thinkingBudget"] = 0 };

        var payload = new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["parts"] = new object[]
                    {
                        new Dictionary<string, object?> { ["text"] = _cfg.Prompt },
                        new Dictionary<string, object?>
                        {
                            ["inline_data"] = new Dictionary<string, object?>
                            {
                                ["mime_type"] = "image/jpeg",
                                ["data"] = Convert.ToBase64String(jpeg),
                            },
                        },
                    },
                },
            },
            ["generationConfig"] = generation,
            ["safetySettings"] = new object[]
            {
                new Dictionary<string, object?> { ["category"] = "HARM_CATEGORY_HARASSMENT", ["threshold"] = "BLOCK_NONE" },
                new Dictionary<string, object?> { ["category"] = "HARM_CATEGORY_HATE_SPEECH", ["threshold"] = "BLOCK_NONE" },
                new Dictionary<string, object?> { ["category"] = "HARM_CATEGORY_SEXUALLY_EXPLICIT", ["threshold"] = "BLOCK_NONE" },
                new Dictionary<string, object?> { ["category"] = "HARM_CATEGORY_DANGEROUS_CONTENT", ["threshold"] = "BLOCK_NONE" },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string> ResolveModelAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.Model)) return _cfg.Model.Trim();
        if (_model != null) return _model;

        await _resolveGate.WaitAsync(ct);
        try
        {
            if (_model != null) return _model;

            var url = $"{BaseUrl}/models?pageSize=1000&key={Uri.EscapeDataString(_cfg.ApiKey)}";
            using var response = await _http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new GeminiException(ExtractError(body));

            var names = new List<string>();
            using (var doc = JsonDocument.Parse(body))
            {
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        var name = model.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                        if (name.StartsWith("models/", StringComparison.Ordinal)) name = name[7..];

                        bool supports = false;
                        if (model.TryGetProperty("supportedGenerationMethods", out var methods))
                        {
                            foreach (var method in methods.EnumerateArray())
                            {
                                if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                                    supports = true;
                            }
                        }

                        if (supports && !IsUnsupported(name)) names.Add(name);
                    }
                }
            }

            _model = PickBest(names) ?? throw new GeminiException("нет доступных моделей generateContent — проверь ключ");
            Log.Write("Gemini API: выбрана модель " + _model);
            return _model;
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    private static bool IsUnsupported(string name)
    {
        string[] bad = { "thinking", "image", "tts", "audio", "embedding", "learnlm", "robotics", "computer-use", "aqa" };
        foreach (var b in bad)
            if (name.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
        return !name.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PickBest(List<string> names)
    {
        if (names.Count == 0) return null;
        return names
            .Select(n => (Name: n, Score: Score(n)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .First()
            .Name;
    }

    private static int Score(string name)
    {
        int score = 0;
        if (name.Contains("flash-lite", StringComparison.OrdinalIgnoreCase)) score += 100;
        else if (name.Contains("flash", StringComparison.OrdinalIgnoreCase)) score += 70;
        else if (name.Contains("pro", StringComparison.OrdinalIgnoreCase)) score += 20;

        if (name.Contains("latest", StringComparison.OrdinalIgnoreCase)) score += 15;
        if (name.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("exp", StringComparison.OrdinalIgnoreCase)) score -= 25;
        if (Regex.IsMatch(name, @"-00\d$")) score -= 10;

        return score;
    }

    private static string ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var builder = new StringBuilder();

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];

            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text))
                        builder.Append(text.GetString());
                }
            }

            if (builder.Length == 0 && candidate.TryGetProperty("finishReason", out var finish))
            {
                var reason = finish.GetString();
                if (reason == "SAFETY") throw new GeminiException("Gemini заблокировал ответ (SAFETY)");
                throw new GeminiException("Gemini не вернул текст (" + reason + ")");
            }
        }

        if (builder.Length == 0 &&
            root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var blockReason))
        {
            throw new GeminiException("Gemini заблокировал запрос (" + blockReason.GetString() + ")");
        }

        var result = builder.ToString().Trim();
        if (result.Length == 0) throw new GeminiException("Gemini вернул пустой ответ");
        return result;
    }

    private static bool LooksLikeThinkingUnsupported(string error) =>
        error.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Unknown name", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Invalid JSON payload", StringComparison.OrdinalIgnoreCase);

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return "Gemini API: " + message.GetString();
            }
        }
        catch { }

        var text = body.Trim();
        if (text.Length > 220) text = text[..220] + "…";
        return "Gemini API: " + text;
    }

    public static string Clean(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace("```", "").Trim();
        var lines = text.Split('\n')
            .Select(line => line.Trim().TrimStart('*', '#', '•').Trim())
            .Select(line => line.TrimEnd('*').Trim())
            .Where(line => line.Length > 0)
            .ToList();

        // Gemini иногда добавляет в текст ответа служебные подписи вложений
        // («image_xxx.png», «Ещё 4»). Оставляем только строки «номер — ответ».
        var answers = lines.Where(line => AnswerLine.IsMatch(line)).ToList();
        return answers.Count > 0 ? string.Join("\n", answers) : string.Join("\n", lines);
    }

    private static readonly Regex AnswerLine = new(@"^\d{1,3}\s*[-–—.):]\s*\S", RegexOptions.Compiled);

    public void Dispose() => _http.Dispose();
}
