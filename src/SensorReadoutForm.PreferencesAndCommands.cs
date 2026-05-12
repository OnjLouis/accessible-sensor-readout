using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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

        if (keyCode == Keys.D6 || keyCode == Keys.NumPad6)
        {
            return SelectCategoryByKey("type|Audio");
        }

        if (keyCode == Keys.D7 || keyCode == Keys.NumPad7)
        {
            return SelectCategoryByKey("type|Display");
        }

        if (keyCode == Keys.D8 || keyCode == Keys.NumPad8)
        {
            return SelectCategoryByKey("type|Battery");
        }

        if (keyCode == Keys.D9 || keyCode == Keys.NumPad9)
        {
            return SelectCategoryByKey("type|Devices");
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
            dialog.InstallToLocalAppDataRequested += delegate
            {
                ApplyPreferencesFromDialog(dialog, false, false);
                InstallToLocalAppDataAndRestart();
            };
            dialog.UninstallLocalAppDataRequested += delegate
            {
                ApplyPreferencesFromDialog(dialog, false, false);
                UninstallLocalInstallAndClose();
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

            ApplyPreferencesFromDialog(dialog, true, true);
        }
    }

    private void ApplyPreferencesFromDialog(PreferencesForm dialog, bool updateStartupShortcut, bool refreshAfterSave)
    {
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
        settings.StartupSpeechEnabled = dialog.StartupSpeechEnabled;
        settings.StartupSpeechMessage = dialog.StartupSpeechMessage;
        settings.SpeechIncludesDeviceNames = dialog.SpeechIncludesDeviceNames;
        settings.TrayStatusEnabled = dialog.TrayStatusEnabled;
        settings.RunAtStartup = dialog.RunAtStartup;
        settings.StartMinimizedToTray = dialog.StartMinimizedToTray;
        settings.CheckForUpdatesAtStartup = dialog.CheckForUpdatesAtStartup;
        settings.UpdateCheckFrequency = dialog.UpdateCheckFrequency;
        settings.UpdateAvailableSoundFile = dialog.UpdateAvailableSoundFile;
        settings.DiagnosticsSpeakProgress = dialog.DiagnosticsSpeakProgress;
        settings.DiagnosticsPlaySounds = dialog.DiagnosticsPlaySounds;
        settings.DiagnosticsStartSoundFile = dialog.DiagnosticsStartSoundFile;
        settings.DiagnosticsCompleteSoundFile = dialog.DiagnosticsCompleteSoundFile;
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

        if (updateStartupShortcut)
        {
            try
            {
                SetRunAtStartup(settings.RunAtStartup, settings.StartMinimizedToTray);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not update Windows startup shortcut: " + ex.Message, "Sensor Readout startup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        if (!refreshAfterSave)
        {
            return;
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

    private void ImportPlugInFromZip()
    {
        if (!PlugInZipImporter.PromptAndImport(this, settings))
        {
            return;
        }

        plugInManager = null;
        statusLabel.Text = "Plug-In imported. Enable it from Options, Preferences, Plug-Ins.";
        RefreshSensors(false, false, "plug-in import");
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

    private void ShowReadingSearchDialog()
    {
        var rows = latestRows
            .Where(r => r != null && !IsHiddenSearchRow(r))
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .Cast<object>()
            .ToList();
        var selected = ShowSearchDialog(
            this,
            L("ui.Find reading", "Find reading"),
            L("ui.Search:", "Search:"),
            rows,
            delegate(object item) { return SearchReadingDisplayText((SensorRow)item); },
            delegate(object item) { return SearchReadingSearchText((SensorRow)item); });
        var row = selected as SensorRow;
        if (row == null)
        {
            return;
        }

        SelectReadingSearchResult(row);
    }

    private bool IsHiddenSearchRow(SensorRow row)
    {
        if (reportViewMode || settings.HiddenReadingKeys == null || row == null)
        {
            return false;
        }

        return settings.HiddenReadingKeys.Contains("row|" + RowSettingsKey(row));
    }

    private static string SearchReadingDisplayText(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        return DisplayTypeName(row.Type) + " - " + TrayChoiceLabel(row) + " - " + FormatValue(row);
    }

    private static string SearchReadingSearchText(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        var detailText = row.Details == null ? "" : string.Join(" ", row.Details.Select(kv => (kv.Key ?? "") + " " + (kv.Value ?? "")).ToArray());
        return SearchReadingDisplayText(row) + " " + row.Type + " " + row.Hardware + " " + row.Name + " " + row.Identifier + " " + row.DisplayValue + " " + detailText;
    }

    private void SelectReadingSearchResult(SensorRow row)
    {
        var rowKey = "row|" + RowSettingsKey(row);
        if (!SelectCategoryByKey("type|" + row.Type))
        {
            SelectCategoryByKey("type|Performance");
        }

        UpdateReadingList(rowKey);
        var node = FindTreeNode(readingTree.Nodes, rowKey);
        if (node == null)
        {
            System.Media.SystemSounds.Beep.Play();
            statusLabel.Text = L("status.Search result is hidden or unavailable.", "Search result is hidden or unavailable.");
            return;
        }

        readingTree.SelectedNode = node;
        node.EnsureVisible();
        readingTree.Focus();
        statusLabel.Text = L("status.Search result selected.", "Search result selected.");
    }

    private void ShowSelectedTreeTextReview()
    {
        if (readingTree.SelectedNode == null)
        {
            return;
        }

        var lines = new List<string>();
        AddTreeNodeText(readingTree.SelectedNode, lines, 0);
        if (lines.Count == 0)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, lines.ToArray());
        using (var dialog = new Form())
        {
            dialog.Text = L("ui.Review text", "Review text");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(720, 360);
            dialog.MinimumSize = new Size(420, 220);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = text,
                AccessibleName = L("a11y.Review text", "Review text"),
                AccessibleDescription = L("a11y.Read-only text for the selected reading or branch.", "Read-only text for the selected reading or branch.")
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var closeButton = new Button { Text = L("ui.Close", "Close"), DialogResult = DialogResult.OK, AutoSize = true };
            var copyButton = new Button { Text = L("ui.&Copy", "&Copy"), AutoSize = true };
            copyButton.Click += delegate
            {
                Clipboard.SetText(textBox.Text);
                statusLabel.Text = L("status.Review text copied to clipboard.", "Review text copied to clipboard.");
                AnnounceCopiedToClipboard();
            };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(copyButton);

            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            };
            dialog.Shown += delegate
            {
                textBox.Focus();
                textBox.Select(0, 0);
            };

            dialog.Controls.Add(textBox);
            dialog.Controls.Add(buttons);
            dialog.AcceptButton = closeButton;
            dialog.CancelButton = closeButton;
            dialog.ShowDialog(this);
        }
    }

    public static object ShowSearchDialog(IWin32Window owner, string title, string searchLabelText, IEnumerable<object> choices, Func<object, string> displayText, Func<object, string> searchText)
    {
        var allChoices = (choices ?? Enumerable.Empty<object>()).Where(i => i != null).ToList();
        object selectedChoice = null;

        using (var dialog = new Form())
        {
            dialog.Text = title;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(760, 430);
            dialog.MinimumSize = new Size(440, 260);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var searchLabel = new Label { Text = searchLabelText, AutoSize = true, Dock = DockStyle.Fill };
            var searchBox = new TextBox
            {
                Dock = DockStyle.Fill,
                AccessibleName = L("a11y.Search text", "Search text"),
                AccessibleDescription = L("a11y.Type to narrow the results.", "Type to narrow the results.")
            };
            var resultList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                AccessibleName = L("a11y.Search results", "Search results"),
                AccessibleDescription = L("a11y.Matching results. Press Enter to choose the selected result.", "Matching results. Press Enter to choose the selected result.")
            };
            var status = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "" };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = new Button { Text = L("ui.Close", "Close"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var selectButton = new Button { Text = L("ui.&Select", "&Select"), AutoSize = true };
            var clearButton = new Button { Text = L("ui.C&lear", "C&lear"), AutoSize = true };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(selectButton);
            buttons.Controls.Add(clearButton);

            layout.Controls.Add(searchLabel, 0, 0);
            layout.Controls.Add(searchBox, 0, 1);
            layout.Controls.Add(resultList, 0, 2);
            layout.Controls.Add(status, 0, 3);
            layout.Controls.Add(buttons, 0, 4);

            Action refreshResults = delegate
            {
                var terms = NormalizeSearchQuery(searchBox.Text);
                var matches = allChoices
                    .Where(i => SearchChoiceMatches(i, terms, displayText, searchText))
                    .ToList();
                resultList.BeginUpdate();
                try
                {
                    resultList.Items.Clear();
                    foreach (var match in matches)
                    {
                        resultList.Items.Add(new SearchListItem(match, displayText));
                    }
                }
                finally
                {
                    resultList.EndUpdate();
                }

                if (resultList.Items.Count > 0)
                {
                    resultList.SelectedIndex = 0;
                }

                status.Text = resultList.Items.Count == 1
                    ? L("ui.1 result", "1 result")
                    : string.Format(L("ui.{0} results", "{0} results"), resultList.Items.Count);
            };

            Action acceptSelection = delegate
            {
                var item = resultList.SelectedItem as SearchListItem;
                if (item == null)
                {
                    System.Media.SystemSounds.Beep.Play();
                    return;
                }

                selectedChoice = item.Value;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };

            searchBox.TextChanged += delegate { refreshResults(); };
            searchBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Down && resultList.Items.Count > 0)
                {
                    resultList.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            resultList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    acceptSelection();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            resultList.DoubleClick += delegate { acceptSelection(); };
            selectButton.Click += delegate { acceptSelection(); };
            clearButton.Click += delegate
            {
                searchBox.Clear();
                searchBox.Focus();
            };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            dialog.Shown += delegate
            {
                refreshResults();
                searchBox.Focus();
            };

            dialog.Controls.Add(layout);
            dialog.AcceptButton = selectButton;
            dialog.CancelButton = closeButton;
            dialog.ShowDialog(owner);
        }

        return selectedChoice;
    }

    private static string[] NormalizeSearchQuery(string query)
    {
        return (query ?? "")
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();
    }

    private static bool SearchChoiceMatches(object item, string[] terms, Func<object, string> displayText, Func<object, string> searchText)
    {
        if (terms == null || terms.Length == 0)
        {
            return true;
        }

        var text = ((displayText == null ? "" : displayText(item)) + " " + (searchText == null ? "" : searchText(item)) ?? "").ToUpperInvariant();
        return terms.All(term => text.IndexOf(term.ToUpperInvariant(), StringComparison.Ordinal) >= 0);
    }

    private sealed class SearchListItem
    {
        private readonly Func<object, string> displayText;

        public SearchListItem(object value, Func<object, string> displayText)
        {
            Value = value;
            this.displayText = displayText;
        }

        public object Value { get; private set; }

        public override string ToString()
        {
            return displayText == null ? "" : displayText(Value);
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
        if (!CanRenameReadingRow(row))
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

    private void UpdateRenameMenuVisibility()
    {
        UpdateSelectedTreeCommandVisibility();
    }

    private void UpdateSelectedTreeCommandVisibility()
    {
        var visible = CanRenameSelectedTreeNode();
        if (editRenameMenuItem != null)
        {
            editRenameMenuItem.Visible = visible;
        }

        if (treeDetailsMenuItem != null)
        {
            treeDetailsMenuItem.Visible = CanShowSelectedReadingDetails();
        }

        if (treeRenameMenuItem != null)
        {
            treeRenameMenuItem.Visible = visible;
        }
    }

    private bool CanRenameSelectedTreeNode()
    {
        return CanRenameReadingRow(GetSelectedReadingRow());
    }

    private static bool CanRenameReadingRow(SensorRow row)
    {
        return row != null && (row.Type == "Fan" || row.Type == "Fan Control");
    }

    private bool CanShowSelectedReadingDetails()
    {
        var row = GetSelectedReadingRow();
        return row != null && row.Details != null && row.Details.Count > 0;
    }

    private void UpdateViewMenuVisibility()
    {
        if (batteryViewMenuItem != null)
        {
            batteryViewMenuItem.Visible = latestRows.Any(r => string.Equals(r.Type, "Battery", StringComparison.OrdinalIgnoreCase));
        }
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
