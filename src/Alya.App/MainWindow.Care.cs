using System.Runtime.InteropServices;
using System.Diagnostics;
using Avalonia.Threading;
using Alya.Core;

namespace Alya.App;

public partial class MainWindow
{
    private CareState _care = CareState.Load();
    private DispatcherTimer? _careTimer;
    private (int X, int Y) _lastMouse;
    private readonly TelemostLauncher _launcher = new();

    private void StartCare()
    {
        _careTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _careTimer.Tick += (_, _) => CareTick();
        _careTimer.Start();
        CareTick();
    }

    private void CareTick()
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");

        if (now.Hour >= 23 && _care.LastNightNotice != today)
        {
            _care.LastNightNotice = today;
            _care.Save();
            var tomorrow = now.Date.AddDays(1);
            var text = CareState.IsDayOff(tomorrow)
                ? "Уже одиннадцать. Можешь ещё чуть посидеть, но не долго! А то режим поломаешь."
                : "Уже одиннадцать вечера. Пора закругляться — завтра рано вставать.";
            SayCare(text);
        }

        if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && now.Hour is >= 8 and <= 11
            && _care.LastMorningAsk != today
            && string.IsNullOrEmpty(_care.MorningAnswer))
        {
            _care.LastMorningAsk = today;
            _care.Save();
            SayCare("Доброе утро! Ты сегодня на удалёнке или идёшь в колледж?");
        }

        if (_care.Away && !_care.AwaitingReturn && (now - _care.AwaySince).TotalMinutes > 20)
        {
            var cursor = GetCursorPosition();
            if (Math.Abs(cursor.X - _lastMouse.X) > 3 || Math.Abs(cursor.Y - _lastMouse.Y) > 3)
            {
                _care.AwaitingReturn = true;
                _care.LastReturnAsk = now;
                _care.Save();
                SayCare("Вижу, ты за компьютером. Вернулся уже?");
            }
        }
        else if (_care.AwaitingReturn && (now - _care.LastReturnAsk).TotalSeconds > 120)
        {
            _care.AwaitingReturn = false;
            _care.Save();
        }

        _lastMouse = GetCursorPosition();
    }

    private string? HandleCareAnswer(string text)
    {
        var lower = text.ToLowerInvariant();
        var morningPending = DateTime.Now.ToString("yyyy-MM-dd") == _care.LastMorningAsk
                             && string.IsNullOrEmpty(_care.MorningAnswer)
                             && DateTime.Now.Hour is >= 8 and <= 13;

        if (morningPending && (lower.Contains("удал") || lower.Contains("дома") || lower.Contains("онлайн")))
        {
            _care.MorningAnswer = "remote";
            _care.Save();
            var opened = _launcher.OpenTelemost();
            return opened ? "Открыла Телемост. Хорошей учёбы!" : "Не нашла ярлык Телемоста — открой вручную.";
        }

        if (morningPending && (lower.Contains("колледж") || lower.Contains("иду") || lower.Contains("учёб") || lower.Contains("учеб")))
        {
            _care.MorningAnswer = "college";
            _care.Away = true;
            _care.AwaySince = DateTime.Now;
            _care.AwaitingReturn = false;
            _care.Save();
            return "Поняла. Хорошего дня в колледже! Дай знать, когда вернёшься.";
        }

        if (_care.AwaitingReturn && (lower.Contains("вернул") || lower.Contains("приш") || lower.Contains("пришёл") || lower.Contains("дома") || lower == "да"))
        {
            _care.AwaitingReturn = false;
            _care.Away = false;
            _care.Save();
            _launcher.OpenYouTube();
            return "С возвращением! Открыла YouTube — отдыхай.";
        }

        if (_care.AwaitingReturn && (lower.Contains("нет") || lower.Contains("ещё") || lower.Contains("еще")))
        {
            _care.AwaitingReturn = false;
            _care.Save();
            return "Хорошо, тогда продолжу ждать.";
        }

        return null;
    }

    private void SayCare(string text)
    {
        Add("alya", text);
        if (_settings.TtsEnabled) _ = _tts.SpeakAsync(text, "neutral");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CarePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool CareGetCursorPos(out CarePoint point);

    private (int X, int Y) GetCursorPosition()
    {
        return GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);
    }
}

/// <summary>Ярлыки Телемоста и YouTube.</summary>
public sealed class TelemostLauncher
{
    public bool OpenTelemost()
    {
        try
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                     })
            {
                if (!Directory.Exists(root)) continue;
                var shortcut = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(file => file.Contains("Telemost", StringComparison.OrdinalIgnoreCase)
                                            || file.Contains("Телемост", StringComparison.OrdinalIgnoreCase));
                if (shortcut is not null)
                {
                    Process.Start(new ProcessStartInfo(shortcut) { UseShellExecute = true });
                    return true;
                }
            }
        }
        catch
        {
            // ярлык не найден
        }
        return false;
    }

    public void OpenYouTube()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.youtube.com") { UseShellExecute = true });
        }
        catch
        {
            // браузер не открылся
        }
    }
}
