using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Alya.System;

/// <summary>Установленные игры Steam: чтение libraryfolders.vdf и appmanifest_*.acf.</summary>
public sealed class SteamLibrary
{
    private static readonly Regex AppIdRegex = new("\"appid\"\\s+\"(\\d+)\"", RegexOptions.Compiled);
    private static readonly Regex NameRegex = new("\"name\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex PathRegex = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly List<(int AppId, string Name)> _games = new();
    private bool _loaded;

    public IReadOnlyList<(int AppId, string Name)> Games
    {
        get
        {
            EnsureLoaded();
            return _games;
        }
    }

    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["кс"] = new[] { "counter-strike" },
        ["кс2"] = new[] { "counter-strike 2" },
        ["кс 2"] = new[] { "counter-strike 2" },
        ["кс го"] = new[] { "counter-strike" },
        ["cs"] = new[] { "counter-strike" },
        ["cs2"] = new[] { "counter-strike 2" },
        ["контра"] = new[] { "counter-strike" },
        ["контру"] = new[] { "counter-strike" },
        ["дбд"] = new[] { "dead by daylight" },
        ["dbd"] = new[] { "dead by daylight" },
        ["дед бай дейлайт"] = new[] { "dead by daylight" },
        ["дедбайдейлайт"] = new[] { "dead by daylight" },
        ["мертвый свет"] = new[] { "dead by daylight" },
        ["дота"] = new[] { "dota 2" },
        ["доту"] = new[] { "dota 2" },
        ["dota"] = new[] { "dota 2" },
        ["ржавчина"] = new[] { "rust" },
        ["раст"] = new[] { "rust" },
        ["rust"] = new[] { "rust" },
        ["гта"] = new[] { "grand theft auto" },
        ["gta"] = new[] { "grand theft auto" },
        ["пабг"] = new[] { "pubg" },
        ["pubg"] = new[] { "pubg" },
        ["террария"] = new[] { "terraria" },
        ["террарию"] = new[] { "terraria" },
        ["майнкрафт"] = new[] { "minecraft" },
        ["апекс"] = new[] { "apex legends" },
        ["apex"] = new[] { "apex legends" },
        ["р6"] = new[] { "rainbow six" },
        ["рэйнбоу"] = new[] { "rainbow six" },
        ["варфейс"] = new[] { "warface" },
        ["саундпад"] = new[] { "soundpad" },
        ["обои"] = new[] { "wallpaper engine" },
        ["втюб"] = new[] { "vtube studio" },
        ["втуб"] = new[] { "vtube studio" }
    };

    public (int AppId, string Name)? Find(string query)
    {
        EnsureLoaded();
        var value = query.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (value.Length == 0) return null;

        if (Aliases.TryGetValue(value, out var patterns))
        {
            foreach (var pattern in patterns)
            {
                var hit = _games.FirstOrDefault(game => game.Name.ToLowerInvariant().Contains(pattern));
                if (hit.AppId > 0) return hit;
            }
        }

        var direct = _games.FirstOrDefault(game => game.Name.ToLowerInvariant().Contains(value));
        if (direct.AppId > 0) return direct;

        // поиск по отдельным словам: «запусти кс го» → counter-strike
        var tokens = value.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2)
            .ToArray();
        if (tokens.Length == 0) return null;

        (int AppId, string Name)? best = null;
        var bestScore = 0;
        foreach (var game in _games)
        {
            var name = game.Name.ToLowerInvariant();
            var score = tokens.Count(token => name.Contains(token));
            if (score > bestScore)
            {
                bestScore = score;
                best = game;
            }
        }
        return bestScore > 0 ? best : null;
    }

    public bool Launch(string query, out string name)
    {
        name = query;
        var game = Find(query);
        if (game is null) return false;
        name = game.Value.Name;
        try
        {
            Process.Start(new ProcessStartInfo($"steam://run/{game.Value.AppId}") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var steamPath = @"C:\Program Files (x86)\Steam";
            if (!Directory.Exists(steamPath)) steamPath = @"C:\Program Files\Steam";
            if (!Directory.Exists(steamPath)) return;

            var libraries = new List<string> { steamPath };
            var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                foreach (Match match in PathRegex.Matches(File.ReadAllText(libraryFile)))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(path)) libraries.Add(path);
                }
            }

            foreach (var library in libraries.Distinct())
            {
                var steamApps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamApps)) continue;
                foreach (var manifest in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var content = File.ReadAllText(manifest);
                        var idMatch = AppIdRegex.Match(content);
                        var nameMatch2 = NameRegex.Match(content);
                        if (!idMatch.Success || !nameMatch2.Success) continue;
                        var match = idMatch;
                        var nameValue = nameMatch2.Groups[1].Value;
                        if (!match.Success) continue;
                        var appId = int.Parse(match.Groups[1].Value);
                        var name = nameValue;
                        if (_games.All(game => game.AppId != appId)) _games.Add((appId, name));
                    }
                    catch
                    {
                        // битый манифест пропускаем
                    }
                }
            }

            _games.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Steam может быть не установлен
        }
    }
}
