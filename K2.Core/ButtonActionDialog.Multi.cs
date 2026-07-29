using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Multi Action" panel — an ordered, editable chain of
/// sub-actions with a per-step delay, code-built the same way
/// <c>ButtonActionDialog.Profile.cs</c>'s dynamic target rows are (a <see cref="StackPanel"/>
/// of code-built rows, each carrying its own small record class via <c>Tag</c>).
///
/// Storage/execution: <see cref="ActionExecutor.RunMultiAction"/> already exists (used today
/// only by Base Camp import) and expects a JSON array of <see cref="ActionExecutor.MultiStep"/>.
/// Rows built here store the K2-native tag (e.g. <c>"url"</c>, <c>"keys"</c>) directly in
/// <c>FunctionType</c> rather than a Base Camp label — <see cref="ActionExecutor.MapSubAction"/>
/// was extended to pass those straight through, so this reuses the exact same execution path
/// as imported Multi Action data with no duplication.
/// </summary>
public partial class ButtonActionDialog
{
    private sealed class MultiStepRow
    {
        public required Border Container { get; init; }
        public required ComboBox CbType { get; init; }
        public required TextBox TxtValue { get; init; }
        public required ComboBox CbValue { get; init; }
        public required TextBox TxtDelay { get; init; }
    }

    /// <summary>The native K2 action tags a Multi Action step can be — the same set
    /// <see cref="ButtonActionEngine"/>'s <c>ExecuteSub</c> already dispatches.</summary>
    private static readonly (string Tag, string LocKey)[] MultiStepTypes =
    {
        ("url", "act_url"), ("exec", "act_exec"), ("folder", "act_folder"),
        ("browser", "act_browser"), ("oscmd", "act_oscmd"), ("media", "act_media"),
        ("mouse", "act_mouse"), ("keys", "act_keys"), ("text", "act_text"),
        ("profile", "act_profile"),
    };

    private static readonly Brush MultiStepBorderBrush = new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x3C));

    private void EnsureMultiPanel()
    {
        if (PnlMultiSteps.Children.Count == 0)
            AddMultiStepRow(DefaultMultiStep());
    }

    private static ActionExecutor.MultiStep DefaultMultiStep()
        => new() { FunctionType = "keys", FunctionValue = "", ActionDelay = 50 };

    private void LoadMultiSpec(string json)
    {
        PnlMultiSteps.Children.Clear();
        List<ActionExecutor.MultiStep>? steps = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                steps = JsonSerializer.Deserialize<List<ActionExecutor.MultiStep>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { steps = null; }
        }
        if (steps is null || steps.Count == 0)
        {
            AddMultiStepRow(DefaultMultiStep());
            return;
        }
        foreach (var s in steps) AddMultiStepRow(s);
    }

    private string SaveMultiSpec()
    {
        var steps = new List<ActionExecutor.MultiStep>();
        int id = 0;
        foreach (var child in PnlMultiSteps.Children.OfType<Border>())
        {
            if (child.Tag is not MultiStepRow row) continue;
            var typeTag = (row.CbType.SelectedItem as ComboBoxItem)?.Tag as string ?? "keys";
            string value = typeTag is "oscmd" or "media" or "mouse"
                ? (row.CbValue.SelectedItem as ComboBoxItem)?.Tag as string ?? ""
                : row.TxtValue.Text?.Trim() ?? "";
            int delay = int.TryParse(row.TxtDelay.Text?.Trim(), out var d) ? Math.Max(d, 0) : 50;
            steps.Add(new ActionExecutor.MultiStep
            {
                Id = id++,
                FunctionType = typeTag,
                FunctionValue = value,
                ActionDelay = delay,
            });
        }
        return JsonSerializer.Serialize(steps);
    }

    private void BtnMultiAddStep_Click(object sender, RoutedEventArgs e) => AddMultiStepRow(DefaultMultiStep());

    private void AddMultiStepRow(ActionExecutor.MultiStep step)
    {
        var outer = new Border
        {
            BorderBrush = MultiStepBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var stack = new StackPanel();

        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cbType = new ComboBox { Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var (t, locKey) in MultiStepTypes)
            cbType.Items.Add(new ComboBoxItem { Content = Loc.Get(locKey), Tag = t });

        var txtDelay = new TextBox
        {
            Width = 56,
            Text = step.ActionDelay > 0 ? step.ActionDelay.ToString() : "50",
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = Loc.Get("multi_delay_ms"),
        };

        var btnUp     = new Button { Content = "▲", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0) };
        var btnDown   = new Button { Content = "▼", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0) };
        var btnRemove = new Button { Content = "✕", Padding = new Thickness(6, 2, 6, 2) };

        Grid.SetColumn(cbType, 0);
        Grid.SetColumn(txtDelay, 1);
        Grid.SetColumn(btnUp, 2);
        Grid.SetColumn(btnDown, 3);
        Grid.SetColumn(btnRemove, 4);
        headerGrid.Children.Add(cbType);
        headerGrid.Children.Add(txtDelay);
        headerGrid.Children.Add(btnUp);
        headerGrid.Children.Add(btnDown);
        headerGrid.Children.Add(btnRemove);

        var txtValue = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        var cbValue  = new ComboBox { VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

        stack.Children.Add(headerGrid);
        stack.Children.Add(txtValue);
        stack.Children.Add(cbValue);
        outer.Child = stack;

        var row = new MultiStepRow { Container = outer, CbType = cbType, TxtValue = txtValue, CbValue = cbValue, TxtDelay = txtDelay };
        outer.Tag = row;

        cbType.SelectionChanged += (_, _) => UpdateMultiRowValueControl(row);
        btnUp.Click     += (_, _) => MoveMultiStepRow(outer, -1);
        btnDown.Click   += (_, _) => MoveMultiStepRow(outer, 1);
        btnRemove.Click += (_, _) =>
        {
            PnlMultiSteps.Children.Remove(outer);
            if (PnlMultiSteps.Children.Count == 0) AddMultiStepRow(DefaultMultiStep());
        };

        var typeTagToSelect = (step.FunctionType ?? "keys").ToLowerInvariant();
        var matchType = cbType.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == typeTagToSelect);
        cbType.SelectedItem = matchType ?? cbType.Items[0];

        if (typeTagToSelect is "oscmd" or "media" or "mouse")
        {
            var match = cbValue.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string?)i.Tag, step.FunctionValue, StringComparison.OrdinalIgnoreCase));
            cbValue.SelectedItem = match ?? (cbValue.Items.Count > 0 ? cbValue.Items[0] : null);
        }
        else
        {
            txtValue.Text = step.FunctionValue ?? "";
        }

        PnlMultiSteps.Children.Add(outer);
    }

    private void UpdateMultiRowValueControl(MultiStepRow row)
    {
        var tag = (row.CbType.SelectedItem as ComboBoxItem)?.Tag as string ?? "keys";
        bool isCombo = tag is "oscmd" or "media" or "mouse";
        row.TxtValue.Visibility = isCombo ? Visibility.Collapsed : Visibility.Visible;
        row.CbValue.Visibility  = isCombo ? Visibility.Visible : Visibility.Collapsed;
        if (isCombo)
        {
            row.CbValue.Items.Clear();
            foreach (var opt in OptionsFor(tag))
                row.CbValue.Items.Add(new ComboBoxItem { Content = Loc.Get(opt.LocKey), Tag = opt.Value });
            if (row.CbValue.Items.Count > 0) row.CbValue.SelectedIndex = 0;
        }
    }

    private void MoveMultiStepRow(Border row, int direction)
    {
        int index = PnlMultiSteps.Children.IndexOf(row);
        int newIndex = index + direction;
        if (newIndex < 0 || newIndex >= PnlMultiSteps.Children.Count) return;
        PnlMultiSteps.Children.RemoveAt(index);
        PnlMultiSteps.Children.Insert(newIndex, row);
    }
}
