using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Alya.System;
using Alya.Voice;

namespace Alya.App;

public partial class MainWindow : Window
{
    private readonly WindowsSystemController _system = new();
    private readonly TcpSileroTtsService _tts = new();
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
            Position = new PixelPoint(screen.X + screen.Width - 420, screen.Y + 60);
            StatusText.Text = $"голос: {(_tts.Available ? "Silero" : "нет")}";
            CreateTray();
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

        var reply = Reply(text);
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

    private static string Reply(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("привет")) return "Привет. Я пока на C#, но уже с голосом.";
        if (lower.Contains("как дела")) return "Нормально. Интерфейс вот собираю.";
        if (lower.Contains("стим")) return "Steam? Пока не умею, но скоро научусь.";
        return $"Пока я умею немного. Ты сказал: {text}";
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
