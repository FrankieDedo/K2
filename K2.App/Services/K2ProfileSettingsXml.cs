using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// The <c>&lt;K2ProfileSettings&gt;</c> block of a K2-format profile export: a verbatim
/// dump of the per-profile settings namespaces a device's panels persist, and its inverse
/// on import.
///
/// <para><b>Why a generic dump instead of named fields.</b> Until 2026-08-22 the only
/// settings that survived an export were the handful that happen to have a Base Camp
/// column — Game Mode, the Core indicator LED, and the two Display Dial values BC keeps in
/// its <c>KeyboardSettings</c> table (auto-off timer, clock format). Everything else the
/// panels save was lost: the whole Display Dial page selection/screensaver/menu colour, the
/// keycap appearance block, and every setting any panel might gain later. Since all of it
/// already lives in one flat <c>Key/Value</c> table under a per-profile prefix, copying the
/// prefix wholesale round-trips the lot and keeps working when a panel adds a key.</para>
///
/// <para><b>K2-format exports only.</b> Base Camp has no element like this and its importer
/// is schema-driven, so a BC-compatible export must not carry it (same rule as the
/// K2-only lighting rows, see <see cref="KeyboardLightingXml"/>).</para>
///
/// <para><b>Slot independence.</b> Keys are stored with the slot segment REMOVED —
/// <c>settings.p3.game_mode</c> travels as <c>settings.game_mode</c> — so a profile
/// exported from slot 3 imports cleanly into whatever free slot the target machine has.</para>
/// </summary>
internal static class K2ProfileSettingsXml
{
    public const string WrapperName = "K2ProfileSettings";

    /// <summary>Prefix families for the Everest Max: the Settings section
    /// (<c>settings.p{n}.</c> — Game Mode, indicator LED, keycap appearance) and the
    /// Display Dial section (<c>dial.p{n}.</c>). Lighting (<c>rgb.</c>/<c>custom.</c>) and
    /// display-key state (<c>ndk.</c>) are deliberately absent: they already have their own,
    /// richer blocks in the same file (<see cref="KeyboardLightingXml"/> /
    /// the <c>KeyboardBinding</c> items) and duplicating them here would give the importer
    /// two sources of truth.</summary>
    public static readonly string[] EverestFamilies = { "settings.", "dial." };

    /// <summary>Prefix family for every other device: the Settings section only (no
    /// Display Dial hardware).</summary>
    public static readonly string[] SettingsOnlyFamilies = { "settings." };

    /// <summary>
    /// Keys that live in a per-profile family but describe the PHYSICAL UNIT, not the
    /// profile, so they must not travel with a profile: the keyboard body colour is a fact
    /// about the plastic in front of the user. Matched on the full store key as it would
    /// appear with no slot segment.
    /// </summary>
    private static readonly HashSet<string> s_deviceGlobalKeys = new(StringComparer.Ordinal)
    {
        "settings.keyboard_color",
    };

    /// <summary>
    /// Builds the block for <paramref name="slot"/>.
    /// <paramref name="getByPrefix"/> is the store's <c>GetSettingsWithPrefix</c>.
    ///
    /// <para>Falls back to the SHARED namespace of a family when the profile-scoped one is
    /// empty: with "sync across profiles" on, the panels write to <c>settings.</c>/
    /// <c>dial.</c> without a slot segment (see <c>EvSettingsPrefix</c>/<c>EvDialPrefix</c>),
    /// so that is where a synced profile's real values are. Other profiles' rows are
    /// filtered out of that sweep by <see cref="LooksSlotScoped"/>.</para>
    /// </summary>
    /// <param name="preferSharedFor">
    /// Given a family prefix (<c>"settings."</c> / <c>"dial."</c>), returns true when that
    /// section's "sync across profiles" flag is on, i.e. the panel is reading and writing
    /// the shared namespace right now. Then the shared values are the ones the user can
    /// actually see, and any per-profile rows left over from before the flag was turned on
    /// are stale — so the shared namespace is read FIRST instead of only as a fallback.
    /// Null (the default) means "no section is synced" (every family per-profile-first).
    /// Since 2026-08-28 each section has its own flag, hence per-family rather than one bool.
    /// </param>
    public static XElement Build(
        Func<string, IReadOnlyDictionary<string, string>> getByPrefix,
        int slot, IReadOnlyList<string> families, Func<string, bool>? preferSharedFor = null)
    {
        var wrapper = new XElement(WrapperName);
        foreach (var family in families)
        {
            bool preferShared = preferSharedFor?.Invoke(family) ?? false;
            IReadOnlyDictionary<string, string> Shared() => getByPrefix(family)
                .Where(kv => !LooksSlotScoped(kv.Key)
                          && !s_deviceGlobalKeys.Contains(family + kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            var rows = preferShared ? Shared() : getByPrefix($"{family}p{slot}.");
            if (rows.Count == 0)
                rows = preferShared ? getByPrefix($"{family}p{slot}.") : Shared();

            foreach (var kv in rows.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (s_deviceGlobalKeys.Contains(family + kv.Key)) continue;
                wrapper.Add(new XElement("S",
                    new XAttribute("k", family + kv.Key),
                    new XAttribute("v", kv.Value)));
            }
        }
        return wrapper;
    }

    /// <summary>
    /// Writes the block back under <paramref name="slot"/>'s own namespace and returns how
    /// many settings were restored. A missing/empty block is not an error — every profile
    /// exported before 2026-08-22, and every genuine Base Camp file, simply has none.
    /// Entries whose key doesn't belong to one of <paramref name="families"/> are ignored,
    /// so a file exported from a different device type can't smuggle keys in.
    /// </summary>
    public static int Apply(
        XElement? root, Action<string, string> setSetting, int slot, IReadOnlyList<string> families)
    {
        var wrapper = root?.Descendants(WrapperName).FirstOrDefault();
        if (wrapper is null) return 0;

        int applied = 0;
        foreach (var el in wrapper.Elements("S"))
        {
            string key = el.Attribute("k")?.Value ?? "";
            string value = el.Attribute("v")?.Value ?? "";
            if (key.Length == 0) continue;
            if (s_deviceGlobalKeys.Contains(key)) continue;

            string? family = families.FirstOrDefault(f => key.StartsWith(f, StringComparison.Ordinal));
            if (family is null) continue;
            // Defence against a hand-edited file that kept a slot segment: dropping it
            // keeps the "import into any slot" guarantee.
            string rest = key[family.Length..];
            if (LooksSlotScoped(rest)) rest = rest[(rest.IndexOf('.') + 1)..];
            if (rest.Length == 0) continue;

            setSetting($"{family}p{slot}.{rest}", value);
            applied++;
        }
        return applied;
    }

    /// <summary>True for the remainder of a key that still carries a slot segment
    /// (<c>p3.game_mode</c>) — as opposed to a plain setting name that merely starts with
    /// "p" (the Display Dial's own <c>pages</c>).</summary>
    private static bool LooksSlotScoped(string rest)
    {
        if (rest.Length < 3 || rest[0] != 'p' || !char.IsDigit(rest[1])) return false;
        int dot = rest.IndexOf('.');
        return dot > 1 && rest.AsSpan(1, dot - 1).ToString().All(char.IsDigit);
    }
}
