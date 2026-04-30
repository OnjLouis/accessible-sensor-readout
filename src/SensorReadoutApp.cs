using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class SensorRow
{
    public string Type;
    public string Hardware;
    public string Name;
    public string Identifier;
    public float? Value;
    public string DisplayValue;
    public string Source;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? Identifier : Name;
    }
}

public sealed class DeviceFilter
{
    public string Key;
    public string DisplayName;
    public string Type;
    public string Hardware;

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed class SensorReadoutForm : Form
{
    private const int WM_CLOSE = 0x0010;
    private const int SW_MINIMIZE = 6;
    private const string FanControlPath = @"D:\SOFTWARE\FanControl_244_net_4_8\FanControl.exe";
    private const string FanControlConfigFolder = @"D:\SOFTWARE\FanControl_244_net_4_8\Configurations";
    private const string GeneratedFanControlConfigName = "AccessibleSensorReadout-FanControl.json";
    private const string FanControlActionLogName = "AccessibleSensorReadout-FanControl.log";
    private const string FanLabelFileName = "FanControlLabels.json";
    private const int FanControlStartupWaitMs = 12000;
    private const int RefreshIntervalMs = 8000;
    private readonly ListBox deviceList;
    private readonly ListBox readingList;
    private readonly Label statusLabel;
    private readonly Button refreshButton;
    private readonly Button applyFanButton;
    private readonly Button autoFanButton;
    private readonly Button elevatedFanButton;
    private readonly Button maxFanButton;
    private readonly Button saveFanLabelButton;
    private readonly Button saveReportButton;
    private readonly ComboBox fanControlBox;
    private readonly TextBox fanLabelBox;
    private readonly NumericUpDown fanPercentBox;
    private readonly CheckBox showStoppedFansCheckBox;
    private readonly CheckBox pauseCheckBox;
    private readonly Timer timer;
    private readonly List<SensorRow> latestRows = new List<SensorRow>();
    private Computer lhmComputer;
    private string selectedFilterKey = "all";
    private bool updatingFanControlBox;
    private bool refreshInProgress;

    public SensorReadoutForm()
    {
        Text = "Sensor Readout";
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 620);
        MinimumSize = new Size(700, 420);
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                RefreshSensors();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                SaveReport();
                e.Handled = true;
            }
        };

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        refreshButton = new Button
        {
            Text = "Refresh &now",
            AutoSize = true,
            AccessibleName = "Refresh sensors now"
        };
        refreshButton.Click += delegate { RefreshSensors(); };

        saveReportButton = new Button
        {
            Text = "Save repor&t",
            AutoSize = true,
            AccessibleName = "Save sensor report"
        };
        saveReportButton.Click += delegate { SaveReport(); };

        fanControlBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 360,
            AccessibleName = "Fan control target",
            AccessibleDescription = "Choose which fan control to adjust"
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
            Width = 180,
            AccessibleName = "Fan label",
            AccessibleDescription = "Friendly name for the selected fan control"
        };

        saveFanLabelButton = new Button
        {
            Text = "Save &label",
            AutoSize = true,
            AccessibleName = "Save label for selected fan control"
        };
        saveFanLabelButton.Click += delegate { SaveSelectedFanLabel(); };

        fanPercentBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Increment = 5,
            Value = 50,
            Width = 70,
            AccessibleName = "Fan manual percentage"
        };
        fanPercentBox.Enter += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Click += delegate { fanPercentBox.Select(0, fanPercentBox.Text.Length); };
        fanPercentBox.Leave += delegate { ClampFanPercentBox(); };

        applyFanButton = new Button
        {
            Text = "&Manual adjust",
            AutoSize = true,
            AccessibleName = "Apply manual percentage to selected fan control"
        };
        applyFanButton.Click += delegate { ApplySelectedFanControl(true); };

        autoFanButton = new Button
        {
            Text = "All fans &reset",
            AutoSize = true,
            AccessibleName = "Return all fan controls to automatic"
        };
        autoFanButton.Click += delegate { ResetAllFanControls(); };

        elevatedFanButton = new Button
        {
            Text = "All fans &75",
            AutoSize = true,
            AccessibleName = "Set all visible fan controls to 75 percent"
        };
        elevatedFanButton.Click += delegate { ApplyAllVisibleFanControls(75, "elevated"); };

        maxFanButton = new Button
        {
            Text = "All fans ma&x",
            AutoSize = true,
            AccessibleName = "Set all visible fan controls to 100 percent"
        };
        maxFanButton.Click += delegate { ApplyAllVisibleFanControls(100, "max"); };

        pauseCheckBox = new CheckBox
        {
            Text = "&Pause updates",
            AutoSize = true,
            AccessibleName = "Pause automatic updates"
        };

        showStoppedFansCheckBox = new CheckBox
        {
            Text = "Show &stopped",
            AutoSize = true,
            AccessibleName = "Show stopped or unpopulated fan headers"
        };
        showStoppedFansCheckBox.CheckedChanged += delegate { UpdateFanControlBox(); };

        topPanel.Controls.Add(refreshButton);
        topPanel.Controls.Add(saveReportButton);
        topPanel.Controls.Add(fanControlBox);
        topPanel.Controls.Add(fanLabelBox);
        topPanel.Controls.Add(saveFanLabelButton);
        topPanel.Controls.Add(fanPercentBox);
        topPanel.Controls.Add(applyFanButton);
        topPanel.Controls.Add(autoFanButton);
        topPanel.Controls.Add(elevatedFanButton);
        topPanel.Controls.Add(maxFanButton);
        topPanel.Controls.Add(showStoppedFansCheckBox);
        topPanel.Controls.Add(pauseCheckBox);

        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 340,
            FixedPanel = FixedPanel.Panel1,
            TabStop = false
        };
        splitContainer.Panel1.TabStop = false;
        splitContainer.Panel2.TabStop = false;

        deviceList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Device or sensor category",
            AccessibleDescription = "Choose which device or sensor category to show in the readings list"
        };
        deviceList.SelectedIndexChanged += delegate
        {
            var filter = deviceList.SelectedItem as DeviceFilter;
            if (filter != null)
            {
                selectedFilterKey = filter.Key;
                UpdateReadingList();
            }
        };

        readingList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Readings",
            AccessibleDescription = "Current sensor readings for the selected device or category"
        };

        splitContainer.Panel1.Controls.Add(deviceList);
        splitContainer.Panel2.Controls.Add(readingList);

        statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(8, 5, 8, 0),
            Text = "Starting..."
        };

        Controls.Add(splitContainer);
        Controls.Add(statusLabel);
        Controls.Add(topPanel);

        timer = new Timer { Interval = RefreshIntervalMs };
        timer.Tick += delegate
        {
            if (!pauseCheckBox.Checked && !ContainsFocus)
            {
                RefreshSensors();
            }
        };

        Shown += delegate
        {
            RefreshSensors();
            timer.Start();
        };
    }

    private void RefreshSensors()
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        try
        {
            EnsureFanControlRunning();

            var rows = GetFanControlSensors()
                .Concat(GetLibreHardwareMonitorSensors())
                .Concat(GetWindowsSmartRows())
                .Concat(GetFanControlRows())
                .Where(s => s.Type == "Temperature" || s.Type == "Fan" || s.Type == "SMART" || s.Type == "Fan Control")
                .Where(s => !IsDuplicateNuvotonMotherboardRow(s))
                .GroupBy(s => (s.Type + "|" + s.Hardware + "|" + s.Name + "|" + s.Identifier).ToLowerInvariant())
                .Select(g => g.First())
                .OrderBy(s => s.Type == "Temperature" ? 0 : 1)
                .ThenBy(s => s.Hardware)
                .ThenBy(s => s.Name)
                .ToList();

            latestRows.Clear();
            latestRows.AddRange(rows);
            UpdateFanControlBox();
            UpdateDeviceList();
            UpdateReadingList();

            if (rows.Count == 0)
            {
                statusLabel.Text = "No sensor rows returned yet. FanControl may still be starting.";
            }
            else
            {
                var sources = string.Join(", ", rows.Select(r => r.Source).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s).ToArray());
                statusLabel.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss") + " from " + sources + ". " + rows.Count + " sensor rows.";
            }
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private IEnumerable<SensorRow> GetFanControlRows()
    {
        try
        {
            return ReadFanControlControlRows().ToList();
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private static IEnumerable<SensorRow> ReadFanControlControlRows()
    {
        var configPath = GetCurrentFanControlConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !System.IO.File.Exists(configPath))
        {
            configPath = System.IO.Path.Combine(FanControlConfigFolder, GeneratedFanControlConfigName);
        }

        if (!System.IO.File.Exists(configPath))
        {
            configPath = System.IO.Path.Combine(FanControlConfigFolder, "MonitorOnly-MERJILLE.json");
        }

        if (!System.IO.File.Exists(configPath))
        {
            yield break;
        }

        var root = JObject.Parse(System.IO.File.ReadAllText(configPath));
        var controls = root["FanControl"] == null ? null : root["FanControl"]["Controls"] as JArray;
        if (controls == null)
        {
            yield break;
        }

        foreach (var control in controls.OfType<JObject>())
        {
            var identifier = Convert.ToString(control["Identifier"]);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            var nickname = Convert.ToString(control["NickName"]);
            var manual = Convert.ToBoolean(control["ManualControl"] ?? false);
            var enabled = Convert.ToBoolean(control["Enable"] ?? false);
            var value = Convert.ToSingle(control["ManualControlValue"] ?? 0);
            yield return new SensorRow
            {
                Type = "Fan Control",
                Hardware = "Fan controls",
                Name = string.IsNullOrWhiteSpace(nickname) ? identifier : nickname,
                Identifier = identifier,
                Value = value,
                DisplayValue = (enabled && manual ? "Manual " : "Automatic ") + Math.Round(value, 0).ToString("0") + "%",
                Source = "FanControl config"
            };
        }
    }

    private void ApplySelectedFanControl(bool manual)
    {
        ClampFanPercentBox();
        var row = GetSelectedFanControlTarget();
        if (row == null || row.Type != "Fan Control")
        {
            statusLabel.Text = "Select a row from Fan controls, or select a fan speed row that has a matching control.";
            LogFanControlAction(statusLabel.Text);
            return;
        }

        try
        {
            if (!manual)
            {
                fanPercentBox.Value = 50;
            }

            EnsureFanControlRunning();
            var outputPath = WriteFanControlConfig(row.Identifier, (int)fanPercentBox.Value, manual);
            var client = FanControl.IPC.IPCFactory.GetFanControlClient();
            client.RequestTakeover(new TakeoverRequest { Requester = ProcessType.App, User = Environment.UserName });
            var reply = client.LoadConfig(new LoadConfigRequest { Path = outputPath });
            client.Refresh(new RefreshRequest());
            KeepFanControlQuiet();
            statusLabel.Text = "FanControl " + reply.Status + ": " + row.Name + " " + (manual ? fanPercentBox.Value + "%" : "automatic") + ".";
            LogFanControlAction(statusLabel.Text + " Config: " + outputPath);
            RefreshSensors();
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.GetType().Name + ": " + ex.Message;
            LogFanControlAction(statusLabel.Text);
        }
    }

    private void ClampFanPercentBox()
    {
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
        try
        {
            fanPercentBox.Value = 50;
            EnsureFanControlRunning();
            var outputPath = WriteAllFanControlsAutomatic();
            var client = FanControl.IPC.IPCFactory.GetFanControlClient();
            client.RequestTakeover(new TakeoverRequest { Requester = ProcessType.App, User = Environment.UserName });
            var reply = client.LoadConfig(new LoadConfigRequest { Path = outputPath });
            client.Refresh(new RefreshRequest());
            KeepFanControlQuiet();
            statusLabel.Text = "FanControl " + reply.Status + ": all fan controls automatic.";
            LogFanControlAction(statusLabel.Text + " Config: " + outputPath);
            RefreshSensors();
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.GetType().Name + ": " + ex.Message;
            LogFanControlAction(statusLabel.Text);
        }
    }

    private void ApplyAllVisibleFanControls(int percent, string profileName)
    {
        try
        {
            fanPercentBox.Value = percent;
            var controls = fanControlBox.Items.Cast<SensorRow>().ToList();
            if (controls.Count == 0)
            {
                statusLabel.Text = "No visible fan controls to adjust.";
                LogFanControlAction(statusLabel.Text);
                return;
            }

            EnsureFanControlRunning();
            var outputPath = WriteMultipleFanControls(controls.Select(c => c.Identifier).ToList(), percent);
            var client = FanControl.IPC.IPCFactory.GetFanControlClient();
            client.RequestTakeover(new TakeoverRequest { Requester = ProcessType.App, User = Environment.UserName });
            var reply = client.LoadConfig(new LoadConfigRequest { Path = outputPath });
            client.Refresh(new RefreshRequest());
            KeepFanControlQuiet();
            statusLabel.Text = "FanControl " + reply.Status + ": " + profileName + " profile, " + percent + "% on " + controls.Count + " controls.";
            LogFanControlAction(statusLabel.Text + " Config: " + outputPath);
            RefreshSensors();
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.GetType().Name + ": " + ex.Message;
            LogFanControlAction(statusLabel.Text);
        }
    }

    private SensorRow GetSelectedFanControlTarget()
    {
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
        if (fanControlBox.Focused || fanLabelBox.Focused || fanPercentBox.Focused)
        {
            return;
        }

        var labels = LoadFanLabels();
        var controls = ReadFanControlControlRows()
            .OrderBy(r => ControlSortKey(r.Identifier))
            .ToList();
        if (controls.Count == 0)
        {
            controls = latestRows
                .Where(r => r.Type == "Fan Control")
                .OrderBy(r => ControlSortKey(r.Identifier))
                .ToList();
        }
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
        var rpmText = rpm.HasValue ? Math.Round(rpm.Value, 0).ToString("0") + " RPM" : "no RPM reading";
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
        }
        else
        {
            labels[row.Identifier] = label;
        }

        SaveFanLabels(labels);
        statusLabel.Text = "Saved fan label for " + BaseFanControlName(row.Name) + ".";
        UpdateFanControlBox();
    }

    private void SaveReport()
    {
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "Save Sensor Report";
            dialog.Filter = "Text report (*.txt)|*.txt|HTML report (*.html)|*.html";
            dialog.FileName = "SensorReadout-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            dialog.DefaultExt = "txt";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var html = dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
            var report = html ? BuildHtmlReport() : BuildTextReport();
            System.IO.File.WriteAllText(dialog.FileName, report);
            statusLabel.Text = "Saved report to " + dialog.FileName;
        }
    }

    private string BuildTextReport()
    {
        var lines = new List<string>();
        lines.Add("Sensor Readout report");
        lines.Add("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        lines.Add("");

        foreach (var group in latestRows.OrderBy(r => r.Type).ThenBy(r => r.Hardware).ThenBy(r => r.Name).GroupBy(r => r.Type))
        {
            lines.Add(group.Key);
            foreach (var row in group)
            {
                lines.Add(ShortHardwareName(row.Hardware) + ", " + CleanSensorName(row.Name) + ", " + FormatValue(row));
            }

            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private string BuildHtmlReport()
    {
        var html = new System.Text.StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html><head><meta charset=\"utf-8\"><title>Sensor Readout report</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif} table{border-collapse:collapse} th,td{border:1px solid #888;padding:4px 8px;text-align:left}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>Sensor Readout report</h1>");
        html.AppendLine("<p>Generated " + HtmlEncode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "</p>");
        html.AppendLine("<table><thead><tr><th>Type</th><th>Hardware</th><th>Sensor</th><th>Value</th><th>Source</th></tr></thead><tbody>");
        foreach (var row in latestRows.OrderBy(r => r.Type).ThenBy(r => r.Hardware).ThenBy(r => r.Name))
        {
            html.AppendLine("<tr><td>" + HtmlEncode(row.Type) + "</td><td>" + HtmlEncode(ShortHardwareName(row.Hardware)) + "</td><td>" + HtmlEncode(CleanSensorName(row.Name)) + "</td><td>" + HtmlEncode(FormatValue(row)) + "</td><td>" + HtmlEncode(row.Source) + "</td></tr>");
        }

        html.AppendLine("</tbody></table></body></html>");
        return html.ToString();
    }

    private SensorRow GetSelectedReadingRow()
    {
        var filter = deviceList.SelectedItem as DeviceFilter;
        var rows = ApplyFilter(latestRows, filter)
            .OrderBy(r => r.Type == "Temperature" ? 0 : r.Type == "Fan" ? 1 : r.Type == "SMART" ? 2 : 3)
            .ThenBy(r => r.Name)
            .ToList();

        if (readingList.SelectedIndex < 0 || readingList.SelectedIndex >= rows.Count)
        {
            return null;
        }

        return rows[readingList.SelectedIndex];
    }

    private static string WriteFanControlConfig(string controlIdentifier, int percent, bool manual)
    {
        var configPath = GetCurrentFanControlConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !System.IO.File.Exists(configPath))
        {
            configPath = System.IO.Path.Combine(FanControlConfigFolder, "MonitorOnly-MERJILLE.json");
        }

        var root = JObject.Parse(System.IO.File.ReadAllText(configPath));
        var controls = root["FanControl"] == null ? null : root["FanControl"]["Controls"] as JArray;
        if (controls == null)
        {
            throw new InvalidOperationException("FanControl config has no controls section.");
        }

        var found = false;
        foreach (var control in controls.OfType<JObject>())
        {
            if (!string.Equals(Convert.ToString(control["Identifier"]), controlIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            control["Enable"] = manual;
            control["ManualControl"] = manual;
            control["ManualControlValue"] = percent;
            control["ForceApply"] = true;
            found = true;
            break;
        }

        if (!found)
        {
            throw new InvalidOperationException("Could not find selected fan control in FanControl config.");
        }

        var outputPath = System.IO.Path.Combine(FanControlConfigFolder, GeneratedFanControlConfigName);
        System.IO.File.WriteAllText(outputPath, root.ToString(Formatting.Indented));
        return outputPath;
    }

    private static string WriteAllFanControlsAutomatic()
    {
        var configPath = GetCurrentFanControlConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !System.IO.File.Exists(configPath))
        {
            configPath = System.IO.Path.Combine(FanControlConfigFolder, "MonitorOnly-MERJILLE.json");
        }

        var root = JObject.Parse(System.IO.File.ReadAllText(configPath));
        var controls = root["FanControl"] == null ? null : root["FanControl"]["Controls"] as JArray;
        if (controls == null)
        {
            throw new InvalidOperationException("FanControl config has no controls section.");
        }

        foreach (var control in controls.OfType<JObject>())
        {
            control["Enable"] = false;
            control["ManualControl"] = false;
            control["ManualControlValue"] = 50;
            control["ForceApply"] = false;
        }

        var outputPath = System.IO.Path.Combine(FanControlConfigFolder, GeneratedFanControlConfigName);
        System.IO.File.WriteAllText(outputPath, root.ToString(Formatting.Indented));
        return outputPath;
    }

    private static string WriteMultipleFanControls(List<string> controlIdentifiers, int percent)
    {
        var configPath = GetCurrentFanControlConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !System.IO.File.Exists(configPath))
        {
            configPath = System.IO.Path.Combine(FanControlConfigFolder, "MonitorOnly-MERJILLE.json");
        }

        var root = JObject.Parse(System.IO.File.ReadAllText(configPath));
        var controls = root["FanControl"] == null ? null : root["FanControl"]["Controls"] as JArray;
        if (controls == null)
        {
            throw new InvalidOperationException("FanControl config has no controls section.");
        }

        var wanted = new HashSet<string>(controlIdentifiers, StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls.OfType<JObject>())
        {
            var identifier = Convert.ToString(control["Identifier"]);
            if (!wanted.Contains(identifier))
            {
                continue;
            }

            control["Enable"] = true;
            control["ManualControl"] = true;
            control["ManualControlValue"] = percent;
            control["ForceApply"] = true;
        }

        var outputPath = System.IO.Path.Combine(FanControlConfigFolder, GeneratedFanControlConfigName);
        System.IO.File.WriteAllText(outputPath, root.ToString(Formatting.Indented));
        return outputPath;
    }

    private static string GetCurrentFanControlConfigPath()
    {
        var cachePath = System.IO.Path.Combine(FanControlConfigFolder, "CACHE");
        try
        {
            if (System.IO.File.Exists(cachePath))
            {
                var cache = JObject.Parse(System.IO.File.ReadAllText(cachePath));
                var current = Convert.ToString(cache["CurrentConfigFileName"]);
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return current;
                }
            }
        }
        catch
        {
        }

        return System.IO.Path.Combine(FanControlConfigFolder, "MonitorOnly-MERJILLE.json");
    }

    private static string CleanControlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return name
            .Replace("Control 1 - NVIDIA GeForce RTX 4060", "Fan 1 - NVIDIA GeForce RTX 4060")
            .Trim();
    }

    private static string BaseFanControlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var comma = name.IndexOf(',');
        if (comma > -1)
        {
            name = name.Substring(0, comma);
        }

        return CleanControlName(name);
    }

    private static bool IsGpuControl(string identifier)
    {
        return !string.IsNullOrWhiteSpace(identifier) &&
            identifier.IndexOf("NVApi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ControlSortKey(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return 100000;
        }

        var marker = "/control/";
        var index = identifier.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            int value;
            if (int.TryParse(identifier.Substring(index + marker.Length), out value))
            {
                return value;
            }
        }

        return IsGpuControl(identifier) ? 90000 : 80000;
    }

    private static string GuessControlIdentifier(string fanIdentifier)
    {
        if (string.IsNullOrWhiteSpace(fanIdentifier))
        {
            return "";
        }

        return fanIdentifier
            .Replace("/fan/", "/control/")
            .Replace("/fan/", "/control/")
            .Replace("-A/fan/", "-A/control/");
    }

    private static string GuessFanIdentifier(string controlIdentifier)
    {
        if (string.IsNullOrWhiteSpace(controlIdentifier))
        {
            return "";
        }

        return controlIdentifier
            .Replace("/control/", "/fan/")
            .Replace("-A/control/", "-A/fan/");
    }

    private static string GetFanLabel(string identifier, string fallback)
    {
        var labels = LoadFanLabels();
        return labels.ContainsKey(identifier) ? labels[identifier] : fallback;
    }

    private static Dictionary<string, string> LoadFanLabels()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FanLabelFileName);
            if (System.IO.File.Exists(path))
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(System.IO.File.ReadAllText(path)) ??
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveFanLabels(Dictionary<string, string> labels)
    {
        var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FanLabelFileName);
        System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(labels, Formatting.Indented));
    }

    private static void LogFanControlAction(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(FanControlConfigFolder, FanControlActionLogName);
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private void KeepFanControlQuiet()
    {
        try
        {
            SetFanControlStartMinimized();
            foreach (var process in Process.GetProcessesByName("FanControl"))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    PostMessage(process.MainWindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }

            Activate();
            BringToFront();
            Focus();
        }
        catch
        {
        }
    }

    private static void SetFanControlStartMinimized()
    {
        try
        {
            var cachePath = System.IO.Path.Combine(FanControlConfigFolder, "CACHE");
            if (!System.IO.File.Exists(cachePath))
            {
                return;
            }

            var cache = JObject.Parse(System.IO.File.ReadAllText(cachePath));
            if (cache["Main"] == null)
            {
                cache["Main"] = new JObject();
            }

            cache["Main"]["StartMinimized"] = true;
            cache["Main"]["WindowState"] = 1;
            System.IO.File.WriteAllText(cachePath, cache.ToString(Formatting.Indented));
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void EnsureFanControlRunning()
    {
        if (Process.GetProcessesByName("FanControl").Any())
        {
            return;
        }

        if (!System.IO.File.Exists(FanControlPath))
        {
            statusLabel.Text = "FanControl was not found at " + FanControlPath;
            return;
        }

        statusLabel.Text = "Starting FanControl sensor service...";
        Application.DoEvents();

        var startInfo = new ProcessStartInfo
        {
            FileName = FanControlPath,
            WorkingDirectory = System.IO.Path.GetDirectoryName(FanControlPath),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized
        };
        Process.Start(startInfo);

        var deadline = DateTime.UtcNow.AddMilliseconds(FanControlStartupWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("FanControl").Any())
            {
                return;
            }

            Application.DoEvents();
            System.Threading.Thread.Sleep(250);
        }
    }

    private static IEnumerable<SensorRow> GetFanControlSensors()
    {
        var process = Process.GetProcessesByName("FanControl").FirstOrDefault();
        if (process == null)
        {
            yield break;
        }

        var client = FanControl.IPC.IPCFactory.GetSensorClient();
        var reply = client.GetAllSensors(new GetAllSensorsRequest());

        foreach (var sensor in reply.Sensors)
        {
            if (sensor.Type != SensorMessageType.Temperature && sensor.Type != SensorMessageType.Rpm)
            {
                continue;
            }

            yield return new SensorRow
            {
                Type = sensor.Type == SensorMessageType.Rpm ? "Fan" : "Temperature",
                Hardware = sensor.Origin ?? "",
                Name = sensor.Name ?? "",
                Identifier = sensor.Identifier ?? "",
                Value = sensor.HasValue ? sensor.Value : (float?)null,
                Source = "FanControl"
            };
        }
    }

    private IEnumerable<SensorRow> GetLibreHardwareMonitorSensors()
    {
        try
        {
            if (lhmComputer == null)
            {
                lhmComputer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                    IsControllerEnabled = true,
                    IsMemoryEnabled = true
                };
                lhmComputer.Open();
            }

            foreach (var hardware in lhmComputer.Hardware)
            {
                UpdateHardware(hardware);
            }

            return lhmComputer.Hardware.SelectMany(ReadLibreHardwareMonitorSensors).ToList();
        }
        catch
        {
            return Enumerable.Empty<SensorRow>();
        }
    }

    private static void UpdateHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var subHardware in hardware.SubHardware)
        {
            UpdateHardware(subHardware);
        }
    }

    private static IEnumerable<SensorRow> ReadLibreHardwareMonitorSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            var sensorType = sensor.SensorType.ToString();
            var hardwareType = hardware.HardwareType.ToString();
            var isStorage = hardware.HardwareType == HardwareType.Storage;
            var type = sensorType == "Fan" ? "Fan" : sensorType == "Temperature" ? "Temperature" : isStorage ? "SMART" : "";
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var value = sensor.Value.Value;
            if (type == "Temperature" && value <= 0)
            {
                continue;
            }

            yield return new SensorRow
            {
                Type = type,
                Hardware = hardware.Name ?? "",
                Name = isStorage && sensorType != "Temperature" ? sensor.Name + " (" + sensorType + ")" : sensor.Name,
                Identifier = sensor.Identifier == null ? "" : sensor.Identifier.ToString(),
                Value = value,
                DisplayValue = isStorage ? FormatLibreHardwareMonitorStorageValue(sensorType, sensor.Name, value) : null,
                Source = "LibreHardwareMonitor"
            };
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            foreach (var row in ReadLibreHardwareMonitorSensors(subHardware))
            {
                yield return row;
            }
        }
    }

    private static IEnumerable<SensorRow> GetWindowsSmartRows()
    {
        foreach (var row in GetPhysicalDiskRows())
        {
            yield return row;
        }

        foreach (var row in GetStorageReliabilityRows())
        {
            yield return row;
        }
    }

    private static IEnumerable<SensorRow> GetPhysicalDiskRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, HealthStatus, MediaType, OperationalStatus, Size FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject disk in searcher.Get())
                {
                    var name = Convert.ToString(disk["FriendlyName"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Physical disk";
                    }

                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Health", DisplayValue = DecodeHealthStatus(disk["HealthStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Media type", DisplayValue = DecodeMediaType(disk["MediaType"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Operational status", DisplayValue = DecodeOperationalStatus(disk["OperationalStatus"]), Source = "Windows Storage" });
                    rows.Add(new SensorRow { Type = "SMART", Hardware = name, Name = "Size", DisplayValue = FormatBytes(disk["Size"]), Source = "Windows Storage" });
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static IEnumerable<SensorRow> GetStorageReliabilityRows()
    {
        var rows = new List<SensorRow>();
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_StorageReliabilityCounter"))
            {
                foreach (ManagementObject counter in searcher.Get())
                {
                    var name = Convert.ToString(counter["DeviceId"]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "Storage device";
                    }

                    AddSmartCounter(rows, name, counter, "Temperature", "Temperature", true);
                    AddSmartCounter(rows, name, counter, "TemperatureMax", "Maximum temperature", true);
                    AddSmartCounter(rows, name, counter, "PowerOnHours", "Power on hours", false);
                    AddSmartCounter(rows, name, counter, "ReadErrorsTotal", "Read errors total", false);
                    AddSmartCounter(rows, name, counter, "WriteErrorsTotal", "Write errors total", false);
                    AddSmartCounter(rows, name, counter, "Wear", "Wear", false);
                }
            }
        }
        catch
        {
        }

        return rows;
    }

    private static void AddSmartCounter(List<SensorRow> rows, string hardware, ManagementBaseObject counter, string property, string name, bool temperature)
    {
        try
        {
            var raw = counter[property];
            if (raw == null)
            {
                return;
            }

            double value;
            if (!double.TryParse(Convert.ToString(raw), out value))
            {
                return;
            }

            if (value <= 0 && temperature)
            {
                return;
            }

            rows.Add(new SensorRow
            {
                Type = temperature ? "Temperature" : "SMART",
                Hardware = hardware,
                Name = name,
                Value = (float)value,
                DisplayValue = temperature ? Math.Round(value, 1).ToString("0.0") + " C" : Math.Round(value, 0).ToString("0"),
                Source = "Windows Storage"
            });
        }
        catch
        {
        }
    }

    private static string FormatLibreHardwareMonitorStorageValue(string sensorType, string sensorName, float value)
    {
        if (sensorType == "Temperature")
        {
            return Math.Round(value, 1).ToString("0.0") + " C";
        }

        if (sensorType == "Load")
        {
            return Math.Round(value, 1).ToString("0.0") + "%";
        }

        if (sensorType == "Throughput")
        {
            return Math.Round(value, 1).ToString("0.0") + " MB/s";
        }

        if (sensorType == "Data")
        {
            if (!string.IsNullOrWhiteSpace(sensorName) && sensorName.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return FormatBytes(value);
            }

            return Math.Round(value, 1).ToString("0.0");
        }

        if (sensorType == "Level")
        {
            return Math.Round(value, 1).ToString("0.0") + "%";
        }

        if (sensorType == "Factor")
        {
            return Math.Round(value, 0).ToString("0");
        }

        return Math.Round(value, 1).ToString("0.0");
    }

    private void UpdateDeviceList()
    {
        var filters = BuildFilters(latestRows).ToList();
        var currentKeys = deviceList.Items
            .Cast<DeviceFilter>()
            .Select(f => f.Key)
            .ToList();
        var newKeys = filters.Select(f => f.Key).ToList();

        if (currentKeys.SequenceEqual(newKeys))
        {
            return;
        }

        deviceList.BeginUpdate();
        try
        {
            deviceList.Items.Clear();
            foreach (var filter in filters)
            {
                deviceList.Items.Add(filter);
            }

            var selectedIndex = filters.FindIndex(f => f.Key == selectedFilterKey);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                selectedFilterKey = filters.Count > 0 ? filters[0].Key : "all";
            }

            if (filters.Count > 0)
            {
                deviceList.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            deviceList.EndUpdate();
        }
    }

    private static IEnumerable<DeviceFilter> BuildFilters(List<SensorRow> rows)
    {
        yield return new DeviceFilter { Key = "all", DisplayName = "All sensors" };
        yield return new DeviceFilter { Key = "type|Temperature", DisplayName = "All temperatures", Type = "Temperature" };
        yield return new DeviceFilter { Key = "type|Fan", DisplayName = "All fans", Type = "Fan" };
        yield return new DeviceFilter { Key = "type|SMART", DisplayName = "SMART data", Type = "SMART" };
        yield return new DeviceFilter { Key = "type|Fan Control", DisplayName = "Fan controls", Type = "Fan Control" };

        foreach (var hardware in rows.Select(r => r.Hardware).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().OrderBy(h => h))
        {
            yield return new DeviceFilter
            {
                Key = "hardware|" + hardware,
                DisplayName = ShortHardwareName(hardware),
                Hardware = hardware
            };
        }
    }

    private void UpdateReadingList()
    {
        var selectedIndex = readingList.SelectedIndex;
        var filter = deviceList.SelectedItem as DeviceFilter;
        var rows = ApplyFilter(latestRows, filter)
            .OrderBy(r => r.Type == "Temperature" ? 0 : r.Type == "Fan" ? 1 : r.Type == "SMART" ? 2 : 3)
            .ThenBy(r => r.Name)
            .ToList();

        var lines = rows.Select(r => FormatReadingLine(r, filter)).ToList();
        if (lines.Count == 0)
        {
            lines.Add("No readings available.");
        }

        readingList.BeginUpdate();
        try
        {
            readingList.Items.Clear();
            foreach (var line in lines)
            {
                readingList.Items.Add(line);
            }

            if (readingList.Items.Count > 0)
            {
                if (selectedIndex < 0 || selectedIndex >= readingList.Items.Count)
                {
                    selectedIndex = 0;
                }
                readingList.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            readingList.EndUpdate();
        }
    }

    private static IEnumerable<SensorRow> ApplyFilter(IEnumerable<SensorRow> rows, DeviceFilter filter)
    {
        if (filter == null || filter.Key == "all")
        {
            return rows;
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            return rows.Where(r => r.Type == filter.Type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Hardware))
        {
            return rows.Where(r => r.Hardware == filter.Hardware);
        }

        return rows;
    }

    private static string FormatReadingLine(SensorRow row, DeviceFilter filter)
    {
        var value = FormatValue(row);
        var sensorName = CleanSensorName(row.Name);

        if (filter == null || filter.Key == "all")
        {
            return row.Type + ", " + ShortHardwareName(row.Hardware) + ", " + sensorName + ", " + value;
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            return ShortHardwareName(row.Hardware) + ", " + sensorName + ", " + value;
        }

        return sensorName + ", " + value;
    }

    private static string ShortHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return hardware;
    }

    private static bool IsDuplicateNuvotonMotherboardRow(SensorRow row)
    {
        return !string.IsNullOrWhiteSpace(row.Hardware) &&
            row.Hardware.IndexOf("Nuvoton NCT6795D", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string CleanSensorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed sensor";
        }

        return name
            .Replace("Core (Tctl/Tdie)", "CPU package")
            .Replace("CCD1 (Tdie)", "CCD1 Tdie");
    }

    private static string FormatValue(SensorRow row)
    {
        if (!row.Value.HasValue)
        {
            return row.DisplayValue ?? "";
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            return row.DisplayValue;
        }

        if (row.Type == "Fan")
        {
            return Math.Round(row.Value.Value, 0).ToString("0") + " RPM";
        }

        if (row.Type == "SMART")
        {
            return Math.Round(row.Value.Value, 1).ToString("0.0");
        }

        return Math.Round(row.Value.Value, 1).ToString("0.0") + " C";
    }

    private static string HtmlEncode(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && lhmComputer != null)
        {
            lhmComputer.Close();
        }

        base.Dispose(disposing);
    }

    private static string DecodeHealthStatus(object value)
    {
        var code = Convert.ToUInt16(value ?? 0);
        if (code == 0) return "Healthy";
        if (code == 1) return "Warning";
        if (code == 2) return "Unhealthy";
        return "Unknown (" + code + ")";
    }

    private static string DecodeMediaType(object value)
    {
        var code = Convert.ToUInt16(value ?? 0);
        if (code == 3) return "HDD";
        if (code == 4) return "SSD";
        if (code == 5) return "SCM";
        return "Unspecified";
    }

    private static string DecodeOperationalStatus(object value)
    {
        var values = value as ushort[];
        if (values == null)
        {
            var single = value as ushort?;
            values = single.HasValue ? new[] { single.Value } : new ushort[0];
        }

        if (values.Length == 0)
        {
            return "Unknown";
        }

        return string.Join(", ", values.Select(v => v == 2 ? "OK" : "Status " + v).ToArray());
    }

    private static string FormatBytes(object value)
    {
        double bytes;
        if (value == null || !double.TryParse(Convert.ToString(value), out bytes) || bytes <= 0)
        {
            return "";
        }

        var units = new[] { "bytes", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return Math.Round(bytes, unit == 0 ? 0 : 1).ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        bool createdNew;
        using (var mutex = new System.Threading.Mutex(true, @"Local\OnjSensorReadoutApp", out createdNew))
        {
            if (!createdNew)
            {
                MessageBox.Show(
                    "Sensor Readout is already running.",
                    "Sensor Readout",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SensorReadoutForm());
        }
    }
}
