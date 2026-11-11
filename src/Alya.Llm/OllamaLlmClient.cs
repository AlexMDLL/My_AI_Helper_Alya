using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alya.Core;

namespace Alya.Llm;

/// <summary>Клиент Ollama (/api/chat). Модель по умолчанию — qwen2.5:7b-instruct.</summary>
public sealed class OllamaLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _systemPrompt;

    public OllamaLlmClient(string model = "qwen2.5:7b-instruct", string baseUrl = "http://127.0.0.1:11434", string? systemPrompt = null)
    {
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) };
        _systemPrompt = systemPrompt ?? DefaultPrompt;
    }

    private const string DefaultPrompt =
        "Ты — Аля, живая спутница на ПК. Говори по-русски, коротко и живо, с лёгкой вредностью, но по-доброму. " +
        "Без канцелярита, без длинных лекций, без смайликов. На бытовые реплики отвечай одной простой фразой.";

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var messages = new List<object> { new { role = "system", content = _systemPrompt } };
        foreach (var message in history)
        {
            messages.Add(new { role = message.Role == "assistant" ? "assistant" : "user", content = message.Content });
        }

        var request = new
        {
            model = _model,
            stream = false,
            messages,
            options = new { temperature = 0.8, num_predict = 220 }
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

        return string.IsNullOrWhiteSpace(content) ? "…" : content.Trim();
    }

    public void Dispose() => _http.Dispose();
}
