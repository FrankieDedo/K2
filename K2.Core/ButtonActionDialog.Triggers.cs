using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Long press" and "Modifiers" options of a key — see
/// <see cref="TriggerPayload"/>. The main dialog keeps editing the BASE action; the extras are
/// edited in a small window of their own (long press: <see cref="TriggerOptionsWindow"/> below;
/// modifiers: the shared <see cref="ModifiersWindow"/>, also opened from the DisplayPad key
/// popup together with the per-modifier pictures, <see cref="ModifierImages"/>) and kept in
/// <see cref="_trigger"/> until Save wraps them around the base action. Each extra action is
/// picked with a nested <see cref="ButtonActionDialog"/> (with <c>allowTriggers: false</c>, so
/// extras never nest).
/// </summary>
public partial class ButtonActionDialog
{
    private readonly bool _allowTriggers;
    private TriggerPayload? _trigger;

    /// <summary>DisplayPad key popup only: the key's per-modifier pictures, so the "Modifiers"
    /// popup opened from here can edit them alongside the modifier actions. Set before
    /// ShowDialog; after a Save it holds the edited pictures (the caller writes them back).</summary>
    public ModifierImageContext? ModifierImages { get; set; }

    private void UpdateTriggerButton(string tag)
    {
        if (BtnTriggers is null || BtnModifiers is null) return;
        bool can = _allowTriggers && TriggerPayload.CanWrap(tag);
        BtnTriggers.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        BtnTriggers.Content = Loc.Get("trig_button")
            + (_trigger is { Hold: not TriggerPayload.HoldMode.None } ? "  ●" : "");
        // Pictures can be set even on a key whose action can't take modifier actions.
        BtnModifiers.Visibility = can || (_allowTriggers && ModifierImages is not null) ? Visibility.Visible : Visibility.Collapsed;
        bool modsSet = _trigger?.Mods.Count > 0 || ModifierImages?.Images.Count > 0;
        BtnModifiers.Content = Loc.Get("dp_mod_icons") + (modsSet ? "  ●" : "");
    }

    private void BtnModifiers_Click(object sender, RoutedEventArgs e)
    {
        string tag = CurrentTag();
        var work = TriggerPayload.CanWrap(tag)
            ? TriggerPayload.Parse((_trigger ?? new TriggerPayload()).Serialize()) ?? new TriggerPayload()
            : null;
        var images = ModifierImages?.Clone();
        var win = new ModifiersWindow(work, images, _host) { Owner = this };
        if (win.ShowDialog() != true) return;
        if (work is not null) _trigger = work;
        if (images is not null) ModifierImages = images;
        UpdateTriggerButton(tag);
    }

    private void BtnTriggers_Click(object sender, RoutedEventArgs e)
    {
        var work = TriggerPayload.Parse((_trigger ?? new TriggerPayload()).Serialize()) ?? new TriggerPayload();
        var win = new TriggerOptionsWindow(work, _host) { Owner = this };
        if (win.ShowDialog() != true) return;
        _trigger = work;
        UpdateTriggerButton(CurrentTag());
    }

    /// <summary>The options window. Edits the payload it is given in place; the caller copies it
    /// first so Cancel leaves the key untouched.</summary>
    private sealed class TriggerOptionsWindow : Window
    {
        private readonly TriggerPayload _p;
        private readonly IActionHost? _host;
        private readonly ComboBox _cbHold = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBox _txtHoldMs = new() { Width = 80, Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBox _txtRepeatMs = new() { Width = 80, Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly StackPanel _pnlLong = new();
        private readonly StackPanel _pnlRepeat = new();
        private readonly StackPanel _pnlHoldMs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        private Button _btnLong = null!;

        public TriggerOptionsWindow(TriggerPayload p, IActionHost? host)
        {
            _p = p;
            _host = host;
            Title = Loc.Get("trig_title");
            Width = 520; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            if (TryFindResource("K2WindowStyle") is Style st) Style = st;
            Content = Build();
            Refresh();
        }

        private UIElement Build()
        {
            var root = new StackPanel { Margin = new Thickness(14) };
            bool holdOk = _host?.SupportsHoldTriggers != false;

            // ---- hold behaviour ----
            root.Children.Add(Header(Loc.Get("trig_hold_label")));
            foreach (var (mode, key) in new[] { (TriggerPayload.HoldMode.None, "trig_hold_none"),
                                                 (TriggerPayload.HoldMode.Long, "trig_hold_long"),
                                                 (TriggerPayload.HoldMode.Repeat, "trig_hold_repeat") })
                _cbHold.Items.Add(new ComboBoxItem { Content = Loc.Get(key), Tag = mode });
            _cbHold.SelectedIndex = (int)_p.Hold;
            _cbHold.IsEnabled = holdOk;
            _cbHold.SelectionChanged += (_, _) =>
            {
                _p.Hold = (TriggerPayload.HoldMode)((ComboBoxItem)_cbHold.SelectedItem).Tag;
                Refresh();
            };
            root.Children.Add(_cbHold);
            if (!holdOk) root.Children.Add(Note(Loc.Get("trig_hold_unsupported")));

            _txtHoldMs.Text = _p.HoldMs.ToString();
            _txtHoldMs.TextChanged += (_, _) => { if (int.TryParse(_txtHoldMs.Text, out int v)) _p.HoldMs = Math.Clamp(v, 100, 5000); };
            _pnlHoldMs.Children.Add(new TextBlock { Text = Loc.Get("trig_hold_ms"), Width = 200, VerticalAlignment = VerticalAlignment.Center });
            _pnlHoldMs.Children.Add(_txtHoldMs);
            root.Children.Add(_pnlHoldMs);

            _btnLong = ActionButton(() => (_p.LongType, _p.LongValue), (t, v) => { _p.LongType = t; _p.LongValue = v; });
            var longRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            longRow.Children.Add(new TextBlock { Text = Loc.Get("trig_long_action"), Width = 200, VerticalAlignment = VerticalAlignment.Center });
            longRow.Children.Add(_btnLong);
            _pnlLong.Children.Add(longRow);
            root.Children.Add(_pnlLong);

            _txtRepeatMs.Text = _p.RepeatMs.ToString();
            _txtRepeatMs.TextChanged += (_, _) => { if (int.TryParse(_txtRepeatMs.Text, out int v)) _p.RepeatMs = Math.Clamp(v, 20, 5000); };
            var repRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            repRow.Children.Add(new TextBlock { Text = Loc.Get("trig_repeat_ms"), Width = 200, VerticalAlignment = VerticalAlignment.Center });
            repRow.Children.Add(_txtRepeatMs);
            _pnlRepeat.Children.Add(repRow);
            root.Children.Add(_pnlRepeat);

            // ---- footer ----
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = Loc.Get("cancel"), Width = 90, Padding = new Thickness(6, 2, 6, 2), IsCancel = true };
            var ok = new Button { Content = Loc.Get("save"), Width = 90, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
            ok.Click += (_, _) => { DialogResult = true; Close(); };
            footer.Children.Add(cancel);
            footer.Children.Add(ok);
            root.Children.Add(footer);
            return root;
        }

        private void Refresh()
        {
            _pnlHoldMs.Visibility = _p.Hold == TriggerPayload.HoldMode.None ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)_pnlHoldMs.Children[0]).Text = Loc.Get(_p.Hold == TriggerPayload.HoldMode.Repeat ? "trig_repeat_delay" : "trig_hold_ms");
            _pnlLong.Visibility = _p.Hold == TriggerPayload.HoldMode.Long ? Visibility.Visible : Visibility.Collapsed;
            _pnlRepeat.Visibility = _p.Hold == TriggerPayload.HoldMode.Repeat ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>A "pick an action" button that shows the current choice and opens a nested
        /// action dialog; right-click clears it.</summary>
        private Button ActionButton(Func<(string? Type, string? Value)> get, Action<string?, string?> set)
        {
            var btn = new Button { Width = 260, Height = 28, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 0, 8, 0) };
            void Show()
            {
                var (t, v) = get();
                btn.Content = string.IsNullOrEmpty(t) || t == "none" ? Loc.Get("trig_choose") : ActionTypeHelper.ListSummary(t, v);
            }
            btn.Click += (_, _) =>
            {
                var (t, v) = get();
                var dlg = new ButtonActionDialog(0, t, v, _host, allowTriggers: false) { Owner = this };
                dlg.LblHeader.Text = Loc.Get("trig_title");
                if (dlg.ShowDialog() != true) return;
                // Live tiles and the like are handled by the DisplayPad before the engine, which is
                // what runs these: they can't be a long-press or modifier action. Opening a page or
                // an action wheel can — the DisplayPad host runs those for the engine
                // (IActionHost.TryRunHostAction).
                bool pageAction = _host?.SupportsPages == true && dlg.ActionType is "dp_folder" or "dp_wheel";
                if (dlg.ActionType != "none" && !TriggerPayload.CanWrap(dlg.ActionType) && !pageAction)
                {
                    MessageBox.Show(this, Loc.Get("trig_type_unsupported"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                set(dlg.ActionType, dlg.ActionValue);
                Show();
            };
            btn.ToolTip = Loc.Get("trig_clear_hint");
            btn.MouseRightButtonUp += (_, _) => { set(null, null); Show(); };
            Show();
            return btn;
        }

        private static TextBlock Header(string text, double top = 0) => new()
        {
            Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6),
        };

        private static TextBlock Note(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
        };
    }
}
