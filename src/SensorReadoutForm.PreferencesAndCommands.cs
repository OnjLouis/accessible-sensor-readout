using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private bool SelectCategoryByShortcut(Keys keyCode)
    {
        if (keyCode == Keys.D0 || keyCode == Keys.NumPad0)
        {
            return SelectCategoryByKey("type|Performance");
        }

        if (keyCode == Keys.D1 || keyCode == Keys.NumPad1)
        {
            return SelectCategoryByKey("type|Temperature");
        }

        if (keyCode == Keys.D2 || keyCode == Keys.NumPad2)
        {
            return SelectCategoryByKey("type|Fan");
        }

        if (keyCode == Keys.D3 || keyCode == Keys.NumPad3)
        {
            return SelectCategoryByKey("type|SMART");
        }

        if (keyCode == Keys.D4 || keyCode == Keys.NumPad4)
        {
            return SelectCategoryByKey("type|Network");
        }

        if (keyCode == Keys.D5 || keyCode == Keys.NumPad5)
        {
            return SelectCategoryByKey("type|USB");
        }

        return false;
    }

    private bool SelectCategoryByKey(string key)
    {
        var keepTreeFocus = readingTree != null && readingTree.ContainsFocus;
        for (var i = 0; i < deviceList.Items.Count; i++)
        {
            var filter = deviceList.Items[i] as DeviceFilter;
            if (filter != null && filter.Key == key)
            {
                deviceList.SelectedIndex = i;
                if (keepTreeFocus)
                {
                    readingTree.Focus();
                }
                else
                {
                    deviceList.Focus();
                }
                return true;
            }
        }

        return false;
    }

    private void ShowPreferences()
    {
        RefreshLanguageChoices(false);
        using (var dialog = new PreferencesForm(settings, latestRows, languageChoices, lastPreferencesTabName))
        {
            openPreferencesDialog = dialog;
            dialog.ApplyFanProfileRequested += delegate(FanProfileSetting profile)
            {
                ApplyFanProfile(profile, true);
            };
            if (latestRows.Count > 0)
            {
                dialog.UpdateSensorRows(latestRows);
            }
            var result = dialog.ShowDialog(this);
            if (openPreferencesDialog == dialog)
            {
                openPreferencesDialog = null;
            }
            lastPreferencesTabName = dialog.SelectedTabName;
            if (result != DialogResult.OK)
            {
                return;
            }

            settings.AutoRefreshEnabled = dialog.AutoRefreshEnabled;
            settings.RefreshWhileFocused = dialog.RefreshWhileFocused;
            settings.RefreshIntervalSeconds = dialog.RefreshIntervalSeconds;
            settings.TemperatureUnit = dialog.TemperatureUnit;
            settings.DecimalSeparator = dialog.DecimalSeparator;
            settings.LanguageFile = dialog.LanguageFile;
            settings.LanguagePreferenceInitialized = true;
            settings.ShowHideHotKey = dialog.ShowHideHotKey;
            settings.SpeakTrayHotKey = dialog.SpeakTrayHotKey;
            settings.HotKeyCopyDoublePressMs = dialog.HotKeyCopyDoublePressMs;
            settings.StartupSpeechMessage = dialog.StartupSpeechMessage;
            settings.SpeechIncludesDeviceNames = dialog.SpeechIncludesDeviceNames;
            settings.TrayStatusEnabled = dialog.TrayStatusEnabled;
            settings.RunAtStartup = dialog.RunAtStartup;
            settings.StartMinimizedToTray = dialog.StartMinimizedToTray;
            settings.CheckForUpdatesAtStartup = dialog.CheckForUpdatesAtStartup;
            settings.UpdateCheckFrequency = dialog.UpdateCheckFrequency;
            if (settings.RunAtStartup || settings.StartMinimizedToTray)
            {
                settings.TrayStatusEnabled = true;
            }
            settings.LoggingLevel = dialog.LoggingLevel;
            settings.TrayItemKeys = dialog.TrayItemKeys;
            settings.SpokenHotKeys = dialog.SpokenHotKeys;
            settings.FanProfiles = dialog.FanProfiles;
            settings.Alarms = dialog.Alarms;
            settings.StartupSoundFile = dialog.StartupSoundFile;
            settings.ShutdownSoundFile = dialog.ShutdownSoundFile;
            settings.HiddenReadingKeys = dialog.HiddenReadingKeys;
            settings.ReadingSpeechLabels = dialog.ReadingSpeechLabels;
            settings.PlugInsEnabled = dialog.PlugInsEnabled;
            plugInManager = null;
            SaveSettings(settings);
            try
            {
                SetRunAtStartup(settings.RunAtStartup, settings.StartMinimizedToTray);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not update Windows startup shortcut: " + ex.Message, "Sensor Readout startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (settings.TrayStatusEnabled)
            {
                ShowInTaskbar = true;
            }
            else
            {
                Show();
                ShowInTaskbar = true;
                WindowState = FormWindowState.Normal;
            }

            autoRefreshMenuItem.Checked = settings.AutoRefreshEnabled;
            refreshWhileFocusedMenuItem.Checked = settings.RefreshWhileFocused;
            trayStatusMenuItem.Checked = settings.TrayStatusEnabled;
            pauseCheckBox.Checked = !settings.AutoRefreshEnabled;
            activeTemperatureUnit = settings.TemperatureUnit;
            activeDecimalSeparator = settings.DecimalSeparator;
            LoadSelectedLanguage();
            UpdateTemperatureUnitMenu();
            BuildLanguageMenu();
            ApplyLanguage();
            RegisterGlobalHotKeys();
            ApplyTimerSettings();
            StartAutomaticUpdateChecks();
            RefreshSensors(false, false, "plug-in preferences");
            statusLabel.Text = "Preferences saved.";
        }
    }

    private void CopySelectedTreeNode()
    {
        if (readingTree.SelectedNode == null)
        {
            return;
        }

        var lines = new List<string>();
        AddTreeNodeText(readingTree.SelectedNode, lines, 0);
        if (lines.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
            statusLabel.Text = "Copied " + lines.Count + " line" + (lines.Count == 1 ? "" : "s") + " to clipboard.";
        }
    }

    private static void AddTreeNodeText(TreeNode node, List<string> lines, int depth)
    {
        lines.Add(new string(' ', depth * 2) + node.Text);
        foreach (TreeNode child in node.Nodes)
        {
            AddTreeNodeText(child, lines, depth + 1);
        }
    }

    private void HideSelectedTreeNode()
    {
        if (readingTree.SelectedNode == null || string.IsNullOrWhiteSpace(readingTree.SelectedNode.Name))
        {
            return;
        }

        settings.HiddenReadingKeys = settings.HiddenReadingKeys ?? new List<string>();
        var fallbackKey = FindHideFallbackKey(readingTree.SelectedNode);
        if (!settings.HiddenReadingKeys.Contains(readingTree.SelectedNode.Name))
        {
            settings.HiddenReadingKeys.Add(readingTree.SelectedNode.Name);
            SaveSettings(settings);
        }

        statusLabel.Text = "Hidden " + readingTree.SelectedNode.Text + ". Use Options, Preferences, Hidden items to show it again.";
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList(fallbackKey);
    }

    private void RenameSelectedTreeNode()
    {
        var row = GetSelectedReadingRow();
        if (row == null || (row.Type != "Fan" && row.Type != "Fan Control"))
        {
            statusLabel.Text = "Select a fan reading before renaming.";
            return;
        }

        var controlIdentifier = row.Type == "Fan Control" ? row.Identifier : GuessControlIdentifier(row.Identifier);
        if (string.IsNullOrWhiteSpace(controlIdentifier))
        {
            statusLabel.Text = "Could not match this fan reading to a fan control.";
            return;
        }

        var labels = LoadFanLabels();
        string currentLabel;
        labels.TryGetValue(controlIdentifier, out currentLabel);
        var baseName = BaseFanReadingName(row.Name);
        var newLabel = PromptForText("Rename Fan", "Friendly name for " + baseName + ":", currentLabel ?? "");
        if (newLabel == null)
        {
            return;
        }

        newLabel = newLabel.Trim();
        if (string.IsNullOrWhiteSpace(newLabel))
        {
            labels.Remove(controlIdentifier);
            labels.Remove(GuessFanIdentifier(controlIdentifier));
        }
        else
        {
            labels[controlIdentifier] = newLabel;
            labels[GuessFanIdentifier(controlIdentifier)] = newLabel;
        }

        SaveFanLabels(labels);
        statusLabel.Text = string.IsNullOrWhiteSpace(newLabel) ? "Removed fan label for " + baseName + "." : "Renamed " + baseName + " to " + newLabel + ".";
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        RefreshSensors();
    }

    private string PromptForText(string title, string label, string initialValue)
    {
        using (var dialog = new Form())
        {
            dialog.Text = title;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(460, 150);
            dialog.MinimumSize = new Size(360, 140);
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var promptLabel = new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill };
            var textBox = new TextBox { Text = initialValue ?? "", Dock = DockStyle.Fill, AccessibleName = label };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);

            layout.Controls.Add(promptLabel, 0, 0);
            layout.Controls.Add(textBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;
            dialog.Shown += delegate { textBox.Focus(); textBox.SelectAll(); };

            return dialog.ShowDialog(this) == DialogResult.OK ? textBox.Text : null;
        }
    }

}
