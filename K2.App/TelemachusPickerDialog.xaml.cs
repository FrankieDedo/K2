using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>One Telemachus entry as the picker lists it. Public: XAML bindings reach it.</summary>
public sealed class TelemachusPickRow : INotifyPropertyChanged
{
    public string Api { get; init; } = "";
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";

    /// <summary>What the game answers for it right now, or empty while it has not said.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    private string _value = "";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Picks the Telemachus entry a game-studio reading shows — the studio's counterpart of the key's
/// action browser for Kerbal Space Program. Lists what the mod can ANSWER (commands are left out:
/// a reading reads), K2's curated entries first under their translated names, then everything the
/// mod's own API listing adds (<see cref="TelemachusApi"/>), grouped by family.
///
/// <para>While the window is open the listed entries are asked for, so each row shows what the game
/// says for it right now — the fastest way to tell "Surface Speed" from "Surface Velocity" is to
/// see which one moves.</para>
/// </summary>
public partial class TelemachusPickerDialog : Window
{
    /// <summary>The entry chosen. Only meaningful when ShowDialog returned true.</summary>
    public string SelectedApi { get; private set; } = "";

    private readonly string _initial;
    private List<TelemachusPickRow> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TelemachusPickerDialog(string? currentApi)
    {
        InitializeComponent();
        _initial = (currentApi ?? "").Trim();

        Fill();
        _timer.Tick += (_, _) => UpdateValues();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        UpdateValues();
        TxtSearch.Focus();
    }

    private void Fill()
    {
        string keep = (LstEntries.SelectedItem as TelemachusPickRow)?.Api ?? _initial;

        var rows = new List<TelemachusPickRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in KspTelemachus.AllItems())
        {
            // A command key ("SAS") reads through another entry ("v.sasValue"): that entry, under
            // the key's name, is what a reading can use.
            if (item.Read is not { Length: > 0 } api || !seen.Add(api)) continue;
            rows.Add(new TelemachusPickRow { Api = api, Name = Loc.Get(item.LocKey), Group = Loc.Get(item.GroupLocKey) });
        }

        // The current value stays listed even if neither list knows it — a hand-typed entry, or
        // one a newer Telemachus dropped — so opening the picker never loses what was there.
        if (_initial.Length > 0 && seen.Add(_initial))
            rows.Insert(0, new TelemachusPickRow { Api = _initial, Name = _initial, Group = Loc.Get("tmpick_current") });

        _rows = rows;
        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TelemachusPickRow.Group)));
        view.Filter = o => Matches((TelemachusPickRow)o, TxtSearch.Text.Trim());
        LstEntries.ItemsSource = view;

        LstEntries.SelectedItem = _rows.FirstOrDefault(r => r.Api == keep);
        if (LstEntries.SelectedItem is { } sel) LstEntries.ScrollIntoView(sel);
        BtnOk.IsEnabled = LstEntries.SelectedItem is not null;
        UpdateStatus(null);
    }

    private static bool Matches(TelemachusPickRow r, string term) =>
        term.Length == 0 ||
        r.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
        r.Api.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        r.Group.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Asks the mod for the entries currently in view and paints what came back. The
    /// answer lands a poll later, so each tick shows the previous tick's request — invisible at 1 s.</summary>
    private void UpdateValues()
    {
        var visible = (LstEntries.ItemsSource as ICollectionView)?.Cast<TelemachusPickRow>().ToList() ?? new();
        var status = TelemachusClient.Want(visible.Select(r => r.Api));
        foreach (var row in visible)
            row.Value = status.Alive ? TelemachusClient.Text(status.Get(row.Api)) ?? "" : "";
        UpdateStatus(status);
    }

    private void UpdateStatus(TelemachusClient.Status? status)
    {
        int fromMod = KspTelemachus.ApiItems().Count;
        string list = fromMod > 0
            ? Loc.Get("tmpick_count_fmt", _rows.Count, fromMod)
            : Loc.Get("tmpick_count_curated_fmt", _rows.Count);
        string link = status is null ? ""
            : !status.Alive ? Loc.Get("tmpick_offline")
            : status.Paused != 0 ? Loc.Get("tmpick_no_antenna")
            : "";
        LblStatus.Text = link.Length > 0 ? list + " " + link : list;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        (LstEntries.ItemsSource as ICollectionView)?.Refresh();

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnRefresh.IsEnabled = false;
        LblStatus.Text = Loc.Get("tmpick_refreshing");
        int? count = await TelemachusApi.RefreshAsync();
        BtnRefresh.IsEnabled = true;
        if (count is null)
        {
            LblStatus.Text = Loc.Get("tmpick_refresh_failed");
            return;
        }
        Fill();
        UpdateValues();
    }

    private void LstEntries_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BtnOk.IsEnabled = LstEntries.SelectedItem is not null;

    private void LstEntries_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstEntries.SelectedItem is not null) BtnOk_Click(sender, e);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (LstEntries.SelectedItem is not TelemachusPickRow row) return;
        SelectedApi = row.Api;
        DialogResult = true;
    }
}
