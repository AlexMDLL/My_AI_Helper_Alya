using System.Net.Http.Json;
using System.Text.Json;

namespace Alya.System;

/// <summary>Погода через Open-Meteo (без ключей).</summary>
public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<string?> GetAsync(string city, CancellationToken cancellationToken = default)
    {
        try
        {
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=ru&format=json";
            using var geo = JsonDocument.Parse(await Http.GetStringAsync(geoUrl, cancellationToken));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var place = results[0];
            var name = place.GetProperty("name").GetString() ?? city;
            var latitude = place.GetProperty("latitude").GetDouble();
            var longitude = place.GetProperty("longitude").GetDouble();

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&longitude={longitude.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}" +
                      "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m&timezone=auto";
            using var forecast = JsonDocument.Parse(await Http.GetStringAsync(url, cancellationToken));
            var current = forecast.RootElement.GetProperty("current");
            var temperature = current.GetProperty("temperature_2m").GetDouble();
            var feels = current.GetProperty("apparent_temperature").GetDouble();
            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetDouble();
            var code = current.GetProperty("weather_code").GetInt32();

            return $"Сейчас в {name}: {Math.Round(temperature)}°, ощущается как {Math.Round(feels)}°, {Describe(code)}, ветер {Math.Round(wind)} км/ч, влажность {Math.Round(humidity)}%.";
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetTomorrowAsync(string city, CancellationToken cancellationToken = default)
    {
        try
        {
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=ru&format=json";
            using var geo = JsonDocument.Parse(await Http.GetStringAsync(geoUrl, cancellationToken));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var place = results[0];
            var name = place.GetProperty("name").GetString() ?? city;
            var latitude = place.GetProperty("latitude").GetDouble();
            var longitude = place.GetProperty("longitude").GetDouble();
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}" +
                      $"&longitude={longitude.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}" +
                      "&daily=temperature_2m_max,temperature_2m_min,weather_code,precipitation_probability_max&forecast_days=2&timezone=auto";
            using var forecast = JsonDocument.Parse(await Http.GetStringAsync(url, cancellationToken));
            var daily = forecast.RootElement.GetProperty("daily");
            var max = daily.GetProperty("temperature_2m_max")[1].GetDouble();
            var min = daily.GetProperty("temperature_2m_min")[1].GetDouble();
            var code = daily.GetProperty("weather_code")[1].GetInt32();
            var precipitation = daily.GetProperty("precipitation_probability_max")[1].GetDouble();
            return $"Завтра в {name}: от {Math.Round(min)}° до {Math.Round(max)}°, {Describe(code)}, вероятность осадков {Math.Round(precipitation)}%.";
        }
        catch
        {
            return null;
        }
    }
    private static string Describe(int code) => code switch
    {
        0 => "ясно",
        1 or 2 => "переменная облачность",
        3 => "пасмурно",
        45 or 48 => "туман",
        51 or 53 or 55 => "морось",
        61 or 63 or 65 => "дождь",
        66 or 67 => "ледяной дождь",
        71 or 73 or 75 => "снег",
        77 => "снежная крупа",
        80 or 81 or 82 => "ливень",
        85 or 86 => "снегопад",
        95 => "гроза",
        96 or 99 => "гроза с градом",
        _ => "облачно"
    };
}
