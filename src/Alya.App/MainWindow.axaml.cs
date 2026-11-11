using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Alya.Core;
using Alya.Llm;
using Alya.System;
using Alya.Voice;

namespace Alya.App;

public partial class MainWindow : Window
{
    private readonly WindowsSystemController _system = new();
    private readonly TcpSileroTtsService _tts = new();
    private readonly OllamaLlmClient _llm = new();
    private readonly List<ChatMessage> _history = new();
    private readonly Dictionary<string, string[]> _scenes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> _cache = new();
    private readonly Queue<string> _cacheOrder = new();
    private string[] _frames = Array.Empty<string>();
    private string _scene = "VideoStand";
    private int _frame;
    private int _oneShotLeft;
    private bool _talking;
    private bool _prefetching;
    private readonly object _cacheLock = new();
    private DispatcherTimer? _animTimer;
    private DispatcherTimer? _gestureTimer;
    private DispatcherTimer? _fallTimer;
    private bool _dragging;
    private Point _dragOffset;
    private double _fallY;
    private double _fallVelocity;
    private int _fallX;
    private readonly ObservableCollection<Bubble> _messages = new();
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _messages;

        SendButton.Click += (_, _) => Send();
        InputBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Send();
                args.Handled = true;
            }
        };
        CloseButton.Click += (_, _) => Hide();
        CharacterImage.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(CharacterImage);
            if (!point.Properties.IsLeftButtonPressed) return;
            _dragging = true;
            var screen = ScreenPoint(args.GetPosition(CharacterImage));
            _dragOffset = new Point(screen.X - Position.X, screen.Y - Position.Y);
            args.Pointer.Capture(CharacterImage);
            if (_fallTimer is not null)
            {
                _fallTimer.Stop();
                _fallTimer = null;
            }
        };

        CharacterImage.PointerMoved += (_, args) =>
        {
            if (!_dragging) return;
            var screen = ScreenPoint(args.GetPosition(CharacterImage));
            Position = new PixelPoint(
                (int)Math.Round(screen.X - _dragOffset.X),
                (int)Math.Round(screen.Y - _dragOffset.Y));
        };

        CharacterImage.PointerReleased += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            StartFall();
        };


        Opened += (_, _) =>
        {
            var screen = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            Position = new PixelPoint(screen.X + screen.Width - 800, screen.Y + 60);
            StatusText.Text = $"голос: {(_tts.Available ? "Silero" : "нет")}";
            LoadCharacter();
            CreateTray();
            var ask = Environment.GetEnvironmentVariable("ALYA_ASK");
            if (!string.IsNullOrWhiteSpace(ask))
            {
                InputBox.Text = ask;
                Send();
            }
        };
    }

    private void CreateTray()
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem { Header = "Показать", Command = new SimpleCommand(ShowWindow) });
        menu.Items.Add(new NativeMenuItem { Header = "Скрыть", Command = new SimpleCommand(Hide) });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem { Header = "Выход", Command = new SimpleCommand(Close) });

        _tray = new TrayIcon
        {
            ToolTipText = "Аля",
            Menu = menu
        };
        _tray.Clicked += (_, _) => ShowWindow();
    }

    private void LoadCharacter()
    {
        var root = Environment.GetEnvironmentVariable("ALYA_ANIM_ROOT")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Projects", "jarvis", "resources", "anim");
        if (!Directory.Exists(root)) return;

        foreach (var name in new[] { "VideoStand", "VideoTalk", "VideoWink", "VideoDeny", "VideoSurprise", "VideoShyJoy" })
        {
            var directory = Path.Combine(root, name);
            if (!Directory.Exists(directory)) continue;
            var frames = Directory.GetFiles(directory, "*.webp")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (frames.Length > 0) _scenes[name] = frames;
        }
        if (!_scenes.ContainsKey("VideoStand")) return;

        PlayScene("VideoStand");
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _animTimer.Tick += (_, _) => Tick();
        _animTimer.Start();

        _gestureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _gestureTimer.Tick += (_, _) =>
        {
            if (_talking || !_scene.Equals("VideoStand", StringComparison.OrdinalIgnoreCase)) return;
            var gestures = new[] { "VideoWink", "VideoDeny", "VideoSurprise", "VideoShyJoy" };
            PlayScene(gestures[Random.Shared.Next(gestures.Length)], oneShot: true);
        };
        _gestureTimer.Start();
    }

    private void PlayScene(string name, bool oneShot = false)
    {
        if (!_scenes.TryGetValue(name, out var frames)) return;
        _scene = name;
        _frames = frames;
        _frame = 0;
        _oneShotLeft = oneShot ? frames.Length : 0;
        ShowFrame(0);
    }

    private void Tick()
    {
        if (_frames.Length == 0) return;
        if (_oneShotLeft > 0)
        {
            _oneShotLeft--;
            if (_oneShotLeft == 0)
            {
                PlayScene("VideoStand");
                return;
            }
        }
        _frame = (_frame + 1) % _frames.Length;
        ShowFrame(_frame);
    }

    private void ShowFrame(int index)
    {
        if (_frames.Length == 0) return;
        var path = _frames[index];
        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        lock (_cacheLock)
        {
            _cache.TryGetValue(path, out bitmap);
        }
        if (bitmap is null)
        {
            try
            {
                bitmap = new Avalonia.Media.Imaging.Bitmap(path);
            }
            catch
            {
                return;
            }
            lock (_cacheLock)
            {
                _cache[path] = bitmap;
                _cacheOrder.Enqueue(path);
                while (_cacheOrder.Count > 240)
                {
                    var old = _cacheOrder.Dequeue();
                    if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) _cache.Remove(old);
                }
            }
        }
        CharacterImage.Source = bitmap;
        Prefetch();
    }

    private void Prefetch()
    {
        if (_prefetching || _frames.Length == 0) return;
        _prefetching = true;
        var snapshot = _frames;
        var start = _frame;
        Task.Run(() =>
        {
            try
            {
                for (var offset = 1; offset <= 90; offset++)
                {
                    var index = (start + offset) % snapshot.Length;
                    var path = snapshot[index];
                    lock (_cacheLock)
                    {
                        if (_cache.ContainsKey(path)) continue;
                    }
                    try
                    {
                        var bitmap = new Avalonia.Media.Imaging.Bitmap(path);
                        lock (_cacheLock)
                        {
                            _cache[path] = bitmap;
                            _cacheOrder.Enqueue(path);
                            while (_cacheOrder.Count > 240)
                            {
                                var old = _cacheOrder.Dequeue();
                                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) _cache.Remove(old);
                            }
                        }
                    }
                    catch
                    {
                        // отдельный кадр не критичен
                    }
                }
            }
            finally
            {
                _prefetching = false;
            }
        });
    }

    private Point ScreenPoint(Point local)
    {
        var offset = CharacterImage.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        return new Point(
            Position.X + (offset.X + local.X) * RenderScaling,
            Position.Y + (offset.Y + local.Y) * RenderScaling);
    }
    private void StartFall()
    {
        if (Screens.Primary is null) return;
        var area = Screens.Primary.WorkingArea;
        _fallX = Math.Clamp(Position.X, area.X, area.X + area.Width - (int)Width);
        var floor = area.Y + area.Height - (int)Height - 4;
        _fallY = Position.Y;
        if (_fallY >= floor)
        {
            Position = new PixelPoint(_fallX, floor);
            return;
        }
        _fallVelocity = 0;
        _fallTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _fallTimer.Tick += (_, _) =>
        {
            _fallVelocity += 1.2;
            _fallY += _fallVelocity;
            if (_fallY >= floor)
            {
                _fallY = floor;
                _fallVelocity = -_fallVelocity * 0.32;
                if (Math.Abs(_fallVelocity) < 1.6)
                {
                    if (_fallTimer is not null)
                    {
                        _fallTimer.Stop();
                        _fallTimer = null;
                    }
                }
            }
            Position = new PixelPoint(_fallX, (int)Math.Round(_fallY));
        };
        _fallTimer.Start();
    }
    private void ShowWindow()
    {
        Show();
        Activate();
    }

    private async void Send()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Text = string.Empty;
        Add("user", text);
        _history.Add(new ChatMessage("user", text, DateTimeOffset.Now));

        StatusText.Text = "думает…";
        string reply;
        try
        {
            reply = await _llm.CompleteAsync(_history);
        }
        catch (Exception error)
        {
            reply = $"Не получилось подумать: {error.Message}";
        }
        _history.Add(new ChatMessage("assistant", reply, DateTimeOffset.Now));
        Add("alya", reply);
        StatusText.Text = "говорит…";
        try
        {
            _talking = true;
            StatusText.Text = "говорит…";
            await _tts.SpeakAsync(reply, "neutral");
            await Task.Delay(500);
            PlayScene("VideoTalk");
            var duration = Math.Clamp(reply.Length * 65, 1200, 12000);
            await Task.Delay(duration);
        }
        finally
        {
            _talking = false;
            PlayScene("VideoStand");
            StatusText.Text = "на связи";
        }
    }

    private void Add(string role, string text)
    {
        var isUser = role == "user";
        _messages.Add(new Bubble
        {
            Text = text,
            Time = DateTime.Now.ToString("HH:mm"),
            Alignment = isUser ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left,
            Background = isUser
                ? new SolidColorBrush(Color.Parse("#3E6F9E"))
                : new SolidColorBrush(Color.Parse("#1B2C42"))
        });
        Dispatcher.UIThread.Post(() =>
        {
            var scroll = MessagesList.Parent as ScrollViewer;
            scroll?.ScrollToEnd();
        });
    }

    private sealed class SimpleCommand : global::System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public SimpleCommand(Action action) => _action = action;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
