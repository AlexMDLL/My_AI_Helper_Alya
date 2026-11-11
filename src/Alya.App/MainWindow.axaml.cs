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
        var frame = Environment.GetEnvironmentVariable("ALYA_FRAME")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Projects", "jarvis", "resources", "anim", "VideoStand", "0000.webp");
        if (!File.Exists(frame)) return;
        try
        {
            CharacterImage.Source = new Avalonia.Media.Imaging.Bitmap(frame);
        }
        catch
        {
            // кадр не загрузился — окно всё равно работает
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
