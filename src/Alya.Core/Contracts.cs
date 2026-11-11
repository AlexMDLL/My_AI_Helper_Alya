namespace Alya.Core;

/// <summary>Состояния персонажа.</summary>
public enum AlyaState
{
    Idle,
    Listening,
    Thinking,
    Speaking
}

/// <summary>Одно сообщение диалога.</summary>
public sealed record ChatMessage(string Role, string Content, DateTimeOffset At);

/// <summary>Распознавание речи (позже — sherpa-onnx).</summary>
public interface IAsrService
{
    Task<string?> ListenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Синтез речи (позже — Silero через ONNX Runtime).</summary>
public interface ITtsService
{
    Task SpeakAsync(string text, string emotion = "neutral", CancellationToken cancellationToken = default);
    void Stop();
}

/// <summary>Языковая модель (Ollama / OpenRouter / DeepSeek).</summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default);
}

/// <summary>Управление Windows: процессы, окна, запуск, громкость.</summary>
public interface ISystemController
{
    string? ForegroundProcess();
    IReadOnlyList<string> RunningProcesses();
    bool Launch(string target);
    bool Close(string target);
    bool OpenUri(string uri);
    int SetAppVolume(string app, int percent);
    int GetAppVolume(string app);
}

public static class AlyaInfo
{
    public const string Name = "Alya";
    public const string Version = "0.1.0-preview";
}
