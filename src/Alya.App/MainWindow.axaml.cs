using System.Collections.ObjectModel;
using System.Diagnostics;
using Bitmap = System.Drawing.Bitmap;
using Graphics = System.Drawing.Graphics;
using Size = System.Drawing.Size;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using Tesseract;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
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
    private readonly SteamLibrary _steam = new();
    private readonly WeatherService _weather = new();
    private readonly MemoryStore _memory = MemoryStore.Load();
    private readonly TcpSileroTtsService _silero = new();
    private readonly SapiTtsService _sapi = new();
    private readonly PiperTtsService _piper = new();
    private ITtsService _tts => _silero.Available ? _silero : _piper.Available ? _piper : _sapi;
    private OllamaLlmClient _llm = new();
    private AlyaSettings _settings = new();
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
    private bool _dragMoved;
    private bool _hiddenByFilter;
    private bool _initialized;
    private string? _pendingPower;
    private DateTime _pendingPowerAt;
    private DateTime _talkingStarted;
    private DispatcherTimer? _pttTimer;
    private bool _pttDown;
    private CancellationTokenSource? _llmCts;
    private DispatcherTimer? _pttResetTimer;
    private readonly List<(DateTime Due, string Message)> _reminders = new();
    private DispatcherTimer? _reminderTimer;
    private LlmTools? _tools;
    private DateTime _lastActivity = DateTime.Now;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _bubbleTimer;
    private Point _dragOffset;
    private double _fallY;
    private double _fallVelocity;
    private int _fallX;
    private string _touchMode = "drag";
    private bool _lockDrag;
    private DateTime _lastPetAt = DateTime.MinValue;
    private DispatcherTimer? _focusTimer;
    private string _lastFocusCategory = string.Empty;
    private readonly HashSet<string> _knownPrograms = new(StringComparer.OrdinalIgnoreCase);
    private bool _programsInitialized;
    private DateTime _lastFocusReactionAt = DateTime.MinValue;
    private static readonly string[] GameProcesses =
    {
        "cs2", "dota2", "deadbydaylight", "fortniteclient", "gta5", "rdr2", "eldenring",
        "minecraft", "valorant", "leagueclient", "genshinimpact", "warthunder", "rustclient",
        "dayz", "pubg", "apex", "starfield", "cyberpunk2077", "witcher3", "skyrim",
        "fallout4", "pathofexile", "wow", "overwatch", "battlefield"
    };
    private static readonly string[] WorkProcesses =
    {
        "devenv", "code", "photoshop", "illustrator", "figma", "winword", "excel", "powerpnt",
        "notion", "obsidian", "idea64", "pycharm64", "webstorm64", "blender", "unity", "rider64"
    };
    private readonly ObservableCollection<Bubble> _messages = new();
    private readonly TranslateTransform _panelOffset = new();
    private bool _panelDragging;
    private Point _panelStart;
    private double _panelStartX;
    private DispatcherTimer? _clickTimer;
    private bool _clickThrough;
    private IntPtr _hwnd;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
    private readonly VoiceInputService _voiceInput = new();
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AlyaSettings.Load();
        _llm = new OllamaLlmClient(_settings.Model) { Temperature = _settings.Temperature, Persona = _settings.Persona };
        _silero.Speaker = _settings.Speaker;
        _silero.Pitch = _settings.Pitch;
        _piper.Pitch = _settings.Pitch;
        _silero.OnEvent += kind => Dispatcher.UIThread.Post(() => HandleVoiceEvent(kind));
        _sapi.OnEvent += kind => Dispatcher.UIThread.Post(() => HandleVoiceEvent(kind));
        _piper.OnEvent += kind => Dispatcher.UIThread.Post(() => HandleVoiceEvent(kind));
        _llm.ExtraContext = BuildContext();
        ApplyAccent();
        ApplyWindowMode();
        Opacity = Math.Clamp(_settings.Opacity, 0.6, 1);
        MessagesList.ItemsSource = _messages;
        ChatPanel.IsVisible = false;
        var memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        memoryTimer.Tick += (_, _) =>
        {
            try
            {
                lock (_cacheLock)
                {
                    foreach (var key in _cache.Keys.ToList())
                    {
                        if (!_frames.Contains(key)) _cache.Remove(key);
                    }
                    while (_cache.Count > 60)
                    {
                        var oldest = _cacheOrder.Count > 0 ? _cacheOrder.Dequeue() : null;
                        if (oldest is null) break;
                        if (!string.Equals(oldest, _frames[_frame], StringComparison.OrdinalIgnoreCase)) _cache.Remove(oldest);
                    }
                }
                GC.Collect(2, GCCollectionMode.Optimized, false);
            }
            catch
            {
                // очистка не критична
            }
        };
        memoryTimer.Start();
        var talkWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        talkWatchdog.Tick += (_, _) =>
        {
            if (!_talking) return;
            if ((DateTime.Now - _talkingStarted).TotalSeconds < 30) return;
            _talking = false;
            StatusText.Text = "на связи";
            PlayScene("VideoStand");
        };
        talkWatchdog.Start();
        _pttTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pttTimer.Tick += (_, _) =>
        {
            if (!_settings.PushToTalk) return;
            var pressed = (GetAsyncKeyState(0x06) & 0x8000) != 0;
            // 0x06 = Mouse5 (XBUTTON2)
            if (pressed && !_pttDown)
            {
                _pttDown = true;
                if (_talking) StopSpeech();
                _voiceInput.BeginPushToTalk();
                StatusText.Text = "слушаю…";
            }
            else if (!pressed && _pttDown)
            {
                _pttDown = false;
                _voiceInput.EndPushToTalk();
                StatusText.Text = "думает…";
                _pttResetTimer?.Stop();
                _pttResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                _pttResetTimer.Tick += (_, _) =>
                {
                    _pttResetTimer?.Stop();
                    if (StatusText.Text == "думает…") StatusText.Text = "на связи";
                };
                _pttResetTimer.Start();
            }
        };
        _pttTimer.Start();
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();
        _tools = new LlmTools(_system, _steam, _weather, _reminders);

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) =>
        {
            var fresh = AlyaSettings.Load();

            if (_settingsWindow is not null) return;
            if (!ChatPanel.IsVisible) return;
            if (fresh.ChatAutoHideSeconds <= 0) return;
            if ((DateTime.Now - _lastActivity).TotalSeconds < fresh.ChatAutoHideSeconds) return;
            SetChatVisible(false);
        };
        _hideTimer.Start();
        var hideFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        hideFilterTimer.Tick += (_, _) =>
        {
            try
            {
                var process = _system.ForegroundProcess() ?? string.Empty;
                var title = _system.ForegroundWindowTitle();
                var shouldHide = (_settings.HideInGames && GameProcesses.Any(process.Contains))
                    || (_settings.HideInYoutube && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
                    || (_settings.HideInVisualStudio && (process.Contains("devenv") || title.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)));
                if (shouldHide && !_hiddenByFilter)
                {
                    _hiddenByFilter = true;
                    Hide();
                }
                else if (!shouldHide && _hiddenByFilter)
                {
                    _hiddenByFilter = false;
                    Show();
                }
            }
            catch
            {
                // окно может быть уже закрыто
            }
        };
        hideFilterTimer.Start();
        var taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        taskbarTimer.Tick += (_, _) =>
        {
            if (_settings.Fullscreen || _dragging || Screens.Primary is null) return;
            var work = Screens.Primary.WorkingArea;
            var floor = work.Y + work.Height - (int)Height;
            if (Math.Abs(Position.Y - floor) > 120) return;
            if (Position.Y == floor) return;
            Position = new PixelPoint(Position.X, floor);
        };
        taskbarTimer.Start();


        ChatPanel.RenderTransform = _panelOffset;
        try
        {
            Icon = new WindowIcon(global::System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "alya.ico"));
        }
        catch
        {
            // иконка не критична
        }
        _history.AddRange(HistoryStore.Load());
        foreach (var old in _history.TakeLast(6))
        {
            Add(old.Role == "user" ? "user" : "alya", old.Content);
        }
        ChatPanel.IsVisible = false;
        SpeechBubble.IsVisible = false;

        SendButton.Click += (_, _) => Send();
        InputBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                Send();
                args.Handled = true;
            }
        };
        CloseButton.Click += (_, _) => SetChatVisible(false);
        HeaderBar.PointerPressed += (_, args) =>
        {
            if (args.Source is Button) return;
            _panelDragging = true;
            _panelStart = args.GetPosition(this);
            _panelStartX = _panelOffset.X;
            args.Pointer.Capture(HeaderBar);
        };
        HeaderBar.PointerMoved += (_, args) =>
        {
            if (!_panelDragging) return;
            var delta = args.GetPosition(this).X - _panelStart.X;
            var max = Math.Max(0, Bounds.Width - ChatPanel.Bounds.Width - 20);
            _panelOffset.X = Math.Clamp(_panelStartX + delta, 0, max);
        };
        HeaderBar.PointerReleased += (_, _) => _panelDragging = false;

        SettingsButton.Click += (_, _) => OpenSettings();
        CharacterImage.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(CharacterImage);
            if (!point.Properties.IsLeftButtonPressed) return;
            if (args.ClickCount == 2)
            {
                SetChatVisible(!ChatPanel.IsVisible);
                return;
            }
            if (!_settings.DragEnabled || _lockDrag || _touchMode == "pet") return;
            _dragging = true;
            _dragMoved = false;
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
            if (_settings.PetEnabled && _touchMode == "pet" && args.GetCurrentPoint(CharacterImage).Properties.IsLeftButtonPressed)
            {
                if ((DateTime.Now - _lastPetAt).TotalSeconds < 4) return;
                _lastPetAt = DateTime.Now;
                var local = args.GetPosition(CharacterImage);
                var zone = local.Y / Math.Max(1, CharacterImage.Bounds.Height) < 0.32 ? "head" : "body";
                var phrases = zone == "head"
                    ? new[] { "Мурр... ещё разок.", "Осторожно с ушами!", "Ты специально меня смущаешь?" }
                    : new[] { "Куда руки распустил?", "Щекотно! Предупреждай хотя бы.", "Ну и наглость... но приятно." };
                var phrase = phrases[Random.Shared.Next(phrases.Length)];
                PlayScene("VideoShyJoy", oneShot: true);
                _ = _tts.SpeakAsync(phrase, "shy");
                return;
            }
            if (!_dragging) return;
            _dragMoved = true;
            var screen = ScreenPoint(args.GetPosition(CharacterImage));
            Position = new PixelPoint(
                (int)Math.Round(screen.X - _dragOffset.X),
                (int)Math.Round(screen.Y - _dragOffset.Y));
        };

        CharacterImage.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Right) return;
            var menu = new ContextMenu();
            var memoryItem = new MenuItem { Header = "Её память" };
            memoryItem.Click += (_, _) => OpenMemory();
            var settings = new MenuItem { Header = "Настройки" };
            settings.Click += (_, _) => OpenSettings();
            var chat = new MenuItem { Header = ChatPanel.IsVisible ? "Скрыть чат" : "Показать чат" };
            chat.Click += (_, _) =>
            {
                _lastActivity = DateTime.Now;
                SetChatVisible(!ChatPanel.IsVisible);
            };
            var drag = new MenuItem { Header = (_touchMode == "drag" ? "✓ " : "") + "Перетаскивание" };
            drag.Click += (_, _) => _touchMode = "drag";
            var pet = new MenuItem { Header = (_touchMode == "pet" ? "✓ " : "") + "Поглаживания" };
            pet.Click += (_, _) => _touchMode = "pet";
            var hideGames = new MenuItem { Header = (_settings.HideInGames ? "✓ " : "") + "Скрываться в играх" };
            hideGames.Click += (_, _) =>
            {
                _settings.HideInGames = !_settings.HideInGames;
                hideGames.Header = (_settings.HideInGames ? "✓ " : "") + "Скрываться в играх";
                _settings.Save();
            };
            var hideYoutube = new MenuItem { Header = (_settings.HideInYoutube ? "✓ " : "") + "Скрываться в YouTube" };
            hideYoutube.Click += (_, _) =>
            {
                _settings.HideInYoutube = !_settings.HideInYoutube;
                hideYoutube.Header = (_settings.HideInYoutube ? "✓ " : "") + "Скрываться в YouTube";
                _settings.Save();
            };            var hideVs = new MenuItem { Header = (_settings.HideInVisualStudio ? "✓ " : "") + "Скрываться в Visual Studio" };
            hideVs.Click += (_, _) =>
            {
                _settings.HideInVisualStudio = !_settings.HideInVisualStudio;
                hideVs.Header = (_settings.HideInVisualStudio ? "✓ " : "") + "Скрываться в Visual Studio";
                _settings.Save();
            };            var admin = new MenuItem { Header = "Перезапустить от администратора" };
            admin.Click += (_, _) =>
            {
                try
                {
                    var exe = Environment.ProcessPath;
                    if (exe is not null)
                    {
                        Process.Start(new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
                    }
                    Close();
                }
                catch
                {
                    // UAC отменён
                }
            };            var topmost = new MenuItem { Header = (_settings.AlwaysOnTop ? "✓ " : "") + "Поверх всех окон" };
            topmost.Click += (_, _) =>
            {
                _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
                Topmost = _settings.AlwaysOnTop;
                _settings.Save();
            };            var lockDrag = new MenuItem { Header = (_lockDrag ? "✓ " : "") + "Блокировать перетаскивание" };
            lockDrag.Click += (_, _) => _lockDrag = !_lockDrag;
            var exit = new MenuItem { Header = "Выход" };
            exit.Click += (_, _) => Close();
            menu.Items.Add(memoryItem);
            menu.Items.Add(settings);
            menu.Items.Add(chat);
            menu.Items.Add(new Separator());
            menu.Items.Add(drag);
            menu.Items.Add(pet);
            menu.Items.Add(lockDrag);
            menu.Items.Add(hideGames);
            menu.Items.Add(hideYoutube);
            menu.Items.Add(hideVs);
            menu.Items.Add(new Separator());
            menu.Items.Add(admin);
            menu.Items.Add(topmost);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            menu.Open(CharacterImage);
        };
        CharacterImage.PointerReleased += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            if (_dragMoved) StartFall();
        };


        Opened += (_, _) =>
        {
            if (_initialized)
            {
                ApplyWindowMode();
                UpdateWindowSize();
                return;
            }
            _initialized = true;
            ApplyWindowMode();
            UpdateWindowSize();
            AnimateCharacterIn();
            StatusText.Text = $"голос: {(_silero.Available ? "Silero" : _piper.Available ? "Piper" : "Windows")}";
            LoadCharacter();
            _voiceDotUpdate();
            _voiceInput.TextRecognized += OnVoiceText;
            StartCare();
            if (!_settings.PushToTalk) _voiceInput.Start();
        global::System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                await probe.GetStringAsync("http://127.0.0.1:11434/api/tags");
            }
            catch
            {
                try
                {
                    var ollamaApp = global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama app.exe");
                    if (global::System.IO.File.Exists(ollamaApp)) global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(ollamaApp) { UseShellExecute = true });
                }
                catch
                {
                    // Ollama может быть не установлена
                }
            }
        });

            StartFall();
            CreateTray();
            AnimateEntrance();

            _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _clickTimer.Tick += (_, _) => UpdateClickThrough();
            _clickTimer.Start();
            _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _focusTimer.Tick += (_, _) => DetectLaunches();
            _focusTimer.Start();
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

        var dragItem = new NativeMenuItem
        {
            Header = _touchMode == "drag" ? "✓ Перетаскивание" : "Перетаскивание"
        };
        var petItem = new NativeMenuItem
        {
            Header = _touchMode == "pet" ? "✓ Поглаживания" : "Поглаживания"
        };
        var lockItem = new NativeMenuItem
        {
            Header = _lockDrag ? "✓ Блокировать перетаскивание" : "Блокировать перетаскивание"
        };

        dragItem.Click += (_, _) =>
        {
            _touchMode = "drag";
            dragItem.Header = "✓ Перетаскивание";
            petItem.Header = "Поглаживания";
        };
        petItem.Click += (_, _) =>
        {
            _touchMode = "pet";
            petItem.Header = "✓ Поглаживания";
            dragItem.Header = "Перетаскивание";
        };
        lockItem.Click += (_, _) =>
        {
            _lockDrag = !_lockDrag;
            lockItem.Header = _lockDrag ? "✓ Блокировать перетаскивание" : "Блокировать перетаскивание";
        };

        var interaction = new NativeMenuItem { Header = "Взаимодействие" };
        var submenu = new NativeMenu();
        submenu.Items.Add(dragItem);
        submenu.Items.Add(petItem);
        submenu.Items.Add(new NativeMenuItemSeparator());
        submenu.Items.Add(lockItem);
        interaction.Menu = submenu;
        menu.Items.Add(interaction);

        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem { Header = "Настройки", Command = new SimpleCommand(OpenSettings) });
        menu.Items.Add(new NativeMenuItem { Header = "Выход", Command = new SimpleCommand(Close) });

        _tray = new TrayIcon
        {
            ToolTipText = "Аля",
            Menu = menu
        };
        try
        {
            _tray.Icon = new WindowIcon(global::System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "alya.ico"));
        }
        catch
        {
            // без иконки трей всё равно работает
        }
        _tray.Clicked += (_, _) => ShowWindow();
    }

    private void LoadCharacter()
    {
        var candidates = new List<string>();
        var custom = Environment.GetEnvironmentVariable("ALYA_ANIM_ROOT");
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "anim"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects", "jarvis", "resources", "anim"));
        var root = candidates.FirstOrDefault(Directory.Exists);
        if (root is null) return;

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
        _animTimer?.Stop();
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16.6) };
        _animTimer.Tick += (_, _) => Tick();
        _animTimer.Start();


    }

    private void PlayScene(string name, bool oneShot = false)
    {
        if (!_scenes.TryGetValue(name, out var frames)) return;
        _scene = name;
        _frames = frames;
        _frame = 0;
        _oneShotLeft = oneShot ? frames.Length : 0;
        _ = CrossfadeFrame(0);
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
                while (_cacheOrder.Count > 40)
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
                for (var offset = 1; offset <= 30; offset++)
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
                            while (_cacheOrder.Count > 40)
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

    private async Task ExtractFactAsync(string userText)
    {
        try
        {
            var lower = userText.ToLowerInvariant();
            if (userText.Length < 12) return;
            if (!lower.Contains(" я ") && !lower.Contains("меня") && !lower.Contains("мне") && !lower.Contains("мой") && !lower.Contains("моя") && !lower.Contains("живу")) return;
            using var client = new Alya.Llm.OllamaLlmClient(_settings.Model) { Temperature = 0.2 };
            var history = new List<ChatMessage>
            {
                new("system", "Ты извлекаешь факты о пользователе. Если в сообщении есть новый устойчивый факт (имя, город, работа, привычка, предпочтение) — верни его одной короткой фразой от третьего лица. Если факта нет — верни ровно: НЕТ", DateTimeOffset.Now),
                new("user", userText, DateTimeOffset.Now)
            };
            var result = (await client.CompleteAsync(history)).Trim();
            if (result.Length == 0 || result.Length > 140) return;
            if (result.Equals("НЕТ", StringComparison.OrdinalIgnoreCase) || result.Contains("НЕТ")) return;
            _memory.AddFact(result);
            _llm.ExtraContext = BuildContext();
            Dispatcher.UIThread.Post(() => StatusText.Text = "запомнила новое");
        }
        catch
        {
            // извлечение не критично
        }
    }
    private void StopSpeech()
    {
        try
        {
            _tts.Stop();
        }
        catch
        {
            // синтез уже остановлен
        }
        _llmCts?.Cancel();
        _talking = false;
        StatusText.Text = "на связи";
        PlayScene("VideoStand");
    }
    private DispatcherTimer? _statusTimer;

    private void _voiceDotUpdate()
    {
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _statusTimer.Tick += async (_, _) =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                await http.GetStringAsync("http://127.0.0.1:11434/api/tags");
                BrainDot.Fill = new SolidColorBrush(Color.Parse("#5EEAD4"));
            }
            catch
            {
                BrainDot.Fill = new SolidColorBrush(Color.Parse("#FF6B6B"));
            }
            EarsDot.Fill = new SolidColorBrush(Color.Parse(_voiceInput.Available ? "#5EEAD4" : "#FFB020"));
            VoiceDot.Fill = new SolidColorBrush(Color.Parse(_silero.Available || _piper.Available ? "#5EEAD4" : "#FFB020"));
        };
        _statusTimer.Start();
    }
    private void OnVoiceText(string text)
    {
        var stopLower = text.ToLowerInvariant();
        if (stopLower.Contains("стоп") || stopLower.Contains("хватит") || stopLower.Contains("замолчи") || stopLower.Contains("тихо"))
        {
            StopSpeech();
            return;
        }
        if (_talking) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "alya-voice.log"), $"{DateTime.Now:HH:mm:ss} ui got: {text}\n"); } catch { }
            var cleanedText = text.Replace("[музыка]", string.Empty).Replace("[шум]", string.Empty).Replace("[аплодисменты]", string.Empty).Trim();
            if (cleanedText.Length < 2 || cleanedText.StartsWith("[")) return;
            text = cleanedText;
            var trimmed = text.Trim();
            if ((trimmed.StartsWith("[") && trimmed.EndsWith("]")) || (trimmed.StartsWith("*") && trimmed.EndsWith("*"))) return;
            var hallu = new[] { "субтитры", "смотрите видео", "продолжение следует", "спасибо за просмотр", "подписывайтесь на канал", "музыка" };
            if (hallu.Any(text.Contains)) return;
            var lower = text.ToLowerInvariant();
            var hasWake = lower.Contains("алл") || lower.Contains("аля") || lower.Contains("алю") || lower.Contains("але") || lower.Contains("ало") || lower.Contains("оля") || lower.Contains("алекс") || lower.Contains("али");
            if (!_settings.PushToTalk && !_settings.AlwaysListen && !hasWake) return;
            var cleaned = Regex.Replace(text, @"(?i)\b(аля|алла|алле|алло|алли|алю|але|ало|оля|али)\b[\s,!\.\?]*", " ").Trim();
            if (cleaned.Length < 2) return;
            InputBox.Text = cleaned;
            Send();
            return;
        });
    }
    private string? TryOpenApp(string text)
    {
        var match = Regex.Match(text.Trim(), @"(открой|открыть|запусти|включи|вруби)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var target = match.Groups[2].Value.Trim().Trim('.', '!', '?', ',');
        if (target.Length == 0) return null;
        var aliases = new (string Key, string Value)[]
        {
            ("стим", "steam"), ("steam", "steam"), ("стиме", "steam"), ("стиму", "steam"),
            ("фортнайт", "fortnite"), ("фортнай", "fortnite"), ("fortnite", "fortnite"), ("форт", "fortnite"), ("фoрт", "fortnite"),
            ("эпик", "epic"), ("epic", "epic"), ("епки", "epic"), ("эпик геймс", "epic"), ("epic games", "epic"),
            ("хром", "chrome"), ("chrome", "chrome"), ("браузер", "chrome"),
            ("телега", "telegram"), ("телеграм", "telegram"), ("телеге", "telegram"),
            ("дискорд", "discord"), ("discord", "discord"),
            ("блокнот", "notepad"), ("калькулятор", "calc"),
            ("ворд", "winword"), ("эксель", "excel"), ("проводник", "explorer"),
            ("код", "code"), ("вс код", "code")
        };
        var lower = target.ToLowerInvariant();
        var resolved = target;
        foreach (var (key, value) in aliases)
        {
            if (lower.Contains(key)) { resolved = value; break; }
        }
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "alya-voice.log"), $"{DateTime.Now:HH:mm:ss} open target='{target}' resolved='{resolved}'\n"); } catch { }
        var ok = _system.Launch(resolved);
        if (ok) return $"Открываю {target}.";
        if (_steam.Launch(resolved, out var gameName)) return $"Запускаю {gameName}.";
        return $"Не нашла {target}.";
    }
    private async Task<string?> TryWeatherAsync(string text)
    {
        var lower = text.ToLowerInvariant();
        if (!lower.Contains("погод") && !lower.Contains("температур") && !lower.Contains("градус")) return null;
        if (lower.Contains("на улице") && (lower.Contains("снег") || lower.Contains("дождь") || lower.Contains("солнце")))
        {
            return null;
        }
        var city = _settings.City;
        var match = Regex.Match(text, @"(?:в|во)\s+([А-ЯЁа-яё\-]+)");
        if (match.Success && match.Groups[1].Value.Length > 1)
        {
            city = match.Groups[1].Value;
        }
        var weather = lower.Contains("завтра") ? await _weather.GetTomorrowAsync(city) : await _weather.GetAsync(city);
        if (weather is null && !string.Equals(city, _settings.City, StringComparison.OrdinalIgnoreCase))
        {
            weather = await _weather.GetAsync(_settings.City);
            city = _settings.City;
        }
        return weather ?? $"Не получилось узнать погоду в {city}. Проверь интернет.";
    }
    [global::System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private string? TryScreenCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        if (!lower.Contains("на экране") && !lower.Contains("экран") && !lower.Contains("скриншот")) return null;
        try
        {
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            var path = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "alya-screen.png");
            using (var bitmap = new Bitmap(width, height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height));
                bitmap.Save(path, ImageFormat.Png);
            }

            var dataDir = global::System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "tesseract");
            if (!Directory.Exists(dataDir))
            {
                dataDir = global::System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "alya-next", "tools", "tesseract");
            }
            if (!Directory.Exists(dataDir)) return "Не нашла языковые данные OCR.";

            using var engine = new TesseractEngine(dataDir, "rus+eng", EngineMode.Default);
            using var pix = Pix.LoadFromFile(path);
            using var page = engine.Process(pix);
            var recognized = page.GetText()?.Trim() ?? string.Empty;
            recognized = Regex.Replace(recognized, @"\s+", " ").Trim();
            if (recognized.Length == 0) return "На экране не вижу текста — только картинки.";
            if (recognized.Length > 350) recognized = recognized[..350] + "…";
            return $"Вижу на экране: {recognized}";
        }
        catch (Exception error)
        {
            var detail = error.InnerException?.Message ?? error.Message;
            return $"Не получилось прочитать экран: {detail}";
        }
    }
    private string? TryTimerCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("напоминани") && (lower.Contains("какие") || lower.Contains("список") || lower.Contains("что")))
        {
            if (_reminders.Count == 0) return "Напоминаний нет.";
            return "Напоминания: " + string.Join("; ", _reminders.Select(item => $"{item.Message} в {item.Due:HH:mm}"));
        }
        if (lower.Contains("отмен") && lower.Contains("напомин"))
        {
            _reminders.Clear();
            return "Убрала все напоминания.";
        }
        var match = Regex.Match(lower, @"через\s+(\d+)\s*(секунд|сек|минут|мин|час|часа|часов)");
        var timerMatch = Regex.Match(lower, @"таймер\s+(?:на\s+)?(\d+)\s*(секунд|сек|минут|мин|час|часа|часов)");
        var use = match.Success ? match : timerMatch;
        if (!use.Success) return null;
        var value = int.Parse(use.Groups[1].Value);
        var unit = use.Groups[2].Value;
        var seconds = unit.StartsWith("час") ? value * 3600 : unit.StartsWith("сек") ? value : value * 60;
        if (seconds <= 0) return null;
        var message = text;
        var idx = text.IndexOf(use.Groups[0].Value, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) message = text[(idx + use.Groups[0].Value.Length)..].Trim(' ', ',', '.', '!', '?', ':', '-');
        if (string.IsNullOrWhiteSpace(message)) message = "напоминание";
        var due = DateTime.Now.AddSeconds(seconds);
        _reminders.Add((due, message));
        return $"Хорошо, напомню в {due:HH:mm}: {message}.";
    }

    private void CheckReminders()
    {
        if (_reminders.Count == 0) return;
        var due = _reminders.Where(item => item.Due <= DateTime.Now).ToList();
        foreach (var item in due)
        {
            _reminders.Remove(item);
            var text = $"Напоминаю: {item.Message}";
            Add("alya", text);
            _ = _tts.SpeakAsync(text, "neutral");
        }
    }
    private string? TryPowerCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        var isRestart = lower.Contains("перезагруз") || lower.Contains("ребут") || lower.Contains("restart");
        var isShutdown = lower.Contains("выключ") || lower.Contains("отключ") || lower.Contains("shutdown");
        if (!isRestart && !isShutdown) return null;
        var aboutPc = lower.Contains("комп") || lower.Contains("пк") || lower.Contains("систем") || lower.Contains("windows");
        if (!aboutPc && !lower.Contains("перезагруз") && !lower.Contains("выключ")) return null;

        if (lower.Contains("отмен"))
        {
            _pendingPower = null;
            return "Отменила.";
        }
        var action = isRestart ? "restart" : "shutdown";
        if (lower.Contains("подтвержда") || lower.Contains("да, ") || lower.Equals("да"))
        {
            if (_pendingPower is null || (DateTime.Now - _pendingPowerAt).TotalSeconds > 45)
            {
                return "Подтверждение устарело. Скажи ещё раз, что сделать.";
            }
            var command = _pendingPower;
            _pendingPower = null;
            try
            {
                var args = command == "restart" ? "/r /t 10 /c \"Аля: перезагрузка\"" : "/s /t 10 /c \"Аля: выключение\"";
                global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo("shutdown", args) { UseShellExecute = true, CreateNoWindow = true });
                return command == "restart" ? "Перезагружаю компьютер через 10 секунд." : "Выключаю компьютер через 10 секунд.";
            }
            catch (Exception error)
            {
                return $"Не получилось: {error.Message}";
            }
        }
        _pendingPower = action;
        _pendingPowerAt = DateTime.Now;
        return action == "restart"
            ? "Перезагрузить компьютер? Скажи «подтверждаю» в течение 45 секунд."
            : "Выключить компьютер? Скажи «подтверждаю» в течение 45 секунд.";
    }
    private string? TryHistoryCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("экспорт") && (lower.Contains("истори") || lower.Contains("диалог")))
        {
            try
            {
                var messages = HistoryStore.Load();
                var path = global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"alya-history-{DateTime.Now:yyyyMMdd-HHmm}.txt");
                var lines = messages.Select(message => $"[{message.At:HH:mm}] {(message.Role == "user" ? "Ты" : "Аля")}: {message.Content}");
                global::System.IO.File.WriteAllLines(path, lines);
                return $"Экспортировала историю в {path}";
            }
            catch (Exception error)
            {
                return $"Не получилось экспортировать: {error.Message}";
            }
        }
        if (lower.Contains("интернет") || lower.Contains("гугл")) return null;
        var match = Regex.Match(text, @"(?:найди|поищи|ищи)\s+(?:в\s+истории\s+)?(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var query = match.Groups[1].Value.Trim().Trim('.', '!', '?', ',');
        if (query.Length < 2) return null;
        var found = HistoryStore.Load()
            .Where(message => message.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .ToList();
        if (found.Count == 0) return $"В истории не нашла «{query}».";
        return "Нашла в истории: " + string.Join(" | ", found.Select(message => $"{message.At:HH:mm} {(message.Role == "user" ? "ты" : "Аля")}: {message.Content}"));
    }
    private string? TryMediaCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("пауза") || lower.Contains("плей") || lower.Contains("плей") || lower.Contains("включи музыку") || lower.Contains("выключи музыку"))
        {
            _system.MediaKey(0xB3);
            return "Пауза/воспроизведение.";
        }
        if (lower.Contains("следующий") && (lower.Contains("трек") || lower.Contains("песн")))
        {
            _system.MediaKey(0xB0);
            return "Следующий трек.";
        }
        if (lower.Contains("предыдущ") && (lower.Contains("трек") || lower.Contains("песн")))
        {
            _system.MediaKey(0xB1);
            return "Предыдущий трек.";
        }
        if (lower.Contains("громче") || lower.Contains("прибавь звук"))
        {
            _system.MediaKey(0xAF);
            return "Сделала громче.";
        }
        if (lower.Contains("тише") || lower.Contains("убавь звук"))
        {
            _system.MediaKey(0xAE);
            return "Сделала тише.";
        }
        if (lower.Contains("без звука") || lower.Contains("замьють") || lower.Contains("mute"))
        {
            _system.MediaKey(0xAD);
            return "Звук выключен.";
        }
        return null;
    }

    private string? TryWindowCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("сверни") || lower.Contains("свернуть") && lower.Contains("вс"))
        {
            _system.MinimizeAll();
            return "Свернула все окна.";
        }
        if (lower.Contains("что открыто") || lower.Contains("какие окна") || lower.Contains("что у меня открыто"))
        {
            var windows = _system.VisibleWindows();
            return windows.Count == 0 ? "Не вижу открытых окон." : "Открыто: " + string.Join("; ", windows);
        }
        var match = Regex.Match(text, @"переключи(?:сь)?\s+(?:на|в)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var target = match.Groups[1].Value.Trim().Trim('.', '!', '?', ',');
        if (_system.Activate(target)) return $"Переключилась на {target}.";
        return _system.Launch(target) ? $"Окно {target} не было открыто — запустила." : $"Не нашла окно {target}.";
    }
    private string? TryCloseApp(string text)
    {
        var match = Regex.Match(text.Trim(), @"(закрой|закрыть|выключи|заверши|убей)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var target = match.Groups[2].Value.Trim().Trim('.', '!', '?', ',');
        if (target.Length == 0) return null;
        var lower = target.ToLowerInvariant();
        var processes = new List<string>();
        if (lower.Contains("форт") || lower.Contains("fortnite")) processes.Add("FortniteClient-Win64-Shipping");
        else if (lower.Contains("дбд") || lower.Contains("dbd") || lower.Contains("дед бай") || lower.Contains("dead by daylight")) { processes.Add("DeadByDaylight-Win64-Shipping"); processes.Add("DeadByDaylight"); }
        else if (lower.Contains("кс2") || lower.Contains("cs2") || lower.Contains("кс ") || lower.Equals("кс") || lower.Contains("контр") || lower.Contains("counter-strike")) processes.Add("cs2");
        else if (lower.Contains("варфейс") || lower.Contains("warface")) { processes.Add("GameClient"); processes.Add("Warface"); }
        else if (lower.Contains("дота") || lower.Contains("dota")) processes.Add("dota2");
        else if (lower.Contains("раст") || lower.Contains("rust")) processes.Add("RustClient");
        else if (lower.Contains("пабг") || lower.Contains("pubg")) processes.Add("TslGame");
        else if (lower.Contains("апекс") || lower.Contains("apex")) processes.Add("r5apex");
        else if (lower.Contains("террари") || lower.Contains("terraria")) processes.Add("Terraria");
        else if (lower.Contains("обои") || lower.Contains("wallpaper")) processes.Add("wallpaper64");
        else if (lower.Contains("саундпад") || lower.Contains("soundpad")) processes.Add("Soundpad");
        else if (lower.Contains("эпик") || lower.Contains("epic")) processes.Add("EpicGamesLauncher");
        else if (lower.Contains("стим") || lower.Contains("steam")) processes.Add("steam");
        else if (lower.Contains("телег") || lower.Contains("telegram")) processes.Add("Telegram");
        else if (lower.Contains("дискорд") || lower.Contains("discord")) processes.Add("Discord");
        else if (lower.Contains("хром") || lower.Contains("chrome")) processes.Add("chrome");
        else if (lower.Contains("блокнот") || lower.Contains("notepad")) processes.Add("notepad");
        else if (lower.Contains("калькул") || lower.Contains("calc")) processes.Add("calc");
        else if (lower.Contains("ворд") || lower.Contains("word")) processes.Add("winword");
        else if (lower.Contains("эксель") || lower.Contains("excel")) processes.Add("excel");
        else if (lower.Contains("телег") || lower.Contains("telegram") || lower.Contains("тг")) processes.Add("Telegram");
        else if (lower.Contains("фаерфокс") || lower.Contains("firefox") || lower.Contains("файрфокс")) processes.Add("firefox");
        else if (lower.Contains("браузер") || lower.Contains("гугл")) processes.Add("firefox");
        else if (lower.Contains("паинт") || lower.Contains("paint")) processes.Add("mspaint");
        else if (lower.Contains("фотошоп") || lower.Contains("photoshop")) processes.Add("Photoshop");
        else if (lower.Contains("проводник") || lower.Contains("explorer")) processes.Add("explorer");
        else if (lower.Contains("вс код") || lower.Contains("vs code") || lower.Contains("visual studio code")) processes.Add("Code");
        else if (lower.Contains("епки") || lower.Contains("epic games")) processes.Add("EpicGamesLauncher");
        else if (lower.Contains("спотиф") || lower.Contains("spotify")) processes.Add("Spotify");
        else if (lower.Contains("код") || lower.Contains("code")) processes.Add("Code");
        else processes.Add(target);

        var closed = false;
        foreach (var process in processes) closed |= _system.Close(process);
        return closed ? $"Закрываю {target}." : $"{target} и так не запущено.";
    }
    private string? TryLaunchGame(string text)
    {
        var match = Regex.Match(text, @"(запусти|включи|открой|поиграем в)\s+(.+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var query = match.Groups[2].Value.Trim();
        if (query.Length == 0) return null;
        return _steam.Launch(query, out var name)
            ? $"Запускаю {name}."
            : null;
    }
    private string? TryMemoryCommand(string text)
    {
        var value = text.Trim();
        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("запомни"))
        {
            var fact = Regex.Replace(value, "^запомни,?\\s*(что\\s+)?", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (fact.Length == 0) return "Что запомнить-то?";
            _memory.AddFact(fact);
            _llm.ExtraContext = BuildContext();
        ApplyAccent();
        ApplyWindowMode();
        Opacity = Math.Clamp(_settings.Opacity, 0.6, 1);
            return $"Запомнила: {fact}";
        }
        if (lower.StartsWith("правило"))
        {
            var rule = Regex.Replace(value, "^правило\\s*:?\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (rule.Length == 0) return "Какое правило?";
            _memory.AddRule(rule);
            _llm.ExtraContext = BuildContext();
        ApplyAccent();
        ApplyWindowMode();
        Opacity = Math.Clamp(_settings.Opacity, 0.6, 1);
            return "Хорошо, буду соблюдать.";
        }
        if (lower.Contains("что ты помнишь") || lower.Contains("что помнишь"))
        {
            if (_memory.Facts.Count == 0 && _memory.Rules.Count == 0) return "Пока ничего не запоминала.";
            var parts = new List<string>();
            if (_memory.Facts.Count > 0) parts.Add("Факты: " + string.Join("; ", _memory.Facts));
            if (_memory.Rules.Count > 0) parts.Add("Правила: " + string.Join("; ", _memory.Rules));
            return string.Join(". ", parts);
        }
        return null;
    }

    private void DetectLaunches()
    {
        if (!_settings.Care) return;
        if (_talking) return;
        var running = _system.RunningProcesses();
        var interesting = new HashSet<string>(
            running.Where(name => GameProcesses.Any(name.Contains) || WorkProcesses.Any(name.Contains)),
            StringComparer.OrdinalIgnoreCase);
        if (!_programsInitialized)
        {
            foreach (var name in interesting) _knownPrograms.Add(name);
            _programsInitialized = true;
            return;
        }

        var launched = interesting.Where(name => !_knownPrograms.Contains(name)).ToList();
        foreach (var name in interesting) _knownPrograms.Add(name);
        foreach (var name in _knownPrograms.ToList())
        {
            if (!interesting.Contains(name)) _knownPrograms.Remove(name);
        }
        if (launched.Count == 0) return;
        if ((DateTime.Now - _lastFocusReactionAt).TotalMinutes < 3) return;

        var launchedName = launched[0];
        var isGame = GameProcesses.Any(launchedName.Contains);
        var phrases = isGame
            ? new[] { "О, играешь? А со мной ты не играешь?", "Запустил игру без меня? Ну ладно.", "Играешь? Я подожду. Наверное." }
            : new[] { "Работаешь? Ну ладно, не буду мешать! Долго не работай.", "Вижу, открыл рабочее. Не забудь отдохнуть.", "Трудишься? Я рядом, если что." };
        var phrase = phrases[Random.Shared.Next(phrases.Length)];
        _lastFocusReactionAt = DateTime.Now;
        _lastFocusCategory = isGame ? "game" : "work";
        _talking = true;
        Add("alya", phrase);
        PlayScene("VideoTalk");
        _ = SpeakAndReturn(phrase);
    }

    private async Task SpeakAndReturn(string phrase)
    {
        await _tts.SpeakAsync(phrase, "neutral");
    }
    private void HandleVoiceEvent(string kind)
    {
        if (kind == "tts_start")
        {
            _talking = true;
            _talkingStarted = DateTime.Now;
            PlayScene("VideoTalk");
            StatusText.Text = "говорит…";
        }
        else if (kind == "tts_end")
        {
            _talking = false;
            PlayScene("VideoStand");
            StatusText.Text = "на связи";
        }
    }
    private void AnimateEntrance()
    {
        ChatPanel.Opacity = 0;
        var transform = new TranslateTransform(0, 18);
        ChatPanel.RenderTransform = transform;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 320.0, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            ChatPanel.Opacity = eased;
            transform.Y = 18 * (1 - eased);
            if (progress < 1) return;
            timer.Stop();
            ChatPanel.Opacity = 1;
            transform.Y = 0;
        };
        timer.Start();
    }

    private void AnimateEntranceOld()
    {
        ChatPanel.Opacity = 0;
        ChatPanel.RenderTransform = new TranslateTransform(0, 18);
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(320),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, 18d)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d)
                    }
                }
            }
        };
        _ = animation.RunAsync(ChatPanel);
    }

    private async Task CrossfadeFrame(int frameIndex)
    {
        if (_frames.Length == 0) return;
        var path = _frames[frameIndex];
        Avalonia.Media.Imaging.Bitmap bitmap;
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(path, out var cached) || cached is null)
            {
                try
                {
                    cached = new Avalonia.Media.Imaging.Bitmap(path);
                }
                catch
                {
                    return;
                }
                _cache[path] = cached;
            }
            bitmap = cached;
        }

        CharacterFade.Source = bitmap;
        CharacterFade.IsVisible = true;
        CharacterFade.Opacity = 0;
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } }
            }
        };
        await fade.RunAsync(CharacterFade);
        CharacterImage.Source = bitmap;
        CharacterFade.Opacity = 0;
        CharacterFade.Source = null;
        CharacterFade.IsVisible = false;
    }
    private void UpdateClickThrough()
    {
        if (_hwnd == IntPtr.Zero || _dragging) return;
        if (!GetCursorPos(out var point)) return;
        var scale = RenderScaling;
        var x = point.X - Position.X;
        var y = point.Y - Position.Y;
        var panelWidth = 390 * scale;
        var insidePanel = x >= 0 && x <= panelWidth && y >= 0 && y <= Height * scale;
        var insideCharacter = false;
        try
        {
            var offset = CharacterImage.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
            var bounds = CharacterImage.Bounds;
            var left = (offset.X) * scale;
            var top = (offset.Y) * scale;
            var right = left + bounds.Width * scale;
            var bottom = top + bounds.Height * scale;
            insideCharacter = x >= left && x <= right && y >= top && y <= bottom;
        }
        catch
        {
            // окно ещё не готово
        }
        SetClickThrough(!insidePanel && !insideCharacter);
    }

    private void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero || enabled == _clickThrough) return;
        _clickThrough = enabled;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "alya-click.log"), $"{DateTime.Now:HH:mm:ss} clickThrough={enabled} hwnd={_hwnd}\n"); } catch { }
        const int GwlExStyle = -20;
        const int WsExTransparent = 0x20;
        const int WsExNoActivate = 0x08000000;
        var style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        var updated = enabled ? style | WsExTransparent | WsExNoActivate : style & ~WsExTransparent & ~WsExNoActivate;
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(updated));
        const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpFrameChanged = 0x0020, SwpNoActivate = 0x0010;
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpNoActivate);
    }
    private void OpenMemory()
    {
        var window = new MemoryWindow();
        window.Show(this);
    }
    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        var window = new SettingsWindow();
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ApplySettings();
        };
        window.Show(this);
    }

    private void SetChatVisible(bool visible)
    {
        ChatPanel.IsVisible = visible;
        if (visible) Dispatcher.UIThread.Post(AnimateChat, DispatcherPriority.Background);
        _lastActivity = DateTime.Now;
        if (!_settings.Fullscreen) UpdateWindowSize();
    }

    private const int ModelHeight = 504; // 840 * 0.6
    private const int ModelWidth = 324;  // 540 * 0.6

    private void UpdateWindowSize()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var area = screen.Bounds;
        var width = ChatPanel.IsVisible ? 380 + ModelWidth : ModelWidth;
        var height = ModelHeight;
        Width = width;
        Height = height;
        var x = area.X + area.Width - width;
        var y = area.Y + area.Height - height;
        Position = new PixelPoint(x, y);
        ApplyCharacterLayout();
    }

    private void ApplyWindowMode()
    {
        if (_settings.Fullscreen)
        {
            Topmost = _settings.AlwaysOnTop;
            var screen = Screens.Primary;
            if (screen is null) return;
            var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
            Width = screen.Bounds.Width / scaling;
            Height = screen.Bounds.Height / scaling;
            Position = new PixelPoint(screen.Bounds.X, screen.Bounds.Y);
            ApplyCharacterLayout();
            return;
        }
        Topmost = _settings.AlwaysOnTop;
        UpdateWindowSize();
    }

    private void AnimateChat()
    {
        ChatPanel.Opacity = 0;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 220.0, 0, 1);
            ChatPanel.Opacity = 1 - Math.Pow(1 - progress, 3);
            if (progress < 1) return;
            timer.Stop();
            ChatPanel.Opacity = 1;
        };
        timer.Start();
    }

    private void AnimateCharacterIn()
    {
        CharacterImage.Opacity = 0;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 500.0, 0, 1);
            CharacterImage.Opacity = 1 - Math.Pow(1 - progress, 3);
            if (progress < 1) return;
            timer.Stop();
            CharacterImage.Opacity = 1;
        };
        timer.Start();
    }
    private void ApplyCharacterLayout()
    {
        CharacterImage.RenderTransform = null;
        CharacterFade.RenderTransform = null;
        CharacterImage.Margin = new Avalonia.Thickness(0);
        CharacterFade.Margin = new Avalonia.Thickness(0);
    }
    private string BuildContext()
    {
        var profile = $"Пользователя зовут {_settings.UserName}, его город — {_settings.City}. Сейчас {DateTime.Now:dddd, d MMMM, HH:mm}.";
        var memory = _memory.ToPrompt();
        return string.IsNullOrWhiteSpace(memory) ? profile : profile + " " + memory;
    }
    private void ApplyAccent()
    {
        try
        {
            SendButton.Background = new SolidColorBrush(Color.Parse(_settings.Accent));
        }
        catch
        {
            // цвет может быть некорректным — оставляем дефолт
        }
    }
    private void ApplySettings()
    {
        var previousModel = _settings.Model;
        _settings = AlyaSettings.Load();
        _silero.Speaker = _settings.Speaker;
        _silero.Pitch = _settings.Pitch;
        _piper.Pitch = _settings.Pitch;
        if (!string.Equals(previousModel, _settings.Model, StringComparison.OrdinalIgnoreCase))
        {
            _llm = new OllamaLlmClient(_settings.Model) { Temperature = _settings.Temperature, Persona = _settings.Persona };
        }
        if (_animTimer is not null)
        {
            _animTimer.Interval = TimeSpan.FromMilliseconds(16.6);
        }
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
        if (!_settings.FallEnabled) return;
        if (Screens.Primary is null) return;
        var area = Screens.Primary.Bounds;
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
                        try { global::System.Media.SystemSounds.Asterisk.Play(); } catch { }
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
        Bubble? streamedBubble = null;
        try
        {
            var quick = HandleCareAnswer(text) ?? TryMemoryCommand(text) ?? TryTimerCommand(text) ?? TryPowerCommand(text) ?? TryHistoryCommand(text) ?? TryMediaCommand(text) ?? TryWindowCommand(text) ?? TryCloseApp(text) ?? TryOpenApp(text) ?? TryLaunchGame(text);
            if (quick is null) quick = await TryWeatherAsync(text);
            if (quick is not null)
            {
                reply = quick;
            }
            else
            {
                var placeholder = Add("alya", "…");
                _llmCts?.Dispose();
                _llmCts = new CancellationTokenSource();
                try
                {
                    reply = _tools is null
                        ? await _llm.CompleteStreamingAsync(_history, delta => Dispatcher.UIThread.Post(() => placeholder.Text += delta), _llmCts.Token)
                        : await _llm.CompleteWithToolsAsync(_history, _tools.Schema, _tools.ExecuteAsync, _llmCts.Token);
                }
                catch (OperationCanceledException)
                {
                    reply = placeholder.Text.Trim();
                    if (string.IsNullOrWhiteSpace(reply)) reply = "Ладно, молчу.";
                }
                placeholder.Text = reply;
                if (string.IsNullOrWhiteSpace(reply)) reply = "…";
                placeholder.Text = reply;
                streamedBubble = placeholder;
            }
        }
        catch (Exception error)
        {
            reply = $"Не получилось подумать: {error.Message}";
        }
        _history.Add(new ChatMessage("assistant", reply, DateTimeOffset.Now));
        HistoryStore.Save(_history);
        if (streamedBubble is not null) streamedBubble.Text = reply;
        else Add("alya", reply);
        if (_settings.AutoFacts) _ = ExtractFactAsync(text);
        StatusText.Text = "говорит…";
        if (_settings.TtsEnabled) await _tts.SpeakAsync(reply, "neutral");
        else StatusText.Text = "на связи";
    }

    private void ShowBubble(string text)
    {
        SpeechText.Text = text;
        SpeechBubble.IsVisible = true;
        if (_bubbleTimer is null)
        {
            _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _bubbleTimer.Tick += (_, _) =>
            {
                _bubbleTimer?.Stop();
                SpeechBubble.IsVisible = false;
            };
        }
        _bubbleTimer.Stop();
        _bubbleTimer.Start();
    }

    private Bubble Add(string role, string text)
    {
        var isUser = role == "user";
        _lastActivity = DateTime.Now;
        if (!isUser && _settings.Bubbles && text.Length < 160) ShowBubble(text);
        var bubble = new Bubble
        {
            Text = text,
            Time = DateTime.Now.ToString("HH:mm"),
            Alignment = isUser ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left,
            Background = isUser
                ? new SolidColorBrush(Color.Parse("#3E6F9E"))
                : new SolidColorBrush(Color.Parse("#1B2C42"))
        };
        _messages.Add(bubble);
        Dispatcher.UIThread.Post(() =>
        {
            bubble.Opacity = 1;
            var scroll = MessagesList.Parent as ScrollViewer;
            scroll?.ScrollToEnd();
        });
        return bubble;
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
