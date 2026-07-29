namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Hotkey Switch" panel — two independent copies of the
/// Keys panel's modifier-checkboxes + key-combo row (see <see cref="ParseShortcut"/>/
/// <see cref="BuildShortcut"/> in <c>ButtonActionDialog.Keys.cs</c>), stored as a
/// <see cref="HotkeySwitchPayload"/>.
/// </summary>
public partial class ButtonActionDialog
{
    private bool _hotkeySwitchPanelPopulated;

    private void EnsureHotkeySwitchPanel()
    {
        if (_hotkeySwitchPanelPopulated) return;
        _hotkeySwitchPanelPopulated = true;
        PopulateKeyItems(CbHkAValue);
        PopulateKeyItems(CbHkBValue);
    }

    private void LoadHotkeySwitchSpec(string value)
    {
        EnsureHotkeySwitchPanel();
        var spec = HotkeySwitchPayload.Parse(value) ?? new HotkeySwitchPayload();
        ParseShortcut(spec.ShortcutA, ChkHkACtrl, ChkHkAShift, ChkHkAAlt, ChkHkAWin, CbHkAValue);
        ParseShortcut(spec.ShortcutB, ChkHkBCtrl, ChkHkBShift, ChkHkBAlt, ChkHkBWin, CbHkBValue);
    }

    private string SaveHotkeySwitchSpec()
    {
        var spec = new HotkeySwitchPayload
        {
            ShortcutA = BuildShortcut(ChkHkACtrl, ChkHkAShift, ChkHkAAlt, ChkHkAWin, CbHkAValue),
            ShortcutB = BuildShortcut(ChkHkBCtrl, ChkHkBShift, ChkHkBAlt, ChkHkBWin, CbHkBValue),
        };
        return spec.ToJson();
    }
}
