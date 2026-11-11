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
            var voices = _synth.GetInstalledVoices().Select(voice => voice.VoiceInfo).ToList();
            var preferred = voices.FirstOrDefault(voice =>
                    voice.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase) &&
                    (voice.Name.Contains("Irina", StringComparison.OrdinalIgnoreCase) ||
                     voice.Name.Contains("Elena", StringComparison.OrdinalIgnoreCase)))
                ?? voices.FirstOrDefault(voice => voice.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                ?? voices.FirstOrDefault(voice => voice.Gender == VoiceGender.Female);
            if (preferred is not null) _synth.SelectVoice(preferred.Name);
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
                _synth.Rate = 0;
                _synth.SpeakAsyncCancelAll();
                OnEvent?.Invoke("tts_start");
                var safe = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                var ssml = $"<speak version=\"1.0\" xml:lang=\"ru-RU\"><prosody pitch=\"+18%\" rate=\"-4%\">{safe}</prosody></speak>";
                _synth.SpeakSsmlAsync(ssml);
            }
            catch
            {
                OnEvent?.Invoke("tts_end");
            }
        }
        return Task.CompletedTask;
    }

    public void Stop()
    {
        try { _synth.SpeakAsyncCancelAll(); } catch { }
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
