using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void ShowFanControlsDialog()
    {
        EnsureFanControlControls();
        UpdateFanControlBox();

        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Fan Controls", "Fan Controls");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(760, 230);
            dialog.MinimumSize = new Size(620, 210);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F2 && fanLabelBox != null)
                {
                    fanLabelBox.Focus();
                    fanLabelBox.SelectAll();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 4; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(new Label { Text = T("ui.Fan:", "Fan:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
            layout.Controls.Add(fanControlBox, 1, 0);
            layout.Controls.Add(new Label { Text = T("ui.Label:", "Label:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);

            var labelPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            labelPanel.Controls.Add(fanLabelBox);
            var saveFanLabelButton = new Button { Text = T("ui.Save &label", "Save &label"), AutoSize = true, AccessibleName = T("a11y.Save label for selected fan control", "Save label for selected fan control") };
            saveFanLabelButton.Click += delegate { SaveSelectedFanLabel(); };
            labelPanel.Controls.Add(saveFanLabelButton);
            layout.Controls.Add(labelPanel, 1, 1);

            layout.Controls.Add(new Label { Text = T("ui.Manual percent:", "Manual percent:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
            var percentPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            percentPanel.Controls.Add(fanPercentBox);
            var applyFanButton = new Button { Text = T("ui.&Manual adjust", "&Manual adjust"), AutoSize = true, AccessibleName = T("a11y.Apply manual percentage to selected fan control", "Apply manual percentage to selected fan control") };
            applyFanButton.Click += delegate { ApplySelectedFanControl(true); };
            percentPanel.Controls.Add(applyFanButton);
            var autoSelectedFanButton = new Button { Text = T("ui.Selected &auto", "Selected &auto"), AutoSize = true, AccessibleName = T("a11y.Return selected fan control to automatic", "Return selected fan control to automatic") };
            autoSelectedFanButton.Click += delegate { ApplySelectedFanControl(false); };
            percentPanel.Controls.Add(autoSelectedFanButton);
            layout.Controls.Add(percentPanel, 1, 2);

            layout.Controls.Add(new Label { Text = T("ui.Profiles:", "Profiles:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
            var profilePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var autoFanButton = new Button { Text = T("ui.All fans &reset", "All fans &reset"), AutoSize = true, AccessibleName = T("a11y.Return all fan controls to automatic", "Return all fan controls to automatic") };
            autoFanButton.Click += delegate { ResetAllFanControls(); };
            var elevatedFanButton = new Button { Text = T("ui.All fans &75", "All fans &75"), AutoSize = true, AccessibleName = T("a11y.Set all visible fan controls to 75 percent", "Set all visible fan controls to 75 percent") };
            elevatedFanButton.Click += delegate { ApplyAllVisibleFanControls(75, "elevated"); };
            var maxFanButton = new Button { Text = T("ui.All fans ma&x", "All fans ma&x"), AutoSize = true, AccessibleName = T("a11y.Set all visible fan controls to 100 percent", "Set all visible fan controls to 100 percent") };
            maxFanButton.Click += delegate { ApplyAllVisibleFanControls(100, "max"); };
            showStoppedFansCheckBox.Text = T("ui.Show &stopped", "Show &stopped");
            showStoppedFansCheckBox.AccessibleName = T("a11y.Show stopped or unpopulated fan headers", "Show stopped or unpopulated fan headers");
            profilePanel.Controls.Add(autoFanButton);
            profilePanel.Controls.Add(elevatedFanButton);
            profilePanel.Controls.Add(maxFanButton);
            profilePanel.Controls.Add(showStoppedFansCheckBox);
            layout.Controls.Add(profilePanel, 1, 3);

            var closePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = new Button { Text = T("ui.Close", "Close"), DialogResult = DialogResult.OK, AutoSize = true };
            closePanel.Controls.Add(closeButton);
            layout.Controls.Add(closePanel, 1, 4);
            dialog.AcceptButton = closeButton;
            dialog.CancelButton = closeButton;
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }

        fanControlBox = null;
        fanLabelBox = null;
        fanPercentBox = null;
        showStoppedFansCheckBox = null;
    }

    private void EnsureFanControlControls()
    {
        if (fanControlBox != null)
        {
            return;
        }

        fanControlBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 480,
            AccessibleName = T("a11y.Fan control target", "Fan control target"),
            AccessibleDescription = T("a11y.Choose which fan control to adjust", "Choose which fan control to adjust")
        };
        fanControlBox.SelectedIndexChanged += delegate
        {
            if (updatingFanControlBox)
            {
                return;
            }

            var row = fanControlBox.SelectedItem as SensorRow;
            fanLabelBox.Text = row == null ? "" : GetFanLabel(row.Identifier, BaseFanControlName(row.Name));
        };

        fanLabelBox = new TextBox
        {
            Width = 260,
            AccessibleName = T("a11y.Fan label", "Fan label"),
            AccessibleDescription = T("a11y.Friendly name for the selected fan control", "Friendly name for the selected fan control")
        };

        fanPercentBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 5,
            Value = 50,
            Width = 70,
            AccessibleName = T("a11y.Fan manual percentage", "Fan manual percentage")
        };
        fanPercentBox.Enter += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Click += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Leave += delegate { ClampFanPercentBox(); };

        showStoppedFansCheckBox = new CheckBox
        {
            Text = T("ui.Show &stopped", "Show &stopped"),
            AutoSize = true,
            AccessibleName = T("a11y.Show stopped or unpopulated fan headers", "Show stopped or unpopulated fan headers")
        };
        showStoppedFansCheckBox.CheckedChanged += delegate { UpdateFanControlBox(); };
    }

    private void RefreshSensors()
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        statusLabel.Text = T("status.refreshingSensors", "Refreshing sensors...");

        Task.Factory.StartNew(new Func<List<SensorRow>>(CollectSensorRows))
            .ContinueWith(delegate(Task<List<SensorRow>> task)
            {
                try
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    if (task.IsFaulted)
                    {
                        var ex = task.Exception == null ? null : task.Exception.GetBaseException();
                        statusLabel.Text = ex == null ? "Sensor refresh failed." : ex.GetType().Name + ": " + ex.Message;
                        return;
                    }

                    var rows = task.Result;
                    latestRows.Clear();
                    latestRows.AddRange(rows);
                    TryApplySavedFanControlsOnStartupAsync(rows);
                    if (openPreferencesDialog != null && !openPreferencesDialog.IsDisposed)
                    {
                        openPreferencesDialog.UpdateSensorRows(rows);
                    }
                    if (!IsMinimizedOrHidden())
                    {
                        UpdateFanControlBox();
                        UpdateDeviceList();
                        UpdateReadingList();
                    }
                    UpdateTrayStatus();
                    CheckAlarms(rows);

                    if (rows.Count == 0)
                    {
                        statusLabel.Text = T("status.noSensorRows", "No sensor rows returned yet.");
                    }
                    else
                    {
                        var sources = string.Join(", ", rows.Select(r => r.Source).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToArray());
                        statusLabel.Text = BuildRefreshStatus(rows, sources);
                    }
                }
                finally
                {
                    refreshInProgress = false;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private List<SensorRow> CollectSensorRows()
    {
        var rows = GetLibreHardwareMonitorSensors()
            .Concat(GetCoreTempRows())
            .Concat(GetWindowsSmartRows())
            .ToList();

        rows.AddRange(GetSystemPerformanceRows());
        rows.AddRange(GetOverviewRows());
        rows.AddRange(GetStoragePerformanceRows(rows));
        rows.AddRange(GetWindowsLogicalDiskRows());
        rows.AddRange(GetNetworkRows());
        rows = AttachFanControlPercentsToFanRows(rows);
        rows = ApplyFanLabelsToReadings(rows);

        return ConsolidateRelatedRows(rows
            .Where(s => s.Type == "Temperature" || s.Type == "Fan" || s.Type == "SMART" || s.Type == "Performance" || s.Type == "Network" || s.Type == "Fan Control")
            .GroupBy(s => SensorDeduplicationKey(s))
            .Select(g => g.First())
            .ToList())
            .OrderBy(s => TypeSortIndex(s.Type))
            .ThenBy(s => s.Hardware)
            .ThenBy(s => ReadingSortIndex(s.Name))
            .ThenBy(s => s.Name)
            .ToList();
    }

    private static string BuildRefreshStatus(List<SensorRow> rows, string sources)
    {
        var rowCountText = rows.Count + " " + (rows.Count == 1 ? T("status.sensorRowSingular", "sensor row") : T("status.sensorRowPlural", "sensor rows"));
        var status = T("status.updated", "Updated") + " " + DateTime.Now.ToString("HH:mm:ss") + " " + T("status.from", "from") + " " + sources + ". " + rowCountText + ".";
        var hasMotherboard = rows.Any(r => string.Equals(r.Hardware, "Motherboard", StringComparison.OrdinalIgnoreCase));
        var hasFanControls = rows.Any(r => r.Type == "Fan Control");
        if (!hasMotherboard || !hasFanControls)
        {
            status += " " + T("status.fanControlRequirement", "Motherboard fans or controls may require PawnIO, administrator rights, or hardware support.");
        }

        return status;
    }

    private List<SensorRow> ApplyFanLabelsToReadings(List<SensorRow> rows)
    {
        var labels = LoadFanLabels();
        if (labels.Count == 0)
        {
            return rows;
        }

        return rows.Select(row =>
        {
            if (row.Type != "Fan")
            {
                return row;
            }

            var controlIdentifier = GuessControlIdentifier(row.Identifier);
            string label;
            if (string.IsNullOrWhiteSpace(controlIdentifier) || !labels.TryGetValue(controlIdentifier, out label) || string.IsNullOrWhiteSpace(label))
            {
                return row;
            }

            return new SensorRow
            {
                Type = row.Type,
                Hardware = row.Hardware,
                Name = label + ", " + BaseFanControlName(row.Name),
                Identifier = row.Identifier,
                Value = row.Value,
                DisplayValue = row.DisplayValue,
                Source = row.Source
            };
        }).ToList();
    }

    private List<SensorRow> AttachFanControlPercentsToFanRows(List<SensorRow> rows)
    {
        rows = rows ?? new List<SensorRow>();
        var controls = rows
            .Where(r => r.Type == "Fan Control")
            .ToList();
        if (controls.Count == 0)
        {
            return rows;
        }

        return rows.Select(row =>
        {
            if (row.Type != "Fan" || !row.Value.HasValue)
            {
                return row;
            }

            var percent = FanControlPercentForFanRow(row, controls);
            if (!percent.HasValue)
            {
                return row;
            }

            return new SensorRow
            {
                Type = row.Type,
                Hardware = row.Hardware,
                Name = row.Name,
                Identifier = row.Identifier,
                Value = row.Value,
                DisplayValue = FormatNumber(Math.Round(row.Value.Value, 0), "0") + " RPM " + FormatNumber(Math.Round(percent.Value, 1), "0.#") + "%",
                Source = row.Source
            };
        }).ToList();
    }

    private static float? FanControlPercentForFanRow(SensorRow fanRow, List<SensorRow> controls)
    {
        var controlIdentifier = GuessControlIdentifier(fanRow.Identifier);
        var control = controls.FirstOrDefault(r => string.Equals(r.Identifier, controlIdentifier, StringComparison.OrdinalIgnoreCase));
        if (control == null)
        {
            var baseName = BaseFanReadingName(fanRow.Name);
            control = controls.FirstOrDefault(r => string.Equals(BaseFanControlName(r.Name), baseName, StringComparison.OrdinalIgnoreCase));
        }

        return control == null ? null : ExtractPercent(control);
    }

    private void TryApplySavedFanControlsOnStartupAsync(List<SensorRow> rows)
    {
        if (savedFanControlsAppliedThisRun)
        {
            return;
        }

        var saved = LoadFanControlSettings();
        if (saved.Count == 0)
        {
            savedFanControlsAppliedThisRun = true;
            return;
        }

        var manualSettings = saved
            .Where(i => i.Value != null && i.Value.Manual)
            .ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
        if (manualSettings.Count == 0)
        {
            savedFanControlsAppliedThisRun = true;
            return;
        }

        var controls = (rows ?? new List<SensorRow>())
            .Where(r => r.Type == "Fan Control" && !string.IsNullOrWhiteSpace(r.Identifier) && manualSettings.ContainsKey(r.Identifier))
            .Select(r => r.Identifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (controls.Count == 0)
        {
            return;
        }

        savedFanControlsAppliedThisRun = true;
        Task.Factory.StartNew(delegate
        {
            var applied = 0;
            foreach (var identifier in controls)
            {
                FanControlSetting setting;
                if (!manualSettings.TryGetValue(identifier, out setting) || setting == null)
                {
                    continue;
                }

                try
                {
                    SetLibreHardwareMonitorControl(identifier, setting.Percent, setting.Manual);
                    applied++;
                    LogMessage("Debug", "Applied saved fan control " + identifier + ": " + (setting.Manual ? setting.Percent + "% manual" : "automatic/default") + ".");
                }
                catch (Exception ex)
                {
                    LogError("Could not apply saved fan control " + identifier + ": " + ex.Message);
                }
            }

            if (applied > 0)
            {
                LogMessage("Normal", "Applied " + applied + " saved fan control setting" + (applied == 1 ? "" : "s") + " on startup.");
            }
        });
    }

    private void ApplySelectedFanControl(bool manual)
    {
        ClampFanPercentBox();
        var row = GetSelectedFanControlTarget();
        if (row == null || row.Type != "Fan Control")
        {
            statusLabel.Text = "Open fan controls and select a fan control target.";
            LogFanAction(statusLabel.Text);
            return;
        }

        if (!manual)
        {
            fanPercentBox.Value = 50;
        }

        var identifier = row.Identifier;
        var name = row.Name;
        var percent = (int)fanPercentBox.Value;
        RunFanAction(
            manual ? "Setting " + name + " to " + percent + "%..." : "Returning " + name + " to automatic...",
            delegate { SetLibreHardwareMonitorControl(identifier, percent, manual); },
            delegate
            {
                SaveFanControlSetting(identifier, manual, percent);
                statusLabel.Text = "LibreHardwareMonitor: " + name + " " + (manual ? percent + "%" : "automatic/default") + ".";
                RefreshSensorsAfterFanAction();
            });
    }

    private void ClampFanPercentBox()
    {
        if (fanPercentBox == null)
        {
            return;
        }

        if (fanPercentBox.Value < fanPercentBox.Minimum)
        {
            fanPercentBox.Value = fanPercentBox.Minimum;
        }

        if (fanPercentBox.Value > fanPercentBox.Maximum)
        {
            fanPercentBox.Value = fanPercentBox.Maximum;
        }
    }

    private void ResetAllFanControls()
    {
        if (fanPercentBox == null)
        {
            statusLabel.Text = "Open fan controls before resetting fans.";
            return;
        }
        fanPercentBox.Value = 50;
        var count = 0;
        RunFanAction(
            "Returning all fan controls to automatic...",
            delegate { count = SetAllLibreHardwareMonitorControlsDefault(); },
            delegate
            {
                SaveFanControlSettingsForAllKnownControls(false, 50);
                statusLabel.Text = "LibreHardwareMonitor: reset " + count + " fan control" + (count == 1 ? "" : "s") + " to automatic/default.";
                RefreshSensorsAfterFanAction();
            });
    }

    private void RunFanAction(string startingStatus, Action worker, Action completed)
    {
        statusLabel.Text = startingStatus;
        LogFanAction(startingStatus);
        Task.Factory.StartNew(worker).ContinueWith(delegate(Task task)
        {
            if (IsDisposed)
            {
                return;
            }

            if (task.IsFaulted)
            {
                var ex = task.Exception == null ? null : task.Exception.GetBaseException();
                statusLabel.Text = ex == null ? "Fan control action failed." : ex.GetType().Name + ": " + ex.Message;
                LogError(statusLabel.Text);
                return;
            }

            completed();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyAllVisibleFanControls(int percent, string profileName)
    {
        if (fanControlBox == null || fanPercentBox == null)
        {
            statusLabel.Text = "Open fan controls before applying a fan profile.";
            return;
        }
        fanPercentBox.Value = percent;
        var controls = fanControlBox.Items.Cast<SensorRow>().ToList();
        if (controls.Count == 0)
        {
            statusLabel.Text = "No visible fan controls to adjust.";
            LogFanAction(statusLabel.Text);
            return;
        }

        RunFanAction(
            "Applying " + profileName + " profile to " + controls.Count + " fan controls...",
            delegate
            {
                foreach (var control in controls)
                {
                    SetLibreHardwareMonitorControl(control.Identifier, percent, true);
                }
            },
            delegate
            {
                SaveFanControlSettings(controls.Select(c => c.Identifier), true, percent);
                statusLabel.Text = "LibreHardwareMonitor: " + profileName + " profile, " + percent + "% on " + controls.Count + " controls.";
                RefreshSensorsAfterFanAction();
            });
    }

    private void RefreshSensorsAfterFanAction()
    {
        ForceUpdateFanControlBox();
        RefreshSensors();
    }

    private SensorRow GetSelectedFanControlTarget()
    {
        if (fanControlBox == null)
        {
            return null;
        }

        var selectedControl = fanControlBox.SelectedItem as SensorRow;
        if (selectedControl != null)
        {
            return selectedControl;
        }

        var row = GetSelectedReadingRow();
        if (row == null)
        {
            return null;
        }

        if (row.Type == "Fan Control")
        {
            return row;
        }

        if (row.Type != "Fan")
        {
            return null;
        }

        var controlRows = latestRows.Where(r => r.Type == "Fan Control").ToList();
        var byName = controlRows.FirstOrDefault(r => string.Equals(CleanControlName(r.Name), CleanControlName(row.Name), StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            return byName;
        }

        var guessedIdentifier = GuessControlIdentifier(row.Identifier);
        if (!string.IsNullOrWhiteSpace(guessedIdentifier))
        {
            var byIdentifier = controlRows.FirstOrDefault(r => string.Equals(r.Identifier, guessedIdentifier, StringComparison.OrdinalIgnoreCase));
            if (byIdentifier != null)
            {
                return byIdentifier;
            }
        }

        return null;
    }

    private void UpdateFanControlBox()
    {
        UpdateFanControlBox(false);
    }

    private void ForceUpdateFanControlBox()
    {
        UpdateFanControlBox(true);
    }

    private void UpdateFanControlBox(bool force)
    {
        if (fanControlBox == null || fanLabelBox == null || fanPercentBox == null || showStoppedFansCheckBox == null)
        {
            return;
        }

        if (!force && (fanControlBox.Focused || fanLabelBox.Focused || fanPercentBox.Focused))
        {
            return;
        }

        var labels = LoadFanLabels();
        var controls = latestRows
            .Where(r => r.Type == "Fan Control")
            .OrderBy(r => ControlSortKey(r.Identifier))
            .ToList();
        controls = controls
            .Select(c => EnrichFanControlRow(c, labels))
            .Where(c => showStoppedFansCheckBox.Checked || ShouldShowFanControl(c))
            .OrderBy(c => ControlSortKey(c.Identifier))
            .ToList();

        var selectedIdentifier = (fanControlBox.SelectedItem as SensorRow) == null
            ? ""
            : ((SensorRow)fanControlBox.SelectedItem).Identifier;

        var currentSignature = string.Join("|", fanControlBox.Items.Cast<SensorRow>().Select(r => r.Identifier + "=" + r.Name).ToArray());
        var newSignature = string.Join("|", controls.Select(r => r.Identifier + "=" + r.Name).ToArray());
        if (currentSignature == newSignature)
        {
            return;
        }

        updatingFanControlBox = true;
        fanControlBox.BeginUpdate();
        try
        {
            fanControlBox.Items.Clear();
            foreach (var control in controls)
            {
                fanControlBox.Items.Add(control);
            }

            if (fanControlBox.Items.Count > 0)
            {
                var selectedIndex = 0;
                for (var i = 0; i < fanControlBox.Items.Count; i++)
                {
                    var item = (SensorRow)fanControlBox.Items[i];
                    if (string.Equals(item.Identifier, selectedIdentifier, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }

                fanControlBox.SelectedIndex = selectedIndex;
                var row = fanControlBox.SelectedItem as SensorRow;
                fanLabelBox.Text = row == null ? "" : GetFanLabel(row.Identifier, BaseFanControlName(row.Name));
            }
            else
            {
                fanLabelBox.Text = "";
            }
        }
        finally
        {
            fanControlBox.EndUpdate();
            updatingFanControlBox = false;
        }
    }

    private SensorRow EnrichFanControlRow(SensorRow control, Dictionary<string, string> labels)
    {
        var baseName = BaseFanControlName(control.Name);
        var label = labels.ContainsKey(control.Identifier) ? labels[control.Identifier] : baseName;
        var rpm = GetFanRpmForControl(control.Identifier);
            var rpmText = rpm.HasValue ? FormatNumber(Math.Round(rpm.Value, 0), "0") + " RPM" : T("value.no RPM reading", "no RPM reading");
        var state = control.DisplayValue ?? "";

        return new SensorRow
        {
            Type = control.Type,
            Hardware = control.Hardware,
            Name = label + ", " + baseName + ", " + rpmText + ", " + state,
            Identifier = control.Identifier,
            Value = control.Value,
            DisplayValue = control.DisplayValue,
            Source = control.Source
        };
    }

    private bool ShouldShowFanControl(SensorRow control)
    {
        if (IsGpuControl(control.Identifier))
        {
            return true;
        }

        var rpm = GetFanRpmForControl(control.Identifier);
        return rpm.HasValue && rpm.Value > 0;
    }

    private float? GetFanRpmForControl(string controlIdentifier)
    {
        var fanIdentifier = GuessFanIdentifier(controlIdentifier);
        var row = latestRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(r.Identifier, fanIdentifier, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            return row.Value;
        }

        var baseName = BaseFanControlName(controlIdentifier);
        row = latestRows.FirstOrDefault(r => r.Type == "Fan" && string.Equals(BaseFanControlName(r.Name), baseName, StringComparison.OrdinalIgnoreCase));
        return row == null ? (float?)null : row.Value;
    }

    private void SaveSelectedFanLabel()
    {
        if (fanControlBox == null || fanLabelBox == null)
        {
            statusLabel.Text = "Open fan controls before saving a fan label.";
            return;
        }

        var row = fanControlBox.SelectedItem as SensorRow;
        if (row == null)
        {
            statusLabel.Text = "Select a fan control before saving a label.";
            return;
        }

        var labels = LoadFanLabels();
        var label = fanLabelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            labels.Remove(row.Identifier);
            labels.Remove(GuessFanIdentifier(row.Identifier));
        }
        else
        {
            labels[row.Identifier] = label;
            labels[GuessFanIdentifier(row.Identifier)] = label;
        }

        SaveFanLabels(labels);
        statusLabel.Text = "Saved fan label for " + BaseFanControlName(row.Name) + ".";
        UpdateFanControlBox();
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList();
    }

}
