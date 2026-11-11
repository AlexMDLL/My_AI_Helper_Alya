using Alya.Core;

namespace Alya.Voice;

/// <summary>
/// Заглушка Silero TTS. Позже: ONNX Runtime + модель silero v5 (xenia).
/// </summary>
public sealed class SileroTtsService : ITtsService
{
    public Task SpeakAsync(string text, string emotion = "neutral", CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[TTS:{emotion}] {text}");
        return Task.CompletedTask;
    }

    public void Stop() { }
}

/// <summary>
/// Заглушка ASR. Позже: sherpa-onnx streaming Russian.
/// </summary>
public sealed class SherpaAsrService : IAsrService
{
    public Task<string?> ListenAsync(CancellationToken cancellationToken = default)
    {
        Console.Write("[ASR] слушаю... ");
        var line = Console.ReadLine();
        return Task.FromResult(string.IsNullOrWhiteSpace(line) ? null : line);
    }
}
