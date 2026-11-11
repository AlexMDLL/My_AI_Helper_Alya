using System.Speech.Synthesis;
using Alya.Core;

namespace Alya.App;

/// <summary>Резервный голос Windows (SAPI), если Python-мост Silero недоступен.</summary>
public sealed class SapiTtsService : ITtsService, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly object _gate = new();

    public event Action<string>? OnEvent;

    public SapiTtsService()
    {
        try
        {
            var russian = _synth.GetInstalledVoices()
                .FirstOrDefault(voice => voice.VoiceInfo.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
            if (russian is not null) _synth.SelectVoice(russian.VoiceInfo.Name);
        }
        catch
        {
            // оставляем голос по умолчанию
        }

        _synth.SpeakCompleted += (_, _) => OnEvent?.Invoke("tts_end");
    }

    public Task SpeakAsync(string text, string emotion = "neutral", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        lock (_gate)
        {
            try
            {
                _synth.Rate = 1;
                _synth.SpeakAsyncCancelAll();
                OnEvent?.Invoke("tts_start");
                _synth.SpeakAsync(text);
            }
            catch
            {
                OnEvent?.Invoke("tts_end");
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            _synth.SpeakAsyncCancelAll();
            _synth.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
