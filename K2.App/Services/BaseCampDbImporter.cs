using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using K2.Core;
using Microsoft.Data.Sqlite;

namespace K2.App.Services;

/// <summary>
/// Reads profiles from BaseCamp.db and imports them into K2 stores.
///
/// Schema BaseCamp.db relevant tables:
///   Profiles:                ProfileId, Id (1-5 slot), DeviceType, ProfileName,
///                            IsSelected, DeviceId, DeviceGUID
///   DisplayPadLayerBidings:  ProfileId (FK), KeyId (170-179,220,221 = M1-M12),
///                            FunctionType, SubFunctionType, FunctionValue,
///                            base64Image, IsKeyAssigned
///   EverestKeyBidings:       ProfileId (FK), DLLMatrixIndex (= SDK wMatrix),
///                            FunctionType, SubFunctionType, FunctionValue,
///                            base64Image, IsKeyAssigned, IsTouchKey
///                            Used for BOTH DeviceType="Everest" and "MacroPad".
/// </summary>
public sealed class BaseCampDbImporter
{
    /// <summary>Lowest profile slot (1..<paramref name="maxSlots"/>) not present in
    /// <paramref name="existingSlots"/>, or 0 if all are taken. Used by every device's
    /// import flow so an imported profile lands in a fresh slot instead of overwriting
    /// whatever K2 profile already occupies the source's own slot number (BC DB's
    /// <c>Profiles.Id</c> / XML's <c>&lt;Id&gt;</c>) — that source number has no meaning
    /// on this K2 install, it's just whichever slot the profile happened to occupy on
    /// the machine it was exported from.</summary>
    public static int FindFreeSlot(IEnumerable<int> existingSlots, int maxSlots = 5)
    {
        var used = new HashSet<int>(existingSlots);
        for (int s = 1; s <= maxSlots; s++)
            if (!used.Contains(s)) return s;
        return 0;
    }

    // KeyId/DLLMatrixIndex → button index (0-11) for DisplayPad and MacroPad
    internal static readonly Dictionary<int, int> KeyIdToIndex = new()
    {
        { 170, 0 }, { 171, 1 }, { 172, 2 }, { 173, 3 },
        { 174, 4 }, { 175, 5 }, { 176, 6 }, { 177, 7 },
        { 178, 8 }, { 179, 9 }, { 220, 10 }, { 221, 11 },
    };

    /// <summary>Finds the path to BaseCamp.db by searching known installations.</summary>
    public static string? FindBaseCampDb()
    {
        // 1. Explicit environment variable
        var env = Environment.GetEnvironmentVariable("K2_BASECAMP_DB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        // 2. Base Camp installation folders (already discovered by NativeDependencyResolver)
        foreach (var dir in NativeDependencyResolver.BaseCampDirectories())
        {
            // The DB is in resources/bin/ (Electron app)
            var candidate = Path.Combine(dir, "resources", "bin", "BaseCamp.db");
            if (File.Exists(candidate)) return candidate;
            // Fallback: next to the exe
            candidate = Path.Combine(dir, "BaseCamp.db");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>DisplayPad profile read from the Base Camp DB.</summary>
    public sealed record BcProfile(
        int ProfileId,
        int Slot,           // Id in Profiles (1-5)
        string Name,
        int DeviceId,
        string? DeviceGUID,
        bool IsSelected);

    /// <summary>Key of a DisplayPad profile read from the DB.</summary>
    public sealed record BcButton(
        int ButtonIndex,    // 0-11
        string? FunctionType,
        string? SubFunctionType,
        string? FunctionValue,
        string? Base64Image,
        bool IsAssigned,
        int ParentId = 0,           // 0 = root page; > 0 = folder sub-page ID
        string? OptionalText = null, // JSON with {"Id":<pageId>,...} for "Create Folder" keys
        string? CustomURL = null);   // set alongside FunctionType="Run browser" when the key opens a specific URL

    /// <summary>
    /// Reads all DisplayPad profiles from the database.
    /// Groups by DeviceId → list of profiles.
    /// </summary>
    public static Dictionary<int, List<BcProfile>> ReadProfiles(string dbPath)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ProfileId, Id, ProfileName, DeviceId, DeviceGUID, IsSelected
            FROM Profiles
            WHERE DeviceType = 'DisplayPad'
            ORDER BY DeviceId, Id";

        var result = new Dictionary<int, List<BcProfile>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var p = new BcProfile(
                ProfileId:  r.GetInt32(0),
                Slot:       r.GetInt32(1),
                Name:       r.IsDBNull(2) ? "" : r.GetString(2),
                DeviceId:   r.GetInt32(3),
                DeviceGUID: r.IsDBNull(4) ? null : r.GetString(4),
                IsSelected: r.GetInt32(5) != 0);

            if (!result.TryGetValue(p.DeviceId, out var list))
            {
                list = new List<BcProfile>();
                result[p.DeviceId] = list;
            }
            list.Add(p);
        }
        return result;
    }

    /// <summary>Reads the keys of a specific profile (all pages, including sub-folders).</summary>
    public static List<BcButton> ReadButtons(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();

        // Check which optional columns exist
        bool hasParentId    = ColumnExistsInDb(conn, "DisplayPadLayerBidings", "ParentId");
        bool hasOptionalText = ColumnExistsInDb(conn, "DisplayPadLayerBidings", "OptionalText");
        bool hasCustomUrl   = ColumnExistsInDb(conn, "DisplayPadLayerBidings", "CustomURL");

        string extra = (hasParentId ? ", ParentId" : "") + (hasOptionalText ? ", OptionalText" : "")
            + (hasCustomUrl ? ", CustomURL" : "");
        cmd.CommandText = $@"
            SELECT KeyId, FunctionType, SubFunctionType, FunctionValue,
                   base64Image, IsKeyAssigned{extra}
            FROM DisplayPadLayerBidings
            WHERE ProfileId = $pid
            ORDER BY {(hasParentId ? "ParentId, " : "")}KeyId";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcButton>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int keyId = r.GetInt32(0);
            if (!KeyIdToIndex.TryGetValue(keyId, out int idx)) continue;

            int parentId = 0;
            string? optText = null;
            string? customUrl = null;
            int col = 6;
            if (hasParentId)    { parentId = r.IsDBNull(col) ? 0 : r.GetInt32(col);   col++; }
            if (hasOptionalText){ optText   = r.IsDBNull(col) ? null : r.GetString(col); col++; }
            if (hasCustomUrl)   { customUrl = r.IsDBNull(col) ? null : r.GetString(col); }

            result.Add(new BcButton(
                ButtonIndex:     idx,
                FunctionType:    r.IsDBNull(1) ? null : r.GetString(1),
                SubFunctionType: r.IsDBNull(2) ? null : r.GetString(2),
                FunctionValue:   r.IsDBNull(3) ? null : r.GetString(3),
                Base64Image:     r.IsDBNull(4) ? null : r.GetString(4),
                IsAssigned:      r.GetInt32(5) != 0,
                ParentId:        parentId,
                OptionalText:    optText,
                CustomURL:       customUrl));
        }
        return result;
    }

    private static bool ColumnExistsInDb(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (r.GetString(1) == column) return true;
        return false;
    }

    /// <summary>
    /// Imports a Base Camp profile into the K2 store for a specific device, into
    /// <paramref name="targetSlot"/> (a fresh slot picked by the caller via
    /// <see cref="FindFreeSlot"/> — NOT necessarily <c>profile.Slot</c>, which is only
    /// where it happened to live on the source Base Camp install).
    /// Saves the base64 images to disk and the translated actions.
    /// Returns the number of keys imported.
    /// </summary>
    public static int ImportProfile(
        string dbPath,
        BcProfile profile,
        int k2DeviceId,
        DisplayPadStore store,
        int targetSlot,
        IReadOnlyCollection<string>? macroNames = null)
    {
        var buttons = ReadButtons(dbPath, profile.ProfileId);
        int slot = targetSlot;
        int imported = 0;

        // Directory for the imported images
        string iconsDir = Path.Combine(
            K2Paths.For("K2.DisplayPad"), "imported_bc", $"dev{k2DeviceId}_slot{slot}_{profile.Name}");
        Directory.CreateDirectory(iconsDir);

        store.ClearProfile(k2DeviceId, slot);

        foreach (var btn in buttons)
        {
            // Skip only if truly empty (no action AND no image)
            if (!btn.IsAssigned && string.IsNullOrEmpty(btn.Base64Image)) continue;

            int pageId = btn.ParentId; // 0 = root, >0 = folder sub-page

            // Save image
            string? imagePath = null;
            if (!string.IsNullOrEmpty(btn.Base64Image))
            {
                try
                {
                    var imgBytes = DecodeBase64Image(btn.Base64Image);
                    if (imgBytes is not null)
                    {
                        string iconFile = pageId == 0
                            ? Path.Combine(iconsDir, $"key_{btn.ButtonIndex}.png")
                            : Path.Combine(iconsDir, $"key_p{pageId}_{btn.ButtonIndex}.png");
                        File.WriteAllBytes(iconFile, imgBytes);
                        imagePath = iconFile;
                    }
                    // else: BC internal path — no image available, skip silently
                }
                catch { /* corrupted image or invalid encoding, skip */ }
            }

            // Translate action (folder/back handled specially)
            string? actionType, actionValue;
            if (btn.FunctionType == "Create Folder")
            {
                int folderPageId = ParseFolderPageId(btn.OptionalText);
                actionType  = "dp_folder";
                actionValue = folderPageId > 0 ? folderPageId.ToString() : null;
                if (folderPageId > 0 && !string.IsNullOrEmpty(btn.SubFunctionType))
                    store.SetFolderName(folderPageId, btn.SubFunctionType);
            }
            else if (btn.FunctionType == "Back")
            {
                actionType  = "dp_back";
                actionValue = null;

                // BC's own data rarely carries a real per-key icon for its "Back" button
                // (usually just BC's internal chrome, not a base64 image — see the
                // "else: BC internal path" case above). Give it the same auto-generated
                // arrow+caption tile the in-app "Set as Back button" context-menu item
                // uses (MainWindow.DisplayPad.cs::DpMnuSetBack_Click), instead of leaving
                // it iconless. Only when BC's XML/db genuinely had NO image for this key —
                // an actually-customized icon (imagePath already set above) is left alone.
                if (imagePath is null)
                {
                    string dest = Path.Combine(iconsDir, pageId == 0
                        ? $"key_{btn.ButtonIndex}_back.png"
                        : $"key_p{pageId}_{btn.ButtonIndex}_back.png");
                    if (IconImageGenerator.TryGenerateBackIcon(Loc.Get("dp_back"), DpHidNative.IconSize, dest))
                        imagePath = dest;
                }
            }
            else
            {
                (actionType, actionValue) = TranslateAction(
                    btn.FunctionType, btn.SubFunctionType, btn.FunctionValue, macroNames, btn.CustomURL);
            }

            store.SaveButton(k2DeviceId, slot, pageId, btn.ButtonIndex,
                imagePath, actionType, actionValue);
            imported++;
        }

        return imported;
    }

    /// <summary>
    /// True if the action is Base Camp's "Default" placeholder preserved verbatim by an
    /// older import (<c>bc:Default</c>) — it means "no custom binding", so every store
    /// load treats it as an empty button instead of a mapping. New imports no longer
    /// produce it (<see cref="TranslateAction"/> maps "Default" to no action).
    /// </summary>
    internal static bool IsBcDefaultAction(string? actionType) =>
        string.Equals(actionType, "bc:Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps Base Camp's "Run browser" action to K2's native "browser" action instead of the
    /// generic "url" type or a valueless placeholder — pre-selects the first browser
    /// <see cref="BrowserDetector"/> finds installed (its fixed chrome/edge/firefox/opera/brave
    /// order) so the imported button already points at a real, launchable browser instead of
    /// relying on the legacy "no browser chosen" fallback (OS default via ShellExecute).
    /// </summary>
    private static (string? ActionType, string? ActionValue) ImportBrowserAction(string? url)
    {
        var installed = BrowserDetector.DetectInstalled();
        var payload = new BrowserActionPayload
        {
            Browser = installed.Count > 0 ? installed[0].Id : "other",
            Url     = url ?? "",
        };
        return ("browser", payload.ToJson());
    }

    /// <summary>
    /// Maps Base Camp's "Run Program" action to K2's "exec" action — unless the target
    /// executable is one of the well-known browsers (<see cref="BrowserDetector.TryIdentifyByExeName"/>),
    /// in which case it becomes K2's native "browser" action with that browser pre-selected instead
    /// (a "Run Program" pointed at chrome.exe/msedge.exe/etc. is really a browser-open action that
    /// just wasn't expressed as one in Base Camp).
    /// </summary>
    private static (string? ActionType, string? ActionValue) ImportExecOrBrowserAction(string? execPath)
    {
        string? browserId = BrowserDetector.TryIdentifyByExeName(execPath);
        if (browserId is null) return ("exec", execPath);

        var payload = new BrowserActionPayload { Browser = browserId, Url = "" };
        return ("browser", payload.ToJson());
    }

    /// <summary>Parses {"Id":2407,...} from OptionalText to extract the folder page ID.</summary>
    internal static int ParseFolderPageId(string? optionalText)
    {
        if (string.IsNullOrEmpty(optionalText)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(optionalText);
            if (doc.RootElement.TryGetProperty("Id", out var el))
                return el.GetInt32();
        }
        catch { /* malformed JSON */ }
        return 0;
    }

    /// <summary>
    /// Where a Base Camp binding's payload actually lives: <c>SubFunctionType</c> on
    /// DisplayPad rows (which duplicate it into <c>FunctionValue</c>), but ONLY in
    /// <c>FunctionValue</c> on Everest Max / Everest 60 / Makalu rows — every
    /// SubFunctionType there is NULL in the DB and absent from the XML export entirely
    /// (the serializer omits null strings). Confirmed 2026-07-26 against a real
    /// BaseCamp.db (DisplayPadLayerBidings: "Open Folder"/sub=path/value=path, vs
    /// Everest60KeyBidings: "Open Folder"/sub=NULL/value=path) and the matching
    /// <c>ev60_test.xml</c> export. Before this, every arm below read subType alone, so
    /// on those devices an imported Open Folder / Media / Mouse / Profile / OS Command
    /// key landed with a null value — a mapped key with nothing in it (user report
    /// 2026-07-26). Whitespace-only counts as missing (BC writes "" as often as NULL).
    /// </summary>
    private static string? BcPayload(string? subType, string? funcValue) =>
        !string.IsNullOrWhiteSpace(subType)   ? subType
        : !string.IsNullOrWhiteSpace(funcValue) ? funcValue
        : null;

    /// <summary>
    /// Translates Base Camp's FunctionType/SubFunctionType/FunctionValue
    /// into K2.Core action types (ActionType/ActionValue).
    /// </summary>
    public static (string? ActionType, string? ActionValue) TranslateAction(
        string? funcType, string? subType, string? funcValue,
        IReadOnlyCollection<string>? macroNames = null, string? customUrl = null)
    {
        if (string.IsNullOrEmpty(funcType)) return (null, null);

        // See BcPayload: subType is only populated on DisplayPad rows, so every arm that
        // needs the binding's payload takes it from here, not from subType directly.
        string? payload = BcPayload(subType, funcValue);

        return funcType switch
        {
            "Run Program" =>
                ImportExecOrBrowserAction(payload),   // payload = path to exe

            "Open Folder" =>
                ("folder", payload),              // opens OS folder in Explorer

            // DisplayPad sub-page navigation (page ID in ActionValue when known)
            "Create Folder" =>
                ("dp_folder", null),              // caller should use ParseFolderPageId for the page ID
            "Back" =>
                ("dp_back", null),

            // Real Base Camp always writes FunctionValue="Run browser" verbatim (confirmed
            // against decompiled BC source + the real DB schema): a per-key destination URL
            // is never in FunctionValue, it lives in the sibling CustomURL column instead
            // (BaseCamp.Data.DisplayPadLayerBidings/KeyboardBinding/Everest60KeyBinding, all
            // [Column("CustomURL")], and BaseCamp.Repository's schema SQL). When that's set,
            // the key isn't "launch a browser" — it's "open this specific page" — so it maps
            // to K2's own "url" (Apri URL) action instead of "browser" (Apri browser) with an
            // empty destination. Previously CustomURL was never read at all, so every such
            // key imported as a bare "Run a browser" with no target.
            "Run browser" => !string.IsNullOrWhiteSpace(customUrl)
                ? ("url", customUrl.Trim())
                : ImportBrowserAction(funcValue is null or "" or "Run browser" ? null : funcValue),

            "Profile" => payload switch
            {
                "Next Profile" or "Profile Cycle" => ("profile", "next"),
                "Previous Profile"                => ("profile", "prev"),
                _ when int.TryParse(payload, out var n) => ("profile", n.ToString()),
                // A named-profile target (SubFunctionType = the destination profile's NAME,
                // e.g. jump straight to "TEST1") is real Base Camp data (confirmed via a user
                // XML export, 2026-07-19). K2's profile-switch executors (MpSwitchProfile/
                // EvSwitchProfile/Ev60SwitchProfile/DpSwitchProfile) now resolve a name too
                // (case-insensitive match against that device's own profile names, tried
                // after Next/Previous/a numeric slot) — added 2026-07-27, see TODO.md — so
                // the raw name is passed through as-is instead of the old unrecognized `bc:`
                // passthrough. If the name doesn't match any profile on the target device at
                // execution time, the executor logs "not resolved" and no-ops, same as any
                // other unresolved target string.
                _ when !string.IsNullOrWhiteSpace(payload) => ("profile", payload),
                _ => ($"bc:{funcType}", payload)
            },

            "Key Shortcut" or "Shortcut Key" or "Keyboard Shortcuts" =>
                ("keys", funcValue),

            "Multi Key" =>
                ("keys", funcValue),

            // App-specific shortcut-library categories: Base Camp's FunctionValue is
            // already the literal keyboard shortcut to send (confirmed via real user XML,
            // 2026-07-19: e.g. FunctionType="Adobe"/SubFunctionType="Illustrator"/
            // FunctionValue="Ctrl + Z") — same shape as "Keyboard Shortcuts", just grouped
            // under an app name instead of a generic label.
            "Adobe" or "DaVinci" or "Zoom" =>
                ("keys", funcValue),

            // Real Base Camp XML/DB data uses "Mouse" (not "Mouse Button" as this switch
            // assumed before 2026-07-19) — confirmed against a user export where every
            // mouse-button/scroll/forward-backward binding carries FunctionType="Mouse".
            // The old "Mouse Button" case never matched anything real, so every mouse
            // action silently fell through to the generic bc: passthrough on import.
            "Mouse" =>
                ("mouse", payload?.ToLowerInvariant()),

            "OS Commands" =>
                ("oscmd", ActionTypeHelper.NormalizeOsCommand(payload)),

            // Inline per-key action chain (distinct from "Run Macro"'s named Macro Library
            // reference): FunctionValue is a JSON array of {FunctionType,SubFunctionType,
            // FunctionValue,ActionDelay,...} steps, already understood end-to-end by K2's
            // own "multi" action type (ActionExecutor.RunMultiAction/MapSubAction — the
            // step vocabulary there already covers Media/OS Commands/Mouse/Adobe/DaVinci/
            // Zoom/Keyboard Shortcuts/Profile, mirroring this same switch).
            "Multi Action" =>
                ("multi", funcValue),

            // "Run Macro" is the FunctionType real Base Camp data uses for a Macro Library
            // reference (verified in the user's live BaseCamp.db): DisplayPad rows carry the
            // macro's name in BOTH SubFunctionType and FunctionValue, Everest rows in
            // FunctionValue only — TranslateDefaultAction's subType-then-funcValue fallback
            // covers both shapes. Previously only "Macro" (never seen in real data) and
            // "Default" were handled, so every real macro key fell through to the generic
            // "bc:Run Macro" arm below and showed up as an unrecognized action.
            "Macro" or "Run Macro" =>
                TranslateDefaultAction(subType, funcValue, macroNames),

            "Open Website" or "Open URL" =>
                ("url", funcValue),

            // Base Camp's own media wording, normalized to K2's canonical vocabulary —
            // see ActionTypeHelper.NormalizeMediaKey for why the old snake_case output
            // here ("play_pause") was wrong on both ends (executor and picker).
            "Media" =>
                ("media", ActionTypeHelper.NormalizeMediaKey(payload)),

            "Text" =>
                ("text", funcValue),

            "Mouse Button" =>
                ("mouse", payload?.ToLowerInvariant()),

            // A key Base Camp explicitly disabled. Imported as K2's own "disable" action
            // (was dropped outright before 2026-07-26, so the key came back alive after
            // an import) — see ButtonActionEngine's "disable" arm for what it does per
            // device.
            "Disable" or "Disabled" =>
                ("disable", null),

            "Default" =>
                TranslateDefaultAction(subType, funcValue, macroNames),

            _ =>
                // Unknown type: preserve it generically
                ($"bc:{funcType}", funcValue ?? subType)
        };
    }

    /// <summary>
    /// Base Camp's "Default" FunctionType is ALWAYS a reference to a NAMED macro from BC's own
    /// Macro Library (SubFunctionType holds the macro's name) — including single-character
    /// entries like "À": confirmed via a real decompiled snapshot of a user's BC macro DB
    /// (K2.DisplayPad/Assets/BaseCampMacros.json) that lists "À"/"È"/etc. as genuine named
    /// macros (type "text", value = that same character), not a distinct raw-literal case.
    /// An earlier version of this method special-cased single-character names as literal
    /// "text" actions directly, which produced the right on-screen character by coincidence
    /// but skipped macro-name matching entirely — reported by the user as "le macro non sono
    /// state riconosciute come macro ma come paste text" after importing a profile whose only
    /// Default bindings happened to be single accented characters. K2 doesn't import BC's
    /// macro CONTENT automatically (it lives in a separate DB table real BC XML exports don't
    /// even include), so every name becomes K2's own "macro" (Play Macro) action type, matched
    /// case-insensitively against <paramref name="macroNames"/> (the caller's current K2 macro
    /// library) when a same-named macro already exists there — otherwise left with no macro
    /// assigned, which <see cref="ActionTypeHelper.IsMacroMissingTarget"/> flags so the UI's
    /// "action not found" warning triangle surfaces it for manual assignment instead of
    /// silently dropping the binding. Also used for the "Run Macro"/"Macro" FunctionTypes
    /// (same named-macro reference, name in SubFunctionType and/or FunctionValue).
    /// </summary>
    private static (string? ActionType, string? ActionValue) TranslateDefaultAction(
        string? subType, string? funcValue, IReadOnlyCollection<string>? macroNames)
    {
        var name = !string.IsNullOrEmpty(subType) ? subType : funcValue;
        if (string.IsNullOrEmpty(name)) return (null, null);

        string? matched = macroNames?.FirstOrDefault(
            n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        // Unmatched: keep the original Base Camp macro name behind the "***" marker
        // (ActionTypeHelper.UnresolvedMacroPrefix) instead of discarding it — the UI shows
        // it with a yellow warning triangle and the name in the summary, so the user knows
        // WHICH macro to create/assign; the engine never plays a marked value.
        return ("macro", matched ?? ActionTypeHelper.UnresolvedMacroPrefix + name);
    }

    // =========================================================
    // Shared keyboard lighting — KeyboardLightings table / <EverestLightings> XML.
    // Used by BOTH Everest Max AND MacroPad — confirmed 2026-07-26 against a real
    // BaseCamp.db: KeyboardLightings has rows for both DeviceType=Everest and
    // DeviceType=MacroPad ProfileIds, and its numeric EffIndex column (0/1/3/4/5/
    // 6/7/9/10/11/12) matches EverestSdkNative.EffectIndex/MacroPadSdkNative.
    // EffectIndex exactly (same firmware family, confirmed byte-identical). Real
    // Base Camp XML exports serialize the same field as the enum's NAME instead
    // ("Static"/"ColorWave"/"Reactivea"/... — XmlSerializer default for an enum-
    // typed property), never the number — see ResolveLightingEffectByte.
    // =========================================================

    /// <summary>One lighting effect slot, device-agnostic (Everest Max/MacroPad
    /// share the same effect byte space and Settings key schema — see
    /// <see cref="ApplyLightingToStore"/>).</summary>
    public sealed record BcLightingRow(
        byte EffectByte, int Speed, int Brightness, int RawDirection,
        int Color1, int Color2, int Color3, bool IsActive);

    /// <summary>Resolves a lighting effect to its firmware byte from either the DB's
    /// numeric EffIndex (<paramref name="numeric"/>) or the XML's string EffIndex
    /// name (<paramref name="name"/>) — see class-level doc comment.</summary>
    internal static byte ResolveLightingEffectByte(string? name, int? numeric)
    {
        if (numeric is int n && n is >= 0 and <= 255) return (byte)n;
        return (name ?? "").Trim().ToLowerInvariant() switch
        {
            "static"                              => (byte)EverestSdkNative.EffectIndex.Static,
            "colorwave" or "color wave" or "wave" => (byte)EverestSdkNative.EffectIndex.Wave,
            "tornado"                             => (byte)EverestSdkNative.EffectIndex.Tornado,
            "breathing" or "breath"               => (byte)EverestSdkNative.EffectIndex.Breath,
            "reactivea" or "reactive"             => (byte)EverestSdkNative.EffectIndex.ReactiveA,
            "reactiveb"                           => (byte)EverestSdkNative.EffectIndex.ReactiveB,
            "reactivec"                           => (byte)EverestSdkNative.EffectIndex.ReactiveC,
            "matrix"                              => (byte)EverestSdkNative.EffectIndex.Matrix,
            "custom"                              => (byte)EverestSdkNative.EffectIndex.Custom,
            "yeti" or "yeti mode" or "yetimode"   => (byte)EverestSdkNative.EffectIndex.Yeti,
            "off"                                 => (byte)EverestSdkNative.EffectIndex.Off,
            _                                     => (byte)EverestSdkNative.EffectIndex.Static,
        };
    }

    // Direction raw-code tables — confirmed identical between Everest Max's and
    // MacroPad's own CapsFor() tables in MainWindow.Everest.cs/MainWindow.MacroLed.cs
    // (same firmware family): Wave Right/Down/Left/Up -> 0/2/4/6, Tornado
    // Clockwise/Counter-CW -> 9/10. Every other effect has no direction control in
    // either panel, so a raw value there is meaningless (index stays 0).
    private static readonly Dictionary<byte, int[]> s_lightingDirCodes = new()
    {
        { (byte)EverestSdkNative.EffectIndex.Wave,    new[] { 0, 2, 4, 6 } },
        { (byte)EverestSdkNative.EffectIndex.Tornado, new[] { 9, 10 } },
    };

    /// <summary>Direction index for an effect, from Base Camp's own Direction value.
    /// CORRECTED 2026-07-26: real Base Camp data (DB column and XML alike) stores a
    /// 0-based UI index, NOT the firmware wire code — a real export has Direction=3 on
    /// both Color Wave and Tornado (and even on Off), impossible in the wire-code space
    /// (Wave 0/2/4/6, Tornado 9/10). The old pure <c>IndexOf</c> lookup therefore
    /// silently collapsed every 1/3 to 0 ("Right"). An in-range value is now taken as
    /// the index directly, with the wire-code lookup kept as a fallback so a genuine
    /// code (6, 9, 10 — outside the index range) still resolves.</summary>
    internal static int LightingDirIndex(byte effectByte, int rawDirection)
    {
        if (!s_lightingDirCodes.TryGetValue(effectByte, out var codes)) return 0;
        if (rawDirection >= 0 && rawDirection < codes.Length) return rawDirection;
        return Math.Max(0, Array.IndexOf(codes, rawDirection));
    }

    /// <summary>Same index-vs-wire-code resolution as <see cref="LightingDirIndex"/>,
    /// for Everest 60's own (Label, Code) direction tables.</summary>
    internal static int Everest60DirIndexFor(Everest60Protocol.Effect eff, int rawDirection)
    {
        var table = eff switch
        {
            Everest60Protocol.Effect.Wave    => Everest60Protocol.WaveDirections,
            Everest60Protocol.Effect.Tornado => Everest60Protocol.TornadoDirections,
            _ => null,
        };
        if (table is null || table.Length == 0) return 0;
        if (rawDirection >= 0 && rawDirection < table.Length) return rawDirection;
        return Math.Max(0, Array.FindIndex(table, d => d.Code == rawDirection));
    }

    /// <summary>
    /// Maps a Base Camp Everest 60 <c>DLLKeyId</c> to K2's own LED index: 0-63 for the
    /// main board (<c>Everest60RemapData.LedIndexToDllKeyIdArray</c>) or
    /// <c>Everest60Protocol.NumpadLedIndexBase + n</c> for the accessory numpad
    /// (<c>Everest60RemapData.NumpadDllKeyId</c>). Returns -1 for a catalog key this
    /// hardware doesn't have (F1-F24, nav cluster, …) — Base Camp exports a base-layer
    /// row for EVERY catalog entry, not just the physical keys.
    ///
    /// NEVER use the XML's <c>DLLMatrixIndex</c> for this: that's Base Camp's own matrix
    /// numbering and only coincides with the LED index for the first 40 keys — confirmed
    /// 2026-07-26 against a real BC export where 24 of the 64 physical keys (Enter
    /// onwards) drift by a growing offset and the non-60% rows carry values up to 110,
    /// far outside the 64-LED map (the XML import used to write those verbatim).
    /// </summary>
    internal static int Everest60LedIndexFromDllKeyId(int dllKeyId)
    {
        int main = Array.IndexOf(Everest60RemapData.LedIndexToDllKeyIdArray, dllKeyId);
        if (main >= 0) return main;
        int np = Array.IndexOf(Everest60RemapData.NumpadDllKeyId, dllKeyId);
        return np >= 0 ? Everest60Protocol.NumpadLedIndexBase + np : -1;
    }

    /// <summary>Everest 60's per-LED Custom paint, split into the four dictionaries
    /// <c>Ev60LightingRecord</c> keeps: main keys + numpad keys (the latter at
    /// <c>NumpadLedIndexBase + n</c>), the 44-LED side ring and the 22-LED numpad
    /// ring.</summary>
    public sealed record BcEverest60Custom(
        Dictionary<int, int> KeyColors,
        Dictionary<int, int> SideColors,
        Dictionary<int, int> NumpadRingColors);

    /// <summary>
    /// Parses Everest 60's <c>CustomLightings</c> payload — a JSON array of
    /// <c>{"Ids":..,"KeyCode":..,"ColorHex":".."}</c> (same shape in the DB column and in
    /// real BC XML exports), 192 entries = the whole
    /// <see cref="Everest60Protocol.ColorEntryCount"/> address space.
    ///
    /// CORRECTED 2026-07-26: <c>KeyCode</c> is the firmware LED HARDWARE ADDRESS, not
    /// K2's logical key index — this used to be read as a logical index and everything
    /// past Esc landed on the wrong key, with the side ring and numpad dropped entirely.
    /// Proven on a real painted profile: the 64 addresses in
    /// <see cref="Everest60Protocol.LedIndex"/> all carried the same red, the 44
    /// <see cref="Everest60Protocol.SideLedIndex"/> ones the same yellow, the 17
    /// <c>Everest60RemapData.NumpadDllKeyId</c>-parallel
    /// <see cref="Everest60Protocol.NumpadLedIndex"/> ones the same blue and the 22
    /// <see cref="Everest60Protocol.NumpadSideLedIndex"/> ones yellow again — i.e. exactly
    /// "main red, numpad blue, both rings yellow", whereas reading 0..63 as logical
    /// indices produced a meaningless 30-red/26-white/8-blue mix. The 45 addresses no
    /// physical LED uses stay at Base Camp's #ffffff filler and are skipped.
    /// </summary>
    internal static BcEverest60Custom ParseEverest60Custom(string? json)
    {
        var keys = new Dictionary<int, int>();
        var side = new Dictionary<int, int>();
        var numpadRing = new Dictionary<int, int>();
        var result = new BcEverest60Custom(keys, side, numpadRing);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "undefined" or "null") return result;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("KeyCode", out var kc) || !el.TryGetProperty("ColorHex", out var ch)) continue;
                int addr = kc.GetInt32();
                if (addr is < 0 or > byte.MaxValue) continue;
                int rgb = ParseBcColor(ch.GetString());

                int main = Array.IndexOf(Everest60Protocol.LedIndex, (byte)addr);
                if (main >= 0) { keys[main] = rgb; continue; }

                int np = Array.IndexOf(Everest60Protocol.NumpadLedIndex, (byte)addr);
                if (np >= 0) { keys[Everest60Protocol.NumpadLedIndexBase + np] = rgb; continue; }

                int sd = Array.IndexOf(Everest60Protocol.SideLedIndex, (byte)addr);
                if (sd >= 0) { side[sd] = rgb; continue; }

                int nr = Array.IndexOf(Everest60Protocol.NumpadSideLedIndex, (byte)addr);
                if (nr >= 0) numpadRing[nr] = rgb;
                // anything else: an address with no physical LED behind it
            }
        }
        catch { /* malformed JSON */ }
        return result;
    }

    /// <summary>Per-key paint state of the "Custom" effect — static colors and
    /// dynamic-effect assignments, mutually exclusive per LED exactly like K2's own
    /// <c>_customKeyColors</c>/<c>_customKeyEffects</c> split.</summary>
    /// <param name="Colors">LED index → 0xRRGGBB (black/unpainted keys omitted).</param>
    /// <param name="Effects">LED index → effect byte (<c>EverestSdkNative.EffectIndex</c>/
    /// <c>MacroPadSdkNative.EffectIndex</c>, same space as <c>EverestService.Effect</c>/
    /// <c>MacroPadService.Effect</c>).</param>
    public sealed record BcCustomLighting(Dictionary<int, int> Colors, Dictionary<int, byte> Effects);

    /// <summary>Length of Base Camp's per-key Custom arrays for Everest Max: exactly the
    /// 126 real keycap LEDs (<c>EverestSideLedProtocol.KeycapWireCount</c>'s meaningful
    /// range 0-125, the 7 padding slots excluded), so array position = K2's own LED index
    /// = wire position. Index identity is inferred from that exact length match plus the
    /// fact that K2's raw-HID keycap pages were captured from Base Camp itself — worth
    /// re-checking on hardware the first time a painted BC profile is imported.</summary>
    internal const int EverestKeycapLedCount = 126;

    /// <summary>Same, for the MacroPad: 12 keys in M1..M12 order.</summary>
    internal const int MacroPadKeyCount = 12;

    /// <summary>
    /// Parses the <c>CustomLightings</c> payload carried by the "Custom" row of
    /// KeyboardLightings/<c>&lt;EverestLightings&gt;</c> (Everest Max AND MacroPad —
    /// same shape, only the array length differs: 126 keycap LEDs vs 12 keys).
    /// Decoded 2026-07-26 from real Base Camp exports: it's a JSON ARRAY of the 8
    /// selectable paint brushes (one per effect, numeric <c>EffIndex</c>), each carrying
    /// its OWN nested <c>CustomLightings</c> string —
    /// <c>{"color":[{"r":..,"g":..,"b":..}, ...]}</c> for the Static brush (the per-key
    /// static colors) and <c>{"effValue":[0/1, ...]}</c> for every dynamic brush (which
    /// keys that effect is painted on). Array position = LED index (the 126 meaningful
    /// keycap wire positions / the 12 M-keys in M1..M12 order).
    /// Returns null when there's nothing usable to import.
    /// </summary>
    internal static BcCustomLighting? ParseKeyboardCustomLighting(string? json, int ledCount)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "undefined" or "null") return null;

        var colors = new Dictionary<int, int>();
        var effects = new Dictionary<int, byte>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            foreach (var brush in doc.RootElement.EnumerateArray())
            {
                if (!brush.TryGetProperty("CustomLightings", out var innerEl)) continue;
                string? inner = innerEl.GetString();
                if (string.IsNullOrWhiteSpace(inner)) continue;

                byte effByte = brush.TryGetProperty("EffIndex", out var ei) && ei.TryGetInt32(out var effInt)
                    ? (byte)effInt
                    : (byte)EverestSdkNative.EffectIndex.Static;

                using var innerDoc = System.Text.Json.JsonDocument.Parse(inner);

                if (innerDoc.RootElement.TryGetProperty("color", out var colorArr)
                    && colorArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var c in colorArr.EnumerateArray())
                    {
                        if (i >= ledCount) break;
                        int r = c.TryGetProperty("r", out var re) ? re.GetInt32() : 0;
                        int g = c.TryGetProperty("g", out var ge) ? ge.GetInt32() : 0;
                        int b = c.TryGetProperty("b", out var be) ? be.GetInt32() : 0;
                        int rgb = ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
                        // Black = never painted (K2 treats an absent LED as black too),
                        // so it's dropped rather than stored as an explicit black.
                        if (rgb != 0) colors[i] = rgb;
                        i++;
                    }
                }
                else if (innerDoc.RootElement.TryGetProperty("effValue", out var effArr)
                         && effArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var v in effArr.EnumerateArray())
                    {
                        if (i >= ledCount) break;
                        if (v.TryGetInt32(out var on) && on != 0) effects[i] = effByte;
                        i++;
                    }
                }
            }
        }
        catch { return null; /* malformed JSON */ }

        // A LED is in exactly one of the two maps — a dynamic brush wins over the
        // static color it painted over (same invariant K2's own PaintLed enforces).
        foreach (var led in effects.Keys) colors.Remove(led);

        return colors.Count == 0 && effects.Count == 0 ? null : new BcCustomLighting(colors, effects);
    }

    /// <summary>
    /// True when a Custom payload looks like a board the user really painted, and the
    /// imported profile should therefore LAND on the Custom effect instead of whatever
    /// Base Camp had active (user request 2026-07-26: "se piu' LED sono assegnati
    /// individualmente, assegna direttamente come effetto il custom"). Base Camp keeps
    /// the painted board and the active effect independent, so a profile could import
    /// with a full board and still show Color Wave — the paint was there, just never
    /// visible (confirmed on a real import: 126 colors landed in the store while
    /// <c>rgb.p1.effect</c> stayed 4).
    ///
    /// A UNIFORM board doesn't count: that's the shape of Base Camp's own filler
    /// (126 x #FFFFFF on Everest Max, 12 x #000000 on the MacroPad — see
    /// <see cref="ParseKeyboardCustomLighting"/>). A partial board does, even in a single
    /// color, since unpainted LEDs are dropped rather than stored black.
    ///
    /// This only works because Base Camp NULLS the payload out on these two devices when
    /// a profile never used Custom (verified: every nested entry null on two such
    /// profiles), so "there is a painted board" really is evidence. **Everest 60 is the
    /// exception and deliberately has no equivalent**: it always stores the full
    /// 192-address board and keeps it forever, so two exports of the same profile — one
    /// on Custom, one on Color Wave — carry a byte-identical payload. There the only
    /// honest signal is Base Camp's own IsActive, which is exactly what
    /// <see cref="ReadEverest60LightingRaw"/> uses (user report 2026-07-26: importing an
    /// Everest 60 profile from XML landed on Custom without checking whether Custom was
    /// really in use).
    /// </summary>
    internal static bool LooksPainted(BcCustomLighting custom, int ledCount)
    {
        if (custom.Effects.Count > 0) return true;
        if (custom.Colors.Count == 0) return false;
        if (custom.Colors.Count < ledCount) return true;
        return custom.Colors.Values.Distinct().Take(2).Count() >= 2;
    }

    /// <summary>Reads the "Custom" row's per-key paint state for a profile
    /// (EffIndex 10 = <c>EverestSdkNative.EffectIndex.Custom</c>, confirmed against a
    /// real BaseCamp.db) — see <see cref="ParseKeyboardCustomLighting"/>.</summary>
    public static BcCustomLighting? ReadKeyboardCustomLighting(string dbPath, int profileId, int ledCount)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT CustomLightings FROM KeyboardLightings
            WHERE ProfileId = $pid AND EffIndex = $eff";
        cmd.Parameters.AddWithValue("$pid", profileId);
        cmd.Parameters.AddWithValue("$eff", (int)EverestSdkNative.EffectIndex.Custom);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        return ParseKeyboardCustomLighting(r.GetString(0), ledCount);
    }

    /// <summary>Writes a parsed <see cref="BcCustomLighting"/> into a device store using
    /// the JSON shapes the Custom Lighting panels already persist
    /// (<c>{"ledIndex":"#RRGGBB"}</c> and <c>{"ledIndex":effectByte}</c> — see
    /// MainWindow.CustomLighting.cs's SaveCustomColorsToStore and its MacroPad twin).
    /// Key names are passed in whole because the two devices namespace them
    /// differently (<c>custom.p{slot}.keyLedColors</c> vs
    /// <c>macroled.p{slot}.custom.keyColors</c>).</summary>
    internal static void ApplyCustomLightingToStore(
        Action<string, string> setSetting, string colorsKey, string effectsKey, BcCustomLighting custom)
    {
        setSetting(colorsKey, System.Text.Json.JsonSerializer.Serialize(
            custom.Colors.ToDictionary(kv => kv.Key.ToString(), kv => $"#{kv.Value:X6}")));
        setSetting(effectsKey, System.Text.Json.JsonSerializer.Serialize(
            custom.Effects.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
    }

    /// <summary>Reads every KeyboardLightings row for a profile (Everest Max or
    /// MacroPad — same table, see class-level note).</summary>
    public static List<BcLightingRow> ReadKeyboardLightings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EffIndex, Speed, Brightness, Direction, Color1, Color2, Color3, IsActive
            FROM KeyboardLightings WHERE ProfileId = $pid";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcLightingRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            byte eff = ResolveLightingEffectByte(null, r.GetInt32(0));
            result.Add(new BcLightingRow(
                eff,
                r.IsDBNull(1) ? 50 : r.GetInt32(1),
                r.IsDBNull(2) ? 100 : r.GetInt32(2),
                r.IsDBNull(3) ? 0 : r.GetInt32(3),
                ParseBcColor(r.IsDBNull(4) ? null : r.GetString(4), 0x900000),
                ParseBcColor(r.IsDBNull(5) ? null : r.GetString(5), 0x000000),
                ParseBcColor(r.IsDBNull(6) ? null : r.GetString(6), 0x000000),
                !r.IsDBNull(7) && r.GetInt32(7) != 0));
        }
        return result;
    }

    /// <summary>Writes lighting rows into a device store's per-effect Settings keys
    /// under <paramref name="prefix"/> (e.g. <c>"rgb.p3."</c> or <c>"macroled.p3."</c>)
    /// — same schema MainWindow's RGB/MacroLed panels read via their own
    /// LoadEffectParamsIntoControls/LoadMacroLedFromStore. Rainbow/colorDouble are
    /// deliberately left at the panel's own default (single color): Base Camp's
    /// "Type" column doesn't reliably distinguish them (see BcLightingRow's absence
    /// of a Type field) and guessing wrong would be worse than the existing default,
    /// same call already made for Everest 60 (Ev60LightingRecord.Rainbow always
    /// false on import). Sets <c>{prefix}effect</c> to whichever row had IsActive,
    /// if any.</summary>
    public static void ApplyLightingToStore(Action<string, string> setSetting, string prefix, IEnumerable<BcLightingRow> rows)
    {
        byte? active = null;
        foreach (var row in rows)
        {
            string p = $"{prefix}{row.EffectByte}.";
            setSetting(p + "speed", Math.Clamp(row.Speed, 0, 100).ToString());
            setSetting(p + "direction", LightingDirIndex(row.EffectByte, row.RawDirection).ToString());
            setSetting(p + "brightness", Math.Clamp(row.Brightness, 0, 100).ToString());
            setSetting(p + "color1", row.Color1.ToString());
            setSetting(p + "color2", row.Color2.ToString());
            setSetting(p + "color3", row.Color3.ToString());
            if (row.IsActive) active = row.EffectByte;
        }
        if (active is byte a) setSetting(prefix + "effect", ((int)a).ToString());
    }

    // =========================================================
    // Shared keyboard settings — KeyboardSettings table / <EverestKeyboardSettings>
    // XML. Same sharing pattern as KeyboardLightings above (confirmed against a
    // real BaseCamp.db: rows exist for both Everest Max and MacroPad ProfileIds).
    // Game Mode bit layout confirmed by decompiling Base Camp's own
    // EverestOperations.SaveSettings (see MainWindow.Everest.cs's EvGameModeBitmask
    // doc comment): 0x1=DisableShift, 0x2=DisableAltF4, 0x4=DisableWin,
    // 0x8=DisableAltTab. TurnOffAfter is a "H:MM:SS" TimeSpan string (e.g.
    // "0:00:10"), not a plain seconds count.
    // =========================================================

    /// <summary>Game Mode + Core Indicator LED + the Display Dial fields that are
    /// actually present in this per-profile table (auto-off timer, clock format) —
    /// NOT the full Dial page/screensaver configuration, which real Base Camp keeps
    /// in a separate, profile-agnostic DisplayDials table not exposed in per-profile
    /// XML/DB export at all (confirmed against a real BaseCamp.db: DisplayDials has
    /// exactly one global row, no ProfileId column).</summary>
    public sealed record BcKeyboardSettings(
        int GameModeBitmask, bool IndicatorLed,
        bool DialTurnOffEnable, int DialTurnOffSeconds, int DialClockType);

    internal static int TurnOffSecondsFromTimeSpanText(string? hms) =>
        !string.IsNullOrWhiteSpace(hms) && TimeSpan.TryParse(hms, out var ts) ? (int)ts.TotalSeconds : 0;

    /// <summary>Reads the KeyboardSettings row for a profile (Everest Max or
    /// MacroPad — see class-level note; MacroPad has no Game Mode/Core LED/Dial UI
    /// of its own, so callers for that device only use the key-binding half of this
    /// table's data — this reader stays device-agnostic like ReadKeyboardLightings).</summary>
    public static BcKeyboardSettings? ReadKeyboardSettings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DisableShift, DisableAltF4, DisableWin, DisableAltTab, EnableCoreLED,
                   IsTurnOffAfter, TurnOffAfter, ClockType
            FROM KeyboardSettings WHERE ProfileId = $pid";
        cmd.Parameters.AddWithValue("$pid", profileId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        int mode = (!r.IsDBNull(0) && r.GetInt32(0) != 0 ? 0x1 : 0)
                 | (!r.IsDBNull(1) && r.GetInt32(1) != 0 ? 0x2 : 0)
                 | (!r.IsDBNull(2) && r.GetInt32(2) != 0 ? 0x4 : 0)
                 | (!r.IsDBNull(3) && r.GetInt32(3) != 0 ? 0x8 : 0);
        bool led = !r.IsDBNull(4) && r.GetInt32(4) != 0;
        bool turnOffEnable = !r.IsDBNull(5) && r.GetInt32(5) != 0;
        int turnOffSeconds = TurnOffSecondsFromTimeSpanText(r.IsDBNull(6) ? null : r.GetString(6));
        int clockType = r.IsDBNull(7) ? 0 : r.GetInt32(7);

        return new BcKeyboardSettings(mode, led, turnOffEnable, turnOffSeconds, clockType);
    }

    // =========================================================
    // Everest + MacroPad — EverestKeyBidings table
    // =========================================================

    /// <summary>
    /// Key binding read from EverestKeyBidings.
    /// Used for both DeviceType="Everest" and DeviceType="MacroPad".
    /// <c>DLLMatrixIndex</c> equals the SDK wMatrix value:
    ///   • Everest: arbitrary index (stored as KeyMatrix in EverestStore)
    ///   • MacroPad: 170-179 / 220-221 → button index 0-11 via <see cref="KeyIdToIndex"/>
    /// <c>IsTouchKey=true</c> on Everest = numpad display key (has LCD, has image).
    /// </summary>
    public sealed record BcKeyBinding(
        int     DLLMatrixIndex,
        string? FunctionType,
        string? SubFunctionType,
        string? FunctionValue,
        string? Base64Image,
        bool    IsAssigned,
        bool    IsTouchKey,
        string? CustomURL = null); // set alongside FunctionType="Run browser" when the key opens a specific URL

    /// <summary>Reads Everest profiles (DeviceType="Everest") grouped by DeviceId.</summary>
    public static Dictionary<int, List<BcProfile>> ReadEverestProfiles(string dbPath)
        => ReadProfilesByType(dbPath, "Everest");

    /// <summary>Reads MacroPad profiles (DeviceType="MacroPad") grouped by DeviceId.</summary>
    public static Dictionary<int, List<BcProfile>> ReadMacroPadProfiles(string dbPath)
        => ReadProfilesByType(dbPath, "MacroPad");

    private static Dictionary<int, List<BcProfile>> ReadProfilesByType(string dbPath, string deviceType)
        => ReadProfilesByTypes(dbPath, deviceType);

    /// <summary>Same as <see cref="ReadProfilesByType"/> but matches any of several
    /// DeviceType values — needed for Makalu, where the 67/Max models write different
    /// strings ("Makalu67"/"MakaluMax") for what K2 treats as one device module.</summary>
    private static Dictionary<int, List<BcProfile>> ReadProfilesByTypes(string dbPath, params string[] deviceTypes)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        string placeholders = string.Join(",", deviceTypes.Select((_, i) => $"$dt{i}"));
        cmd.CommandText = $@"
            SELECT ProfileId, Id, ProfileName, DeviceId, DeviceGUID, IsSelected
            FROM Profiles
            WHERE DeviceType IN ({placeholders})
            ORDER BY DeviceId, Id";
        for (int i = 0; i < deviceTypes.Length; i++)
            cmd.Parameters.AddWithValue($"$dt{i}", deviceTypes[i]);

        var result = new Dictionary<int, List<BcProfile>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var p = new BcProfile(
                ProfileId:  r.GetInt32(0),
                Slot:       r.GetInt32(1),
                Name:       r.IsDBNull(2) ? "" : r.GetString(2),
                DeviceId:   r.GetInt32(3),
                DeviceGUID: r.IsDBNull(4) ? null : r.GetString(4),
                IsSelected: r.GetInt32(5) != 0);
            if (!result.TryGetValue(p.DeviceId, out var list))
                result[p.DeviceId] = list = new List<BcProfile>();
            list.Add(p);
        }
        return result;
    }

    /// <summary>Reads all key bindings from EverestKeyBidings for a profile.</summary>
    public static List<BcKeyBinding> ReadKeyBindings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();

        bool hasCustomUrl = ColumnExistsInDb(conn, "EverestKeyBidings", "CustomURL");
        cmd.CommandText = $@"
            SELECT DLLMatrixIndex, FunctionType, SubFunctionType, FunctionValue,
                   base64Image, IsKeyAssigned, IsTouchKey{(hasCustomUrl ? ", CustomURL" : "")}
            FROM EverestKeyBidings
            WHERE ProfileId = $pid
            ORDER BY DLLMatrixIndex";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcKeyBinding>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new BcKeyBinding(
                DLLMatrixIndex:  r.GetInt32(0),
                FunctionType:    r.IsDBNull(1) ? null : r.GetString(1),
                SubFunctionType: r.IsDBNull(2) ? null : r.GetString(2),
                FunctionValue:   r.IsDBNull(3) ? null : r.GetString(3),
                Base64Image:     r.IsDBNull(4) ? null : r.GetString(4),
                IsAssigned:      r.GetInt32(5) != 0,
                IsTouchKey:      r.GetInt32(6) != 0,
                CustomURL:       hasCustomUrl && !r.IsDBNull(7) ? r.GetString(7) : null));
        }
        return result;
    }

    /// <summary>
    /// Imports an Everest profile into <see cref="EverestStore"/>.
    /// Regular keys (IsTouchKey=false) → Keys table by DLLMatrixIndex.
    /// Touch keys (IsTouchKey=true, LCD display keys) → image saved to disk, path+action
    /// stored in Settings as <c>ndk.{slot}.{i}.imagePath</c> / <c>ndk.{slot}.{i}.actionType</c>
    /// etc. — PER PROFILE (each firmware profile stores its own 4 NDK pictures, confirmed via
    /// USB capture — see MainWindow.NumpadDisplayKeys.cs's UploadNdkImage doc comment).
    /// Returns (regularKeys, touchKeys) counts. <paramref name="targetSlot"/> is a fresh
    /// slot picked by the caller via <see cref="FindFreeSlot"/>, not <c>profile.Slot</c>.
    /// </summary>
    public static (int Regular, int Touch) ImportEverestProfile(
        string dbPath, BcProfile profile, EverestStore store, int targetSlot,
        IReadOnlyCollection<string>? macroNames = null)
    {
        var bindings = ReadKeyBindings(dbPath, profile.ProfileId);
        int slot = targetSlot;
        int regular = 0, touch = 0;

        // Register the profile's name unconditionally, BEFORE translating any binding —
        // mirrors ImportMakaluProfile/ImportEverest60Profile. Without this, a profile whose
        // regular keys all translate to (null, null) (e.g. only "Default"/unmapped bindings,
        // or a profile that's entirely NDK/touch-key content) writes no Keys row and stays
        // entirely invisible to EverestStore.GetExistingProfiles (which has no other way to
        // know the slot exists — the NDK settings written below aren't checked by it either),
        // so the profile silently disappears after import instead of showing up empty.
        // Confirmed real bug (user report 2026-07-17: "a volte quando si importa da xml non
        // viene creato nessun nuovo profilo, resta solo il primo").
        store.SetProfileName(slot, profile.Name);

        // Split: regular keys (actions) vs touch keys (LCD images)
        var touchKeys = bindings.Where(b => b.IsTouchKey).OrderBy(b => b.DLLMatrixIndex).ToList();
        var regularKeys = bindings.Where(b => !b.IsTouchKey && b.IsAssigned).ToList();

        // ── Regular keys ──────────────────────────────────────
        // Clear existing, then write new records
        // (EverestStore has no ClearProfile: we just overwrite via SaveKey)
        foreach (var b in regularKeys)
        {
            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames, b.CustomURL);
            if (at is null) continue;
            // DLLMatrixIndex is the raw SDK wMatrix code, a different numbering space from
            // the VK-code matrixId a physical key press resolves to (see MainWindow.Everest.cs's
            // EvTranslateMatrix) and that manually-created keys are already keyed by — without
            // this translation, an imported key's KeyMatrix never matches what a live press looks
            // up, so the action silently never fires (confirmed user report 2026-07-19).
            int keyMatrix = Models.EverestWMatrixMap.Translate(b.DLLMatrixIndex);
            store.SaveKey(new EverestKeyRecord(slot, keyMatrix, null, at, av));
            regular++;
        }

        // ── Touch / numpad display keys ────────────────────────
        string? iconsDir = null;
        for (int i = 0; i < touchKeys.Count && i < 4; i++)
        {
            var b = touchKeys[i];
            string? imagePath = null;
            if (!string.IsNullOrEmpty(b.Base64Image))
            {
                try
                {
                    iconsDir ??= Path.Combine(
                        K2Paths.For("K2.App"), "imported_bc_ev", $"slot{slot}_{profile.Name}");
                    Directory.CreateDirectory(iconsDir);
                    string iconFile = Path.Combine(iconsDir, $"ndk_{i}.png");
                    var imgBytes = DecodeBase64Image(b.Base64Image);
                    if (imgBytes is not null)
                    {
                        File.WriteAllBytes(iconFile, imgBytes);
                        imagePath = iconFile;
                    }
                }
                catch { /* corrupted image — skip */ }
            }

            string prefix = $"ndk.{slot}.{i}";
            if (imagePath is not null)
                store.SetSetting($"{prefix}.imagePath", imagePath);

            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames, b.CustomURL);
            if (at is not null)
            {
                store.SetSetting($"{prefix}.actionType",  at);
                store.SetSetting($"{prefix}.actionValue", av ?? "");
            }
            touch++;
        }

        // ── Lighting + Settings (Game Mode/Core LED/Dial turn-off/clock) ──────
        // Always written under the profile-scoped namespace (never the shared
        // "rgb."/"settings."/"dial." keys, regardless of the live "sync across
        // profiles" toggle) so an import never clobbers whatever the user already
        // has configured device-wide — MainWindow's Load*FromStore methods always
        // check the profile-scoped key first anyway (see EvRgbPrefix/EvSettingsPrefix/
        // EvDialPrefix's fallback chain), so this is effective either way.
        var lighting = ReadKeyboardLightings(dbPath, profile.ProfileId);
        if (lighting.Count > 0)
            ApplyLightingToStore(store.SetSetting, $"rgb.p{slot}.", lighting);

        // Per-key paint state of the Custom effect (126 keycap LEDs) — profile-scoped,
        // same namespace MainWindow.CustomLighting.cs's EvCustomPrefix reads.
        var custom = ReadKeyboardCustomLighting(dbPath, profile.ProfileId, EverestKeycapLedCount);
        if (custom is not null)
        {
            ApplyCustomLightingToStore(store.SetSetting,
                $"custom.p{slot}.keyLedColors", $"custom.p{slot}.keyEffects", custom);
            // A really painted board wins over Base Camp's active effect — see LooksPainted.
            if (LooksPainted(custom, EverestKeycapLedCount))
                store.SetSetting($"rgb.p{slot}.effect", ((int)EverestSdkNative.EffectIndex.Custom).ToString());
        }

        var settings = ReadKeyboardSettings(dbPath, profile.ProfileId);
        if (settings is not null)
        {
            string sp = $"settings.p{slot}.";
            store.SetSetting(sp + "game_mode", settings.GameModeBitmask.ToString());
            store.SetSetting(sp + "indicator_led", settings.IndicatorLed ? "1" : "0");

            string dp = $"dial.p{slot}.";
            store.SetSetting(dp + "turnOffEnable", settings.DialTurnOffEnable ? "1" : "0");
            store.SetSetting(dp + "turnOff", settings.DialTurnOffSeconds.ToString());
            store.SetSetting(dp + "clockType", settings.DialClockType.ToString());
        }

        return (regular, touch);
    }

    // =========================================================
    // MacroPad — EverestKeyBidings table (NOT MakaluKeyBindings!)
    //
    // CORRECTED 2026-07-26 against a REAL BaseCamp.db (previous sessions guessed
    // this from decompiled source only and got it wrong — MakaluKeyBindings is
    // empty for every real install checked, and a real MacroPad profile's 12 keys
    // (KeyId 170-179/220/221 = M1-M12, same scheme DisplayPad/Everest use) live in
    // EverestKeyBidings, the SAME table Everest Max uses, just filtered by the
    // MacroPad's own ProfileId — confirmed by a real BaseCamp.db (ProfileId 13,
    // DeviceType="MacroPad", 12 EverestKeyBidings rows) and a real BC XML export
    // (<EverestKeyBindings><KeyboardBinding> wrapper/item, exact FunctionType
    // vocabulary "OS Commands"/"Run Program"/"Run Macro"/... — the SAME shared
    // TranslateAction vocabulary already used for Everest Max/DisplayPad, not the
    // MakaluKeyBindings-specific one below). IsTouchKey in that table is NOT
    // meaningful for MacroPad rows (every real row has IsTouchKey=1 regardless of
    // content, unlike Everest Max where it distinguishes NDK display keys) — so
    // unlike ReadKeyBindings (Everest Max), every row is imported regardless of it.
    //
    // TranslateMakaluAction/MakaluKeyBindings below are WRONG for the physical
    // MacroPad (12-key macro pad) — kept only because MpProfileExporter's old
    // K2-only export format (pre-2026-07-26) used that shape, so previously
    // exported K2 XML files can still be read back (see BtnMpImportXml_Click's
    // legacy fallback). The real Makalu MOUSE (Makalu 67/Max) genuinely does use
    // MakaluKeyBindings — see TranslateMakaluRemapFunction/ImportMakaluProfile
    // further below, a different code path entirely.
    // =========================================================

    /// <summary>One MacroPad key binding read from the real EverestKeyBidings table.</summary>
    public sealed record BcMacroPadKeyBinding(
        int KeyId, string? FunctionType, string? SubFunctionType, string? FunctionValue, bool IsAssigned,
        string? CustomURL = null); // set alongside FunctionType="Run browser" when the key opens a specific URL

    /// <summary>Reads a MacroPad profile's key bindings from EverestKeyBidings
    /// (shared with Everest Max — see class-level note), keyed by the same
    /// 170-179/220-221 KeyId scheme as DisplayPad's M1-M12, translated via
    /// <see cref="KeyIdToIndex"/>.</summary>
    public static List<BcMacroPadKeyBinding> ReadMacroPadKeyBindings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();

        bool hasCustomUrl = ColumnExistsInDb(conn, "EverestKeyBidings", "CustomURL");
        cmd.CommandText = $@"
            SELECT KeyId, FunctionType, SubFunctionType, FunctionValue, IsKeyAssigned{(hasCustomUrl ? ", CustomURL" : "")}
            FROM EverestKeyBidings
            WHERE ProfileId = $pid
            ORDER BY KeyId";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcMacroPadKeyBinding>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new BcMacroPadKeyBinding(
                KeyId:           r.GetInt32(0),
                FunctionType:    r.IsDBNull(1) ? null : r.GetString(1),
                SubFunctionType: r.IsDBNull(2) ? null : r.GetString(2),
                FunctionValue:   r.IsDBNull(3) ? null : r.GetString(3),
                IsAssigned:      r.GetInt32(4) != 0,
                CustomURL:       hasCustomUrl && !r.IsDBNull(5) ? r.GetString(5) : null));
        return result;
    }

    /// <summary>
    /// Imports a MacroPad profile into <see cref="MacroPadStore"/>: key bindings
    /// (real EverestKeyBidings table, see class-level note) + LED lighting (shared
    /// KeyboardLightings table, see <see cref="ApplyLightingToStore"/>). Returns the
    /// number of keys imported. <paramref name="targetSlot"/> is a fresh slot picked
    /// by the caller via <see cref="FindFreeSlot"/>, not <c>profile.Slot</c>.
    /// </summary>
    public static int ImportMacroPadProfile(
        string dbPath, BcProfile profile, int k2DeviceId, MacroPadStore store, int targetSlot,
        IReadOnlyCollection<string>? macroNames = null)
    {
        int slot = targetSlot;
        int imported = 0;

        foreach (var b in ReadMacroPadKeyBindings(dbPath, profile.ProfileId))
        {
            if (!b.IsAssigned) continue;
            if (!KeyIdToIndex.TryGetValue(b.KeyId, out int idx)) continue;

            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames, b.CustomURL);
            if (at is null) continue;
            store.SaveKey(new MacroKeyRecord(k2DeviceId, slot, idx, at, av));
            imported++;
        }

        var lighting = ReadKeyboardLightings(dbPath, profile.ProfileId);
        if (lighting.Count > 0)
            ApplyLightingToStore(store.SetSetting, $"macroled.p{slot}.", lighting);

        // Per-key paint state of the Custom effect (12 M-keys) — same namespace
        // MainWindow.MpCustomLighting.cs's MacroLedPrefix reads.
        var custom = ReadKeyboardCustomLighting(dbPath, profile.ProfileId, MacroPadKeyCount);
        if (custom is not null)
        {
            ApplyCustomLightingToStore(store.SetSetting,
                $"macroled.p{slot}.custom.keyColors", $"macroled.p{slot}.custom.keyEffects", custom);
            if (LooksPainted(custom, MacroPadKeyCount))
                store.SetSetting($"macroled.p{slot}.effect", ((int)MacroPadSdkNative.EffectIndex.Custom).ToString());
        }

        return imported;
    }

    /// <summary>
    /// Translates Base Camp's FunctionType/FunctionValue (MakaluKeyBindings schema,
    /// WITHOUT SubFunctionType) into K2.Core action types. Hardware-native functions
    /// with no K2 equivalent (DPI, brightness/effect cycle, battery check, named
    /// macros) become <c>("none", "[placeholder] value")</c> — no crash,
    /// but no execution: preserved only so the information isn't lost.
    /// </summary>
    public static (string? ActionType, string? ActionValue) TranslateMakaluAction(
        string? functionType, string? functionValue,
        IReadOnlyCollection<string>? macroNames = null)
    {
        var ft = (functionType ?? "").Trim();
        var fv = (functionValue ?? "").Trim();
        if (string.IsNullOrEmpty(ft)) return (null, null);

        switch (ft)
        {
            case "Run Program":
                return string.IsNullOrEmpty(fv) ? (null, null) : ImportExecOrBrowserAction(fv);

            case "Keyboard Shortcuts":
                return string.IsNullOrEmpty(fv) ? (null, null) : ("keys", fv);

            case "Media":
                // Two entries of BC's "Media" category aren't media keys at all, so they
                // keep their own arms; everything else goes through the shared normalizer
                // (which emits K2's canonical vocabulary — this used to emit snake_case
                // tokens the executor and the picker both rejected).
                return fv switch
                {
                    "Run browser"     => ImportBrowserAction(null),
                    "Calculator"      => ("oscmd", "Calculator"),
                    "" or null        => (null, null),
                    _ => ("media", ActionTypeHelper.NormalizeMediaKey(fv))
                };

            case "Mouse":
                return fv switch
                {
                    "Left button"     => ("mouse", "left button"),
                    "Right button"    => ("mouse", "right button"),
                    "Middle button"   => ("mouse", "middle button"),
                    "Backward"        => ("mouse", "backward"),
                    "Forward"         => ("mouse", "forward"),
                    "Next Profile"    => ("profile", "next"),
                    "Previous Profile"=> ("profile", "prev"),
                    _ => ("none", $"[mouse] {fv}") // DPI Sniper/+/-, battery/brightness/effect: no K2 equivalent
                };

            case "Mouse Wheel":
                return fv switch
                {
                    "Scroll Up"   => ("mouse", "scroll up"),
                    "Scroll Down" => ("mouse", "scroll down"),
                    _ => (null, null)
                };

            case "OS Commands":
                return fv switch
                {
                    "Run task manager" => ("oscmd", "Task Manager"),
                    "Run browser"      => ImportBrowserAction(null),
                    "Lock computer"    => ("oscmd", "Lock"),
                    "Shut down"        => ("oscmd", "Shutdown"),
                    "Sleep"            => ("oscmd", "Sleep"),
                    "Hibernate"        => ("oscmd", "Hibernate"),
                    "Calculator"       => ("oscmd", "Calculator"),
                    _ => (null, null)
                };

            case "Run Macro":
                // Named-macro reference, same as the shared TranslateAction's "Run Macro"
                // arm: K2's macro engine plays these via ButtonActionEngine's "macro"
                // action, so resolve the name against the user's K2 macro library instead
                // of the old inert "[macro]" placeholder (written when K2 had no macro
                // engine yet). Unmatched names stay as a valueless "macro" action, flagged
                // by ActionTypeHelper.IsMacroMissingTarget.
                return string.IsNullOrEmpty(fv)
                    ? (null, null)
                    : TranslateDefaultAction(null, fv, macroNames);

            case "Battery level check":
            case "Brightness cycle":
            case "Effect cycle":
            case "DPI Cyclic Increase":
            case "DPI Cyclic Decrease":
                // Hardware-native functions from the "Mouse" category of the shared
                // Mountain firmware, with no MacroPad equivalent in K2.
                return ("none", $"[{ft.ToLowerInvariant()}]");

            case "Disable":
                // Explicitly disabled key — a real binding, not an empty one (same
                // reasoning as TranslateAction's own "Disable" arm).
                return ("disable", null);

            case "Default":
                return (null, null);

            default:
                return ($"bc:{ft}", fv);
        }
    }

    // =========================================================
    // Makalu mouse — MakaluKeyBindings / MakaluLightings / MakaluSettings /
    // DPILevels tables. DeviceType is "Makalu67"/"MakaluMax" for the Profiles row
    // (CONFIRMED 2026-07-29 against a real BaseCamp.db with a paired Makalu 67 — the
    // previous guess here, bare "Makalu", never matched anything and was the root
    // cause of a user report that BC import silently found zero profiles; "MakaluMax"
    // itself is still an unverified extrapolation, no Max unit seen yet). That same
    // real profile also confirmed the effect-name vocabulary below ("Color Wave"/
    // "Reactive" replace the previously-guessed "Rainbow"/"Responsive"/"RGB Breathing",
    // and "Next profile"/"Previous profile" use lowercase "profile") and that Lighting/
    // Settings/DPI/KeyBindings field shapes are correct (real rows read back cleanly:
    // 8 key bindings, 7 lighting rows, 1 settings row, DPI levels). The button
    // FunctionType/FunctionValue vocabulary in TranslateMakaluRemapFunction below is
    // otherwise still inferred by analogy with the confirmed MacroPad translator
    // (TranslateMakaluAction, same decompiled family) — falls back to skipping (not
    // guessing) anything that doesn't match exactly.
    // =========================================================

    /// <summary>Reads Makalu mouse profiles grouped by DeviceId. CONFIRMED 2026-07-29
    /// against a real BaseCamp.db with a paired mouse (previous sessions never had one —
    /// see the class doc comment above): DeviceType is "Makalu67" for the 67-key model,
    /// not the bare "Makalu" guessed before — that mismatch made every real Makalu
    /// import silently find zero profiles (user report: "l'importazione non funziona").
    /// "MakaluMax" is the model-name-mirroring guess for the Max variant (same naming
    /// convention as MakaluService.Model/DeviceInfo's "Makalu 67"/"Makalu Max"), still
    /// unverified — no Max unit paired in any session so far.</summary>
    public static Dictionary<int, List<BcProfile>> ReadMakaluProfiles(string dbPath)
        => ReadProfilesByTypes(dbPath, "Makalu67", "MakaluMax");

    public sealed record BcMakaluMouseBinding(
        int ButtonIndex, string? FunctionType, string? FunctionValue, string? FunctionEnteredValue, bool IsAssigned);

    /// <summary>Reads MakaluKeyBindings for a profile. KeyId is already the
    /// 1-based physical button index MakaluService/MakaluRemapData use directly
    /// (no translation table, unlike MacroPad's 170-179/220-221 KeyIds).</summary>
    public static List<BcMakaluMouseBinding> ReadMakaluMouseKeyBindings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT KeyId, FunctionType, FunctionValue, FunctionEnteredValue, IsKeyAssigned
            FROM MakaluKeyBindings WHERE ProfileId = $pid ORDER BY KeyId";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcMakaluMouseBinding>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new BcMakaluMouseBinding(
                ButtonIndex:          r.GetInt32(0),
                FunctionType:         r.IsDBNull(1) ? null : r.GetString(1),
                FunctionValue:        r.IsDBNull(2) ? null : r.GetString(2),
                FunctionEnteredValue: r.IsDBNull(3) ? null : r.GetString(3),
                IsAssigned:           r.GetInt32(4) != 0));
        return result;
    }

    /// <summary>Translates a Makalu button's BC function into one of
    /// MakaluRemapData's function-key strings ("left"/"dpi+"/"sniper:800"/...).
    /// Unlike every other TranslateXxx here, the target is NOT a K2.Core
    /// action pair — Makalu buttons write straight to firmware
    /// (MakaluService.SetButtonRemap), there is no IActionHost for this
    /// device (see architectural note in _PROJECT_MAP.md). Returns null for
    /// anything not an exact, confirmed match (factory-only functions with
    /// no K2 remap equivalent, e.g. battery/brightness/effect cycle) rather
    /// than guessing.</summary>
    public static string? TranslateMakaluRemapFunction(string? functionType, string? functionValue, string? enteredValue)
    {
        var ft = (functionType ?? "").Trim();
        var fv = (functionValue ?? "").Trim();
        switch (ft)
        {
            case "Mouse":
                return fv switch
                {
                    "Left button"   => "left",
                    "Right button"  => "right",
                    "Middle button" => "middle",
                    "Backward"      => "back",
                    "Forward"       => "forward",
                    "DPI +"         => "dpi+",
                    "DPI -"         => "dpi-",
                    "DPI Sniper"    => int.TryParse(enteredValue, out int dpi) ? $"sniper:{dpi}" : "sniper:800",
                    // Confirmed 2026-07-28 via a real USBPcap capture (see MakaluProtocol.
                    // RemapFunctions' doc comment) — category 0x08, same F1/F3 code pair as
                    // dpi+/dpi- (category 0x09). Casing (lowercase "profile") CONFIRMED
                    // 2026-07-29 against a real BaseCamp.db row — differs from the
                    // "Next Profile"/"Previous Profile" capitalization DisplayPad/MacroPad's
                    // own "Profile" FunctionType category uses (unrelated table, left as-is).
                    "Next profile"     => "profile_next",
                    "Previous profile" => "profile_prev",
                    // Confirmed 2026-07-28, same capture — order verbally confirmed by the
                    // user (Brightness selected before Effect) to disambiguate the two
                    // categories seen on the wire (0x21/0x22, both code 0x01).
                    "Brightness cycle" => "brightness_cycle",
                    "Effect cycle"     => "effect_cycle",
                    _ => null, // battery level check: no Makalu remap equivalent yet
                };
            case "Mouse Wheel":
                return fv switch
                {
                    "Scroll Up"   => "scroll_up",
                    "Scroll Down" => "scroll_down",
                    _ => null,
                };
            case "Disable":
            case "Default":
                return "disabled";
            default:
                return null;
        }
    }

    /// <summary>Parses a Base Camp color string — real data shows BOTH
    /// "#RRGGBB" and "rgb(r, g, b)" forms in the same table (the app rewrites
    /// a slot to "rgb(...)" once the user touches its color picker, otherwise
    /// it keeps the C# constructor's "#hex" default) — into a packed 0xRRGGBB
    /// int.</summary>
    internal static int ParseBcColor(string? raw, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        raw = raw.Trim();
        try
        {
            if (raw.StartsWith('#'))
                return Convert.ToInt32(raw[1..], 16) & 0xFFFFFF;
            if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                int open = raw.IndexOf('(');
                int close = raw.IndexOf(')');
                if (open < 0 || close < 0) return fallback;
                var parts = raw[(open + 1)..close].Split(',');
                if (parts.Length < 3) return fallback;
                int r = int.Parse(parts[0].Trim());
                int g = int.Parse(parts[1].Trim());
                int b = int.Parse(parts[2].Trim());
                return ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
            }
        }
        catch { /* malformed color string */ }
        return fallback;
    }

    /// <summary>Parses the Makalu mouse's <c>CustomMakaluLightings</c> payload — the
    /// same <c>[{"KeyCode":..,"ColorHex":".."}]</c> shape Everest 60 uses, over the
    /// mouse's 8 LEDs. Shared with the XML import path, which used to drop the colors
    /// entirely (it passed a fresh <c>int[8]</c>).</summary>
    internal static int[] ParseMakaluCustomColors(string? json)
    {
        var colors = new int[8];
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "undefined" or "null") return colors;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return colors;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("KeyCode", out var kc) || !el.TryGetProperty("ColorHex", out var ch)) continue;
                int idx = kc.GetInt32();
                if (idx < 0 || idx >= colors.Length) continue;
                colors[idx] = ParseBcColor(ch.GetString());
            }
        }
        catch { /* malformed JSON — leave at default black */ }
        return colors;
    }

    /// <summary>Real Base Camp effect names (confirmed 2026-07-29 against a real
    /// profile + wwwroot/js/makalu.js/Makalu67.js, which enumerate exactly these 7
    /// names) are "Off"/"Custom"/"Static"/"Breathing"/"Color Wave"/"Reactive"/
    /// "YETI MODE" — NOT "Rainbow"/"Responsive"/"RGB Breathing" as previously
    /// guessed. Shared by the DB (<see cref="ReadMakaluMouseLighting"/>) and XML
    /// (MainWindow.Makalu.cs's BtnMkImportXml_Click) import paths so they can't
    /// silently drift apart again. <paramref name="isRainbowColorMode"/> is BC's
    /// ColorType==RAINBOW (confirmed via decompiled BaseCamp.Data.EffectColorType:
    /// SINGLE=0/DUAL=1/RAINBOW=2) — the same concept K2's own Breathing+Rainbow-radio
    /// already resolves to RgbBreathing (see MakaluRgbSettingsPanel.
    /// ResolveMkWireEffect's doc comment).</summary>
    internal static MakaluProtocol.Effect TranslateMakaluEffectName(string? effectName, bool isRainbowColorMode) =>
        (effectName ?? "").Trim().ToLowerInvariant() switch
        {
            "static"              => MakaluProtocol.Effect.Static,
            "breathing"           => isRainbowColorMode ? MakaluProtocol.Effect.RgbBreathing : MakaluProtocol.Effect.Breathing,
            "color wave"          => isRainbowColorMode ? MakaluProtocol.Effect.Rainbow : MakaluProtocol.Effect.Breathing,
            "reactive"            => MakaluProtocol.Effect.Responsive,
            "yeti" or "yeti mode" => MakaluProtocol.Effect.Yeti,
            "off"                 => MakaluProtocol.Effect.Off,
            "custom"              => MakaluProtocol.Effect.Custom,
            _                     => MakaluProtocol.Effect.Static,
        };

    /// <summary>Normalizer for MakaluSettings.PollingRate. CONFIRMED 2026-07-29 by the
    /// user directly (not decompile/capture this time): the DB stores a 1-based index
    /// into Base Camp's own UI ordering, slowest to fastest — 1=125Hz/2=250Hz/3=500Hz/
    /// 4=1000Hz. This supersedes an earlier same-day guess in this codebase (the wire
    /// CODE MakaluProtocol.SetPollingRate sends over HID, 1=1000Hz/2=500Hz/4=250Hz/
    /// 8=125Hz) which turned out to be wrong — the two tables share small integers but
    /// mean different things; don't conflate them again. Literal Hz values pass through
    /// unchanged too (covers K2's own XML exports, which always wrote real Hz).</summary>
    internal static int NormalizeMakaluPollingHz(int raw) => raw switch
    {
        125 or 250 or 500 or 1000 => raw,
        1 => 125,
        2 => 250,
        3 => 500,
        4 => 1000,
        _ => 1000,
    };

    /// <summary>Reads the currently-selected (IsEffectSelected=1) MakaluLightings
    /// row for a profile and translates it into a MakaluLightingRecord.
    /// EffectName is matched by name against MakaluProtocol.Effect (EffectId's
    /// own ordering was never cross-checked against a real profile) — falls
    /// back to Static if nothing matches or no row exists.</summary>
    public static MakaluLightingRecord? ReadMakaluMouseLighting(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EffectName, ColorType, SingleColor, DualColor1, DualColor2, Speed, Brightness,
                   Direction, IsEffectSelected, CustomMakaluLightings
            FROM MakaluLightings WHERE ProfileId = $pid ORDER BY IsEffectSelected DESC";
        cmd.Parameters.AddWithValue("$pid", profileId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        string effectName = r.IsDBNull(0) ? "Static" : r.GetString(0);
        // ColorType==2 is the "RAINBOW" color-mode radio confirmed against a real
        // profile (wwwroot/js/makalu.js's SelectedType=='RAINBOW' branch) — same concept
        // K2's own Breathing+RbMkColorRainbow already resolves to RgbBreathing (see
        // MakaluRgbSettingsPanel.ResolveMkWireEffect's doc comment).
        bool isRainbowColorMode = !r.IsDBNull(1) && r.GetInt32(1) == 2;
        // Dual-color effects use DualColor1/2; single-color effects only ever
        // populate SingleColor (DualColor1/2 stay at their C# ctor defaults) —
        // prefer DualColor1 when it differs from the default so a genuinely
        // dual-color slot isn't flattened to SingleColor's value.
        string? singleColor = r.IsDBNull(2) ? null : r.GetString(2);
        string? dualColor1  = r.IsDBNull(3) ? null : r.GetString(3);
        string? dualColor2  = r.IsDBNull(4) ? null : r.GetString(4);
        int color1 = ParseBcColor(dualColor1 ?? singleColor, 0x900000);
        int color2 = ParseBcColor(dualColor2, 0x000000);
        int speed = r.IsDBNull(5) ? 1 : Math.Clamp(r.GetInt32(5), 0, 2);
        int brightness = r.IsDBNull(6) ? 100 : r.GetInt32(6);
        int direction = r.IsDBNull(7) ? 1 : r.GetInt32(7);
        string? customJson = r.IsDBNull(9) ? null : r.GetString(9);

        var eff = TranslateMakaluEffectName(effectName, isRainbowColorMode);
        bool customActive = effectName.Trim().Equals("Custom", StringComparison.OrdinalIgnoreCase);

        // Parsed whatever the active effect is (it used to be gated on customActive):
        // the paint belongs to the profile, same call already made for Everest 60.
        var customColors = ParseMakaluCustomColors(customJson);

        return new MakaluLightingRecord(
            (int)eff, color1, color2, speed, direction, brightness, customActive, customColors);
    }

    /// <summary>Reads MakaluSettings + DPILevels for a profile. No dedicated
    /// "debounce ms" column exists in Base Camp's schema — ButtonResponseTime
    /// is the closest analog (both are a small-int firmware debounce-style
    /// setting); a real profile has ButtonResponseTime=2 (a valid
    /// MakaluProtocol.DebounceValuesMs entry) but a SIBLING real profile has
    /// ButtonResponseTime=15, which ISN'T in that array (max 12) — so even this
    /// "confirmed" literal-ms reading may not cover Base Camp's full real range;
    /// not investigated further, flagged here for whoever touches this next.
    /// PollingRate's encoding is now CONFIRMED (by the user directly, 2026-07-29) —
    /// see <see cref="NormalizeMakaluPollingHz"/>'s doc comment for the exact mapping
    /// and for the earlier same-day guess it corrects.</summary>
    public static (MakaluDeviceSettingsRecord? Settings, MakaluDpiRecord? Dpi) ReadMakaluMouseSettings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        int pollingHz = 1000, debounceMs = 2, selectedDpiId = 1, sensitivity = 10, clickSpeed = 0;
        bool angleOn = false, liftHigh = false, liftCustom = false;
        bool foundSettings = false;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT PollingRate, ButtonResponseTime, AngleSnapping, LiftOffDistance, SelectedDPILevelId,
                       Sensitivity, ClickSpeed
                FROM MakaluSettings WHERE ProfileId = $pid";
            cmd.Parameters.AddWithValue("$pid", profileId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                foundSettings = true;
                pollingHz    = NormalizeMakaluPollingHz(r.IsDBNull(0) ? 1000 : r.GetInt32(0));
                debounceMs   = r.IsDBNull(1) ? 2 : r.GetInt32(1);
                angleOn      = !r.IsDBNull(2) && r.GetString(2).Equals("On", StringComparison.OrdinalIgnoreCase);
                // "Custom" is the 3rd lift-off surface-calibration option (see
                // MakaluDeviceSettingsRecord.LiftOffCustom's doc comment) — anything
                // other than "High"/"Custom" (i.e. "Low") leaves both flags false.
                string liftOff = r.IsDBNull(3) ? "" : r.GetString(3);
                liftHigh   = liftOff.Equals("High", StringComparison.OrdinalIgnoreCase);
                liftCustom = liftOff.Equals("Custom", StringComparison.OrdinalIgnoreCase);
                selectedDpiId = r.IsDBNull(4) ? 1 : r.GetInt32(4);
                // Sensitivity/ClickSpeed (2026-07-29): OS-level mouse settings, not
                // firmware — see MakaluOsMouseSettings' doc comment. Real DB rows use a
                // 0-11 scale (same as BaseCamp.Data.MakaluSetting's own ctor defaults).
                sensitivity = r.IsDBNull(5) ? 10 : Math.Clamp(r.GetInt32(5), MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
                clickSpeed  = r.IsDBNull(6) ? 0  : Math.Clamp(r.GetInt32(6), MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
            }
        }

        var levels = new List<(int Id, int Dpi)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DPILevelId, DPI FROM DPILevels WHERE ProfileId = $pid ORDER BY DPILevelId";
            cmd.Parameters.AddWithValue("$pid", profileId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) levels.Add((r.GetInt32(0), r.GetInt32(1)));
        }

        MakaluDeviceSettingsRecord? settings = foundSettings
            ? new MakaluDeviceSettingsRecord(pollingHz, debounceMs, angleOn, liftHigh, liftCustom,
                Sensitivity: sensitivity, ClickSpeed: clickSpeed)
            : null;

        MakaluDpiRecord? dpi = null;
        if (levels.Count > 0)
        {
            // Exactly as many levels as Base Camp actually defined (1-5, firmware's real
            // range — see MakaluProtocol.DpiLevelCountMax) — NOT always padded to 5. K2's
            // own DPI level UI already keys entirely off this array's LENGTH (see
            // MakaluRgbSettingsPanel._mkDpiCount's doc comment), so a shorter array here
            // is all that's needed for "found 1 level -> show 1 level, not 5" (user
            // report 2026-07-29).
            int count = Math.Clamp(levels.Count, MakaluProtocol.DpiLevelCountMin, MakaluProtocol.DpiLevelCountMax);
            var dpiValues = new int[count];
            for (int i = 0; i < count; i++) dpiValues[i] = levels[i].Dpi;
            int activeIdx = Math.Max(0, levels.FindIndex(l => l.Id == selectedDpiId));
            activeIdx = Math.Min(activeIdx, count - 1);
            dpi = new MakaluDpiRecord(dpiValues, activeIdx);
        }

        return (settings, dpi);
    }

    /// <summary>Imports a Makalu mouse profile: lighting + DPI + settings +
    /// button remap into MakaluStore. Returns (remapped button count, lighting
    /// imported, settings imported). <paramref name="targetSlot"/> is a fresh slot
    /// picked by the caller via <see cref="FindFreeSlot"/>, not <c>profile.Slot</c>.</summary>
    public static (int Remap, bool Lighting, bool Settings) ImportMakaluProfile(
        string dbPath, BcProfile profile, MakaluStore store, int targetSlot)
    {
        int slot = targetSlot;
        store.ClearProfile(slot);
        store.SetProfileName(slot, profile.Name);

        var lighting = ReadMakaluMouseLighting(dbPath, profile.ProfileId);
        if (lighting is not null) store.SaveLighting(slot, lighting);

        var (settings, dpi) = ReadMakaluMouseSettings(dbPath, profile.ProfileId);
        if (settings is not null) store.SaveSettings(slot, settings);
        if (dpi is not null) store.SaveDpi(slot, dpi);

        int remapped = 0;
        foreach (var b in ReadMakaluMouseKeyBindings(dbPath, profile.ProfileId))
        {
            if (!b.IsAssigned) continue;
            string? fn = TranslateMakaluRemapFunction(b.FunctionType, b.FunctionValue, b.FunctionEnteredValue);
            if (fn is null) continue;
            store.SaveRemapButton(slot, b.ButtonIndex, fn);
            remapped++;
        }

        return (remapped, lighting is not null, settings is not null);
    }

    // =========================================================
    // Everest 60 — Everest60KeyBidings / Everest60Lightings tables.
    // DeviceType="EverestMini" for the Profiles row — CONFIRMED directly
    // against a real BaseCamp.db (this install has one real EverestMini
    // profile, 232 key-binding rows + 9 lighting rows, one per effect slot,
    // see _PROJECT_MAP.md). Lighting import is high-confidence (verified
    // field shapes/color formats against that real data). Key Binding import
    // (2026-07-14, second pass) now goes through the same
    // <see cref="TranslateAction"/> FunctionType/SubFunctionType/FunctionValue
    // vocabulary as Everest Max/DisplayPad/MacroPad, since Everest 60 Key
    // Binding is no longer a raw firmware remap in K2 — it's a K2Action like
    // every other device (see Everest60Store/Everest60KeyBindingPanel).
    // Every IsKeyAssigned=1 row in the one real profile available so far is
    // a LayerType=3 factory Fn-legend ("FN + 10"), not a real user remap —
    // LayerType!=1 rows are skipped for that reason, same as before.
    // =========================================================

    /// <summary>Reads Everest 60 profiles (DeviceType="EverestMini") grouped by DeviceId.</summary>
    public static Dictionary<int, List<BcProfile>> ReadEverest60Profiles(string dbPath)
        => ReadProfilesByType(dbPath, "EverestMini");

    public sealed record BcEverest60KeyBinding(
        int DllKeyId, int LayerType, string? FunctionType, string? SubFunctionType,
        string? FunctionValue, string? FunctionEnteredValue, bool IsAssigned,
        string? CustomURL = null); // set alongside FunctionType="Run browser" when the key opens a specific URL

    public static List<BcEverest60KeyBinding> ReadEverest60KeyBindingsRaw(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();

        bool hasCustomUrl = ColumnExistsInDb(conn, "Everest60KeyBidings", "CustomURL");
        cmd.CommandText = $@"
            SELECT DLLKeyId, LayerType, FunctionType, SubFunctionType, FunctionValue,
                   FunctionEnteredValue, IsKeyAssigned{(hasCustomUrl ? ", CustomURL" : "")}
            FROM Everest60KeyBidings WHERE ProfileId = $pid ORDER BY DLLKeyId";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcEverest60KeyBinding>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new BcEverest60KeyBinding(
                DllKeyId:             r.GetInt32(0),
                LayerType:            r.GetInt32(1),
                FunctionType:         r.IsDBNull(2) ? null : r.GetString(2),
                SubFunctionType:      r.IsDBNull(3) ? null : r.GetString(3),
                FunctionValue:        r.IsDBNull(4) ? null : r.GetString(4),
                FunctionEnteredValue: r.IsDBNull(5) ? null : r.GetString(5),
                IsAssigned:           r.GetInt32(6) != 0,
                CustomURL:            hasCustomUrl && !r.IsDBNull(7) ? r.GetString(7) : null));
        return result;
    }

    /// <summary>Reads the active (IsActive=1) Everest60Lightings row and
    /// translates it into an Ev60LightingRecord. EffIndex 1..9 maps to
    /// Base Camp's own EV60EffectIndex enum (Static/ColorWave/Tornado/
    /// Breathing/Reactive/Matrix/Custom/Yeti/Off) — Matrix has no
    /// Everest60Protocol.Effect equivalent (falls back to Static);
    /// Custom sets ActiveMode="custom" instead of a regular effect.
    /// Color3 (Base Camp's 3rd tracked color, unverified which role it plays)
    /// is no longer read — K2's own "side ring uniform color" concept it used
    /// to feed was removed 2026-07-24, superseded by per-LED border painting.</summary>
    public static Ev60LightingRecord? ReadEverest60LightingRaw(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);

        // The per-key Custom paint lives on the Custom row (EffIndex 7) and survives
        // there whatever effect is currently active — reading it from the active row
        // (as this method used to, via a single ORDER BY IsActive DESC query) silently
        // lost the whole painted board whenever the profile was on a preset effect.
        // Confirmed 2026-07-26 against a real BaseCamp.db + two exports of the same
        // profile: identical 192-entry payload with Custom active and inactive.
        string? customJson;
        using (var customCmd = conn.CreateCommand())
        {
            customCmd.CommandText =
                "SELECT CustomLightings FROM Everest60Lightings WHERE ProfileId = $pid AND EffIndex = 7";
            customCmd.Parameters.AddWithValue("$pid", profileId);
            using var cr = customCmd.ExecuteReader();
            customJson = cr.Read() && !cr.IsDBNull(0) ? cr.GetString(0) : null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EffIndex, Speed, Brightness, Direction, Color1, Color2
            FROM Everest60Lightings WHERE ProfileId = $pid ORDER BY IsActive DESC";
        cmd.Parameters.AddWithValue("$pid", profileId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        int effIndex = r.GetInt32(0);
        int speedPct = r.IsDBNull(1) ? 50 : r.GetInt32(1);
        double brightness = r.IsDBNull(2) ? 100 : r.GetInt32(2);
        int rawDirection = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        int color1 = ParseBcColor(r.IsDBNull(4) ? null : r.GetString(4), 0x900000);
        int color2 = ParseBcColor(r.IsDBNull(5) ? null : r.GetString(5), 0x000000);

        var eff = effIndex switch
        {
            1 => Everest60Protocol.Effect.Static,
            2 => Everest60Protocol.Effect.Wave,
            3 => Everest60Protocol.Effect.Tornado,
            4 => Everest60Protocol.Effect.Breathing,
            5 => Everest60Protocol.Effect.Reactive,
            // Custom is a real entry of BOTH Everest60Protocol.Effect and the panel's
            // Ev60EffectList — mapping it to Static (as the default arm used to, on the
            // assumption ActiveMode alone would carry it) left the effect dropdown showing
            // "Static" on every imported Custom profile, even though the painted board was
            // correctly applied to the hardware. User report 2026-07-26.
            7 => Everest60Protocol.Effect.Custom,
            8 => Everest60Protocol.Effect.Yeti,
            9 => Everest60Protocol.Effect.Off,
            _ => Everest60Protocol.Effect.Static, // 6=Matrix: no Everest 60 equivalent
        };
        string activeMode = effIndex == 7 ? "custom" : "preset";

        int dirIndex = Everest60DirIndexFor(eff, rawDirection);

        // Always parsed, not only when Custom is the active mode — the paint belongs to
        // the profile, not to whichever effect happens to be selected.
        // NB: unlike Everest Max/MacroPad, the presence of paint here says NOTHING about
        // whether Custom is in use — see LooksPainted's doc comment. activeMode stays
        // whatever IsActive said; the board is imported regardless so switching to Custom
        // in K2 shows it.
        var custom = ParseEverest60Custom(customJson);

        return new Ev60LightingRecord(
            (int)eff, color1, color2, speedPct, dirIndex, Rainbow: false,
            brightness, CustomBrightness: brightness, activeMode, custom.KeyColors,
            ColorDouble: false, custom.SideColors, custom.NumpadRingColors);
    }

    /// <summary>Reads the Everest60Settings row for a profile — a dedicated table
    /// (confirmed against a real BaseCamp.db), NOT the shared KeyboardSettings table
    /// Everest Max/MacroPad use, but the same DisableShift/AltF4/Win/AltTab/
    /// EnableCoreLED columns (no Display Dial fields at all — Everest 60 has no
    /// dial). Same Game Mode bit layout as <see cref="ReadKeyboardSettings"/>.</summary>
    public static (int GameModeBitmask, bool IndicatorLed)? ReadEverest60Settings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DisableShift, DisableAltF4, DisableWin, DisableAltTab, EnableCoreLED
            FROM Everest60Settings WHERE ProfileId = $pid";
        cmd.Parameters.AddWithValue("$pid", profileId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        int mode = (!r.IsDBNull(0) && r.GetInt32(0) != 0 ? 0x1 : 0)
                 | (!r.IsDBNull(1) && r.GetInt32(1) != 0 ? 0x2 : 0)
                 | (!r.IsDBNull(2) && r.GetInt32(2) != 0 ? 0x4 : 0)
                 | (!r.IsDBNull(3) && r.GetInt32(3) != 0 ? 0x8 : 0);
        bool led = !r.IsDBNull(4) && r.GetInt32(4) != 0;
        return (mode, led);
    }

    /// <summary>Imports an Everest 60 profile: lighting (high confidence) +
    /// key bindings (via the shared <see cref="TranslateAction"/> vocabulary,
    /// see class-level doc comment) + Game Mode/Core LED into Everest60Store.
    /// Returns the number of key bindings imported. <paramref name="targetSlot"/>
    /// is a fresh slot picked by the caller via <see cref="FindFreeSlot"/>, not
    /// <c>profile.Slot</c>.</summary>
    public static int ImportEverest60Profile(string dbPath, BcProfile profile, Everest60Store store, int targetSlot,
        IReadOnlyCollection<string>? macroNames = null)
    {
        int slot = targetSlot;
        store.ClearProfile(slot);
        store.SetProfileName(slot, profile.Name);

        var lighting = ReadEverest60LightingRaw(dbPath, profile.ProfileId);
        if (lighting is not null) store.SaveLighting(slot, lighting);

        var settings = ReadEverest60Settings(dbPath, profile.ProfileId);
        if (settings is not null)
        {
            string sp = $"settings.p{slot}.";
            store.SetSetting(sp + "game_mode", settings.Value.GameModeBitmask.ToString());
            store.SetSetting(sp + "indicator_led", settings.Value.IndicatorLed ? "1" : "0");
        }

        int imported = 0;
        foreach (var b in ReadEverest60KeyBindingsRaw(dbPath, profile.ProfileId))
        {
            // Only the base layer is imported — see class-level doc comment
            // for why LayerType=3 (Fn) factory legends are never real remaps.
            if (b.LayerType != 1 || !b.IsAssigned) continue;

            // Main board OR accessory numpad (LED index 1000+n) — see the helper's
            // doc comment; the numpad arm was missing here, so every numpad binding
            // was silently dropped on import.
            int ledIndex = Everest60LedIndexFromDllKeyId(b.DllKeyId);
            if (ledIndex < 0) continue;

            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames, b.CustomURL);
            if (at is null) continue;

            store.SaveKey(new Ev60KeyRecord(slot, ledIndex, null, at, av));
            imported++;
        }

        return imported;
    }

    // =========================================================

    /// <summary>
    /// Decodes a base64-encoded image string, stripping any data URI prefix
    /// (e.g. <c>data:image/png;base64,</c>) that Base Camp stores in the DB.
    /// Returns null when <paramref name="raw"/> is a BC internal resource path
    /// (e.g. <c>/images/DKD/DPBack.png</c>) rather than actual base64 data.
    /// </summary>
    internal static byte[]? DecodeBase64Image(string raw)
    {
        // BC sometimes stores a filesystem path instead of embedded base64 data
        // (e.g. a custom icon that was picked but never re-encoded into the
        // export). Load it straight from disk if it still exists there.
        var trimmed = raw.Trim();
        try
        {
            if (File.Exists(trimmed))
                return File.ReadAllBytes(trimmed);
        }
        catch { /* not a usable path — fall through and try base64 */ }

        // BC's internal asset paths (e.g. "/Icons/foo.png") and remote URLs
        // aren't resolvable outside Base Camp and won't exist on disk — skip them.
        if (trimmed.StartsWith('/') || trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        int comma = raw.IndexOf(',');
        string b64 = comma >= 0 ? raw[(comma + 1)..] : raw;

        // XDocument may preserve whitespace (newlines, spaces) inside long text nodes.
        // Convert.FromBase64String does NOT tolerate whitespace, so strip it first.
        if (b64.IndexOfAny(['\r', '\n', ' ', '\t']) >= 0)
            b64 = b64.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");

        return Convert.FromBase64String(b64);
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        // Open read-only to not interfere with Base Camp
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }
}
