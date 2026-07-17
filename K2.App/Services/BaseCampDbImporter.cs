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
        string? OptionalText = null); // JSON with {"Id":<pageId>,...} for "Create Folder" keys

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

        string extra = (hasParentId ? ", ParentId" : "") + (hasOptionalText ? ", OptionalText" : "");
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
            int col = 6;
            if (hasParentId)    { parentId = r.IsDBNull(col) ? 0 : r.GetInt32(col);   col++; }
            if (hasOptionalText){ optText   = r.IsDBNull(col) ? null : r.GetString(col); }

            result.Add(new BcButton(
                ButtonIndex:     idx,
                FunctionType:    r.IsDBNull(1) ? null : r.GetString(1),
                SubFunctionType: r.IsDBNull(2) ? null : r.GetString(2),
                FunctionValue:   r.IsDBNull(3) ? null : r.GetString(3),
                Base64Image:     r.IsDBNull(4) ? null : r.GetString(4),
                IsAssigned:      r.GetInt32(5) != 0,
                ParentId:        parentId,
                OptionalText:    optText));
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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.DisplayPad", "imported_bc", $"dev{k2DeviceId}_slot{slot}_{profile.Name}");
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
                    btn.FunctionType, btn.SubFunctionType, btn.FunctionValue, macroNames);
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
    /// Translates Base Camp's FunctionType/SubFunctionType/FunctionValue
    /// into K2.Core action types (ActionType/ActionValue).
    /// </summary>
    public static (string? ActionType, string? ActionValue) TranslateAction(
        string? funcType, string? subType, string? funcValue,
        IReadOnlyCollection<string>? macroNames = null)
    {
        if (string.IsNullOrEmpty(funcType)) return (null, null);

        return funcType switch
        {
            "Run Program" =>
                ImportExecOrBrowserAction(subType ?? funcValue),   // subType = path to exe

            "Open Folder" =>
                ("folder", subType),              // opens OS folder in Explorer

            // DisplayPad sub-page navigation (page ID in ActionValue when known)
            "Create Folder" =>
                ("dp_folder", null),              // caller should use ParseFolderPageId for the page ID
            "Back" =>
                ("dp_back", null),

            "Run browser" =>
                ImportBrowserAction(funcValue is null or "" or "Run browser" ? null : funcValue),

            "Profile" => subType switch
            {
                "Next Profile"     => ("profile", "next"),
                "Previous Profile" => ("profile", "prev"),
                _ when int.TryParse(subType, out var n) => ("profile", n.ToString()),
                _ => (null, null)
            },

            "Key Shortcut" or "Shortcut Key" or "Keyboard Shortcuts" =>
                ("keys", funcValue),

            "Multi Key" =>
                ("keys", funcValue),

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

            "Media" => subType switch
            {
                "Volume Up"    => ("media", "volume_up"),
                "Volume Down"  => ("media", "volume_down"),
                "Mute"         => ("media", "mute"),
                "Play/Pause"   => ("media", "play_pause"),
                "Next Track"   => ("media", "next_track"),
                "Prev Track"   => ("media", "prev_track"),
                "Stop"         => ("media", "stop"),
                _ => ("media", subType?.ToLowerInvariant()?.Replace(" ", "_"))
            },

            "Text" =>
                ("text", funcValue),

            "Mouse Button" =>
                ("mouse", subType?.ToLowerInvariant()),

            "Disable" or "Disabled" =>
                (null, null),

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
        return ("macro", matched);
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
        bool    IsTouchKey);

    /// <summary>Reads Everest profiles (DeviceType="Everest") grouped by DeviceId.</summary>
    public static Dictionary<int, List<BcProfile>> ReadEverestProfiles(string dbPath)
        => ReadProfilesByType(dbPath, "Everest");

    /// <summary>Reads MacroPad profiles (DeviceType="MacroPad") grouped by DeviceId.</summary>
    public static Dictionary<int, List<BcProfile>> ReadMacroPadProfiles(string dbPath)
        => ReadProfilesByType(dbPath, "MacroPad");

    private static Dictionary<int, List<BcProfile>> ReadProfilesByType(string dbPath, string deviceType)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ProfileId, Id, ProfileName, DeviceId, DeviceGUID, IsSelected
            FROM Profiles
            WHERE DeviceType = $dt
            ORDER BY DeviceId, Id";
        cmd.Parameters.AddWithValue("$dt", deviceType);

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
        cmd.CommandText = @"
            SELECT DLLMatrixIndex, FunctionType, SubFunctionType, FunctionValue,
                   base64Image, IsKeyAssigned, IsTouchKey
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
                IsTouchKey:      r.GetInt32(6) != 0));
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
            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames);
            if (at is null) continue;
            store.SaveKey(new EverestKeyRecord(slot, b.DLLMatrixIndex, null, at, av));
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
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "K2.App", "imported_bc_ev", $"slot{slot}_{profile.Name}");
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

            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames);
            if (at is not null)
            {
                store.SetSetting($"{prefix}.actionType",  at);
                store.SetSetting($"{prefix}.actionValue", av ?? "");
            }
            touch++;
        }

        return (regular, touch);
    }

    // =========================================================
    // MacroPad — MakaluKeyBindings table (NOT EverestKeyBidings!)
    //
    // Reverse-engineered from the decompiled Base Camp "Makalu" module
    // (K2/_reference/BaseCamp_Decompiled/Makalu/Makalu.cs). The MacroPad has its
    // own DB entity, distinct from Everest/DisplayPad's "EverestKeyBidings":
    //   MakaluKeyBinding: KeyId (1-12, plain button number — NOT 170-221!),
    //   KeyName, IsKeyAssigned, FunctionType, FunctionValue (no SubFunctionType
    //   column at all), FunctionEnteredValue, ONKeyPressRelease,
    //   SyncAcrossProfilesKeyBinding, CustomURL. No per-key images.
    //
    // FunctionType vocabulary confirmed from Button_Function.Function_String:
    //   "Mouse Wheel", "Mouse", "Keyboard Shortcuts", "Media", "Run Macro",
    //   "Run Program", "Default", "Disable", "OS Commands",
    //   "Battery level check", "Brightness cycle", "Effect cycle",
    //   "DPI Cyclic Increase", "DPI Cyclic Decrease".
    // Sub-vocabularies (single FunctionValue, no SubFunctionType):
    //   Mouse       -> Mouse_Key_String: Left/Right/Middle button, Backward,
    //                  Forward, Next Profile, Previous Profile, DPI Sniper,
    //                  DPI +, DPI -, Battery level check, Brightness cycle,
    //                  Effect cycle, DPI Cyclic Increase/Decrease.
    //   Mouse Wheel -> Mouse_Wheel_String: Scroll Up, Scroll Down.
    //   Media       -> Consumer_Key_String: Play/Pause, Stop, Previous track,
    //                  Next track, Volume up, Volume down, Mute, Mic Mute,
    //                  Run browser, Calculator.
    //   OS Commands -> OS_Command_String: Run task manager, Run browser,
    //                  Lock computer, "Shut down" (WITH a space — different
    //                  from DisplayPad's "Shutdown"!), Sleep, Hibernate,
    //                  Calculator. No "Run explorer" entry exists for MacroPad.
    //
    // NEVER VERIFIED against a real imported/exported MacroPad profile on
    // hardware — only against decompiled source. See _PROJECT_MAP.md.
    // =========================================================

    /// <summary>MacroPad key read from MakaluKeyBindings.</summary>
    public sealed record BcMakaluButton(
        int ButtonIndex,     // 0-11 (KeyId - 1)
        string? FunctionType,
        string? FunctionValue,
        string? FunctionEnteredValue,
        bool IsAssigned);

    /// <summary>Reads the keys of a MacroPad profile from the real Base Camp table
    /// (MakaluKeyBindings), not from EverestKeyBidings.</summary>
    public static List<BcMakaluButton> ReadMakaluBindings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT KeyId, FunctionType, FunctionValue, FunctionEnteredValue, IsKeyAssigned
            FROM MakaluKeyBindings
            WHERE ProfileId = $pid
            ORDER BY KeyId";
        cmd.Parameters.AddWithValue("$pid", profileId);

        var result = new List<BcMakaluButton>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int keyId = r.GetInt32(0);
            if (keyId < 1 || keyId > 12) continue; // outside the physical 12-key layout

            result.Add(new BcMakaluButton(
                ButtonIndex:          keyId - 1,
                FunctionType:         r.IsDBNull(1) ? null : r.GetString(1),
                FunctionValue:        r.IsDBNull(2) ? null : r.GetString(2),
                FunctionEnteredValue: r.IsDBNull(3) ? null : r.GetString(3),
                IsAssigned:           r.GetInt32(4) != 0));
        }
        return result;
    }

    /// <summary>
    /// Imports a MacroPad profile into <see cref="MacroPadStore"/>, reading the
    /// real MakaluKeyBindings table. Returns the number of keys imported.
    /// <paramref name="targetSlot"/> is a fresh slot picked by the caller via
    /// <see cref="FindFreeSlot"/>, not <c>profile.Slot</c>.
    /// </summary>
    public static int ImportMacroPadProfile(
        string dbPath, BcProfile profile, int k2DeviceId, MacroPadStore store, int targetSlot,
        IReadOnlyCollection<string>? macroNames = null)
    {
        var bindings = ReadMakaluBindings(dbPath, profile.ProfileId);
        int slot = targetSlot;
        int imported = 0;

        foreach (var b in bindings)
        {
            if (!b.IsAssigned) continue;

            var (at, av) = TranslateMakaluAction(b.FunctionType, b.FunctionValue, macroNames);
            if (at is null) continue;
            store.SaveKey(new MacroKeyRecord(k2DeviceId, slot, b.ButtonIndex, at, av));
            imported++;
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
                return fv switch
                {
                    "Play/Pause"      => ("media", "play_pause"),
                    "Stop"            => ("media", "stop"),
                    "Previous track"  => ("media", "prev_track"),
                    "Next track"      => ("media", "next_track"),
                    "Volume up"       => ("media", "volume_up"),
                    "Volume down"     => ("media", "volume_down"),
                    "Mute"            => ("media", "mute"),
                    "Mic Mute"        => ("media", "mic_mute"),
                    "Run browser"     => ImportBrowserAction(null),
                    "Calculator"      => ("oscmd", "calculator"),
                    _ => ("none", $"[media] {fv}")
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
                    "Run task manager" => ("oscmd", "run task manager"),
                    "Run browser"      => ImportBrowserAction(null),
                    "Lock computer"    => ("oscmd", "lock computer"),
                    "Shut down"        => ("oscmd", "shutdown"),
                    "Sleep"            => ("oscmd", "sleep"),
                    "Hibernate"        => ("oscmd", "hibernate"),
                    "Calculator"       => ("oscmd", "calculator"),
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
            case "Default":
                return (null, null);

            default:
                return ($"bc:{ft}", fv);
        }
    }

    // =========================================================
    // Makalu mouse — MakaluKeyBindings / MakaluLightings / MakaluSettings /
    // DPILevels tables. DeviceType="Makalu" for the Profiles row (confirmed
    // via the class's own MakaluLightings/MakaluKeyBindings/MakaluSettings
    // navigation properties on BaseCamp.Data.Profile, plus the RPC bridge
    // JS embedded in BaseCamp.UI.dll using {'Class':'Makalu', ...} literally
    // — see _PROJECT_MAP.md). UNVERIFIED against a real exported Makalu
    // profile: this Base Camp install never had a physical Makalu paired
    // (all 4 tables are empty here) — Lighting/Settings/DPI field-for-field
    // copies are high confidence (schema decompiled from real .NET metadata),
    // but the button FunctionType/FunctionValue vocabulary below is inferred
    // by analogy with the already-confirmed MacroPad translator
    // (TranslateMakaluAction, same decompiled family) rather than a captured
    // sample — falls back to skipping (not guessing) anything that doesn't
    // match exactly.
    // =========================================================

    /// <summary>Reads Makalu mouse profiles (DeviceType="Makalu") grouped by DeviceId.</summary>
    public static Dictionary<int, List<BcProfile>> ReadMakaluProfiles(string dbPath)
        => ReadProfilesByType(dbPath, "Makalu");

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
                    _ => null, // Next/Previous Profile, battery/brightness/effect cycle: no Makalu remap equivalent
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

        var eff = effectName.Trim().ToLowerInvariant() switch
        {
            "static"                    => MakaluProtocol.Effect.Static,
            "breathing"                => MakaluProtocol.Effect.Breathing,
            "rgb breathing"             => MakaluProtocol.Effect.RgbBreathing,
            "rainbow"                   => MakaluProtocol.Effect.Rainbow,
            "responsive"                => MakaluProtocol.Effect.Responsive,
            "yeti" or "yeti mode"       => MakaluProtocol.Effect.Yeti,
            "off"                       => MakaluProtocol.Effect.Off,
            "custom"                    => MakaluProtocol.Effect.Off, // Custom isn't a MakaluProtocol.Effect value — handled via CustomActive below
            _                           => MakaluProtocol.Effect.Static,
        };
        bool customActive = effectName.Trim().Equals("Custom", StringComparison.OrdinalIgnoreCase);

        var customColors = new int[8];
        if (customActive && !string.IsNullOrEmpty(customJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(customJson);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("KeyCode", out var kc) || !el.TryGetProperty("ColorHex", out var ch)) continue;
                    int idx = kc.GetInt32();
                    if (idx < 0 || idx >= 8) continue;
                    customColors[idx] = ParseBcColor(ch.GetString());
                }
            }
            catch { /* malformed JSON — leave customColors at default black */ }
        }

        return new MakaluLightingRecord(
            (int)eff, color1, color2, speed, direction, brightness, customActive, customColors);
    }

    /// <summary>Reads MakaluSettings + DPILevels for a profile. No dedicated
    /// "debounce ms" column exists in Base Camp's schema — ButtonResponseTime
    /// is the closest analog (both are a small-int firmware debounce-style
    /// setting) and is used as a best-effort stand-in, UNVERIFIED against a
    /// real profile.</summary>
    public static (MakaluDeviceSettingsRecord? Settings, MakaluDpiRecord? Dpi) ReadMakaluMouseSettings(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        int pollingHz = 1000, debounceMs = 2, selectedDpiId = 1;
        bool angleOn = false, liftHigh = false;
        bool foundSettings = false;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT PollingRate, ButtonResponseTime, AngleSnapping, LiftOffDistance, SelectedDPILevelId
                FROM MakaluSettings WHERE ProfileId = $pid";
            cmd.Parameters.AddWithValue("$pid", profileId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                foundSettings = true;
                pollingHz    = r.IsDBNull(0) ? 1000 : r.GetInt32(0);
                debounceMs   = r.IsDBNull(1) ? 2 : r.GetInt32(1);
                angleOn      = !r.IsDBNull(2) && r.GetString(2).Equals("On", StringComparison.OrdinalIgnoreCase);
                liftHigh     = !r.IsDBNull(3) && r.GetString(3).Equals("High", StringComparison.OrdinalIgnoreCase);
                selectedDpiId = r.IsDBNull(4) ? 1 : r.GetInt32(4);
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
            ? new MakaluDeviceSettingsRecord(pollingHz, debounceMs, angleOn, liftHigh)
            : null;

        MakaluDpiRecord? dpi = null;
        if (levels.Count > 0)
        {
            var dpiValues = new int[5];
            for (int i = 0; i < 5; i++) dpiValues[i] = i < levels.Count ? levels[i].Dpi : dpiValues[Math.Max(0, i - 1)];
            int activeIdx = Math.Max(0, levels.FindIndex(l => l.Id == selectedDpiId));
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
        string? FunctionValue, string? FunctionEnteredValue, bool IsAssigned);

    public static List<BcEverest60KeyBinding> ReadEverest60KeyBindingsRaw(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DLLKeyId, LayerType, FunctionType, SubFunctionType, FunctionValue,
                   FunctionEnteredValue, IsKeyAssigned
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
                IsAssigned:           r.GetInt32(6) != 0));
        return result;
    }

    /// <summary>Reads the active (IsActive=1) Everest60Lightings row and
    /// translates it into an Ev60LightingRecord. EffIndex 1..9 maps to
    /// Base Camp's own EV60EffectIndex enum (Static/ColorWave/Tornado/
    /// Breathing/Reactive/Matrix/Custom/Yeti/Off) — Matrix has no
    /// Everest60Protocol.Effect equivalent (falls back to Static);
    /// Custom sets ActiveMode="custom" instead of a regular effect.
    /// Color3 is assumed to be the side-ring accent (the one color beyond
    /// the effect's own primary/secondary K2 tracks) — UNVERIFIED, Base
    /// Camp's schema doesn't label which of the 3 colors is which.</summary>
    public static Ev60LightingRecord? ReadEverest60LightingRaw(string dbPath, int profileId)
    {
        using var conn = OpenReadOnly(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT EffIndex, Speed, Brightness, Direction, Color1, Color2, Color3, CustomLightings
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
        int sideColor = ParseBcColor(r.IsDBNull(6) ? null : r.GetString(6), 0x900000);
        string? customJson = r.IsDBNull(7) ? null : r.GetString(7);

        var eff = effIndex switch
        {
            1 => Everest60Protocol.Effect.Static,
            2 => Everest60Protocol.Effect.Wave,
            3 => Everest60Protocol.Effect.Tornado,
            4 => Everest60Protocol.Effect.Breathing,
            5 => Everest60Protocol.Effect.Reactive,
            8 => Everest60Protocol.Effect.Yeti,
            9 => Everest60Protocol.Effect.Off,
            _ => Everest60Protocol.Effect.Static, // 6=Matrix, 7=Custom (handled below)
        };
        string activeMode = effIndex == 7 ? "custom" : "preset";

        int dirIndex = eff switch
        {
            Everest60Protocol.Effect.Wave    => Math.Max(0, Array.FindIndex(Everest60Protocol.WaveDirections, d => d.Code == rawDirection)),
            Everest60Protocol.Effect.Tornado => Math.Max(0, Array.FindIndex(Everest60Protocol.TornadoDirections, d => d.Code == rawDirection)),
            _ => 0,
        };

        var customColors = new Dictionary<int, int>();
        if (activeMode == "custom" && !string.IsNullOrEmpty(customJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(customJson);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("KeyCode", out var kc) || !el.TryGetProperty("ColorHex", out var ch)) continue;
                    int idx = kc.GetInt32();
                    if (idx < 0 || idx >= Everest60Protocol.NumKeys) continue; // side ring/numpad addresses live outside 0..63 — not this dictionary's scope
                    customColors[idx] = ParseBcColor(ch.GetString());
                }
            }
            catch { /* malformed JSON */ }
        }

        return new Ev60LightingRecord(
            (int)eff, color1, color2, speedPct, dirIndex, Rainbow: false,
            brightness, sideColor, CustomBrightness: brightness, activeMode, customColors);
    }

    /// <summary>Imports an Everest 60 profile: lighting (high confidence) +
    /// key bindings (via the shared <see cref="TranslateAction"/> vocabulary,
    /// see class-level doc comment) into Everest60Store. Returns the number
    /// of key bindings imported. <paramref name="targetSlot"/> is a fresh slot
    /// picked by the caller via <see cref="FindFreeSlot"/>, not <c>profile.Slot</c>.</summary>
    public static int ImportEverest60Profile(string dbPath, BcProfile profile, Everest60Store store, int targetSlot,
        IReadOnlyCollection<string>? macroNames = null)
    {
        int slot = targetSlot;
        store.ClearProfile(slot);
        store.SetProfileName(slot, profile.Name);

        var lighting = ReadEverest60LightingRaw(dbPath, profile.ProfileId);
        if (lighting is not null) store.SaveLighting(slot, lighting);

        int imported = 0;
        foreach (var b in ReadEverest60KeyBindingsRaw(dbPath, profile.ProfileId))
        {
            // Only the base layer is imported — see class-level doc comment
            // for why LayerType=3 (Fn) factory legends are never real remaps.
            if (b.LayerType != 1 || !b.IsAssigned) continue;

            int ledIndex = Array.IndexOf(Everest60RemapData.LedIndexToDllKeyIdArray, b.DllKeyId);
            if (ledIndex < 0) continue;

            var (at, av) = TranslateAction(b.FunctionType, b.SubFunctionType, b.FunctionValue, macroNames);
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
