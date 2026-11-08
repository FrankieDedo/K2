using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace K2.Core.Services;

/// <summary>One installed Steam game: its AppID, its display name and the library folder
/// (the one holding <c>steamapps\</c>) it is installed in.</summary>
public sealed record SteamGame(string AppId, string Name, string LibraryPath, string InstallDir);

/// <summary>
/// The local Steam library, read straight from Steam's own files — no Steam login, no Web
/// API key: the install path from the registry, the library folders from
/// <c>steamapps\libraryfolders.vdf</c>, the installed games from each library's
/// <c>appmanifest_*.acf</c>, and per-game metadata (type, icon hash, launch exe) from the
/// binary <c>appcache\appinfo.vdf</c>. Only INSTALLED games are listed: ownership of the rest
/// of the library is only known server-side.
///
/// The icon of a game is the <c>clienticon</c> .ico Steam keeps in <c>steam\games\</c> (up to
/// 256 px, the same file Steam puts on a desktop shortcut); failing that the game's own exe,
/// then the small JPG icon of <c>appcache\librarycache</c>.
/// </summary>
public static class SteamLibrary
{
    /// <summary>The "steamgame" action type. Its value is <c>"&lt;appid&gt;|&lt;name&gt;"</c>
    /// — the name travels with the key so it still reads right on a PC without the game.</summary>
    public const string ActionType = "steamgame";

    public static (string AppId, string Name)? ParseValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        int bar = value.IndexOf('|');
        string id = (bar < 0 ? value : value[..bar]).Trim();
        if (id.Length == 0 || !id.All(char.IsDigit)) return null;
        return (id, bar < 0 ? "" : value[(bar + 1)..]);
    }

    public static string ToValue(SteamGame g) => $"{g.AppId}|{g.Name}";

    public static string LaunchUri(string appId) => $"steam://rungameid/{appId}";

    /// <summary>Steam's install folder, or null when Steam isn't installed.</summary>
    public static string? SteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p))
                return Path.GetFullPath(p.Replace('/', '\\'));
        }
        catch { }
        return null;
    }

    private static readonly object Gate = new();
    private static SteamGame[]? _games;
    private static DateTime _gamesAt;

    /// <summary>Installed games, sorted by name. Cached for a few seconds (the picker and the
    /// key renderer ask in bursts); never throws.</summary>
    public static SteamGame[] InstalledGames()
    {
        lock (Gate)
        {
            if (_games is not null && DateTime.UtcNow - _gamesAt < TimeSpan.FromSeconds(10)) return _games;
            _games = LoadGames();
            _gamesAt = DateTime.UtcNow;
            return _games;
        }
    }

    private static SteamGame[] LoadGames()
    {
        var result = new Dictionary<string, SteamGame>();
        try
        {
            string? steam = SteamPath();
            if (steam is null) return Array.Empty<SteamGame>();
            var info = AppInfo(steam);

            foreach (string lib in LibraryFolders(steam))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    var root = Vdf.Parse(File.ReadAllText(acf, Encoding.UTF8));
                    var state = Vdf.Child(root, "AppState");
                    string? id = Vdf.Str(state, "appid");
                    string? name = Vdf.Str(state, "name");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

                    // Redistributables, Proton, SteamVR runtimes... are "tool"/"config":
                    // only what the user would call a game (or a Steam application) is offered.
                    string? type = info.Get(id) is { } kv ? Vdf.Str(Vdf.Child(kv, "common"), "type") : null;
                    if (type is not null && !type.Equals("game", StringComparison.OrdinalIgnoreCase)
                                         && !type.Equals("application", StringComparison.OrdinalIgnoreCase)
                                         && !type.Equals("demo", StringComparison.OrdinalIgnoreCase))
                        continue;

                    result[id] = new SteamGame(id, name, lib, Vdf.Str(state, "installdir") ?? "");
                }
            }
        }
        catch { }
        return result.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static IEnumerable<string> LibraryFolders(string steam)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        yield return steam;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        Dictionary<string, object> root;
        try { root = Vdf.Parse(File.ReadAllText(vdf, Encoding.UTF8)); } catch { yield break; }
        foreach (var entry in Vdf.Child(root, "libraryfolders")?.Values.OfType<Dictionary<string, object>>()
                              ?? Enumerable.Empty<Dictionary<string, object>>())
        {
            if (Vdf.Str(entry, "path") is not { Length: > 0 } p) continue;
            string full;
            try { full = Path.GetFullPath(p); } catch { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    /// <summary>The game's icon at the best resolution on disk, or null. Steam's client .ico
    /// wins when it carries a frame of at least <see cref="SharpIconPx"/>; many only hold a
    /// 32 px one (Among Us), and upscaled that reads as a blur, so the game exe's own icon
    /// (normally 256 px) is tried next. Decoded directly, never through the shell: the shell's
    /// thumbnail of a small .ico came back with white columns down its sides.</summary>
    public static System.Drawing.Bitmap? LoadBestIcon(string appId)
    {
        System.Drawing.Bitmap? best = null;
        foreach (string source in IconSources(appId))
        {
            var bmp = LoadPicture(source);
            if (bmp is null) continue;
            if (bmp.Width >= SharpIconPx) { best?.Dispose(); return bmp; }
            if (best is null || bmp.Width > best.Width) { best?.Dispose(); best = bmp; }
            else bmp.Dispose();
        }
        return best;
    }

    private const int SharpIconPx = 64;

    /// <summary>An .exe's icon at 256 px; the largest frame of an .ico; a .jpg as-is.</summary>
    private static System.Drawing.Bitmap? LoadPicture(string path)
    {
        try
        {
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var icon = System.Drawing.Icon.ExtractIcon(path, 0, 256);
                return icon?.ToBitmap();
            }

            using var input = new MemoryStream(File.ReadAllBytes(path));
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            var png = new MemoryStream();
            encoder.Save(png);
            png.Position = 0;
            using var decoded = new System.Drawing.Bitmap(png);
            return new System.Drawing.Bitmap(decoded);   // detached from the stream
        }
        catch { return null; }
    }

    /// <summary>Every picture of the game on disk, best first: Steam's client .ico, the game
    /// exe (from the launch config), the small library .jpg.</summary>
    private static IEnumerable<string> IconSources(string appId)
    {
        var found = new List<string>();
        try
        {
            string? steam = SteamPath();
            if (steam is null) return found;
            var kv = AppInfo(steam).Get(appId);
            var common = Vdf.Child(kv, "common");

            if (Vdf.Str(common, "clienticon") is { Length: > 0 } hash)
            {
                string ico = Path.Combine(steam, "steam", "games", hash + ".ico");
                if (File.Exists(ico)) found.Add(ico);
            }

            if (InstalledGames().FirstOrDefault(g => g.AppId == appId) is { } game && game.InstallDir.Length > 0
                && Vdf.Child(Vdf.Child(kv, "config"), "launch") is { } launch)
            {
                string dir = Path.Combine(game.LibraryPath, "steamapps", "common", game.InstallDir);
                foreach (var entry in launch.Values.OfType<Dictionary<string, object>>())
                {
                    string? os = Vdf.Str(Vdf.Child(entry, "config"), "oslist");
                    if (os is { Length: > 0 } && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Vdf.Str(entry, "executable") is not { Length: > 0 } exe
                        || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string full = Path.Combine(dir, exe.Replace('/', '\\'));
                    if (File.Exists(full)) { found.Add(full); break; }
                }
            }

            string cache = Path.Combine(steam, "appcache", "librarycache");
            if (Vdf.Str(common, "icon") is { Length: > 0 } small)
            {
                string jpg = Path.Combine(cache, appId, small + ".jpg");
                if (File.Exists(jpg)) found.Add(jpg);
            }
            string legacy = Path.Combine(cache, appId + "_icon.jpg");
            if (File.Exists(legacy)) found.Add(legacy);
        }
        catch { }
        return found;
    }

    // ── appinfo.vdf (binary) ─────────────────────────────

    private static AppInfoFile? _appInfo;

    private static AppInfoFile AppInfo(string steam)
    {
        string path = Path.Combine(steam, "appcache", "appinfo.vdf");
        DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        lock (Gate)
        {
            if (_appInfo is null || _appInfo.Stamp != stamp)
                _appInfo = AppInfoFile.Load(path, stamp);
            return _appInfo;
        }
    }

    /// <summary>
    /// Steam's binary app-info cache. Layout (v28 0x07564428, v29 0x07564429): magic, universe,
    /// [v29: int64 offset of the key string table]; then per app: appid u32 (0 ends the list),
    /// size u32, then <c>size</c> bytes = infoState u32, lastUpdated u32, picsToken u64,
    /// sha1[20], changeNumber u32, binarySha1[20] and a binary KeyValues tree. In v29 every
    /// key is an int32 index into the string table instead of an inline C string. Only the
    /// entry offsets are indexed up front; an app's tree is decoded when asked for.
    /// </summary>
    private sealed class AppInfoFile
    {
        public DateTime Stamp { get; private init; }
        private byte[] _data = Array.Empty<byte>();
        private string[]? _strings;
        private readonly Dictionary<string, (int Offset, int End)> _index = new();
        private readonly Dictionary<string, Dictionary<string, object>?> _parsed = new();

        public static AppInfoFile Load(string path, DateTime stamp)
        {
            var f = new AppInfoFile { Stamp = stamp };
            try
            {
                if (!File.Exists(path)) return f;
                byte[] d = File.ReadAllBytes(path);
                uint magic = BitConverter.ToUInt32(d, 0);
                if (magic != 0x07564428 && magic != 0x07564429) return f;
                f._data = d;
                int pos = 8;
                if (magic == 0x07564429)
                {
                    long tableAt = BitConverter.ToInt64(d, 8);
                    pos = 16;
                    int t = (int)tableAt;
                    int count = (int)BitConverter.ToUInt32(d, t); t += 4;
                    var strings = new string[count];
                    for (int i = 0; i < count; i++) strings[i] = CString(d, ref t);
                    f._strings = strings;
                }
                while (pos + 8 <= d.Length)
                {
                    uint appId = BitConverter.ToUInt32(d, pos);
                    if (appId == 0) break;
                    int size = (int)BitConverter.ToUInt32(d, pos + 4);
                    int start = pos + 8;
                    f._index[appId.ToString()] = (start + 60, start + size);
                    pos = start + size;
                }
            }
            catch { }
            return f;
        }

        public Dictionary<string, object>? Get(string appId)
        {
            lock (_parsed)
            {
                if (_parsed.TryGetValue(appId, out var hit)) return hit;
                Dictionary<string, object>? tree = null;
                if (_index.TryGetValue(appId, out var at))
                {
                    try
                    {
                        int p = at.Offset;
                        var root = ReadMap(ref p, at.End);
                        tree = Vdf.Child(root, "appinfo") ?? root;
                    }
                    catch { }
                }
                _parsed[appId] = tree;
                return tree;
            }
        }

        private Dictionary<string, object> ReadMap(ref int p, int end)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (p < end)
            {
                byte type = _data[p++];
                if (type is 0x08 or 0x0B) break;
                string key;
                if (_strings is not null) { key = _strings[BitConverter.ToInt32(_data, p)]; p += 4; }
                else key = CString(_data, ref p);
                switch (type)
                {
                    case 0x00: map[key] = ReadMap(ref p, end); break;
                    case 0x01: map[key] = CString(_data, ref p); break;
                    case 0x02: case 0x04: case 0x06:
                        map[key] = BitConverter.ToInt32(_data, p).ToString(); p += 4; break;
                    case 0x03: map[key] = BitConverter.ToSingle(_data, p).ToString(System.Globalization.CultureInfo.InvariantCulture); p += 4; break;
                    case 0x07: map[key] = BitConverter.ToUInt64(_data, p).ToString(); p += 8; break;
                    case 0x0A: map[key] = BitConverter.ToInt64(_data, p).ToString(); p += 8; break;
                    default: throw new InvalidDataException($"appinfo: kv type {type}");
                }
            }
            return map;
        }

        private static string CString(byte[] d, ref int p)
        {
            int s = p;
            while (d[p] != 0) p++;
            string v = Encoding.UTF8.GetString(d, s, p - s);
            p++;
            return v;
        }
    }

    // ── Text VDF (libraryfolders.vdf, appmanifest_*.acf) ─────────────

    private static class Vdf
    {
        public static Dictionary<string, object>? Child(Dictionary<string, object>? m, string key) =>
            m is not null && m.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static string? Str(Dictionary<string, object>? m, string key) =>
            m is not null && m.TryGetValue(key, out var v) ? v as string : null;

        /// <summary>Quoted keys and values, braces for nesting, // comments. Keys are
        /// case-insensitive, as Steam treats them.</summary>
        public static Dictionary<string, object> Parse(string text)
        {
            int p = 0;
            return ReadMap(text, ref p);
        }

        private static Dictionary<string, object> ReadMap(string t, ref int p)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string? key = Token(t, ref p);
                if (key is null || key == "}") return map;
                string? val = Token(t, ref p);
                if (val is null) return map;
                map[key] = val == "{" ? ReadMap(t, ref p) : val;
            }
        }

        private static string? Token(string t, ref int p)
        {
            while (p < t.Length)
            {
                char c = t[p];
                if (char.IsWhiteSpace(c)) { p++; continue; }
                if (c == '/' && p + 1 < t.Length && t[p + 1] == '/')
                {
                    while (p < t.Length && t[p] != '\n') p++;
                    continue;
                }
                if (c is '{' or '}') { p++; return c.ToString(); }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    p++;
                    while (p < t.Length && t[p] != '"')
                    {
                        if (t[p] == '\\' && p + 1 < t.Length) { p++; sb.Append(t[p] == 'n' ? '\n' : t[p]); }
                        else sb.Append(t[p]);
                        p++;
                    }
                    p++;
                    return sb.ToString();
                }
                int s = p;
                while (p < t.Length && !char.IsWhiteSpace(t[p]) && t[p] is not ('{' or '}' or '"')) p++;
                return t[s..p];
            }
            return null;
        }
    }
}
