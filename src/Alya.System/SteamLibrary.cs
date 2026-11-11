using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Alya.System;

/// <summary>Установленные игры Steam: чтение libraryfolders.vdf и appmanifest_*.acf.</summary>
public sealed class SteamLibrary
{
    private static readonly Regex AppManifestRegex = new("\"appid\"\\s+\"(\\d+)\"\\s+\"name\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);
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

    public (int AppId, string Name)? Find(string query)
    {
        EnsureLoaded();
        var value = query.Trim().ToLowerInvariant();
        if (value.Length == 0) return null;
        return _games.FirstOrDefault(game => game.Name.ToLowerInvariant().Contains(value)) is { AppId: > 0 } match
            ? match
            : null;
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
                        var match = AppManifestRegex.Match(File.ReadAllText(manifest));
                        if (!match.Success) continue;
                        var appId = int.Parse(match.Groups[1].Value);
                        var name = match.Groups[2].Value;
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
