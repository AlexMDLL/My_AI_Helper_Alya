using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Alya.Core;

namespace Alya.Llm;

/// <summary>Клиент Ollama (/api/chat). Модель по умолчанию — qwen2.5:7b-instruct.</summary>
public sealed class OllamaLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _systemPrompt;
    public string ExtraContext { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.8;
    public string KeepAlive { get; set; } = "10m";
    public string Persona { get; set; } = string.Empty;

    public OllamaLlmClient(string model = "qwen2.5:7b-instruct", string baseUrl = "http://127.0.0.1:11434", string? systemPrompt = null)
    {
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) };
        _systemPrompt = systemPrompt ?? DefaultPrompt;
    }

    private const string DefaultPrompt =
        "Ты — Аля, живая спутница на ПК. Говори по-русски, коротко и живо, с лёгкой вредностью, но по-доброму. " +
        "Без канцелярита, без длинных лекций, без смайликов. На бытовые реплики отвечай одной простой фразой. " +
        "Пиши только по-русски кириллицей: никаких китайских иероглифов, английских вставок и прочих алфавитов. " +
        "Никогда не пиши слова user, assistant, system и служебные метки. Ты живая Аля, а не «виртуальный помощник» — не говори так о себе.";

    private static string CleanRussian(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var withoutCjk = Regex.Replace(text, @"[\u3000-\u303F\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uAC00-\uD7AF\uFF00-\uFFEF]+", " ");
        var collapsed = Regex.Replace(withoutCjk, @"\s+", " ");
        collapsed = Regex.Replace(collapsed, @"\s+([,\.!?;:])", "$1");
        return collapsed.Trim();
    }
    public async Task<string> CompleteWithToolsAsync(IReadOnlyList<ChatMessage> history, object[] tools, Func<string, JsonElement, Task<string>> executor, CancellationToken cancellationToken = default)
    {
        var prompt = string.IsNullOrWhiteSpace(ExtraContext) ? (string.IsNullOrWhiteSpace(Persona) ? _systemPrompt : Persona) : (string.IsNullOrWhiteSpace(Persona) ? _systemPrompt : Persona) + " " + ExtraContext;
        var messages = new List<object> { new { role = "system", content = prompt } };
        foreach (var message in history)
        {
            messages.Add(new { role = message.Role == "assistant" ? "assistant" : "user", content = message.Content });
        }

        for (var round = 0; round < 4; round++)
        {
            var request = new
            {
                model = _model,
                stream = false,
                messages,
                tools,
                options = new { temperature = Temperature, num_predict = 260 },
                keep_alive = KeepAlive
            };
            using var response = await _http.PostAsJsonAsync("/api/chat", request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Ollama {response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}");
            }
            using var document = JsonDocument.Parse(raw);
            var message = document.RootElement.GetProperty("message");
            var content = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
            var hasTools = message.TryGetProperty("tool_calls", out var calls) && calls.GetArrayLength() > 0;
            if (!hasTools)
            {
                return CleanRussian(content);
            }

            messages.Add(new { role = "assistant", content, tool_calls = calls });
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var arguments = function.TryGetProperty("arguments", out var args) ? args : default;
                var result = await executor(name, arguments);
                messages.Add(new { role = "tool", content = result });
            }
        }
        return "Не получилось выполнить команду.";
    }
    public async Task<string> CompleteStreamingAsync(IReadOnlyList<ChatMessage> history, Action<string> onDelta, CancellationToken cancellationToken = default)
    {
        var prompt = string.IsNullOrWhiteSpace(ExtraContext) ? (string.IsNullOrWhiteSpace(Persona) ? _systemPrompt : Persona) : (string.IsNullOrWhiteSpace(Persona) ? _systemPrompt : Persona) + " " + ExtraContext;
        var messages = new List<object> { new { role = "system", content = prompt } };
        foreach (var message in history)
        {
            messages.Add(new { role = message.Role == "assistant" ? "assistant" : "user", content = message.Content });
        }
        var request = new
        {
            model = _model,
            stream = true,
            messages,
            options = new { temperature = Temperature, num_predict = 220 },
            keep_alive = KeepAlive
        };
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request)
        };
        using var response = await _http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama {response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var builder = new StringBuilder();
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("message", out var message)) continue;
                if (!message.TryGetProperty("content", out var content)) continue;
                var delta = content.GetString();
                if (string.IsNullOrEmpty(delta)) continue;
                builder.Append(delta);
                onDelta(delta);
            }
            catch
            {
                // пропускаем битую строку
            }
        }
        return CleanRussian(builder.ToString());
    }
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var basePrompt = string.IsNullOrWhiteSpace(Persona) ? _systemPrompt : Persona;
        var prompt = string.IsNullOrWhiteSpace(ExtraContext) ? basePrompt : basePrompt + " " + ExtraContext;
        var messages = new List<object> { new { role = "system", content = prompt } };
        foreach (var message in history)
        {
            messages.Add(new { role = message.Role == "assistant" ? "assistant" : "user", content = message.Content });
        }

        var request = new
        {
            model = _model,
            stream = false,
            messages,
            options = new { temperature = Temperature, num_predict = 220 },
            keep_alive = KeepAlive
        };

        using var response = await _http.PostAsJsonAsync("/api/chat", request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama {response.StatusCode}: {raw[..Math.Min(200, raw.Length)]}");
        }

        using var document = JsonDocument.Parse(raw);
        var content = document.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        var cleaned = CleanRussian(content);
        return string.IsNullOrWhiteSpace(cleaned) ? "…" : cleaned;
    }

    public void Dispose() => _http.Dispose();
}
