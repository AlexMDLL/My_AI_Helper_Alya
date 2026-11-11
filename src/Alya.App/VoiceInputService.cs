using System.Text;
using Avalonia.Threading;
using NAudio.Wave;
using Whisper.net;

namespace Alya.App;

/// <summary>Микрофон + VAD + Whisper (офлайн-распознавание русской речи).</summary>
public sealed class VoiceInputService : IDisposable
{
    private readonly string _modelPath;
    private readonly WaveInEvent _wave;
    private readonly MemoryStream _buffer = new();
    private readonly object _gate = new();
    private WhisperProcessor? _processor;
    private DateTime _lastVoice = DateTime.MinValue;
    private bool _hadSpeech;
    private bool _busy;
    private DateTime _lastLevelLog = DateTime.MinValue;
    private DateTime _lastUsed = DateTime.MinValue;
    private DispatcherTimer? _idleTimer;
    private readonly object _processorGate = new();
    private bool _pushToTalk;
    private int _sampleRate = 16000;

    public event Action<string>? TextRecognized;
    public event Action<bool>? ListeningChanged;

    public VoiceInputService(string? modelPath = null)
    {
        var candidates = new List<string>();
        var custom = Environment.GetEnvironmentVariable("ALYA_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "whisper", "ggml-small.bin"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "whisper", "ggml-base.bin"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "alya-next", "tools", "whisper", "ggml-small.bin"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "alya-next", "tools", "whisper", "ggml-base.bin"));
        _modelPath = modelPath ?? candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
        _wave = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _wave.DataAvailable += OnData;
    }

    public bool Available => File.Exists(_modelPath);
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "alya-voice.log");

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}\n"); } catch { }
    }

    public void Start()
    {
        if (!Available) return;
        try
        {
            Log($"start model={_modelPath} exists={File.Exists(_modelPath)}");
            _processor ??= WhisperFactory.FromPath(_modelPath).CreateBuilder().WithLanguage("ru").Build();
            Log("whisper loaded");
            _wave.StartRecording();
            Log("mic started");
            _lastUsed = DateTime.Now;
            _idleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _idleTimer.Tick += (_, _) =>
            {
                if ((DateTime.Now - _lastUsed).TotalMinutes < 10) return;
                if (_processor is null) return;
                _processor.Dispose();
                _processor = null;
                Log("whisper unloaded (idle)");
            };
            _idleTimer.Start();
            ListeningChanged?.Invoke(true);
        }
        catch (Exception error)
        {
            Log($"start failed: {error.Message}");
        }
    }

    private void EnsureProcessor()
    {
        if (_processor is not null) return;
        lock (_processorGate)
        {
            if (_processor is not null) return;
            try
            {
                _processor = WhisperFactory.FromPath(_modelPath).CreateBuilder().WithLanguage("ru").Build();
                Log("whisper reloaded");
            }
            catch (Exception error)
            {
                Log($"whisper reload failed: {error.Message}");
            }
        }
    }
    public void Stop()
    {
        try
        {
            _wave.StopRecording();
        }
        catch
        {
            // ignore
        }
        ListeningChanged?.Invoke(false);
    }

    public void BeginPushToTalk()
    {
        lock (_gate)
        {
            _buffer.SetLength(0);
            _pushToTalk = true;
            _hadSpeech = true;
            _lastUsed = DateTime.Now;
        }
        try { _wave.StartRecording(); } catch { }
    }

    public void EndPushToTalk()
    {
        byte[] audio;
        lock (_gate)
        {
            _pushToTalk = false;
            audio = _buffer.ToArray();
            _buffer.SetLength(0);
        }
        try { _wave.StopRecording(); } catch { }
        if (audio.Length < _sampleRate * 2 * 0.3) return;
        _ = Task.Run(() => Transcribe(audio));
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        if (_pushToTalk)
        {
            lock (_gate) _buffer.Write(args.Buffer, 0, args.BytesRecorded);
            return;
        }
        var chunk = new byte[args.BytesRecorded];
        Array.Copy(args.Buffer, chunk, args.BytesRecorded);
        var level = Rms(chunk);
        var hasVoice = level > 0.004f;

        lock (_gate)
        {
            _buffer.Write(chunk, 0, chunk.Length);
            if (hasVoice)
            {
                _hadSpeech = true;
                _lastVoice = DateTime.Now;
            }

            if (!_hadSpeech)
            {
                if (_buffer.Length > _sampleRate * 2 * 2)
                {
                    var keep = (int)(_sampleRate * 2 * 1.2);
                    var data = _buffer.ToArray();
                    _buffer.SetLength(0);
                    _buffer.Write(data, data.Length - keep, keep);
                }
                return;
            }
            if (_busy) return;
            if ((DateTime.Now - _lastVoice).TotalMilliseconds < 700) return;
            if (_buffer.Length < _sampleRate * 2 * 0.3) return;

            _hadSpeech = false;
            _busy = true;
            var audio = _buffer.ToArray();
            _buffer.SetLength(0);
            _ = Task.Run(() => Transcribe(audio));
        }
    }

    private async Task Transcribe(byte[] audio)
    {
        try
        {
            var samples = new float[audio.Length / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(audio, i * 2) / 32768f;
            }

            EnsureProcessor();
            if (_processor is null) return;
            var builder = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(samples))
            {
                builder.Append(segment.Text).Append(' ');
            }
            _lastUsed = DateTime.Now;
            var text = builder.ToString().Trim();
            Log($"heard: {text}");
            if (text.Length >= 2) TextRecognized?.Invoke(text);
        }
        catch (Exception error)
        {
            Log($"transcribe failed: {error.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static float Rms(byte[] buffer)
    {
        var sum = 0.0;
        var count = buffer.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            sum += sample * sample;
        }
        return count == 0 ? 0 : (float)Math.Sqrt(sum / count);
    }

    public void Dispose()
    {
        try
        {
            _wave.StopRecording();
            _wave.Dispose();
        }
        catch
        {
            // ignore
        }
        _processor?.Dispose();
    }
}
