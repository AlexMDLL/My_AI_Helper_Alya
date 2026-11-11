using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Alya.Core;

namespace Alya.Voice;

/// <summary>
/// Silero TTS через TCP-мост (voice/voice_bridge.py).
/// Надёжнее stdin-пайпа и не мешает рабочему Electron-приложению.
/// </summary>
public sealed class TcpSileroTtsService : ITtsService, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _python;
    private readonly string _bridge;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly object _gate = new();
    private Process? _server;

    public TcpSileroTtsService(string? voiceDirectory = null, int port = 47653)
    {
        var directory = voiceDirectory
            ?? Environment.GetEnvironmentVariable("ALYA_VOICE_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "jarvis", "voice");

        _bridge = Path.Combine(directory, "voice_bridge.py");
        _python = Path.Combine(directory, ".venv", "Scripts", "python.exe");
        _host = "127.0.0.1";
        _port = port;
    }

    public bool Available => File.Exists(_bridge) && File.Exists(_python);
    public string Speaker { get; set; } = "xenia";
    public double Pitch { get; set; } = 0.97;

    public Task SpeakAsync(string text, string emotion = "neutral", CancellationToken cancellationToken = default)
    {
        if (!Available)
        {
            Console.WriteLine($"[TTS без моста] {text}");
            return Task.CompletedTask;
        }

        var payload = JsonSerializer.Serialize(new
        {
            cmd = "speak",
            text,
            pitch = Pitch,
            speaker = Speaker,
            model = "v5",
            emotion
        });

        Send(payload);
        return Task.CompletedTask;
    }

    private void Send(string payload)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                EnsureConnected();
                if (_writer is null || _reader is null) return;
                try
                {
                    _writer.WriteLine(payload);
                    _writer.Flush();
                    var ack = _reader.ReadLine();
                    if (!string.IsNullOrEmpty(ack)) return;
                }
                catch
                {
                    // мост не ответил — переподключаемся и пробуем ещё раз
                }
                Close();
                Thread.Sleep(200);
            }
        }
    }

    private void EnsureConnected()
    {
        if (_client is { Connected: true } && _writer is not null) return;
        Close();
        StartServerIfNeeded();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                _client = new TcpClient();
                _client.Connect(_host, _port);
                _client.ReceiveTimeout = 15000;
                var stream = _client.GetStream();
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
                return;
            }
            catch
            {
                Thread.Sleep(300);
            }
        }
    }

    private void StartServerIfNeeded()
    {
        if (_server is { HasExited: false }) return;
        var info = new ProcessStartInfo(_python)
        {
            WorkingDirectory = Path.GetDirectoryName(_bridge)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add(_bridge);
        _server = new Process { StartInfo = info, EnableRaisingEvents = true };
        _server.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Console.WriteLine($"[voice-bridge] {args.Data}");
        };
        _server.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Console.WriteLine($"[voice-bridge:err] {args.Data}");
        };
        _server.Start();
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _client?.Dispose();
        }
        catch
        {
            // ignore
        }
        _writer = null;
        _reader = null;
        _client = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
            try
            {
                if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
            _server?.Dispose();
            _server = null;
        }
    }
}
