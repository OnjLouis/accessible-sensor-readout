using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private bool SelectCategoryByShortcut(Keys keyCode)
    {
        return SelectCategoryByShortcut(keyCode, 0);
    }

    private bool SelectCategoryByShortcut(Keys keyCode, int offset)
    {
        var digit = DigitFromShortcutKey(keyCode);
        if (digit < 0 || deviceList == null)
        {
            return false;
        }

        var index = offset + digit;
        if (index < 0 || index >= deviceList.Items.Count)
        {
            return false;
        }

        var filter = deviceList.Items[index] as DeviceFilter;
        return filter != null && SelectCategoryByKey(filter.Key);
    }

    private static int DigitFromShortcutKey(Keys keyCode)
    {
        if (keyCode >= Keys.D0 && keyCode <= Keys.D9)
        {
            return keyCode - Keys.D0;
        }

        if (keyCode >= Keys.NumPad0 && keyCode <= Keys.NumPad9)
        {
            return keyCode - Keys.NumPad0;
        }

        return -1;
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

    private void RebuildViewMenu(ToolStripMenuItem viewMenu)
    {
        if (viewMenu == null)
        {
            return;
        }

        viewMenu.DropDownItems.Clear();
        var filters = deviceList == null
            ? new List<DeviceFilter>()
            : deviceList.Items.Cast<object>().OfType<DeviceFilter>().ToList();
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            if (filter == null || string.IsNullOrWhiteSpace(filter.Key))
            {
                continue;
            }

            var shortcutText = CategoryShortcutText(i);
            var text = filter.DisplayName;
            if (!string.IsNullOrWhiteSpace(shortcutText))
            {
                text += "\t" + shortcutText;
            }

            var key = filter.Key;
            viewMenu.DropDownItems.Add(text, null, delegate { SelectCategoryByKey(key); });
        }

        if (viewMenu.DropDownItems.Count > 0)
        {
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
        }
        viewMenu.DropDownItems.Add(CreateShortcutMenuItem("&Refresh now", Keys.F5, delegate { RefreshSensors(); }));
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(CreateShortcutMenuItem("&Expand all", Keys.Control | Keys.Shift | Keys.Right, delegate { ExpandAllReadings(); }));
        viewMenu.DropDownItems.Add(CreateShortcutMenuItem("C&ollapse all", Keys.Control | Keys.Shift | Keys.Left, delegate { CollapseAllReadings(); }));
    }

    private static string CategoryShortcutText(int index)
    {
        if (index >= 0 && index <= 9)
        {
            return "Ctrl+" + index.ToString();
        }

        if (index >= 10 && index <= 19)
        {
            return "Ctrl+Shift+" + (index - 10).ToString();
        }

        return "";
    }

    private void ShowPreferences()
    {
        ShowPreferences(lastPreferencesTabName);
    }

    private void ShowPreferences(string initialTabName)
    {
        RefreshLanguageChoices(false);
        using (var dialog = new PreferencesForm(settings, latestRows, languageChoices, string.IsNullOrWhiteSpace(initialTabName) ? lastPreferencesTabName : initialTabName))
        {
            openPreferencesDialog = dialog;
            dialog.LivePreferencesSaved += delegate
            {
                ApplyLivePreferencesFromOpenDialog(dialog);
            };
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
            dialog.ResetAllSettingsRequested += delegate
            {
                ResetAllSettingsAndRestart(dialog);
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

    private void ApplyLivePreferencesFromOpenDialog(PreferencesForm dialog)
    {
        if (dialog == null || dialog.IsDisposed)
        {
            return;
        }

        var previousReadingTreeExpansionMode = lastAppliedReadingTreeExpansionMode;
        ApplyPreferencesFromDialog(dialog, false, false);
        autoRefreshMenuItem.Checked = settings.AutoRefreshEnabled;
        refreshWhileFocusedMenuItem.Checked = settings.RefreshWhileFocused;
        trayStatusMenuItem.Checked = settings.TrayStatusEnabled;
        pauseCheckBox.Checked = !settings.AutoRefreshEnabled;
        activeTemperatureUnit = settings.TemperatureUnit;
        activeDecimalSeparator = settings.DecimalSeparator;
        RegisterGlobalHotKeys();
        BuildHotkeysMenu();
        UpdateTrayStatus();
        ApplyTimerSettings();
        if (!string.Equals(previousReadingTreeExpansionMode, settings.ReadingTreeExpansionMode, StringComparison.OrdinalIgnoreCase))
        {
            UpdateReadingList();
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
        settings.TrayTooltipShowsPartialReadings = dialog.TrayTooltipShowsPartialReadings;
        settings.ReadingTreeExpansionMode = dialog.ReadingTreeExpansionMode;
        settings.ShowTipsOnStartup = dialog.ShowTipsOnStartup;
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
        settings.CategoryOrderKeys = dialog.CategoryOrderKeys;
        settings.HiddenCategoryKeys = dialog.HiddenCategoryKeys;
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
        selectedFilterKey = deviceList.SelectedItem is DeviceFilter ? ((DeviceFilter)deviceList.SelectedItem).Key : selectedFilterKey;
        UpdateDeviceList();
        RegisterGlobalHotKeys();
        ApplyTimerSettings();
        StartAutomaticUpdateChecks();
        RefreshSensors(false, false, "plug-in preferences");
        statusLabel.Text = L("status.Preferences saved.", "Preferences saved.");
    }

    private void ImportPlugInFromZip()
    {
        if (!PlugInZipImporter.PromptAndImport(this, settings))
        {
            return;
        }

        plugInManager = null;
        statusLabel.Text = L("status.Plug-In imported. Enable it from Options, Preferences, Plug-Ins.", "Plug-In imported. Enable it from Options, Preferences, Plug-Ins.");
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
            statusLabel.Text = string.Format(L("status.Copied lines to clipboard.", "Copied {0} line{1} to clipboard."), lines.Count, lines.Count == 1 ? "" : "s");
        }
    }

    private void CopySelectedTreeNodeValueOnly()
    {
        if (readingTree.SelectedNode == null)
        {
            return;
        }

        var lines = new List<string>();
        AddTreeNodeValueText(readingTree.SelectedNode, lines);
        if (lines.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
            statusLabel.Text = string.Format(L("status.Copied values to clipboard.", "Copied {0} value{1} to clipboard."), lines.Count, lines.Count == 1 ? "" : "s");
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
            var closeButton = CreateCloseButton();
            closeButton.DialogResult = DialogResult.OK;
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
            var closeButton = CreateCloseButton();
            closeButton.DialogResult = DialogResult.Cancel;
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
                var matches = FilterSearchChoices(allChoices, terms, displayText, searchText);
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

        var text = ((displayText == null ? "" : displayText(item)) + " " + (searchText == null ? "" : searchText(item)) ?? "");
        return SearchTextMatches(text, terms);
    }

    private static List<object> FilterSearchChoices(List<object> allChoices, string[] terms, Func<object, string> displayText, Func<object, string> searchText)
    {
        if (allChoices == null)
        {
            return new List<object>();
        }

        if (terms == null || terms.Length == 0)
        {
            return allChoices.ToList();
        }

        var visibleMatches = allChoices
            .Where(i => SearchTextMatches(displayText == null ? "" : displayText(i), terms))
            .ToList();
        if (visibleMatches.Count > 0)
        {
            return visibleMatches;
        }

        return allChoices
            .Where(i => SearchChoiceMatches(i, terms, displayText, searchText))
            .ToList();
    }

    private static bool SearchTextMatches(string text, string[] terms)
    {
        if (terms == null || terms.Length == 0)
        {
            return true;
        }

        text = (text ?? "").ToUpperInvariant();
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

    private static void AddTreeNodeValueText(TreeNode node, List<string> lines)
    {
        if (node == null || lines == null)
        {
            return;
        }

        var row = node.Tag as SensorRow;
        if (row != null)
        {
            var value = FormatValue(row);
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(value);
            }
            return;
        }

        if (node.Nodes.Count == 0)
        {
            var value = TextAfterFirstColon(node.Text);
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, node.Text, StringComparison.Ordinal))
            {
                lines.Add(value);
            }
            return;
        }

        foreach (TreeNode child in node.Nodes)
        {
            AddTreeNodeValueText(child, lines);
        }
    }

    private static string TextAfterFirstColon(string text)
    {
        text = text ?? "";
        var index = text.IndexOf(':');
        return index < 0 || index + 1 >= text.Length ? text.Trim() : text.Substring(index + 1).Trim();
    }

    private void HideSelectedTreeNode()
    {
        if (readingTree.SelectedNode == null || string.IsNullOrWhiteSpace(readingTree.SelectedNode.Name))
        {
            return;
        }

        settings.HiddenReadingKeys = settings.HiddenReadingKeys ?? new List<string>();
        var fallbackKey = FindHideFallbackKey(readingTree.SelectedNode);
        var hiddenKey = readingTree.SelectedNode.Name;
        var hiddenText = readingTree.SelectedNode.Text;
        if (!settings.HiddenReadingKeys.Contains(hiddenKey))
        {
            settings.HiddenReadingKeys.Add(hiddenKey);
            RegisterUndo(
                string.Format(L("ui.Hide reading action", "Hide {0}"), hiddenText),
                delegate
                {
                    settings.HiddenReadingKeys.RemoveAll(k => string.Equals(k, hiddenKey, StringComparison.OrdinalIgnoreCase));
                    SaveSettings(settings);
                    lastReadingTreeSignature = "";
                    lastReadingTreeShapeSignature = "";
                    UpdateReadingList();
                },
                delegate
                {
                    settings.HiddenReadingKeys = settings.HiddenReadingKeys ?? new List<string>();
                    if (!settings.HiddenReadingKeys.Contains(hiddenKey))
                    {
                        settings.HiddenReadingKeys.Add(hiddenKey);
                    }
                    SaveSettings(settings);
                    lastReadingTreeSignature = "";
                    lastReadingTreeShapeSignature = "";
                    UpdateReadingList();
                });
            SaveSettings(settings);
        }

            statusLabel.Text = string.Format(L("status.Hidden reading. Use preferences to show it again.", "Hidden {0}. Use Options, Preferences, Hidden items to show it again."), readingTree.SelectedNode.Text);
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateReadingList(fallbackKey);
    }

    private void HideSelectedCategory()
    {
        var filter = deviceList == null ? null : deviceList.SelectedItem as DeviceFilter;
        if (filter == null || string.IsNullOrWhiteSpace(filter.Key))
        {
            return;
        }

        var selectedIndex = deviceList.SelectedIndex;
        settings.HiddenCategoryKeys = settings.HiddenCategoryKeys ?? new List<string>();
        if (!settings.HiddenCategoryKeys.Contains(filter.Key, StringComparer.OrdinalIgnoreCase))
        {
            var hiddenKey = filter.Key;
            var hiddenName = filter.DisplayName;
            settings.HiddenCategoryKeys.Add(hiddenKey);
            RegisterUndo(
                string.Format(L("ui.Hide category action", "Hide {0} category"), hiddenName),
                delegate
                {
                    settings.HiddenCategoryKeys.RemoveAll(k => string.Equals(k, hiddenKey, StringComparison.OrdinalIgnoreCase));
                    SaveSettings(settings);
                    UpdateDeviceList();
                    SelectCategoryByKey(hiddenKey);
                },
                delegate
                {
                    settings.HiddenCategoryKeys = settings.HiddenCategoryKeys ?? new List<string>();
                    if (!settings.HiddenCategoryKeys.Contains(hiddenKey))
                    {
                        settings.HiddenCategoryKeys.Add(hiddenKey);
                    }
                    SaveSettings(settings);
                    UpdateDeviceList();
                });
            SaveSettings(settings);
        }

        statusLabel.Text = string.Format(L("status.Hidden category. Use preferences to show it again.", "Hidden {0}. Use Options, Preferences, Hidden items to show it again."), filter.DisplayName);
        UpdateDeviceList();
        if (deviceList.Items.Count > 0)
        {
            deviceList.SelectedIndex = Math.Min(selectedIndex, deviceList.Items.Count - 1);
        }
        UpdateSelectedCategoryStatus();
        statusLabel.Text = string.Format(L("status.Hidden category. Use preferences to show it again.", "Hidden {0}. Use Options, Preferences, Hidden items to show it again."), filter.DisplayName);
        UpdateUndoRedoMenuItems();
    }

    private void MoveSelectedCategory(int direction)
    {
        if (deviceList == null || direction == 0)
        {
            return;
        }

        var index = deviceList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var target = index + direction;
        if (target < 0 || target >= deviceList.Items.Count)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var currentFilter = deviceList.Items[index] as DeviceFilter;
        if (currentFilter == null || string.IsNullOrWhiteSpace(currentFilter.Key))
        {
            return;
        }

        var visibleOrder = deviceList.Items
            .Cast<object>()
            .OfType<DeviceFilter>()
            .Select(f => f.Key)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToList();
        var key = visibleOrder[index];
        visibleOrder.RemoveAt(index);
        visibleOrder.Insert(target, key);

        var knownOrder = (settings.CategoryOrderKeys ?? new List<string>())
            .Concat(DefaultCategoryChoices().Select(c => c.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !visibleOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        settings.CategoryOrderKeys = visibleOrder.Concat(knownOrder).ToList();
        selectedFilterKey = currentFilter.Key;
        SaveSettings(settings);
        UpdateDeviceList();
        SelectCategoryByKey(currentFilter.Key);
        UpdateSelectedCategoryStatus();
        statusLabel.Text = string.Format(
            L("status.Moved category.", "Moved {0}. Shortcut is now {1}."),
            currentFilter.DisplayName,
            CategoryShortcutText(deviceList.SelectedIndex));
    }

    private void UpdateSelectedCategoryCommandVisibility()
    {
        if (deviceList == null || deviceList.ContextMenuStrip == null)
        {
            return;
        }

        var hasSelection = deviceList.SelectedIndex >= 0;
        foreach (ToolStripItem item in deviceList.ContextMenuStrip.Items)
        {
            if (item is ToolStripSeparator)
            {
                continue;
            }

            item.Enabled = hasSelection;
        }

        if (!hasSelection)
        {
            return;
        }

        if (deviceList.ContextMenuStrip.Items.Count >= 2)
        {
            deviceList.ContextMenuStrip.Items[0].Enabled = deviceList.SelectedIndex > 0;
            deviceList.ContextMenuStrip.Items[1].Enabled = deviceList.SelectedIndex < deviceList.Items.Count - 1;
        }
    }

    private void UpdateSelectedCategoryStatus()
    {
        if (deviceList == null || statusLabel == null)
        {
            return;
        }

        var filter = deviceList.SelectedItem as DeviceFilter;
        if (filter == null)
        {
            return;
        }

        var shortcut = CategoryShortcutText(deviceList.SelectedIndex);
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            statusLabel.Text = string.Format(L("status.Category selected.", "{0} category selected."), filter.DisplayName);
            return;
        }

        var message = string.Format(L("status.Category selected with shortcut.", "{0} category selected. Shortcut {1}."), filter.DisplayName, shortcut);
        statusLabel.Text = message;
        if (!deviceList.ContainsFocus)
        {
            return;
        }

        string error;
        if (!ScreenReaderOutput.TrySpeakPolite(message, out error))
        {
            LogMessage("Debug", "Category shortcut hint was not spoken. " + error);
        }
    }

    private void RegisterUndo(string actionName, Action undoAction, Action redoAction)
    {
        lastUndoEntry = new UndoRedoEntry(actionName, undoAction, redoAction);
        lastRedoEntry = null;
    }

    private void UndoLastAction()
    {
        var entry = lastUndoEntry;
        if (entry == null || entry.UndoAction == null)
        {
            statusLabel.Text = L("status.Nothing to undo.", "Nothing to undo.");
            return;
        }

        lastUndoEntry = null;
        entry.UndoAction();
        lastRedoEntry = entry.RedoAction == null ? null : entry;
        statusLabel.Text = string.Format(L("status.Undid action.", "Undid {0}."), entry.ActionName);
        UpdateUndoRedoMenuItems();
    }

    private void RedoLastAction()
    {
        var entry = lastRedoEntry;
        if (entry == null || entry.RedoAction == null)
        {
            statusLabel.Text = L("status.Nothing to redo.", "Nothing to redo.");
            return;
        }

        lastRedoEntry = null;
        entry.RedoAction();
        lastUndoEntry = entry.UndoAction == null ? null : entry;
        statusLabel.Text = string.Format(L("status.Redid action.", "Redid {0}."), entry.ActionName);
        UpdateUndoRedoMenuItems();
    }

    private void UpdateEditMenuOpening()
    {
        UpdateRenameMenuVisibility();
        UpdateUndoRedoMenuItems();
    }

    private void UpdateUndoRedoMenuItems()
    {
        if (editUndoMenuItem != null)
        {
            editUndoMenuItem.Text = WithShortcutText(
                lastUndoEntry == null
                    ? L("ui.&Undo", "&Undo")
                    : string.Format(L("ui.&Undo action", "&Undo {0}"), lastUndoEntry.ActionName),
                "Ctrl+Z");
            editUndoMenuItem.Enabled = lastUndoEntry != null;
        }

        if (editRedoMenuItem != null)
        {
            editRedoMenuItem.Text = WithShortcutText(
                lastRedoEntry == null
                    ? L("ui.&Redo", "&Redo")
                    : string.Format(L("ui.&Redo action", "&Redo {0}"), lastRedoEntry.ActionName),
                "Ctrl+Y");
            editRedoMenuItem.Enabled = lastRedoEntry != null;
        }
    }

    private void RenameSelectedTreeNode()
    {
        var row = GetSelectedReadingRow();
        if (!CanRenameReadingRow(row))
        {
            statusLabel.Text = L("status.Select a fan reading before renaming.", "Select a fan reading before renaming.");
            return;
        }

        var controlIdentifier = row.Type == "Fan Control" ? row.Identifier : GuessControlIdentifier(row.Identifier);
        if (string.IsNullOrWhiteSpace(controlIdentifier))
        {
            statusLabel.Text = L("status.Could not match this fan reading to a fan control.", "Could not match this fan reading to a fan control.");
            return;
        }

        var labels = LoadFanLabels();
        string currentLabel;
        labels.TryGetValue(controlIdentifier, out currentLabel);
        var baseName = BaseFanReadingName(row.Name);
        var newLabel = PromptForText(L("ui.Rename Fan", "Rename Fan"), string.Format(L("ui.Friendly name for fan:", "Friendly name for {0}:"), baseName), currentLabel ?? "");
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
        statusLabel.Text = string.IsNullOrWhiteSpace(newLabel)
            ? string.Format(L("status.Removed fan label for.", "Removed fan label for {0}."), baseName)
            : string.Format(L("status.Renamed fan to.", "Renamed {0} to {1}."), baseName, newLabel);
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

        var spokenHotKeyVisible = CanAssignSelectedReadingToSpokenHotKey();
        if (hotkeysSpokenHotKeyMenuItem != null)
        {
            hotkeysSpokenHotKeyMenuItem.Visible = spokenHotKeyVisible;
        }

        if (editSpokenHotKeyMenuItem != null)
        {
            editSpokenHotKeyMenuItem.Visible = spokenHotKeyVisible;
        }

        var windowsSettingVisible = CanOpenSelectedWindowsSetting();
        var windowsSettingTarget = GetRelatedWindowsSettingsTarget(GetSelectedReadingRow());
        var windowsSettingText = windowsSettingTarget != null && !string.IsNullOrWhiteSpace(windowsSettingTarget.FilePath)
            ? T("ui.Open file &location...", "Open file &location...")
            : T("ui.Open &Windows setting...", "Open &Windows setting...");
        if (editWindowsSettingMenuItem != null)
        {
            editWindowsSettingMenuItem.Visible = windowsSettingVisible;
            editWindowsSettingMenuItem.Text = windowsSettingText;
        }

        if (treeDetailsMenuItem != null)
        {
            treeDetailsMenuItem.Visible = CanShowSelectedReadingDetails();
        }

        if (treeWindowsSettingMenuItem != null)
        {
            treeWindowsSettingMenuItem.Visible = windowsSettingVisible;
            treeWindowsSettingMenuItem.Text = windowsSettingText;
        }

        if (treeSpokenHotKeyMenuItem != null)
        {
            treeSpokenHotKeyMenuItem.Visible = spokenHotKeyVisible;
        }

        if (treeRenameMenuItem != null)
        {
            treeRenameMenuItem.Visible = visible;
        }

        UpdateTrendLoggingMenuState();
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

    private bool CanAssignSelectedReadingToSpokenHotKey()
    {
        if (reportViewMode)
        {
            return false;
        }

        var row = GetSelectedReadingRow();
        return IsSelectableReadoutRow(row);
    }

    private bool ShowSpokenHotKeyAssignmentDialog()
    {
        var row = GetSelectedReadingRow();
        if (reportViewMode || !IsSelectableReadoutRow(row))
        {
            System.Media.SystemSounds.Beep.Play();
            statusLabel.Text = L("status.Select a reading that can be spoken by a hotkey.", "Select a reading that can be spoken by a hotkey.");
            return false;
        }

        settings.SpokenHotKeys = settings.SpokenHotKeys ?? new List<SpokenHotKeySetting>();
        var rowKey = RowSettingsKey(row);
        using (var dialog = new Form())
        {
            dialog.Text = L("ui.Hotkey and tray assignment", "Hotkey and tray assignment");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(600, 390);
            dialog.MinimumSize = new Size(460, 280);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var label = new Label
            {
                Text = L("ui.Choose where to add or remove:", "Choose where to add or remove:") + " " + TrayChoiceLabel(row) + " - " + FormatValue(row),
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            var targetList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                AccessibleName = L("a11y.Hotkey and tray destinations", "Hotkey and tray destinations"),
                AccessibleDescription = L("a11y.Choose notification area status or a spoken hotkey profile to add or remove the selected reading.", "Choose notification area status or a spoken hotkey profile to add or remove the selected reading.")
            };

            var status = new Label { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = L("a11y.Spoken hotkey assignment status", "Spoken hotkey assignment status") };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = CreateCloseButton();
            closeButton.DialogResult = DialogResult.Cancel;
            var toggleButton = new Button { AutoSize = true };
            var newProfileButton = new Button
            {
                AutoSize = true,
                Text = L("ui.&New spoken hotkey...", "&New spoken hotkey...")
            };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(toggleButton);
            buttons.Controls.Add(newProfileButton);

            Action populateTargets = delegate
            {
                var previous = targetList.SelectedItem == null ? "" : Convert.ToString(targetList.SelectedItem);
                targetList.Items.Clear();
                targetList.Items.Add(new QuickReadoutAssignmentTarget(null, TrayAssignmentDisplayText()));
                foreach (var profile in settings.SpokenHotKeys.Where(p => p != null))
                {
                    targetList.Items.Add(new QuickReadoutAssignmentTarget(profile));
                }

                if (!string.IsNullOrWhiteSpace(previous))
                {
                    for (var i = 0; i < targetList.Items.Count; i++)
                    {
                        if (string.Equals(Convert.ToString(targetList.Items[i]), previous, StringComparison.Ordinal))
                        {
                            targetList.SelectedIndex = i;
                            break;
                        }
                    }
                }

                if (targetList.SelectedIndex < 0 && targetList.Items.Count > 0)
                {
                    targetList.SelectedIndex = 0;
                }
            };
            Func<QuickReadoutAssignmentTarget> selectedTarget = delegate { return targetList.SelectedItem as QuickReadoutAssignmentTarget; };
            Func<SpokenHotKeySetting> selectedProfile = delegate
            {
                var target = selectedTarget();
                return target == null ? null : target.Profile;
            };
            Func<bool> selectedContainsRow = delegate
            {
                var target = selectedTarget();
                if (target == null)
                {
                    return false;
                }

                if (target.IsTray)
                {
                    return settings.TrayItemKeys != null &&
                        settings.TrayItemKeys.Any(k => string.Equals(k, rowKey, StringComparison.OrdinalIgnoreCase));
                }

                var profile = target.Profile;
                return profile != null &&
                    profile.ReadingKeys != null &&
                    profile.ReadingKeys.Any(k => string.Equals(k, rowKey, StringComparison.OrdinalIgnoreCase));
            };
            Action updateDialogState = delegate
            {
                var target = selectedTarget();
                var hasTarget = target != null;
                toggleButton.Enabled = hasTarget;
                toggleButton.Text = !hasTarget
                    ? L("ui.&Add", "&Add")
                    : selectedContainsRow()
                        ? (target.IsTray ? L("ui.&Remove from tray", "&Remove from tray") : L("ui.&Remove from hotkey", "&Remove from hotkey"))
                        : (target.IsTray ? L("ui.&Add to tray", "&Add to tray") : L("ui.&Add to hotkey", "&Add to hotkey"));
                status.Text = hasTarget
                    ? (selectedContainsRow()
                        ? (target.IsTray ? L("status.Selected reading is already in notification area status.", "Selected reading is already in notification area status.") : L("status.Selected reading is already on this spoken hotkey.", "Selected reading is already on this spoken hotkey."))
                        : (target.IsTray ? L("status.Selected reading is not in notification area status.", "Selected reading is not in notification area status.") : L("status.Selected reading is not on this spoken hotkey.", "Selected reading is not on this spoken hotkey.")))
                    : "";
            };
            Action toggleAssignment = delegate
            {
                var target = selectedTarget();
                if (target == null)
                {
                    return;
                }

                if (target.IsTray)
                {
                    settings.TrayItemKeys = settings.TrayItemKeys ?? new List<string>();
                    var existingTray = settings.TrayItemKeys.FirstOrDefault(k => string.Equals(k, rowKey, StringComparison.OrdinalIgnoreCase));
                    if (existingTray == null)
                    {
                        if (settings.TrayItemKeys.Count >= MaxTrayStatusReadings)
                        {
                            MessageBox.Show(
                                dialog,
                                string.Format(L("message.Notification area status can contain up to {0} readings. Remove one first.", "Notification area status can contain up to {0} readings. Remove one first."), MaxTrayStatusReadings),
                                L("ui.Notification area status", "Notification area status"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            return;
                        }

                        settings.TrayItemKeys.Add(rowKey);
                        settings.TrayStatusEnabled = true;
                        trayStatusMenuItem.Checked = true;
                        statusLabel.Text = L("status.Added reading to notification area status.", "Added reading to notification area status.");
                    }
                    else
                    {
                        settings.TrayItemKeys.Remove(existingTray);
                        statusLabel.Text = L("status.Removed reading from notification area status.", "Removed reading from notification area status.");
                    }
                }
                else
                {
                    var profile = target.Profile;
                    if (profile == null)
                    {
                        return;
                    }

                    profile.ReadingKeys = profile.ReadingKeys ?? new List<string>();
                    var existing = profile.ReadingKeys.FirstOrDefault(k => string.Equals(k, rowKey, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        profile.ReadingKeys.Add(rowKey);
                        statusLabel.Text = L("status.Added reading to spoken hotkey.", "Added reading to spoken hotkey.");
                    }
                    else
                    {
                        profile.ReadingKeys.Remove(existing);
                        statusLabel.Text = L("status.Removed reading from spoken hotkey.", "Removed reading from spoken hotkey.");
                    }
                }

                SaveSettings(settings);
                RegisterGlobalHotKeys();
                UpdateTrayStatus();
                updateDialogState();
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };
            Action createProfile = delegate
            {
                var profile = PromptForNewSpokenHotKeyProfile(dialog, TrayChoiceLabel(row));
                if (profile == null)
                {
                    return;
                }

                settings.SpokenHotKeys.Add(profile);
                SaveSettings(settings);
                RegisterGlobalHotKeys();
                populateTargets();
                for (var i = 0; i < targetList.Items.Count; i++)
                {
                    var target = targetList.Items[i] as QuickReadoutAssignmentTarget;
                    if (target != null && target.Profile == profile)
                    {
                        targetList.SelectedIndex = i;
                        break;
                    }
                }

                updateDialogState();
                status.Text = L("status.Created new spoken hotkey. Press Add to hotkey to use it for this reading.", "Created new spoken hotkey. Press Add to hotkey to use it for this reading.");
            };

            targetList.SelectedIndexChanged += delegate { updateDialogState(); };
            targetList.DoubleClick += delegate { toggleAssignment(); };
            targetList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    toggleAssignment();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            toggleButton.Click += delegate { toggleAssignment(); };
            newProfileButton.Click += delegate { createProfile(); };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            layout.Controls.Add(label, 0, 0);
            layout.Controls.Add(targetList, 0, 1);
            layout.Controls.Add(status, 0, 2);
            layout.Controls.Add(buttons, 0, 3);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = toggleButton;
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate
            {
                populateTargets();
                updateDialogState();
                targetList.Focus();
            };
            dialog.ShowDialog(this);
        }

        UpdateSelectedTreeCommandVisibility();
        if (readingTree != null)
        {
            readingTree.Focus();
        }
        return true;
    }

    private SpokenHotKeySetting PromptForNewSpokenHotKeyProfile(IWin32Window owner, string readingLabel)
    {
        using (var dialog = new Form())
        {
            dialog.Text = L("ui.New spoken hotkey", "New spoken hotkey");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(520, 220);
            dialog.MinimumSize = new Size(420, 190);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var nameLabel = new Label { Text = L("ui.&Name:", "&Name:"), AutoSize = true, Anchor = AnchorStyles.Left };
            var nameBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = UniqueSpokenHotKeyNameFromSettings(string.IsNullOrWhiteSpace(readingLabel) ? L("ui.New spoken hotkey", "New spoken hotkey") : readingLabel + " hotkey")
            };

            var keyLabel = new Label { Text = L("ui.Hot&key:", "Hot&key:"), AutoSize = true, Anchor = AnchorStyles.Left };
            var keyBox = CreateInlineHotKeyBox("", L("a11y.Spoken hotkey key combination", "Spoken hotkey key combination"));

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var createButton = new Button { Text = L("ui.&Create", "&Create"), AutoSize = true, DialogResult = DialogResult.OK };
            var cancelButton = CreateCloseButton();
            cancelButton.Text = L("ui.&Cancel", "&Cancel");
            cancelButton.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(createButton);

            layout.Controls.Add(nameLabel, 0, 0);
            layout.Controls.Add(nameBox, 1, 0);
            layout.Controls.Add(keyLabel, 0, 1);
            layout.Controls.Add(keyBox, 1, 1);
            layout.Controls.Add(buttons, 0, 2);
            layout.SetColumnSpan(buttons, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = createButton;
            dialog.CancelButton = cancelButton;
            dialog.Shown += delegate
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && !keyBox.Focused)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            while (dialog.ShowDialog(owner) == DialogResult.OK)
            {
                var hotKey = NormalizeHotKeyText(keyBox.Text);
                if (!string.IsNullOrWhiteSpace(hotKey) && IsHotKeyAlreadyAssigned(hotKey))
                {
                    MessageBox.Show(
                        dialog,
                        L("message.That hotkey is already assigned. Choose another key or leave it blank for now.", "That hotkey is already assigned. Choose another key or leave it blank for now."),
                        L("ui.New spoken hotkey", "New spoken hotkey"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    keyBox.Focus();
                    keyBox.SelectAll();
                    continue;
                }

                return new SpokenHotKeySetting
                {
                    Name = UniqueSpokenHotKeyNameFromSettings(nameBox.Text),
                    HotKey = hotKey,
                    SkipUnavailableReadings = true,
                    ReadingKeys = new List<string>()
                };
            }
        }

        return null;
    }

    private string TrayAssignmentDisplayText()
    {
        var hotKey = NormalizeHotKeyText(settings.SpeakTrayHotKey);
        if (string.IsNullOrWhiteSpace(hotKey))
        {
            hotKey = L("ui.no hotkey", "no hotkey");
        }

        var count = settings.TrayItemKeys == null ? 0 : settings.TrayItemKeys.Count;
        return L("ui.Notification area status", "Notification area status") +
            " (" + hotKey + ", " + count + " " +
            (count == 1 ? L("ui.reading", "reading") : L("ui.readings", "readings")) + ")";
    }

    private static TextBox CreateInlineHotKeyBox(string hotKey, string accessibleName)
    {
        var box = new TextBox
        {
            Text = NormalizeHotKeyText(hotKey),
            ReadOnly = true,
            Dock = DockStyle.Fill,
            AccessibleName = accessibleName,
            AccessibleDescription = L("a11y.Press a key combination with at least two modifiers to assign it. Use Backspace or Delete to clear it.", "Press a key combination with at least two modifiers to assign it. Use Backspace or Delete to clear it.")
        };
        box.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Escape || (e.Alt && e.KeyCode == Keys.F4))
            {
                return;
            }

            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
            {
                box.Text = "";
                return;
            }

            if (IsModifierOnlyHotKeyData(e.KeyData))
            {
                return;
            }

            var text = HotKeyTextFromKeyEvent(e);
            if (!string.IsNullOrWhiteSpace(text))
            {
                box.Text = text;
            }
            else
            {
                System.Media.SystemSounds.Beep.Play();
            }
        };
        return box;
    }

    private string UniqueSpokenHotKeyNameFromSettings(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? L("ui.New spoken hotkey", "New spoken hotkey") : name.Trim();
        var candidate = baseName;
        var index = 2;
        while ((settings.SpokenHotKeys ?? new List<SpokenHotKeySetting>()).Any(p => p != null && string.Equals(p.Name ?? "", candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = baseName + " " + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            index++;
        }

        return candidate;
    }

    private bool IsHotKeyAlreadyAssigned(string hotKey)
    {
        var normalized = NormalizeHotKeyText(hotKey);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (string.Equals(NormalizeHotKeyText(settings.ShowHideHotKey), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeHotKeyText(settings.SpeakTrayHotKey), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((settings.SpokenHotKeys ?? new List<SpokenHotKeySetting>()).Any(p => p != null && string.Equals(NormalizeHotKeyText(p.HotKey), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return (settings.FanProfiles ?? new List<FanProfileSetting>()).Any(p => p != null && string.Equals(NormalizeHotKeyText(p.HotKey), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class QuickReadoutAssignmentTarget
    {
        public readonly SpokenHotKeySetting Profile;
        private readonly string displayText;

        public QuickReadoutAssignmentTarget(SpokenHotKeySetting profile, string displayText = null)
        {
            Profile = profile;
            this.displayText = displayText;
        }

        public bool IsTray
        {
            get { return Profile == null; }
        }

        public override string ToString()
        {
            return IsTray ? (displayText ?? "Notification area status") : Profile.ToString();
        }
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
