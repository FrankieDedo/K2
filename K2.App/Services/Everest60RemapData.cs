using System.Collections.Generic;

namespace K2.App.Services;

/// <summary>
/// Static tables for the Everest 60 Key Binding feature: the physical-key ↔
/// <c>DLLKeyId</c> catalog used by <see cref="Everest60SdkNative.ChangeKey"/>/
/// <c>ChangeFnKey</c>/<c>ChangeShortcutKey</c>, and the media-action code table
/// for <c>SetSingleMacroContent</c>.
///
/// <c>DLLKeyId</c> is a DIFFERENT numbering scheme from the LED index used by
/// <see cref="Everest60Protocol"/>/<c>Everest60KeyboardLayout.MainBoard</c>
/// (which drives lighting) — the same physical key has two unrelated numeric
/// identities depending on which subsystem addresses it. <see cref="LedIndexToDllKeyId"/>
/// bridges the two so the existing 64-key on-screen overlay (built for
/// lighting) can double as the Key Binding section's key picker.
///
/// English/US catalog, extracted 2026-07-11 by decompiling
/// <c>BaseCamp.UI.dll</c>'s <c>Everest60Operations.GetEverest60KeyBindings_English</c>
/// (base locale — UK/Dutch/Norwegian layer a small overlay on top of it for a
/// handful of OEM keys, not modeled here yet). Every DLLKeyId below was read
/// directly from an <c>ldc.i4</c>/<c>ldstr</c> pair immediately preceding
/// <c>set_DLLKeyId</c>/<c>set_DLLKeyName</c> in the decompiled IL — none
/// inferred. Confirmed (same decompile pass): <c>ChangeFnKey</c> reuses this
/// exact same DLLKeyId numbering (Fn-layer entries in Base Camp's own catalog
/// are the same rows with <c>LayerType=3</c> instead of the main layer's
/// default) — so a key's Fn binding is programmed with its ordinary
/// DLLKeyId, same as its main-layer binding. See CHANGELOG for the full trace.
/// </summary>
internal static class Everest60RemapData
{
    /// <summary>Key label → DLLKeyId, English/US board catalog (64 physical
    /// keys only — the decompiled catalog also lists ~52 non-physical targets
    /// for this board, numpad/nav-cluster/F13-F24/etc, valid as remap targets
    /// but not included here since this device has none of those keys).</summary>
    public static readonly IReadOnlyDictionary<string, int> KeyCatalog = new Dictionary<string, int>
    {
        { "Esc", 110 }, { "1", 2 }, { "2", 3 }, { "3", 4 }, { "4", 5 },
        { "5", 6 }, { "6", 7 }, { "7", 8 }, { "8", 9 }, { "9", 10 },
        { "0", 11 }, { "-", 12 }, { "=", 13 }, { "Backspace", 15 },
        { "Tab", 16 }, { "Q", 17 }, { "W", 18 }, { "E", 19 }, { "R", 20 },
        { "T", 21 }, { "Y", 22 }, { "U", 23 }, { "I", 24 }, { "O", 25 },
        { "P", 26 }, { "[", 27 }, { "]", 28 }, { "\\", 29 },
        { "Caps Lock", 30 }, { "A", 31 }, { "S", 32 }, { "D", 33 }, { "F", 34 },
        { "G", 35 }, { "H", 36 }, { "J", 37 }, { "K", 38 }, { "L", 39 },
        { ";", 40 }, { "'", 41 }, { "Enter", 43 },
        { "Shift (Left)", 44 }, { "Z", 46 }, { "X", 47 }, { "C", 48 }, { "V", 49 },
        { "B", 50 }, { "N", 51 }, { "M", 52 }, { ",", 53 }, { ".", 54 }, { "/", 55 },
        { "Shift (Right)", 57 }, { "Up Arrow", 83 }, { "Delete", 76 },
        { "Ctrl (Left)", 58 }, { "Win (Left)", 59 }, { "Alt (Left)", 60 },
        { "Space", 61 }, { "Alt (Right)", 62 }, { "Fn", 154 },
        { "Left Arrow", 79 }, { "Down Arrow", 84 }, { "Right Arrow", 89 },
    };

    /// <summary>LED index (0-63, matches Everest60KeyboardLayout.MainBoard's
    /// row order and Everest60Protocol.LedIndex order) → DLLKeyId. Built by
    /// direct positional correspondence, not label lookup: manually verified
    /// 2026-07-11 that Everest60KeyboardLayout.MainBoard's row layout (Esc
    /// row 0-13 / QWERTY row 14-27 / home row 28-40 / shift row 41-54 /
    /// bottom row 55-63) matches the decompiled catalog's row order key for
    /// key. (Positional mapping also sidesteps the Left/Right Shift label
    /// collision KeyCatalog above resolves by suffixing "(Left)"/"(Right)" —
    /// the on-screen board only shows a bare "⇧" glyph for both.)</summary>
    public static readonly int[] LedIndexToDllKeyIdArray =
    {
        // Row 0: Esc 1-0 - = Backspace (idx 0-13)
        110, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15,
        // Row 1: Tab Q-P [ ] \ (idx 14-27)
        16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        // Row 2: Caps A-L ; ' Enter (idx 28-40)
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 43,
        // Row 3: Shift Z-/ Shift Up Del (idx 41-54)
        44, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 57, 83, 76,
        // Row 4: Ctrl Win Alt Space Alt Fn Left Down Right (idx 55-63)
        58, 59, 60, 61, 62, 154, 79, 84, 89,
    };

    /// <summary>Numpad accessory's 17 keys → DLLKeyId, same index order as
    /// <c>Everest60KeyboardLayout.Numpad</c>/<c>KeyDef.NumpadIndex</c>/
    /// <c>Everest60Protocol.NumpadLedIndex</c> (Num,/,*,-, 7,8,9,+, 4,5,6,
    /// 1,2,3,Enter, 0,.). Extracted 2026-07-22 by re-decompiling
    /// <c>Everest60Operations.GetEverest60KeyBindings_English</c> (same
    /// method as <see cref="KeyCatalog"/> above, whose doc comment originally
    /// dismissed these ~52 extra catalog entries as "not needed — this
    /// device has none of those keys", an assumption superseded once the
    /// accessory numpad's own Key Binding protocol was found via USBPcap —
    /// see CHANGELOG). Verified against two independent captures assigning a
    /// real numpad key: "Numpad 7"'s DLLKeyId (91=0x5B) and "Numpad 4"'s
    /// (92=0x5C) both matched the wire value exactly, not inferred.</summary>
    public static readonly int[] NumpadDllKeyId =
    {
        90, 95, 100, 105,   // Num Lock, /, *, -
        91, 96, 101, 106,   // 7, 8, 9, +
        92, 97, 102,        // 4, 5, 6
        93, 98, 103,        // 1, 2, 3
        108,                // Enter
        99, 104,            // 0, .
    };

    /// <summary>Modifier bitmask values for ChangeShortcutKey.</summary>
    public const int ModCtrl = 1;
    public const int ModShift = 2;
    public const int ModAlt = 4;
    public const int ModWin = 8;

    /// <summary>Reset/disable sentinel for ChangeKey/ChangeFnKey.</summary>
    public const int DisabledKeyId = 255;

    /// <summary>
    /// Media/OS action label → SetSingleMacroContent code (type=3).
    /// <b>UNCONFIRMED</b> — the 2026-07-11 decompile pass found the call
    /// sites for ChangeShortcutKey/SetSingleMacroContent but not an
    /// enumerable target-code catalog (values are passed through from raw UI
    /// selection, not resolved from a lookup table in the traced method).
    /// This list is a placeholder ordering (1-7) pending either a confirmed
    /// decompile trace or a USB capture from real hardware — do not treat as
    /// verified. Media key binding is wired end-to-end so it's ready to
    /// correct once the real codes are known.
    /// </summary>
    public static readonly (string Label, int Code)[] MediaActions =
    {
        ("Volume Up (unconfirmed code)", 1),
        ("Volume Down (unconfirmed code)", 2),
        ("Mute (unconfirmed code)", 3),
        ("Play / Pause (unconfirmed code)", 4),
        ("Previous Track (unconfirmed code)", 5),
        ("Next Track (unconfirmed code)", 6),
        ("Stop (unconfirmed code)", 7),
    };
}
