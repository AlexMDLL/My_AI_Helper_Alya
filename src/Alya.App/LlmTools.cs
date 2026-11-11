using System.Text.Json;
using Alya.System;

namespace Alya.App;

/// <summary>Инструменты, которые модель может вызывать сама (Ollama tool-calling).</summary>
public sealed class LlmTools
{
    private readonly WindowsSystemController _system;
    private readonly SteamLibrary _steam;
    private readonly WeatherService _weather;
    private readonly List<(DateTime Due, string Message)> _reminders;

    public LlmTools(
        WindowsSystemController system,
        SteamLibrary steam,
        WeatherService weather,
        List<(DateTime Due, string Message)> reminders)
    {
        _system = system;
        _steam = steam;
        _weather = weather;
        _reminders = reminders;
    }

    public object[] Schema => new object[]
    {
        Tool("open_app", "Открыть программу или игру по названию (хром, стим, дискорд, телега, фортнайт, блокнот и т.п.).",
            new { name = Prop("string", "название программы") }, new[] { "name" }),
        Tool("close_app", "Закрыть запущенную программу по названию.",
            new { name = Prop("string", "название программы") }, new[] { "name" }),
        Tool("media", "Управление воспроизведением и громкостью.",
            new { action = EnumProp("действие", new[] { "play_pause", "next", "prev", "volume_up", "volume_down", "mute" }) }, new[] { "action" }),
        Tool("switch_window", "Переключиться на уже открытое окно по названию программы или заголовку.",
            new { query = Prop("string", "название окна/программы") }, new[] { "query" }),
        Tool("list_windows", "Показать список открытых окон.", new { }, Array.Empty<string>()),
        Tool("set_app_volume", "Изменить громкость конкретного приложения в процентах.",
            new { app = Prop("string", "название приложения"), percent = Prop("integer", "громкость 0-100") }, new[] { "app", "percent" }),
        Tool("power", "Выключить или перезагрузить компьютер.",
            new { action = EnumProp("действие", new[] { "shutdown", "restart" }) }, new[] { "action" }),
        Tool("set_reminder", "Поставить напоминание через N секунд.",
            new { seconds = Prop("integer", "через сколько секунд"), message = Prop("string", "что напомнить") }, new[] { "seconds", "message" }),
        Tool("weather", "Узнать текущую погоду в городе.",
            new { city = Prop("string", "город (необязательно)") }, Array.Empty<string>())
    };

    private static object Tool(string name, string description, object properties, string[] required) => new
    {
        type = "function",
        function = new
        {
            name,
            description,
            parameters = new { type = "object", properties, required }
        }
    };

    private static object Prop(string type, string description) => new { type, description };

    private static object EnumProp(string description, string[] values) => new { type = "string", description, @enum = values };

    public async Task<string> ExecuteAsync(string name, JsonElement arguments)
    {
        string Arg(string key)
        {
            return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out var value)
                ? value.ToString()
                : string.Empty;
        }

        try
        {
            switch (name)
            {
                case "open_app":
                {
                    var target = Arg("name");
                    return _system.Launch(target) ? $"Открыла {target}." : $"Не нашла {target}.";
                }
                case "close_app":
                {
                    var target = Arg("name");
                    return _system.Close(target) ? $"Закрыла {target}." : $"{target} не запущено.";
                }
                case "media":
                {
                    var action = Arg("action");
                    byte key = action switch
                    {
                        "next" => 0xB0,
                        "prev" => 0xB1,
                        "volume_up" => 0xAF,
                        "volume_down" => 0xAE,
                        "mute" => 0xAD,
                        _ => 0xB3
                    };
                    _system.MediaKey(key);
                    return "Готово.";
                }
                case "switch_window":
                    return _system.Activate(Arg("query")) ? "Переключилась." : "Не нашла такое окно.";
                case "list_windows":
                {
                    var windows = _system.VisibleWindows();
                    return windows.Count == 0 ? "Открытых окон нет." : string.Join("; ", windows);
                }
                case "set_app_volume":
                {
                    var app = Arg("app");
                    if (!int.TryParse(Arg("percent"), out var percent)) return "Не поняла громкость.";
                    var value = _system.SetAppVolume(app, percent);
                    return value < 0 ? $"Нет активной сессии {app}." : $"Громкость {app}: {value}%.";
                }
                case "power":
                {
                    var action = Arg("action") == "restart" ? "/r" : "/s";
                    global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo("shutdown", $"{action} /t 15") { CreateNoWindow = true });
                    return "Выполняю через 15 секунд.";
                }
                case "set_reminder":
                {
                    if (!int.TryParse(Arg("seconds"), out var seconds) || seconds <= 0) return "Не поняла время.";
                    var message = Arg("message");
                    _reminders.Add((DateTime.Now.AddSeconds(seconds), message));
                    return $"Напоминание через {seconds} сек: {message}.";
                }
                case "weather":
                {
                    var city = Arg("city");
                    if (string.IsNullOrWhiteSpace(city)) city = Environment.GetEnvironmentVariable("ALYA_CITY") ?? "Сыктывкар";
                    var weather = await _weather.GetAsync(city);
                    return weather ?? "Не удалось узнать погоду.";
                }
                default:
                    return "Неизвестный инструмент.";
            }
        }
        catch (Exception error)
        {
            return $"Ошибка: {error.Message}";
        }
    }
}
