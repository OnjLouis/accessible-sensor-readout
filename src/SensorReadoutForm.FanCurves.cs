using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void ShowFanCurvesDialog()
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Fan Curves", "Fan Curves");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(980, 520);
            dialog.MinimumSize = new Size(800, 420);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var curves = CloneFanCurveSettings(settings.FanCurves);
            var labels = LoadFanLabels();
            var hiddenFanControlKeys = HiddenFanControlKeys();
            var controls = latestRows
                .Where(r => r.Type == "Fan Control")
                .Select(c => EnrichFanControlRow(c, labels))
                .Where(c => ShouldShowFanControl(c))
                .Where(c => !hiddenFanControlKeys.Contains(c.Identifier))
                .OrderBy(r => ControlSortKey(r.Identifier))
                .ToList();
            var temperatures = latestRows
                .Where(r => r.Type == "Temperature" && r.Value.HasValue)
                .OrderBy(r => r.Hardware)
                .ThenBy(r => r.Name)
                .ToList();

            var curveList = new ListBox { Dock = DockStyle.Fill };
            var enabledCheckBox = new CheckBox { Text = T("ui.&Enabled", "&Enabled"), AutoSize = true };
            var nameBox = new TextBox { Dock = DockStyle.Fill };
            var fanBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            var tempBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            var lowTempBox = CreateTemperatureBox(35);
            var lowPercentBox = CreatePercentBox(30);
            var highTempBox = CreateTemperatureBox(75);
            var highPercentBox = CreatePercentBox(100);
            var emergencyTempBox = CreateTemperatureBox(85);
            var emergencyPercentBox = CreatePercentBox(100);
            var minChangeBox = CreatePercentBox(2);
            lowTempBox.AccessibleName = T("a11y.Low temperature Celsius", "Low temperature, Celsius");
            lowPercentBox.AccessibleName = T("a11y.Low fan percent", "Low fan percent");
            highTempBox.AccessibleName = T("a11y.High temperature Celsius", "High temperature, Celsius");
            highPercentBox.AccessibleName = T("a11y.High fan percent", "High fan percent");
            emergencyTempBox.AccessibleName = T("a11y.Emergency temperature Celsius", "Emergency temperature, Celsius");
            emergencyPercentBox.AccessibleName = T("a11y.Emergency fan percent", "Emergency fan percent");
            minChangeBox.AccessibleName = T("a11y.Minimum fan percent change", "Minimum fan percent change");
            var status = new Label { Dock = DockStyle.Fill, AutoSize = true };
            var loading = false;

            foreach (var control in controls)
            {
                fanBox.Items.Add(control);
            }
            foreach (var temperature in temperatures)
            {
                tempBox.Items.Add(temperature);
            }

            Action refreshList = delegate
            {
                var selected = curveList.SelectedItem as FanCurveSetting;
                curveList.BeginUpdate();
                curveList.Items.Clear();
                foreach (var curve in curves)
                {
                    curveList.Items.Add(curve);
                }
                if (curveList.Items.Count > 0)
                {
                    var index = selected == null ? -1 : curves.IndexOf(selected);
                    curveList.SelectedIndex = index >= 0 ? index : 0;
                }
                curveList.EndUpdate();
            };

            Action<FanCurveSetting> loadCurve = delegate(FanCurveSetting curve)
            {
                loading = true;
                try
                {
                    enabledCheckBox.Checked = curve != null && curve.Enabled;
                    nameBox.Text = curve == null ? "" : curve.Name ?? "";
                    SelectComboByIdentifier(fanBox, curve == null ? "" : curve.FanControlKey);
                    SelectComboByIdentifier(tempBox, curve == null ? "" : curve.TemperatureReadingKey);
                    lowTempBox.Value = SafeDecimal(curve == null ? 35 : curve.LowTemperatureC, lowTempBox.Minimum, lowTempBox.Maximum);
                    lowPercentBox.Value = SafeDecimal(curve == null ? 30 : curve.LowPercent, lowPercentBox.Minimum, lowPercentBox.Maximum);
                    highTempBox.Value = SafeDecimal(curve == null ? 75 : curve.HighTemperatureC, highTempBox.Minimum, highTempBox.Maximum);
                    highPercentBox.Value = SafeDecimal(curve == null ? 100 : curve.HighPercent, highPercentBox.Minimum, highPercentBox.Maximum);
                    emergencyTempBox.Value = SafeDecimal(curve == null ? 85 : curve.EmergencyTemperatureC, emergencyTempBox.Minimum, emergencyTempBox.Maximum);
                    emergencyPercentBox.Value = SafeDecimal(curve == null ? 100 : curve.EmergencyPercent, emergencyPercentBox.Minimum, emergencyPercentBox.Maximum);
                    minChangeBox.Value = SafeDecimal(curve == null ? 2 : curve.MinimumChangePercent, minChangeBox.Minimum, minChangeBox.Maximum);
                }
                finally
                {
                    loading = false;
                }
            };

            Action saveSelected = delegate
            {
                if (loading)
                {
                    return;
                }
                var curve = curveList.SelectedItem as FanCurveSetting;
                if (curve == null)
                {
                    return;
                }

                curve.Enabled = enabledCheckBox.Checked;
                curve.Name = nameBox.Text.Trim();
                curve.FanControlKey = SelectedSensorIdentifier(fanBox);
                curve.TemperatureReadingKey = SelectedSensorIdentifier(tempBox);
                curve.LowTemperatureC = Convert.ToDouble(lowTempBox.Value);
                curve.LowPercent = Convert.ToInt32(lowPercentBox.Value);
                curve.HighTemperatureC = Convert.ToDouble(highTempBox.Value);
                curve.HighPercent = Convert.ToInt32(highPercentBox.Value);
                curve.EmergencyTemperatureC = Convert.ToDouble(emergencyTempBox.Value);
                curve.EmergencyPercent = Convert.ToInt32(emergencyPercentBox.Value);
                curve.MinimumChangePercent = Convert.ToInt32(minChangeBox.Value);
                NormalizeFanCurve(curve);
                refreshList();
                curveList.SelectedItem = curve;
            };

            curveList.SelectedIndexChanged += delegate { loadCurve(curveList.SelectedItem as FanCurveSetting); };
            enabledCheckBox.CheckedChanged += delegate { saveSelected(); };
            nameBox.TextChanged += delegate { saveSelected(); };
            fanBox.SelectedIndexChanged += delegate { saveSelected(); };
            tempBox.SelectedIndexChanged += delegate { saveSelected(); };
            lowTempBox.ValueChanged += delegate { saveSelected(); };
            lowPercentBox.ValueChanged += delegate { saveSelected(); };
            highTempBox.ValueChanged += delegate { saveSelected(); };
            highPercentBox.ValueChanged += delegate { saveSelected(); };
            emergencyTempBox.ValueChanged += delegate { saveSelected(); };
            emergencyPercentBox.ValueChanged += delegate { saveSelected(); };
            minChangeBox.ValueChanged += delegate { saveSelected(); };

            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Delete && curveList.Focused)
                {
                    RemoveSelectedFanCurve(curves, curveList, refreshList);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F2 && curveList.Focused)
                {
                    nameBox.Focus();
                    nameBox.SelectAll();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(10) };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(curveList, 0, 0);
            var leftButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var newButton = new Button { Text = T("ui.&New...", "&New..."), AutoSize = true };
            var removeButton = new Button { Text = T("ui.&Remove", "&Remove"), AutoSize = true };
            newButton.Click += delegate
            {
                var curve = CreateDefaultFanCurve(controls, temperatures);
                curves.Add(curve);
                refreshList();
                curveList.SelectedItem = curve;
                nameBox.Focus();
                nameBox.SelectAll();
            };
            removeButton.Click += delegate { RemoveSelectedFanCurve(curves, curveList, refreshList); };
            leftButtons.Controls.Add(newButton);
            leftButtons.Controls.Add(removeButton);
            left.Controls.Add(leftButtons, 0, 1);
            outer.Controls.Add(left, 0, 0);

            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 12 };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 12; i++)
            {
                editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            AddLabeledControl(editor, 0, T("ui.Name:", "Name:"), nameBox);
            AddLabeledControl(editor, 1, T("ui.Fan:", "Fan:"), fanBox);
            AddLabeledControl(editor, 2, T("ui.Temperature:", "Temperature:"), tempBox);
            editor.Controls.Add(enabledCheckBox, 1, 3);
            AddLabeledControl(editor, 4, T("ui.Low point:", "Low point:"), PairControls(lowTempBox, LabelText(T("ui.Celsius unit label", " Celsius, fan ")), lowPercentBox, LabelText(T("ui.Percent unit label", " percent"))));
            AddLabeledControl(editor, 5, T("ui.High point:", "High point:"), PairControls(highTempBox, LabelText(T("ui.Celsius unit label", " Celsius, fan ")), highPercentBox, LabelText(T("ui.Percent unit label", " percent"))));
            AddLabeledControl(editor, 6, T("ui.Emergency:", "Emergency:"), PairControls(emergencyTempBox, LabelText(T("ui.Celsius unit label", " Celsius, fan ")), emergencyPercentBox, LabelText(T("ui.Percent unit label", " percent"))));
            AddLabeledControl(editor, 7, T("ui.Minimum change:", "Minimum change:"), PairControls(minChangeBox, LabelText(T("ui.Percent unit label", " percent"))));
            editor.Controls.Add(status, 1, 8);
            outer.Controls.Add(editor, 1, 0);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var closeButton = new Button { Text = T("ui.Close", "Close"), DialogResult = DialogResult.OK, AutoSize = true };
            var applyButton = new Button { Text = T("ui.&Apply now", "&Apply now"), AutoSize = true };
            applyButton.Click += delegate
            {
                saveSelected();
                settings.FanCurves = CloneFanCurveSettings(curves);
                SaveSettings(settings);
                ApplyFanCurvesAsync(latestRows);
                status.Text = T("status.fanCurvesApplied", "Fan curves applied.");
            };
            bottom.Controls.Add(closeButton);
            bottom.Controls.Add(applyButton);
            outer.SetColumnSpan(bottom, 2);
            outer.Controls.Add(bottom, 0, 1);

            dialog.Controls.Add(outer);
            dialog.AcceptButton = closeButton;
            dialog.CancelButton = closeButton;
            refreshList();
            if (curves.Count == 0)
            {
                status.Text = controls.Count == 0
                    ? T("status.noFanControls", "No fan controls are available yet.")
                    : T("status.noFanCurves", "No fan curves configured.");
            }
            dialog.ShowDialog(this);
            saveSelected();
            settings.FanCurves = CloneFanCurveSettings(curves);
            SaveSettings(settings);
        }
    }

    private void ApplyFanCurvesAsync(List<SensorRow> rows)
    {
        if (refreshInProgress)
        {
            return;
        }

        var curves = CloneFanCurveSettings(settings.FanCurves)
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.FanControlKey) && !string.IsNullOrWhiteSpace(c.TemperatureReadingKey))
            .ToList();
        if (curves.Count == 0)
        {
            return;
        }

        var byIdentifier = (rows ?? new List<SensorRow>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Identifier))
            .GroupBy(r => r.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var actions = new List<KeyValuePair<string, int>>();
        foreach (var curve in curves)
        {
            SensorRow temperature;
            SensorRow control;
            if (!byIdentifier.TryGetValue(curve.TemperatureReadingKey, out temperature) ||
                !temperature.Value.HasValue ||
                !byIdentifier.TryGetValue(curve.FanControlKey, out control))
            {
                continue;
            }

            var percent = CalculateFanCurvePercent(curve, temperature.Value.Value);
            int lastPercent;
            if (lastAppliedFanCurvePercents.TryGetValue(curve.FanControlKey, out lastPercent) &&
                Math.Abs(percent - lastPercent) < curve.MinimumChangePercent)
            {
                continue;
            }

            DateTime lastAppliedUtc;
            if (lastAppliedFanCurveUtc.TryGetValue(curve.FanControlKey, out lastAppliedUtc) &&
                DateTime.UtcNow - lastAppliedUtc < TimeSpan.FromSeconds(10))
            {
                continue;
            }

            actions.Add(new KeyValuePair<string, int>(curve.FanControlKey, percent));
            lastAppliedFanCurvePercents[curve.FanControlKey] = percent;
            lastAppliedFanCurveUtc[curve.FanControlKey] = DateTime.UtcNow;
        }

        if (actions.Count == 0)
        {
            return;
        }

        Task.Factory.StartNew(delegate
        {
            foreach (var action in actions)
            {
                try
                {
                    SetLibreHardwareMonitorControl(action.Key, action.Value, true);
                    LogMessage("Debug", "Fan curve set " + action.Key + " to " + action.Value + "%.");
                }
                catch (Exception ex)
                {
                    LogError("Fan curve could not set " + action.Key + ": " + ex.Message);
                }
            }
        });
    }

    private static int CalculateFanCurvePercent(FanCurveSetting curve, double temperatureC)
    {
        if (temperatureC >= curve.EmergencyTemperatureC)
        {
            return Math.Max(0, Math.Min(100, curve.EmergencyPercent));
        }
        if (temperatureC <= curve.LowTemperatureC)
        {
            return Math.Max(0, Math.Min(100, curve.LowPercent));
        }
        if (temperatureC >= curve.HighTemperatureC)
        {
            return Math.Max(0, Math.Min(100, curve.HighPercent));
        }

        var fraction = (temperatureC - curve.LowTemperatureC) / (curve.HighTemperatureC - curve.LowTemperatureC);
        return Math.Max(0, Math.Min(100, (int)Math.Round(curve.LowPercent + ((curve.HighPercent - curve.LowPercent) * fraction))));
    }

    private int DisableFanCurvesForControls(IEnumerable<string> fanControlKeys)
    {
        var keys = new HashSet<string>(
            (fanControlKeys ?? Enumerable.Empty<string>())
                .Select(IdentifierFromSettingsKey)
                .Where(k => !string.IsNullOrWhiteSpace(k)),
            StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0 || settings.FanCurves == null || settings.FanCurves.Count == 0)
        {
            return 0;
        }

        var disabled = 0;
        foreach (var curve in settings.FanCurves)
        {
            if (curve == null || !curve.Enabled || !keys.Contains(IdentifierFromSettingsKey(curve.FanControlKey)))
            {
                continue;
            }

            curve.Enabled = false;
            disabled++;
            LogMessage("Normal", "Disabled fan curve \"" + (string.IsNullOrWhiteSpace(curve.Name) ? "Fan curve" : curve.Name.Trim()) + "\" because a manual fan action targeted the same fan control.");
        }

        if (disabled > 0)
        {
            SaveSettings(settings);
        }

        return disabled;
    }

    private static string FanCurveDisabledSuffix(int disabledCurveCount)
    {
        if (disabledCurveCount <= 0)
        {
            return "";
        }

        return " Disabled " + disabledCurveCount + " fan curve" + (disabledCurveCount == 1 ? "" : "s") + " for the same fan control" + (disabledCurveCount == 1 ? "" : "s") + ".";
    }

    private static List<FanCurveSetting> CloneFanCurveSettings(IEnumerable<FanCurveSetting> curves)
    {
        return (curves ?? new List<FanCurveSetting>())
            .Where(c => c != null)
            .Select(c =>
            {
                var clone = new FanCurveSetting
                {
                    Name = c.Name ?? "",
                    FanControlKey = c.FanControlKey ?? "",
                    TemperatureReadingKey = c.TemperatureReadingKey ?? "",
                    Enabled = c.Enabled,
                    LowTemperatureC = c.LowTemperatureC,
                    LowPercent = c.LowPercent,
                    HighTemperatureC = c.HighTemperatureC,
                    HighPercent = c.HighPercent,
                    EmergencyTemperatureC = c.EmergencyTemperatureC,
                    EmergencyPercent = c.EmergencyPercent,
                    MinimumChangePercent = c.MinimumChangePercent
                };
                NormalizeFanCurve(clone);
                return clone;
            })
            .ToList();
    }

    private static void NormalizeFanCurve(FanCurveSetting curve)
    {
        if (curve == null)
        {
            return;
        }

        curve.Name = curve.Name ?? "";
        curve.FanControlKey = curve.FanControlKey ?? "";
        curve.TemperatureReadingKey = curve.TemperatureReadingKey ?? "";
        curve.LowTemperatureC = Math.Max(-100, Math.Min(150, curve.LowTemperatureC));
        curve.HighTemperatureC = Math.Max(-100, Math.Min(150, curve.HighTemperatureC));
        if (curve.HighTemperatureC <= curve.LowTemperatureC)
        {
            curve.HighTemperatureC = curve.LowTemperatureC + 1;
        }
        curve.EmergencyTemperatureC = Math.Max(curve.HighTemperatureC, Math.Min(150, curve.EmergencyTemperatureC));
        curve.LowPercent = Math.Max(0, Math.Min(100, curve.LowPercent));
        curve.HighPercent = Math.Max(0, Math.Min(100, curve.HighPercent));
        curve.EmergencyPercent = Math.Max(0, Math.Min(100, curve.EmergencyPercent));
        curve.MinimumChangePercent = Math.Max(0, Math.Min(25, curve.MinimumChangePercent));
    }

    private static FanCurveSetting CreateDefaultFanCurve(List<SensorRow> controls, List<SensorRow> temperatures)
    {
        var control = controls == null ? null : controls.FirstOrDefault();
        var temperature = temperatures == null ? null : temperatures.FirstOrDefault();
        return new FanCurveSetting
        {
            Name = "Fan curve",
            FanControlKey = control == null ? "" : control.Identifier,
            TemperatureReadingKey = temperature == null ? "" : temperature.Identifier,
            Enabled = true
        };
    }

    private HashSet<string> HiddenFanControlKeys()
    {
        var hidden = new HashSet<string>(settings.HiddenReadingKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in latestRows.Where(r => r.Type == "Fan"))
        {
            if (!hidden.Contains("row|" + RowSettingsKey(row)))
            {
                continue;
            }

            var controlIdentifier = GuessControlIdentifier(row.Identifier);
            if (!string.IsNullOrWhiteSpace(controlIdentifier))
            {
                keys.Add(controlIdentifier);
            }
        }

        foreach (var row in latestRows.Where(r => r.Type == "Fan Control"))
        {
            if (hidden.Contains("row|" + RowSettingsKey(row)))
            {
                keys.Add(row.Identifier);
            }
        }

        return keys;
    }

    private static NumericUpDown CreateTemperatureBox(decimal value)
    {
        return new NumericUpDown
        {
            Minimum = -100,
            Maximum = 150,
            DecimalPlaces = 1,
            Increment = 1,
            Value = value,
            Width = 80
        };
    }

    private static NumericUpDown CreatePercentBox(decimal value)
    {
        return new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            Value = value,
            Width = 70
        };
    }

    private static decimal SafeDecimal(double value, decimal minimum, decimal maximum)
    {
        var decimalValue = Convert.ToDecimal(value);
        if (decimalValue < minimum) return minimum;
        if (decimalValue > maximum) return maximum;
        return decimalValue;
    }

    private static void SelectComboByIdentifier(ComboBox comboBox, string identifier)
    {
        if (comboBox == null)
        {
            return;
        }
        comboBox.SelectedIndex = -1;
        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            var row = comboBox.Items[i] as SensorRow;
            if (row != null && string.Equals(row.Identifier, identifier, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }
        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static string SelectedSensorIdentifier(ComboBox comboBox)
    {
        var row = comboBox == null ? null : comboBox.SelectedItem as SensorRow;
        return row == null ? "" : row.Identifier;
    }

    private static void RemoveSelectedFanCurve(List<FanCurveSetting> curves, ListBox curveList, Action refreshList)
    {
        var curve = curveList == null ? null : curveList.SelectedItem as FanCurveSetting;
        if (curve == null || curves == null)
        {
            return;
        }
        curves.Remove(curve);
        if (refreshList != null)
        {
            refreshList();
        }
    }

    private static void AddLabeledControl(TableLayoutPanel table, int row, string labelText, Control control)
    {
        table.Controls.Add(new Label { Text = labelText, AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static Label LabelText(string text)
    {
        return new Label { Text = text, AutoSize = true, Padding = new Padding(2, 6, 2, 0) };
    }

    private static FlowLayoutPanel PairControls(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        foreach (var control in controls)
        {
            panel.Controls.Add(control);
        }
        return panel;
    }
}
