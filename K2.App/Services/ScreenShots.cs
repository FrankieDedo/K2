using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>One screenshot the user chose to keep, and what it is a picture of.</summary>
public sealed record ScreenShotDef
{
    public string Id { get; init; } = "";

    /// <summary>What the browser lists it as. Generated on save from the program and the time —
    /// a screenshot is told apart from its neighbours by WHEN it was taken far more often than by
    /// anything a user would have typed.</summary>
    public string Name { get; init; } = "";

    /// <summary>Program the picture is of. The browser leads with the shots of the program the
    /// probe is aimed at, since a shot of another game is never the one being looked for.</summary>
    public string Process { get; init; } = "";

    public DateTime TakenAt { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// Screenshots the user SAVED, as opposed to the one frame per program K2 keeps by itself
/// (<see cref="ScreenFrameCache"/>).
///
/// <para><b>Why both exist.</b> The automatic cache answers "let me nudge this rectangle with the
/// game shut down" and holds exactly one picture per program, silently replaced by the next
/// capture. That is the wrong shape for calibration work that needs to compare SITUATIONS — the
/// HUD with a full health bar and the same HUD with an empty one, the weapon before and after the
/// swap. Those are pictures the user picks deliberately and must be able to come back to, so they
/// are named, listed, and only ever deleted on purpose.</para>
///
/// <para>PNG for the same reason as the cache: every number in the calibration window is measured
/// on these pixels by the code the pad will run, so a lossy format would show up as a preview that
/// disagrees with the tile.</para>
/// </summary>
internal static class ScreenShotStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "shots");

    private static readonly string IndexPath = Path.Combine(Dir, "shots.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly object _gate = new();
    private static List<ScreenShotDef>? _cache;

    /// <summary>Newest first — the shot someone wants is nearly always the one they just took.</summary>
    public static IReadOnlyList<ScreenShotDef> All()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(IndexPath)
                    ? JsonSerializer.Deserialize<List<ScreenShotDef>>(File.ReadAllText(IndexPath), Json)
                      ?? new List<ScreenShotDef>()
                    : new List<ScreenShotDef>();

                // A shot whose PNG went missing (a hand-cleaned folder, a failed write) is dropped
                // rather than listed: an entry that cannot be shown is worse than no entry.
                _cache = _cache.Where(s => File.Exists(PathFor(s.Id)))
                               .OrderByDescending(s => s.TakenAt)
                               .ToList();
            }
            catch (Exception ex)
            {
                App.WriteLog($"[SHOT] cannot read \"{IndexPath}\": {ex.Message}");
                _cache = new List<ScreenShotDef>();
            }
            return _cache;
        }
    }

    public static ScreenShotDef? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All().FirstOrDefault(s => s.Id == id);

    private static string PathFor(string id) => Path.Combine(Dir, id + ".png");

    /// <summary>Keeps a copy of <paramref name="frame"/> and lists it. Returns the new entry, or
    /// null when the picture could not be written — in which case nothing is listed, so the
    /// browser never offers a shot that is not there.</summary>
    public static ScreenShotDef? Save(string process, Bitmap frame)
    {
        var now = DateTime.Now;
        var def = new ScreenShotDef
        {
            Id = "s" + DateTime.UtcNow.Ticks.ToString("x"),
            Name = (process.Length > 0 ? process : "?") + " · " + now.ToString("dd/MM HH:mm"),
            Process = process,
            TakenAt = now,
            Width = frame.Width,
            Height = frame.Height,
        };

        try
        {
            Directory.CreateDirectory(Dir);
            // Via a temp file, like the cache: a half-written PNG is a shot that never loads again
            // and nothing on screen would say why.
            string path = PathFor(def.Id);
            string tmp = path + ".tmp";
            frame.Save(tmp, ImageFormat.Png);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.WriteLog($"[SHOT] could not save a screenshot of {process}: {ex.Message}");
            return null;
        }

        lock (_gate)
        {
            var list = All().ToList();
            list.Insert(0, def);
            Write(list);
        }
        return def;
    }

    /// <summary>Removes a screenshot, picture and all. Deliberately not undoable: the browser asks
    /// before calling this.</summary>
    public static void Delete(string id)
    {
        lock (_gate)
        {
            try { File.Delete(PathFor(id)); }
            catch (Exception ex) { App.WriteLog($"[SHOT] could not delete {id}: {ex.Message}"); }
            Write(All().Where(s => s.Id != id).ToList());
        }
    }

    /// <summary>The picture, DETACHED from its file so the caller owns it and nothing stays
    /// locked — the same rule as <see cref="ScreenFrameCache.Load"/>.</summary>
    public static Bitmap? Load(string id)
    {
        try
        {
            string path = PathFor(id);
            if (!File.Exists(path)) return null;

            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var loaded = new Bitmap(ms);
            return new Bitmap(loaded);
        }
        catch (Exception ex)
        {
            App.WriteLog($"[SHOT] screenshot {id} unreadable: {ex.Message}");
            return null;
        }
    }

    private static void Write(List<ScreenShotDef> list)
    {
        _cache = list;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(list, Json));
        }
        catch (Exception ex)
        {
            App.WriteLog($"[SHOT] cannot write \"{IndexPath}\": {ex.Message}");
        }
    }
}
