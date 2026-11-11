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
    private readonly Dictionary<int, Avalonia.Media.Imaging.Bitmap> _cache = new();
    private string[] _frames = Array.Empty<string>();
    private int _frame;
    private DispatcherTimer? _animTimer;
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
        var directory = Environment.GetEnvironmentVariable("ALYA_ANIM_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Projects", "jarvis", "resources", "anim", "VideoStand");
        if (!Directory.Exists(directory)) return;

        _frames = Directory.GetFiles(directory, "*.webp")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_frames.Length == 0) return;

        ShowFrame(0);
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(42) };
        _animTimer.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % _frames.Length;
            ShowFrame(_frame);
        };
        _animTimer.Start();
    }

    private void ShowFrame(int index)
    {
        if (_frames.Length == 0) return;
        if (!_cache.TryGetValue(index, out var bitmap))
        {
            try
            {
                bitmap = new Avalonia.Media.Imaging.Bitmap(_frames[index]);
            }
            catch
            {
                return;
            }
            _cache[index] = bitmap;
            TrimCache(index);
        }
        CharacterImage.Source = bitmap;
    }

    private void TrimCache(int current)
    {
        if (_cache.Count <= 60) return;
        foreach (var key in _cache.Keys.ToList())
        {
            var distance = Math.Abs(key - current);
            if (distance > 40 && distance < _frames.Length - 40)
            {
                _cache.Remove(key);
            }
        }
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
            await _tts.SpeakAsync(reply, "neutral");
        }
        finally
        {
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
