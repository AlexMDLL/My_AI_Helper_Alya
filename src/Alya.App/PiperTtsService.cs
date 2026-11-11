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

    public event Action<string>? OnEvent;

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
                OnEvent?.Invoke("tts_start");
                using var player = new SoundPlayer(wav);
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

    public void Dispose()
    {
        // ресурсов не держим
    }
}
