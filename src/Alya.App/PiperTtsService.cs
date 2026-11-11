using System.Diagnostics;
using System.Media;
using Alya.Core;

namespace Alya.App;

/// <summary>Piper TTS (ONNX, локально, без Python). Русский женский голос irina.</summary>
public sealed class PiperTtsService : ITtsService, IDisposable
{
    private readonly string _piperExe;
    private readonly string _model;
    private readonly string _espeakData;
    private readonly object _gate = new();
    private SoundPlayer? _player;

    public event Action<string>? OnEvent;
    public double Pitch { get; set; } = 1.12;

    public PiperTtsService(string? root = null)
    {
        var baseDir = root ?? ResolveRoot();
        _piperExe = Path.Combine(baseDir, "piper", "piper.exe");
        _model = Path.Combine(baseDir, "voices", "ru_RU-irina-medium.onnx");
        _espeakData = Path.Combine(baseDir, "piper", "espeak-ng-data");
    }

    public bool Available => File.Exists(_piperExe) && File.Exists(_model) && Directory.Exists(_espeakData);

    private static string ResolveRoot()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "piper");
        if (Directory.Exists(bundled)) return bundled;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects", "alya-next", "tools", "piper");
    }

    public Task SpeakAsync(string text, string emotion = "neutral", CancellationToken cancellationToken = default)
    {
        if (!Available || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        var wav = Path.Combine(Path.GetTempPath(), $"alya-piper-{Guid.NewGuid():N}.wav");

        lock (_gate)
        {
            try
            {
                var info = new ProcessStartInfo(_piperExe)
                {
                    WorkingDirectory = Path.Combine(Path.GetDirectoryName(_piperExe)!),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add("--model");
                info.ArgumentList.Add(_model);
                info.ArgumentList.Add("--output_file");
                info.ArgumentList.Add(wav);
                info.ArgumentList.Add("--espeak_data");
                info.ArgumentList.Add(_espeakData.Replace('\\', '/'));
                info.ArgumentList.Add("--length_scale");
                info.ArgumentList.Add("1.08");
                info.ArgumentList.Add("--noise_w");
                info.ArgumentList.Add("0.75");

                using var process = Process.Start(info);
                if (process is null) return Task.CompletedTask;
                process.StandardInput.Write(text);
                process.StandardInput.Close();
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(12000);
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(wav)) return;
                PitchShift(wav, 1.0 + (Pitch - 1.0) * 0.6);
                OnEvent?.Invoke("tts_start");
                using var player = new SoundPlayer(wav);
                _player = player;
                player.PlaySync();
            }
            catch
            {
                // не смогли проиграть
            }
            finally
            {
                OnEvent?.Invoke("tts_end");
                try
                {
                    File.Delete(wav);
                }
                catch
                {
                    // ignore
                }
            }
        });

        return Task.CompletedTask;
    }

    private static void PitchShift(string path, double factor)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44) return;
            var dataPos = -1;
            var dataSize = 0;
            var channels = 1;
            var bits = 16;
            var pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                var id = global::System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                var size = BitConverter.ToInt32(bytes, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                }
                if (id == "data")
                {
                    dataPos = pos + 8;
                    dataSize = size;
                    break;
                }
                pos += 8 + size + (size % 2);
            }
            if (dataPos < 0 || bits != 16) return;
            var frameBytes = channels * 2;
            var frames = dataSize / frameBytes;
            var newFrames = (int)(frames / factor);
            if (newFrames <= 0) return;

            var shifted = new byte[newFrames * frameBytes];
            for (var i = 0; i < newFrames; i++)
            {
                var source = i * factor;
                var index = (int)source;
                var fraction = source - index;
                var next = Math.Min(index + 1, frames - 1);
                for (var channel = 0; channel < channels; channel++)
                {
                    var a = BitConverter.ToInt16(bytes, dataPos + index * frameBytes + channel * 2);
                    var b = BitConverter.ToInt16(bytes, dataPos + next * frameBytes + channel * 2);
                    var sample = (int)Math.Round(a + (b - a) * fraction);
                    shifted[i * frameBytes + channel * 2] = (byte)(sample & 0xFF);
                    shifted[i * frameBytes + channel * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }
            }

            var header = new byte[dataPos];
            Array.Copy(bytes, header, dataPos);
            var total = dataPos + shifted.Length;
            BitConverter.GetBytes(total - 8).CopyTo(header, 4);
            BitConverter.GetBytes(shifted.Length).CopyTo(header, dataPos - 4);
            using var output = File.Create(path);
            output.Write(header);
            output.Write(shifted);
        }
        catch
        {
            // если не получилось — играем как есть
        }
    }
    public void Stop()
    {
        try { _player?.Stop(); } catch { }
    }

    public void Dispose()
    {
        // ресурсов не держим
    }
}
