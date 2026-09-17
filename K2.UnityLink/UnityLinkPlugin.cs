using System;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace K2.UnityLink
{
    /// <summary>
    /// Generic BepInEx plugin: exposes whatever a Unity game's own scripts are holding as one JSON
    /// endpoint, for K2's <c>CustomSource.Link</c> (see K2's <c>GameLink.cs</c>) to read like any other
    /// game — same shape as War Thunder's <c>:8111/state</c> or Satisfactory's Ficsit Remote Monitoring,
    /// just sourced by reflection instead of hand-written per game.
    ///
    /// <para><b>Mono only, deliberately.</b> A game built with Unity's IL2CPP backend compiles every
    /// script to native code ahead of time — there is no managed <c>MonoBehaviour</c> to
    /// <c>System.Reflection</c> over at runtime any more (field/type names are frequently stripped
    /// too), so this plugin would need an entirely different approach (Il2CppInterop, per-Unity-version
    /// interop assemblies) to do anything at all. BepInEx 5/Mono is the target here; IL2CPP games are
    /// out of scope for this DLL.</para>
    ///
    /// <para><b>This class only bootstraps — <see cref="UnityLinkRunner"/> does the actual work.</b>
    /// Confirmed on real hardware (Unturned, 2026-09-13, two rounds): a GameObject created and marked
    /// <c>DontDestroyOnLoad</c> from <c>Awake()</c> gets destroyed moments after BepInEx logs
    /// "Chainloader startup complete" — REGARDLESS of whether it is the plugin's own host object or a
    /// brand new, unparented one created purely for this. Since even a fresh root object marked
    /// <c>DontDestroyOnLoad</c> the instant it is created does not survive, the object itself was never
    /// the problem — the TIMING is: <c>Awake()</c> runs during the engine's own native bootstrap,
    /// before Unity's managed scene system has finished coming up for the very first time, and
    /// <c>DontDestroyOnLoad</c> called that early does not reliably stick across the engine's OWN first
    /// scene instantiation (a one-time transition, different from every later <c>LoadScene</c> call,
    /// which is what <c>DontDestroyOnLoad</c> is normally tested against). The fix is to stop doing
    /// anything object-related in <c>Awake()</c> at all: it only subscribes to
    /// <see cref="SceneManager.sceneLoaded"/>, a plain C# event that needs no living GameObject to
    /// fire, and creates the host — for real, <c>DontDestroyOnLoad</c> and all — only once a scene has
    /// actually finished loading, i.e. provably after that risky first transition is over.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class UnityLinkPlugin : BaseUnityPlugin
    {
        private const string Guid = "com.k2tent.unitylink";
        private const string Name = "K2 Unity Link";
        private const string Version = "1.0.0";

        private ConfigEntry<int> _port = null!;
        private ConfigEntry<int> _refreshMs = null!;
        private ConfigEntry<int> _fullRefreshMs = null!;
        private ConfigEntry<int> _maxObjects = null!;
        private ConfigEntry<int> _maxDepth = null!;
        private ConfigEntry<int> _maxFieldsPerComponent = null!;
        private ConfigEntry<bool> _includeInactive = null!;
        private ConfigEntry<bool> _includePrivateFields = null!;
        private ConfigEntry<bool> _showPublicFields = null!;
        private ConfigEntry<int> _maxNestedDepth = null!;

        private void Awake()
        {
            _port = Config.Bind("Server", "Port", 34873,
                "Local HTTP port to serve the JSON snapshot on (http://127.0.0.1:<port>/state). " +
                "Change this if it collides with a port another mod or the game itself already uses.");
            _refreshMs = Config.Bind("Server", "RefreshMs", 500,
                "How often, in milliseconds, to resolve the specific paths K2 last told this mod it " +
                "actually reads (a saved reading's own poll asks by name, via a 'k2want' query " +
                "string K2 adds on its own — nothing to configure on this side). Lower is fresher " +
                "but costs more CPU every tick; K2's own tiles only repaint about once a second, so " +
                "anything under 300 buys nothing.");
            _fullRefreshMs = Config.Bind("Server", "FullRefreshMs", 4000,
                "MAXIMUM AGE, in milliseconds, of the full scene dump K2's studio uses to let you " +
                "BROWSE for a value not saved as a reading yet — not a timer. The full walk is the " +
                "expensive one, so it runs ONLY when something actually asks for it and the last " +
                "answer is older than this; while you are just playing, with saved readings polled " +
                "by name, it does not run at all. (It used to run on a timer, which cost a short " +
                "stutter every few seconds in-game for nothing.) Raise it if browsing still hitches " +
                "and you do not mind older values in the picker.");
            // Renamed from the old "MaxGameObjects" on purpose: that key counted objects found by
            // walking scene hierarchies, scenery included, and a config file written back then still
            // holds its old 1500. This counts only objects that CARRY A SCRIPT, which is a different
            // and much smaller population — a new key gets the new default without anyone having to
            // hand-edit a file to undo a number that no longer means what it used to.
            _maxObjects = Config.Bind("Limits", "MaxScriptedObjects", 6000,
                "Ceiling on GameObjects included per full snapshot. Only objects carrying at least " +
                "one script are listed at all, so this is not a cap on the scene's size.");
            _maxDepth = Config.Bind("Limits", "MaxHierarchyDepth", 10,
                "Ceiling on how deep into a GameObject's children this walks.");
            _maxFieldsPerComponent = Config.Bind("Limits", "MaxFieldsPerComponent", 300,
                "Ceiling on fields+properties read per script component. A big, old, monolithic " +
                "player/character class can legitimately have hundreds of fields — a low ceiling " +
                "here silently cuts the ones declared after it, which can hide exactly the values " +
                "(health, ammo...) a reading was after. Raised from an earlier, too-conservative 40 " +
                "after that happened on real hardware (Unturned, 2026-09-14).");
            _maxNestedDepth = Config.Bind("Limits", "MaxNestedDepth", 2,
                "How many of the GAME's own objects a value may sit inside and still be read. 0 " +
                "reads only scalars sitting directly on a script, which is what this did before " +
                "and which misses most well-organised games: on 7 Days to Die health, food and " +
                "water all live at Stats.Health.Value, two objects down, so nothing below 2 finds " +
                "them. Raise it if a game buries its state deeper; lower it for a smaller snapshot. " +
                "Engine objects are never followed at any setting, so this cannot walk back into " +
                "the scene.");
            _includeInactive = Config.Bind("Limits", "IncludeInactiveObjects", false,
                "Whether to also walk GameObjects that are currently disabled.");
            _includePrivateFields = Config.Bind("Limits", "IncludePrivateFields", true,
                "Whether to also read PRIVATE fields, not just public ones. Health/ammo/hunger and " +
                "similar HUD numbers are usually private fields in well-structured game code " +
                "([SerializeField] private float health;) — without this, most of what a game " +
                "actually tracks is invisible. On by default; set to false for a smaller, cleaner " +
                "snapshot if you only need what the game exposes publicly.");
            // Off by default and marked "advanced" so a BepInEx.ConfigurationManager overlay (F1 in
            // most games that have it) hides it behind "Show advanced settings" — public fields are
            // mostly Unity Inspector references (other GameObjects, prefabs, colors), not gameplay
            // numbers, and with private fields already on by default they are pure noise for the
            // normal case. Still a real, editable entry in the .cfg either way; "debug" here means
            // "hidden unless you go looking", not "requires a special build".
            _showPublicFields = Config.Bind("Limits", "ShowPublicFields", false,
                new ConfigDescription(
                    "Whether to also read PUBLIC fields. Off by default — with private fields " +
                    "already included above, public fields are mostly Unity Inspector references " +
                    "(other GameObjects, prefabs, colours) rather than gameplay numbers, so they " +
                    "mostly just add noise. Turn on if you are specifically looking for something " +
                    "exposed that way.",
                    null, new ConfigurationManagerAttributes { IsAdvanced = true }));

            // A static event, not a GameObject — subscribing needs nothing alive to survive whatever
            // is happening during the engine's own first scene transition. See the class comment.
            SceneManager.sceneLoaded += CreateHostOnce;
        }

        private void CreateHostOnce(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= CreateHostOnce; // one-shot: only the FIRST load matters here
            Logger.LogInfo($"first scene loaded (\"{scene.name}\") — creating host now");

            var host = new GameObject("K2UnityLinkHost");
            DontDestroyOnLoad(host);
            host.AddComponent<UnityLinkRunner>().Init(
                Logger, _port, _refreshMs, _fullRefreshMs, _maxObjects, _maxDepth, _maxFieldsPerComponent,
                _includeInactive, _includePrivateFields, _showPublicFields, _maxNestedDepth);
        }
    }

    /// <summary>Duck-typed for <c>BepInEx.ConfigurationManager</c>'s own tag type of the same name —
    /// that mod finds settings tags by matching PROPERTY NAMES via reflection, not by type identity,
    /// specifically so a plugin never has to reference its assembly (which may not even be
    /// installed) just to mark one setting advanced. Only <see cref="IsAdvanced"/> is defined here
    /// because it is the only one this plugin needs; the rest of ConfigurationManager's real
    /// attributes are simply left absent, which it treats as "unset".</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
    }
}
