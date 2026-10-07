using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using K2.Core;
using K2.Core.Services;

namespace K2.App.Services;

/// <summary>
/// Renders a DisplayPad key's <b>default icon</b> — the picture that belongs to its ACTION
/// (an executable's own icon, a disk folder's Windows icon, a hand-drawn folder/back/nav
/// glyph, ported Base Camp gallery art, a live clock/monitor/speed-test tile, or the MDL2
/// fallback tile) — styled with a <see cref="KeyIconSpec"/>.
///
/// Extracted 2026-09-02 from <c>DpKeyConfigDialog.RenderDefaultIcon</c> so the SAME switch
/// serves three callers: the key-config dialog, the per-key seed sites in
/// <c>MainWindow.DisplayPad.cs</c> (dp_folder/dp_back/Spotify controls), and the one-shot
/// <c>DpRerenderDefaultIcons</c> pass that repaints every default icon when the pad's
/// "default icon background" image changes. The style (background image/colour, text
/// colour, font, caption) reaches the generators through <see cref="IconStyleScope"/>.
/// </summary>
internal static class DpDefaultIconRenderer
{
    /// <summary>Cache dir for generated icons — the SAME folder as
    /// <c>MainWindow.DpAutoIconDir</c> and the old <c>DpKeyConfigDialog.AutoIconCacheRoot</c>.</summary>
    internal static readonly string CacheRoot = Path.Combine(
        K2Paths.For("K2.DisplayPad"), "auto_icons");

    /// <summary>
    /// Returns the generated PNG's path, or null when this action type has no default icon
    /// (or a value-driven type has no value yet). <paramref name="pageName"/> must already be
    /// resolved by the caller for a <c>dp_folder</c> action (the raw value is just a page id).
    /// </summary>
    internal static string? Render(string? actionType, string? actionValue, KeyIconSpec spec,
        string? pageName, int iconSize)
    {
        if (string.IsNullOrEmpty(actionType)) return null;

        // Caption for the two payload-less action types ("dp_emojibrowser"/"dp_back"), which
        // have nothing to draw a caption FROM.
        string? caption = actionType switch
        {
            "dp_emojibrowser" => Loc.Get("emb_caption"),
            "dp_back"         => Loc.Get("dp_back"),
            _                 => null,
        };
        // Only the generators that DRAW FROM the value need one; every other type can still
        // get its glyph tile from ActionIconFallback with an empty value (e.g. "disable").
        bool needsValue = actionType is "exec" or "folder" or "dp_folder" or "googlehome" or "emoji";
        if (needsValue && string.IsNullOrWhiteSpace(actionValue)) return null;

        bool showCaption = spec.ShowText;
        // The user's own caption (typed in "Edit icon") replaces whatever the generator would
        // draw; it also reaches the generators that derive their caption internally through
        // IconStyleScope.OverrideCaption.
        string? userText = string.IsNullOrWhiteSpace(spec.Text) ? null : spec.Text;

        // Cache key includes the style fingerprint (background image + colour + caption + …),
        // so two styled variants of the same action can't collide on one cached PNG.
        string dest = CachePath(actionType,
            $"{caption ?? actionValue ?? ""}|{pageName}|{spec.StyleFingerprint}");

        // Rendered to a private temp file and moved into place only once it is complete. The same
        // cache entry can legitimately be generated twice at once — the game-profile dialog paints
        // its preview on the UI thread while the pad's own refresh paints the same key on the tile
        // timer's thread, and since the Elite preview and live styles stopped differing (the
        // green/orange page flip is gone) the two now land on the SAME filename. Writing in place
        // let one render read a half-written PNG, which loads as nothing: the tile came up with no
        // background, intermittently, on reopening the dialog.
        string work = dest + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

        bool ok;
        using (IconStyleScope.Push(spec))
        {
            // A curated profile (Elite Dangerous) marks its tiles TextOnly so they read as
            // backlit HUD labels instead of a mismatched icon set — see KeyIconSpec.TextOnly.
            // The live-tile types are excluded: they already draw their own text-only variant
            // internally (see LiveTileRenderer.TryRenderEdStatus), keyed off their live state
            // rather than the static caption alone.
            if (spec.TextOnly && actionType is not ("dp_clock" or "dp_sysmon" or "dp_speedtest" or "dp_edstatus"
                                                   or "dp_zcstatus" or KspTelemachus.ActionType or ModLinkGames.ActionType or ModLinkGames.StudioActionType
                                                   or "dp_screen" or "dp_custom"))
            {
                ok = IconImageGenerator.TryGenerateCaptionIcon(userText ?? caption ?? actionValue ?? "", iconSize, work);
                return Commit(ok, work, dest);
            }

            switch (actionType)
            {
                case "dp_back":
                    ok = IconImageGenerator.TryGenerateBackIcon(userText ?? caption!, iconSize, work, showCaption);
                    break;
                case "dp_emojibrowser":
                    ok = EmojiGlyphRenderer.TryGenerateEmojiIcon(
                        "\U0001F600", iconSize, work, showCaption ? userText ?? caption! : "");
                    break;
                case "exec":
                    ok = IconImageGenerator.TryGenerateExecIcon(actionValue!, iconSize, work);
                    break;
                case "folder":
                    ok = IconImageGenerator.TryGenerateDiskFolderIcon(actionValue!, iconSize, work, showCaption);
                    break;
                case "dp_folder":
                    ok = IconImageGenerator.TryGenerateFolderIcon(pageName ?? actionValue!, iconSize, work, showCaption);
                    break;
                case "googlehome":
                    ok = GoogleHomeIconCatalog.TryGenerateKeyIcon(actionValue!, iconSize, work, showCaption);
                    break;
                case "emoji":
                    ok = EmojiGlyphRenderer.TryGenerateEmojiIcon(actionValue!, iconSize, work);
                    break;
                // Live tiles (clock / PC monitor / speed test): what's rendered here is a
                // still snapshot with the real current values — on the hardware these keys are
                // repainted continuously by DpLiveTileService, which owns them.
                case "dp_clock":
                    ok = LiveTileRenderer.TryRenderClock(actionValue, DateTime.Now,
                            showCaption ? userText ?? "" : "", iconSize, work);
                    break;
                case "dp_sysmon":
                {
                    var (text, fraction) = DpLiveTileService.TileValue(actionType, actionValue);
                    ok = LiveTileRenderer.TryRenderGauge(text, fraction,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                }
                case "dp_edstatus":
                    ok = DpLiveTileService.RenderEdTile(actionValue,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                case "dp_zcstatus":
                    ok = DpLiveTileService.RenderZcTile(actionValue,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                case KspTelemachus.ActionType:
                    ok = DpLiveTileService.RenderKspTile(actionValue,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                case ModLinkGames.ActionType:
                case ModLinkGames.StudioActionType:
                    App.WriteCrashLog($"[ICON] modlink tile {actionValue}");
                    ok = DpLiveTileService.RenderModLinkTile(actionValue,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                case "dp_screen":
                    ok = DpLiveTileService.RenderScreenTile(actionValue,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                // A custom tile paints its OWN background, colours and indicator: it is the one
                // live type that ignores the profile's art entirely, because its whole point is
                // that the user chose what it looks like.
                case "dp_custom":
                    ok = CustomActionTile.Render(actionValue,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            iconSize, work);
                    break;
                case "dp_speedtest":
                {
                    var (text, fraction) = DpLiveTileService.TileValue(actionType, actionValue);
                    bool isPing = actionValue == "ping";
                    ok = LiveTileRenderer.TryRenderSpeedTile(text, fraction,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(actionType, actionValue) : "",
                            showCaption ? DpLiveTileService.SpeedTestUnit(actionValue ?? "") : "",
                            iconSize, work,
                            ownValueSize: isPing, valueTopPad: isPing ? 0.06f : 0f,
                            valueSizeReference: isPing ? "888" : null);
                    break;
                }
                default:
                    // A transport/volume/repeat control — "media" or the equivalent "spotify"
                    // Web API command — always gets K2's own solid shape, bypassing the gallery
                    // tie-break entirely (icon_mapping.xml has a Base Camp row for every one of
                    // these which would otherwise win and cost the tile its caption).
                    if (ActionIconFallback.IsControl(actionType, actionValue))
                    {
                        ok = ActionIconFallback.TryGenerate(actionType, actionValue, iconSize, work, showCaption);
                        break;
                    }
                    // Everything else: Base Camp's ported gallery art vs. K2's hand-drawn glyph —
                    // spec.UseK2Icons (the "Edit icon" radio pair) picks which one wins the tie;
                    // whichever side has no art for this action/value falls back to the other.
                    ok = spec.UseK2Icons
                        ? ActionIconFallback.TryGenerate(actionType, actionValue, iconSize, work, showCaption)
                          || IconGalleryDefaults.TryGenerateKeyIcon(actionType, actionValue, iconSize, work)
                        : IconGalleryDefaults.TryGenerateKeyIcon(actionType, actionValue, iconSize, work)
                          || ActionIconFallback.TryGenerate(actionType, actionValue, iconSize, work, showCaption);
                    break;
            }
        }
        return Commit(ok, work, dest);
    }

    /// <summary>Publishes a finished render: moves the temp file over the cache entry, or drops
    /// it when the generator failed. A losing race (the other writer moved first, and the file is
    /// briefly locked) keeps whatever is already there — both renders produce the same picture,
    /// so the loser has nothing to add.</summary>
    private static string? Commit(bool ok, string work, string dest)
    {
        if (!ok) { TryDelete(work); return null; }

        try
        {
            File.Move(work, dest, overwrite: true);
            return dest;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        TryDelete(work);
        return File.Exists(dest) ? dest : null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Bumped whenever a generator's OUTPUT changes for inputs that hash the same, so
    /// the cached PNGs from the previous look are abandoned instead of being served forever. The
    /// cache key is built from the action and its style, neither of which moves when the drawing
    /// code does — dropping the Elite annunciator square (2026-09-05) left every already-rendered
    /// tile showing it.</summary>
    private const int RendererVersion = 3;

    private static string CachePath(string kind, string sourceValue)
    {
        Directory.CreateDirectory(CacheRoot);

        long mtime = 0;
        if (kind == "exec") { try { mtime = File.GetLastWriteTimeUtc(ExecActionPayload.PathOf(sourceValue)).Ticks; } catch { } }
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{kind}|{sourceValue}|{mtime}|v{RendererVersion}"));
        return Path.Combine(CacheRoot, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
    }
}
