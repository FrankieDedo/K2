using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.Core;

namespace K2.App;

/// <summary>
/// The keys that make up a device order, and how a tab maps to one. Kept out of
/// MainWindow so <see cref="DeviceOrderWindow"/> can use it without a MainWindow
/// instance.
///
/// A key is either a static device kind ("everest", "everest60", "makalu",
/// "macropad") — one physical unit each — or a single DisplayPad unit, "dp_&lt;id&gt;"
/// (the tab's own tag; the id is the STABLE logical id, see DisplayPadDeviceMap), so
/// several pads can be ordered individually and even interleaved with the other
/// devices. The saved order also carries the <see cref="DisplayPadAnchor"/>
/// placeholder, which marks where a pad K2 has never seen before is inserted.
/// </summary>
internal static class DeviceOrderCatalog
{
    /// <summary>Stand-in for "any DisplayPad not explicitly listed in the saved order":
    /// a pad plugged in for the first time lands here instead of at the very end.</summary>
    public const string DisplayPadAnchor = "displaypad";

    /// <summary>Built-in order — the one hardcoded in MainWindow.xaml before the
    /// order became user-editable.</summary>
    public static readonly string[] DefaultOrder =
        { "everest", "everest60", "makalu", DisplayPadAnchor, "macropad" };

    public static bool IsDisplayPadUnit(string key) => key.StartsWith("dp_", StringComparison.Ordinal);

    /// <summary>Localization key for a key's display name (reuses the tab labels). Only a
    /// fallback for DisplayPad units — their row shows the tab's own (user-renameable)
    /// header instead.</summary>
    public static string NameKey(string key) => key switch
    {
        "everest"   => "tab_everest",
        "everest60" => "tab_everest60",
        "makalu"    => "tab_makalu",
        "macropad"  => "tab_macropad",
        _           => "tab_displaypad",
    };

    /// <summary>The order key a top-level tab occupies — its tag, i.e. "dp_&lt;id&gt;" for
    /// a DisplayPad unit.</summary>
    public static string KeyOf(TabItem tab) => tab.Tag as string ?? "";

    /// <summary>Builds a complete ranking list out of a persisted order, FOR SORTING THE LIVE
    /// TAB STRIP ONLY — never used to decide what gets saved (that stays the raw, ungapped
    /// list Save() writes; see DeviceOrderWindow, which reads <see cref="AppSettings.DeviceOrder"/>
    /// directly). Drops duplicates and entries that are neither a known static kind nor a
    /// DisplayPad unit, then appends whatever <see cref="DefaultOrder"/> entry is missing so a
    /// device kind never explicitly ordered still sorts at its built-in default position. The
    /// <see cref="DisplayPadAnchor"/>, when missing, is inserted right after the last listed pad
    /// rather than at the end, so it keeps meaning "where the DisplayPads live".</summary>
    public static List<string> Resolve(IEnumerable<string>? saved)
    {
        var result = new List<string>();
        foreach (var k in saved ?? Enumerable.Empty<string>())
            if ((DefaultOrder.Contains(k) || IsDisplayPadUnit(k)) && !result.Contains(k))
                result.Add(k);

        foreach (var k in DefaultOrder)
        {
            if (result.Contains(k)) continue;
            int lastPad = result.FindLastIndex(IsDisplayPadUnit);
            if (k == DisplayPadAnchor && lastPad >= 0) result.Insert(lastPad + 1, k);
            else result.Add(k);
        }
        return result;
    }

    /// <summary>Rebuilds a full order out of the entries the user actually saw in the popup,
    /// re-inserting everything else <paramref name="loadedOrder"/> held (the
    /// <see cref="DisplayPadAnchor"/>, and pads that are currently unplugged) after whichever
    /// of its predecessors survived — so unplugging a pad, reordering, and plugging it back
    /// in doesn't lose its place.</summary>
    public static List<string> Compose(IEnumerable<string> visibleKeys, IReadOnlyList<string> loadedOrder)
    {
        var result = new List<string>(visibleKeys);
        for (int i = 0; i < loadedOrder.Count; i++)
        {
            string k = loadedOrder[i];
            if (result.Contains(k)) continue;
            int at = result.Count;
            for (int j = i - 1; j >= 0; j--)
            {
                int p = result.IndexOf(loadedOrder[j]);
                if (p >= 0) { at = p + 1; break; }
            }
            result.Insert(at, k);
        }
        return result;
    }

    /// <summary>Sort position of an order key. A DisplayPad the saved order doesn't mention
    /// (never plugged in before) sorts at the <see cref="DisplayPadAnchor"/>, just after any
    /// pad that IS listed there — hence the fractional rank; ties keep their input order
    /// under a stable sort.</summary>
    public static double Rank(string key, List<string> order)
    {
        int i = order.IndexOf(key);
        if (i >= 0) return i;
        if (!IsDisplayPadUnit(key)) return order.Count;
        int anchor = order.IndexOf(DisplayPadAnchor);
        if (anchor >= 0) return anchor + 0.5;
        int lastPad = order.FindLastIndex(IsDisplayPadUnit);
        return lastPad >= 0 ? lastPad + 0.5 : order.Count;
    }
}

/// <summary>
/// MainWindow partial: user-defined device order (Settings &gt; Device order popup).
/// A single sort applied to the top-level tab strip; the Home cards then follow
/// automatically because <c>RefreshHomeTiles</c> walks the tabs in their current
/// order. See <see cref="DeviceOrderWindow"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Set while <see cref="ApplyDeviceOrder"/> pulls tabs out of TcDevices and
    /// re-adds them: removing the selected tab makes WPF fire SelectionChanged with a
    /// transient selection, which would swap the visible content panel (and re-activate
    /// another device) for nothing. The real selection is restored right after.</summary>
    private bool _reorderingTabs;

    /// <summary>Sorts the top-level device tabs into the user's saved order (Home always
    /// stays first). Cheap and idempotent: bails out when the tabs are already in order,
    /// so it can be called from RefreshHomeTiles on every connection change.</summary>
    private void ApplyDeviceOrder()
    {
        var order = DeviceOrderCatalog.Resolve(AppSettings.DeviceOrder);

        var current = TcDevices.Items.OfType<TabItem>()
                               .Where(t => !ReferenceEquals(t, TabHome)).ToList();
        // OrderBy is a stable sort: pads the saved order doesn't mention share one rank
        // and keep the SDK-id order DpRefreshDevices created them in.
        var sorted = current.OrderBy(t => DeviceOrderCatalog.Rank(DeviceOrderCatalog.KeyOf(t), order)).ToList();
        if (sorted.SequenceEqual(current)) return;

        var selected = TcDevices.SelectedItem;
        _reorderingTabs = true;
        try
        {
            // Removing each tab and appending it in the target order leaves the list
            // sorted: every processed tab lands after the ones processed before it.
            foreach (var tab in sorted)
            {
                TcDevices.Items.Remove(tab);
                TcDevices.Items.Add(tab);
            }
        }
        finally
        {
            _reorderingTabs = false;
        }

        if (selected is not null && TcDevices.Items.Contains(selected))
            TcDevices.SelectedItem = selected;
    }

    /// <summary>One popup row per CURRENTLY CONNECTED top-level device tab, in their
    /// current (already sorted) order — so each DisplayPad unit is listed and orderable
    /// on its own, under the same (renameable) label its tab shows. A disconnected
    /// device is left out entirely (see DeviceOrderWindow's doc comment for how its
    /// position is nonetheless remembered if it had been ordered before).</summary>
    private List<DeviceOrderWindow.Row> BuildDeviceOrderRows() =>
        TcDevices.Items.OfType<TabItem>()
                 .Where(t => !ReferenceEquals(t, TabHome) && t.Visibility == Visibility.Visible)
                 .Select(t => new DeviceOrderWindow.Row
                 {
                     Key = DeviceOrderCatalog.KeyOf(t),
                     Name = t.Header as string
                            ?? Loc.Get(DeviceOrderCatalog.NameKey(DeviceOrderCatalog.KeyOf(t))),
                 })
                 .ToList();

    private void BtnDeviceOrder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DeviceOrderWindow(BuildDeviceOrderRows(), RefreshHomeTiles) { Owner = this };
        dlg.ShowDialog();
    }
}
