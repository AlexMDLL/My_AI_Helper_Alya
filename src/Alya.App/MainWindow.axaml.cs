using System.Collections.ObjectModel;
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
    private DateTime _lastActivity = DateTime.Now;
    private DispatcherTimer? _hideTimer;
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
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

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
        _llm = new OllamaLlmClient(_settings.Model) { Temperature = _settings.Temperature };
        _silero.Speaker = _settings.Speaker;
        _silero.Pitch = _settings.Pitch;
        _piper.Pitch = _settings.Pitch;
        _silero.OnEvent += kind => Dispatcher.UIThread.Post(() => HandleVoiceEvent(kind));
        _sapi.OnEvent += kind => Dispatcher.UIThread.Post(() => HandleVoiceEvent(kind));
        _piper.OnEvent += kind => Dispatcher.UIThread.Post(() => HandleVoiceEvent(kind));
        _llm.ExtraContext = _memory.ToPrompt();
        ApplyAccent();
        ApplyCharacterLayout();
        Opacity = Math.Clamp(_settings.Opacity, 0.6, 1);
        MessagesList.ItemsSource = _messages;
        ChatPanel.IsVisible = false;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hideTimer.Tick += (_, _) =>
        {
            var fresh = AlyaSettings.Load();
            if (Math.Abs(fresh.PersonaScale - _settings.PersonaScale) > 0.001 || fresh.PersonaOffset != _settings.PersonaOffset)
            {
                _settings.PersonaScale = fresh.PersonaScale;
                _settings.PersonaOffset = fresh.PersonaOffset;
                ApplyCharacterLayout();
            }
            if (_settingsWindow is not null) return;
            if (!ChatPanel.IsVisible) return;
            if (fresh.ChatAutoHideSeconds <= 0) return;
            if ((DateTime.Now - _lastActivity).TotalSeconds < fresh.ChatAutoHideSeconds) return;
            ChatPanel.IsVisible = false;
        };
        _hideTimer.Start();

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
        CloseButton.Click += (_, _) => ChatPanel.IsVisible = false;
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
        CharacterImage.PointerWheelChanged += (_, args) =>
        {
            var delta = args.Delta.Y > 0 ? 0.05 : -0.05;
            if (args.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _settings.PersonaOffset = Math.Clamp(_settings.PersonaOffset - (int)(delta * 200), -100, 200);
            }
            else
            {
                _settings.PersonaScale = Math.Clamp(Math.Round(_settings.PersonaScale + delta, 2), 0.6, 1.3);
            }
            _settings.Save();
            ApplyCharacterLayout();
            args.Handled = true;
        };
        CharacterImage.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(CharacterImage);
            if (!point.Properties.IsLeftButtonPressed) return;
            if (args.ClickCount == 2)
            {
                ChatPanel.IsVisible = !ChatPanel.IsVisible;
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
            var settings = new MenuItem { Header = "Настройки" };
            settings.Click += (_, _) => OpenSettings();
            var chat = new MenuItem { Header = ChatPanel.IsVisible ? "Скрыть чат" : "Показать чат" };
            chat.Click += (_, _) =>
            {
                _lastActivity = DateTime.Now;
                ChatPanel.IsVisible = !ChatPanel.IsVisible;
            };
            var drag = new MenuItem { Header = (_touchMode == "drag" ? "✓ " : "") + "Перетаскивание" };
            drag.Click += (_, _) => _touchMode = "drag";
            var pet = new MenuItem { Header = (_touchMode == "pet" ? "✓ " : "") + "Поглаживания" };
            pet.Click += (_, _) => _touchMode = "pet";
            var lockDrag = new MenuItem { Header = (_lockDrag ? "✓ " : "") + "Блокировать перетаскивание" };
            lockDrag.Click += (_, _) => _lockDrag = !_lockDrag;
            var exit = new MenuItem { Header = "Выход" };
            exit.Click += (_, _) => Close();
            menu.Items.Add(settings);
            menu.Items.Add(chat);
            menu.Items.Add(new Separator());
            menu.Items.Add(drag);
            menu.Items.Add(pet);
            menu.Items.Add(lockDrag);
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
            var screen = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var targetX = screen.X + screen.Width - 800;
            targetX = Math.Clamp(targetX, screen.X, screen.X + screen.Width - (int)Width);
            var targetY = Math.Clamp(screen.Y + 60, screen.Y, screen.Y + screen.Height - (int)Height);
            Position = new PixelPoint(targetX, targetY);
            StatusText.Text = $"голос: {(_silero.Available ? "Silero" : _piper.Available ? "Piper" : "Windows")}";
            LoadCharacter();
            _voiceInput.TextRecognized += OnVoiceText;
            _voiceInput.Start();
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
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_settings.Fps, 24, 240)) };
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

    private void OnVoiceText(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "alya-voice.log"), $"{DateTime.Now:HH:mm:ss} ui got: {text}\n"); } catch { }
            var lower = text.ToLowerInvariant();
            var hasWake = lower.Contains("аля") || lower.Contains("алла") || lower.Contains("алю") || lower.Contains("але") || lower.Contains("ало");
            if (!_settings.AlwaysListen && !hasWake) return;
            var cleaned = Regex.Replace(text, @"(?i)\b(аля|алла|алю|але|ало)\b[\s,!\.\?]*", " ").Trim();
            if (cleaned.Length < 2) return;
            InputBox.Text = cleaned;
            Send();
            return;
        });
    }
    private string? TryOpenApp(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(открой|открыть|запусти|включи)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var target = match.Groups[2].Value.Trim();
        if (target.Length == 0) return null;
        var ok = _system.Launch(target);
        return ok ? $"Открываю {target}." : $"Не нашла {target}.";
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
            _llm.ExtraContext = _memory.ToPrompt();
        ApplyAccent();
        ApplyCharacterLayout();
        Opacity = Math.Clamp(_settings.Opacity, 0.6, 1);
            return $"Запомнила: {fact}";
        }
        if (lower.StartsWith("правило"))
        {
            var rule = Regex.Replace(value, "^правило\\s*:?\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (rule.Length == 0) return "Какое правило?";
            _memory.AddRule(rule);
            _llm.ExtraContext = _memory.ToPrompt();
        ApplyAccent();
        ApplyCharacterLayout();
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
        const int GwlExStyle = -20;
        const int WsExTransparent = 0x20;
        var style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        var updated = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(updated));
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

    private void ApplyCharacterLayout()
    {
        var scale = Math.Clamp(_settings.PersonaScale, 0.6, 1.3);
        CharacterImage.RenderTransform = new ScaleTransform(scale, scale);
        CharacterFade.RenderTransform = new ScaleTransform(scale, scale);
        CharacterImage.Margin = new Avalonia.Thickness(0, 0, 10, -Math.Clamp(_settings.PersonaOffset, -100, 200));
        CharacterFade.Margin = new Avalonia.Thickness(0, 0, 10, -Math.Clamp(_settings.PersonaOffset, -100, 200));
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
            _llm = new OllamaLlmClient(_settings.Model) { Temperature = _settings.Temperature };
        }
        if (_animTimer is not null)
        {
            _animTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_settings.Fps, 24, 240));
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
        try
        {
            reply = TryMemoryCommand(text) ?? TryOpenApp(text) ?? TryLaunchGame(text) ?? await _llm.CompleteAsync(_history);
        }
        catch (Exception error)
        {
            reply = $"Не получилось подумать: {error.Message}";
        }
        _history.Add(new ChatMessage("assistant", reply, DateTimeOffset.Now));
        HistoryStore.Save(_history);
        Add("alya", reply);
        StatusText.Text = "говорит…";
        if (_settings.TtsEnabled) await _tts.SpeakAsync(reply, "neutral");
        else StatusText.Text = "на связи";
    }

    private void ShowBubble(string text)
    {
        SpeechText.Text = text;
        SpeechBubble.IsVisible = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SpeechBubble.IsVisible = false;
        };
        timer.Start();
    }

    private void Add(string role, string text)
    {
        var isUser = role == "user";
        _lastActivity = DateTime.Now;
        ChatPanel.IsVisible = true;
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
