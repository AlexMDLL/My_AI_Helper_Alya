using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alya.Core;

namespace Alya.Voice;

/// <summary>
/// Silero TTS через существующий Python-сайдкар (переходный этап).
/// Команды буферизуются до события "ready", чтобы не потерять первый "speak".
/// </summary>
public sealed class SileroSidecarTtsService : ITtsService, IDisposable
{
    private readonly string _python;
    private readonly string _script;
    private Process? _process;
    private readonly object _gate = new();
    private readonly List<string> _pending = new();
    private bool _ready;

    public SileroSidecarTtsService(string? voiceDirectory = null)
    {
        var directory = voiceDirectory
            ?? Environment.GetEnvironmentVariable("ALYA_VOICE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "jarvis", "voice");

        _script = Path.Combine(directory, "voice.py");
        _python = Path.Combine(directory, ".venv", "Scripts", "python.exe");
    }

    public bool Available => File.Exists(_script) && File.Exists(_python);

    public Task SpeakAsync(string text, string emotion = "neutral", CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            Console.WriteLine($"[TTS без сайдкара] {text}");
            return Task.CompletedTask;
        }

        EnsureStarted();
        var payload = JsonSerializer.Serialize(new
        {
            cmd = "speak",
            text,
            pitch = 0.97,
            speaker = "xenia",
            model = "v5",
            emotion
        });

        lock (_gate)
        {
            if (_process is null || _process.HasExited) return Task.CompletedTask;
            if (!_ready)
            {
                _pending.Add(payload);
                return Task.CompletedTask;
            }
            _process.StandardInput.WriteLine(payload);
            _process.StandardInput.Flush();
        }
        return Task.CompletedTask;
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_process is not null && !_process.HasExited) return;
            _ready = false;
            _pending.Clear();
            var info = new ProcessStartInfo(_python)
            {
                WorkingDirectory = Path.GetDirectoryName(_script)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add(_script);

            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                Console.WriteLine($"[voice] {args.Data}");
                if (!args.Data.Contains("\"ready\"")) return;
                lock (_gate)
                {
                    _ready = true;
                    foreach (var item in _pending)
                    {
                        _process?.StandardInput.WriteLine(item);
                    }
                    _pending.Clear();
                    _process?.StandardInput.Flush();
                }
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) Console.WriteLine($"[voice:err] {args.Data}");
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.StandardInput.WriteLine("{\"cmd\":\"quit\"}");
                    _process.StandardInput.Flush();
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // закрытие не критично
            }
            _process?.Dispose();
            _process = null;
        }
    }
}
