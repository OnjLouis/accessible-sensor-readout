using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private Control BuildTraySelectionPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var availablePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        availablePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        availablePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        availablePanel.Controls.Add(new Label { Text = "Available readings", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        availablePanel.Controls.Add(trayAvailableList, 0, 1);

        var selectedPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectedPanel.Controls.Add(new Label { Text = "Tray order", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        selectedPanel.Controls.Add(traySelectedList, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 24, 8, 0)
        };
        var addButton = CreateShortcutButton("&Add", "Alt+A", Keys.A);
        addButton.AccessibleDescription = "Add selected reading to the tray order. Shortcut Control Right Arrow.";
        addButton.Click += delegate { AddSelectedTrayChoice(); };
        var removeButton = CreateShortcutButton("Re&move", "Alt+M", Keys.M);
        removeButton.AccessibleDescription = "Remove selected reading from the tray order. Shortcut Control Left Arrow.";
        removeButton.Click += delegate { RemoveSelectedTrayChoice(); };
        var renameLabelButton = CreateShortcutButton("&Rename...", "Alt+R", Keys.R);
        renameLabelButton.AccessibleDescription = "Rename the selected spoken label. Shortcut F2.";
        renameLabelButton.Click += delegate { RenameSelectedTrayChoice(); };
        var upButton = CreateShortcutButton("&Up", "Alt+U", Keys.U);
        upButton.AccessibleDescription = "Move selected tray reading up. Shortcut Control Up Arrow.";
        upButton.Click += delegate { MoveSelectedTrayChoice(-1); };
        var downButton = CreateShortcutButton("&Down", "Alt+D", Keys.D);
        downButton.AccessibleDescription = "Move selected tray reading down. Shortcut Control Down Arrow.";
        downButton.Click += delegate { MoveSelectedTrayChoice(1); };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(renameLabelButton);
        buttons.Controls.Add(upButton);
        buttons.Controls.Add(downButton);

        panel.Controls.Add(availablePanel, 0, 0);
        panel.Controls.Add(buttons, 1, 0);
        panel.Controls.Add(selectedPanel, 2, 0);
        return panel;
    }

    private static ShortcutButton CreateShortcutButton(string text, string shortcut, Keys shortcutKeys)
    {
        return new ShortcutButton
        {
            Text = text,
            ShortcutText = shortcut,
            ShortcutKeys = shortcutKeys,
            AutoSize = true
        };
    }

    private Control BuildSpokenHotKeysPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

        var profilePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.Controls.Add(new Label { Text = "Spoken hotkeys", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        profilePanel.Controls.Add(spokenHotKeyList, 0, 1);
        var profileButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var addProfileButton = CreateShortcutButton("&New...", "Alt+N", Keys.N);
        addProfileButton.Click += delegate { AddSpokenHotKeyProfile(); };
        var importProfileButton = CreateShortcutButton("&Import...", "Alt+I", Keys.I);
        importProfileButton.Click += delegate { ImportSpokenHotKeysFromConfig(); };
        var presetProfileButton = CreateShortcutButton(SensorReadoutForm.L("ui.&Presets...", "&Presets..."), "Alt+P", Keys.P);
        presetProfileButton.Click += delegate { ShowSpokenHotKeyPresetsDialog(); };
        var removeProfileButton = CreateShortcutButton("&Remove profile", "Alt+R", Keys.R);
        removeProfileButton.Click += delegate { RemoveSelectedSpokenHotKeyProfile(); };
        profileButtons.Controls.Add(addProfileButton);
        profileButtons.Controls.Add(importProfileButton);
        profileButtons.Controls.Add(presetProfileButton);
        profileButtons.Controls.Add(removeProfileButton);
        profilePanel.Controls.Add(profileButtons, 0, 2);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var namePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        namePanel.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        namePanel.Controls.Add(spokenHotKeyNameBox, 1, 0);

        var keyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.Controls.Add(new Label { Text = "Hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        keyPanel.Controls.Add(spokenHotKeyBox, 1, 0);
        var clearKeyButton = CreateShortcutButton("&Clear", "Alt+C", Keys.C);
        clearKeyButton.Click += delegate { spokenHotKeyBox.Text = ""; };
        keyPanel.Controls.Add(clearKeyButton, 2, 0);

        editor.Controls.Add(namePanel, 0, 0);
        editor.Controls.Add(keyPanel, 0, 1);
        editor.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Choose the readings spoken by this hotkey. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder.", "Choose the readings spoken by this hotkey. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder."), AutoSize = true, Dock = DockStyle.Fill }, 0, 2);
        editor.Controls.Add(BuildSpokenSelectionPanel(), 0, 3);
        editor.Controls.Add(spokenSelectionStatusLabel, 0, 4);

        layout.Controls.Add(profilePanel, 0, 0);
        layout.Controls.Add(editor, 1, 0);
        return layout;
    }

    private Control BuildFanProfilesPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

        var profilePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profilePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profilePanel.Controls.Add(new Label { Text = "Fan profiles", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        profilePanel.Controls.Add(fanProfileList, 0, 1);
        var profileButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var addProfileButton = CreateShortcutButton("&New...", "Alt+N", Keys.N);
        addProfileButton.Click += delegate { AddFanProfile(); };
        var removeProfileButton = CreateShortcutButton("Remove &profile", "Alt+P", Keys.P);
        removeProfileButton.Click += delegate { RemoveSelectedFanProfile(); };
        var applyProfileButton = CreateShortcutButton("Appl&y now", "Alt+Y", Keys.Y);
        applyProfileButton.Click += delegate { ApplySelectedFanProfileFromPreferences(); };
        profileButtons.Controls.Add(addProfileButton);
        profileButtons.Controls.Add(removeProfileButton);
        profileButtons.Controls.Add(applyProfileButton);
        profilePanel.Controls.Add(profileButtons, 0, 2);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11 };
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var namePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        namePanel.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        namePanel.Controls.Add(fanProfileNameBox, 1, 0);

        var keyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPanel.Controls.Add(new Label { Text = "Hotkey:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        keyPanel.Controls.Add(fanProfileHotKeyBox, 1, 0);
        var clearKeyButton = CreateShortcutButton("&Clear", "Alt+C", Keys.C);
        clearKeyButton.Click += delegate { fanProfileHotKeyBox.Text = ""; };
        keyPanel.Controls.Add(clearKeyButton, 2, 0);

        var soundPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        soundPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        soundPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        soundPanel.Controls.Add(new Label { Text = "Sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        soundPanel.Controls.Add(fanProfileSoundBox, 1, 0);

        var speechPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        speechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        speechPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        speechPanel.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Speech message:", "Speech message:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        speechPanel.Controls.Add(fanProfileSpeechMessageBox, 1, 0);

        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actionPanel.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Action for selected fan:", "Action for selected fan:"), AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        actionPanel.Controls.Add(fanProfileActionBox);
        actionPanel.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Percent:", "Percent:"), AutoSize = true, Padding = new Padding(12, 6, 8, 0) });
        actionPanel.Controls.Add(fanProfilePercentBox);

        editor.Controls.Add(namePanel, 0, 0);
        editor.Controls.Add(keyPanel, 0, 1);
        editor.Controls.Add(fanProfileToggleBox, 0, 2);
        editor.Controls.Add(fanProfileSpeakBox, 0, 3);
        editor.Controls.Add(speechPanel, 0, 4);
        editor.Controls.Add(soundPanel, 0, 5);
        editor.Controls.Add(fanProfileShowStoppedBox, 0, 6);
        editor.Controls.Add(actionPanel, 0, 7);
        editor.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Choose the fan controls changed by this profile. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder.", "Choose the fan controls changed by this profile. Use Control Right Arrow to add, Control Left Arrow to remove, and Control Up or Down to reorder."), AutoSize = true, Dock = DockStyle.Fill }, 0, 8);
        editor.Controls.Add(BuildFanProfileSelectionPanel(), 0, 9);
        editor.Controls.Add(fanProfileStatusLabel, 0, 10);

        layout.Controls.Add(profilePanel, 0, 0);
        layout.Controls.Add(editor, 1, 0);
        return layout;
    }

    private Control BuildFanProfileSelectionPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Available fan controls", "Available fan controls"), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = SensorReadoutForm.L("ui.Profile fan actions", "Profile fan actions"), AutoSize = true }, 2, 0);
        layout.Controls.Add(fanProfileAvailableList, 0, 1);
        layout.Controls.Add(fanProfileSelectedList, 2, 1);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 4, ColumnCount = 1 };
        var addButton = CreateShortcutButton("&Add", "Alt+A", Keys.A);
        addButton.Click += delegate { AddSelectedFanProfileChoice(); };
        var removeButton = CreateShortcutButton("&Remove", "Alt+R", Keys.R);
        removeButton.Click += delegate { RemoveSelectedFanProfileChoice(); };
        var upButton = CreateShortcutButton("&Up", "Alt+U", Keys.U);
        upButton.Click += delegate { MoveSelectedFanProfileChoice(-1); };
        var downButton = CreateShortcutButton("&Down", "Alt+D", Keys.D);
        downButton.Click += delegate { MoveSelectedFanProfileChoice(1); };
        buttons.Controls.Add(addButton, 0, 0);
        buttons.Controls.Add(removeButton, 0, 1);
        buttons.Controls.Add(upButton, 0, 2);
        buttons.Controls.Add(downButton, 0, 3);
        layout.Controls.Add(buttons, 1, 1);
        return layout;
    }

    private Control BuildSoundPickerPanel(string labelText, string selectedFile, out ComboBox combo)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = labelText, AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 0);
        combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        PopulateSoundCombo(combo, selectedFile);
        panel.Controls.Add(combo, 1, 0);
        return panel;
    }

    private void PopulateSoundCombo(ComboBox combo, string selectedFile)
    {
        combo.Items.Clear();
        combo.Items.Add(SensorReadoutForm.L("ui.(None)", "(None)"));
        foreach (var sound in soundFiles)
        {
            combo.Items.Add(sound);
        }

        var selected = System.IO.Path.GetFileName(selectedFile ?? "");
        combo.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            for (var i = 1; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i].ToString(), selected, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private static string SelectedSoundFile(ComboBox combo)
    {
        if (combo == null || combo.SelectedIndex <= 0 || combo.SelectedItem == null)
        {
            return "";
        }

        return System.IO.Path.GetFileName(combo.SelectedItem.ToString());
    }

    private void PreviewSelectedSound(ComboBox combo)
    {
        if (loadingPreferences)
        {
            return;
        }

        var fileName = SelectedSoundFile(combo);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            SensorReadoutForm.PreviewSoundFile(fileName);
        }
    }

    private void AlarmListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2)
        {
            FocusAlarmName();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            FocusAlarmThreshold();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedAlarm();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private Control BuildAlarmsPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var alarmButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var newButton = CreateShortcutButton("&New...", "Alt+N", Keys.N);
        var presetButton = CreateShortcutButton(SensorReadoutForm.L("ui.&Presets...", "&Presets..."), "Alt+P", Keys.P);
        var removeButton = CreateShortcutButton("Re&move", "Alt+M", Keys.M);
        newButton.TabIndex = 0;
        presetButton.TabIndex = 1;
        removeButton.TabIndex = 2;
        newButton.Click += delegate { AddAlarm(); };
        presetButton.Click += delegate { ShowAlarmPresetsDialog(); };
        removeButton.Click += delegate { RemoveSelectedAlarm(); };
        alarmButtons.Controls.Add(newButton);
        alarmButtons.Controls.Add(presetButton);
        alarmButtons.Controls.Add(removeButton);

        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9 };
        editor.TabIndex = 1;
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        alarmEnabledCheckBox.TabIndex = 0;
        alarmNameBox.TabIndex = 1;
        alarmReadingBox.TabIndex = 2;
        alarmConditionBox.TabIndex = 3;
        alarmThresholdBox.TabIndex = 4;
        alarmThresholdUnitBox.TabIndex = 5;
        alarmCooldownBox.TabIndex = 6;
        alarmSpeakCheckBox.TabIndex = 7;
        alarmSoundBox.TabIndex = 8;
        editor.Controls.Add(alarmEnabledCheckBox, 1, 0);
        editor.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 1);
        editor.Controls.Add(alarmNameBox, 1, 1);
        editor.Controls.Add(new Label { Text = "Reading:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 2);
        editor.Controls.Add(alarmReadingBox, 1, 2);
        editor.Controls.Add(new Label { Text = "Condition:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 3);
        editor.Controls.Add(alarmConditionBox, 1, 3);
        editor.Controls.Add(new Label { Text = "Threshold:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 4);
        var thresholdPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        thresholdPanel.Controls.Add(alarmThresholdBox);
        thresholdPanel.Controls.Add(alarmThresholdUnitBox);
        editor.Controls.Add(thresholdPanel, 1, 4);
        editor.Controls.Add(new Label { Text = "Cooldown seconds:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 5);
        editor.Controls.Add(alarmCooldownBox, 1, 5);
        editor.Controls.Add(alarmSpeakCheckBox, 1, 6);
        editor.Controls.Add(new Label { Text = "Sound:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, 0, 7);
        editor.Controls.Add(alarmSoundBox, 1, 7);

        alarmList.TabIndex = 0;
        alarmButtons.TabIndex = 2;
        alarmStatusLabel.TabStop = false;
        layout.Controls.Add(alarmList, 0, 0);
        layout.Controls.Add(editor, 1, 0);
        layout.Controls.Add(alarmButtons, 0, 1);
        layout.Controls.Add(alarmStatusLabel, 1, 1);

        PopulateAlarmReadings();
        foreach (var alarm in alarms)
        {
            alarmList.Items.Add(new AlarmChoice(alarm, RowForKey, FormatAlarmThresholdForList));
        }

        if (alarmList.Items.Count > 0)
        {
            alarmList.SelectedIndex = 0;
        }
        else
        {
            LoadSelectedAlarm();
        }

        return layout;
    }

    private Control BuildSpokenSelectionPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var availablePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        availablePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        availablePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        availablePanel.Controls.Add(new Label { Text = "Available readings", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        availablePanel.Controls.Add(spokenAvailableList, 0, 1);

        var selectedPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectedPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        selectedPanel.Controls.Add(new Label { Text = "Spoken readings", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        selectedPanel.Controls.Add(spokenSelectedList, 0, 1);
        var spokenLabelButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var renameButton = CreateShortcutButton("&Rename...", "Alt+R", Keys.R);
        renameButton.Click += delegate { RenameSelectedSpokenChoice(); };
        var resetButton = CreateShortcutButton("Reset &default", "Alt+D", Keys.D);
        resetButton.Click += delegate { ResetSelectedSpokenChoiceLabel(); };
        spokenLabelButtons.Controls.Add(renameButton);
        spokenLabelButtons.Controls.Add(resetButton);
        selectedPanel.Controls.Add(spokenLabelButtons, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8, 24, 8, 0)
        };
        var addButton = CreateShortcutButton("&Add", "Alt+A", Keys.A);
        addButton.Click += delegate { AddSelectedSpokenChoice(); };
        var removeButton = CreateShortcutButton("Re&move", "Alt+M", Keys.M);
        removeButton.Click += delegate { RemoveSelectedSpokenChoice(); };
        var upButton = CreateShortcutButton("&Up", "Alt+U", Keys.U);
        upButton.Click += delegate { MoveSelectedSpokenChoice(-1); };
        var downButton = CreateShortcutButton("Do&wn", "Alt+W", Keys.W);
        downButton.Click += delegate { MoveSelectedSpokenChoice(1); };
        buttons.Controls.Add(addButton);
        buttons.Controls.Add(removeButton);
        buttons.Controls.Add(upButton);
        buttons.Controls.Add(downButton);

        panel.Controls.Add(availablePanel, 0, 0);
        panel.Controls.Add(buttons, 1, 0);
        panel.Controls.Add(selectedPanel, 2, 0);
        return panel;
    }

    private Control BuildPlugInsPanel()
    {
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

        layout.Controls.Add(new Label
        {
            Text = SensorReadoutForm.L("ui.Enable only the Plug-Ins you need for this machine. Each item includes its purpose. Disabled Plug-Ins are not loaded.", "Enable only the Plug-Ins you need for this machine. Each item includes its purpose. Disabled Plug-Ins are not loaded."),
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 0);

        RefreshPlugInList();
        layout.Controls.Add(plugInList, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var importButton = CreateShortcutButton(SensorReadoutForm.L("ui.&Import from ZIP...", "&Import from ZIP..."), "Alt+I", Keys.I);
        importButton.AccessibleDescription = SensorReadoutForm.L("a11y.Import a Plug-In ZIP file into the Plug-Ins folder. Imported Plug-Ins stay disabled until you enable them.", "Import a Plug-In ZIP file into the Plug-Ins folder. Imported Plug-Ins stay disabled until you enable them.");
        importButton.Click += delegate { ImportPlugInFromZip(); };
        buttons.Controls.Add(importButton);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(plugInDetailsLabel, 0, 3);
        UpdatePlugInDetails();
        return layout;
    }

    private void RefreshPlugInList()
    {
        if (plugInList == null)
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            plugIns.Clear();
            plugIns.AddRange(SensorReadoutForm.LoadPlugInPreferenceInfos(liveSettings));
            plugInList.Items.Clear();
            foreach (var plugIn in plugIns)
            {
                plugInList.Items.Add(plugIn, plugIn.Enabled);
            }

            plugInList.SelectedIndex = plugInList.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            loadingPreferences = previousLoading;
        }

        UpdatePlugInDetails();
    }

    private void UpdatePlugInDetails()
    {
        var plugIn = plugInList == null ? null : plugInList.SelectedItem as PlugInPreferenceInfo;
        if (plugIn == null || plugInList.SelectedIndex < 0)
        {
            plugInDetailsLabel.Text = SensorReadoutForm.L("ui.No Plug-Ins found.", "No Plug-Ins found.");
            return;
        }

        plugInDetailsLabel.Text =
            (plugInList.GetItemChecked(plugInList.SelectedIndex) ? SensorReadoutForm.L("ui.Enabled. ", "Enabled. ") : SensorReadoutForm.L("ui.Disabled. ", "Disabled. ")) +
            (string.IsNullOrWhiteSpace(plugIn.Description) ? "" : plugIn.Description + " ") +
            "ID: " + plugIn.Id +
            "; " + SensorReadoutForm.L("ui.author:", "author:") + " " + (string.IsNullOrWhiteSpace(plugIn.Author) ? SensorReadoutForm.L("ui.unknown", "unknown") : plugIn.Author) +
            "; " + SensorReadoutForm.L("ui.status:", "status:") + " " + (string.IsNullOrWhiteSpace(plugIn.Status) ? SensorReadoutForm.L("ui.available", "available") : plugIn.Status) + ".";
    }

    private void ImportPlugInFromZip()
    {
        if (PlugInZipImporter.PromptAndImport(this, liveSettings))
        {
            RefreshPlugInList();
            UpdatePlugInDetails();
        }
    }

    private Dictionary<string, bool> CurrentPlugInSettings()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (plugInList == null)
        {
            return result;
        }

        for (var i = 0; i < plugInList.Items.Count; i++)
        {
            var plugIn = plugInList.Items[i] as PlugInPreferenceInfo;
            if (plugIn == null || string.IsNullOrWhiteSpace(plugIn.Id))
            {
                continue;
            }

            result[plugIn.Id] = plugInList.GetItemChecked(i);
        }

        return result;
    }

    private Control BuildLanguageEditorPanel(List<LanguageChoice> languageChoices)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var filePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        filePanel.Controls.Add(new Label { Text = "File:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        languageEditorFileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, AccessibleName = "Language file to edit" };
        foreach (var choice in languageChoices ?? new List<LanguageChoice>())
        {
            if (!string.IsNullOrWhiteSpace(choice.FileName))
            {
                languageEditorFileBox.Items.Add(choice);
            }
        }
        filePanel.Controls.Add(languageEditorFileBox);
        var reloadButton = new Button { Text = "&Reload", AutoSize = true };
        reloadButton.Click += delegate { LoadLanguageEditorEntries(); };
        filePanel.Controls.Add(reloadButton);
        var openFolderButton = new Button { Text = "&Open folder", AutoSize = true };
        openFolderButton.Click += delegate { SensorReadoutForm.OpenLanguagesFolderStatic(this); };
        filePanel.Controls.Add(openFolderButton);
        var newButton = new Button { Text = "&New...", AutoSize = true };
        newButton.Click += delegate { CreateNewLanguageFile(); };
        filePanel.Controls.Add(newButton);

        languageEntryList = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            AccessibleName = "Language entries",
            AccessibleDescription = "Select an entry, then edit its value below. Press F2 to move to the value field."
        };
        languageEntryList.SelectedIndexChanged += delegate { LoadSelectedLanguageEntryValue(); };
        languageEntryList.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2)
            {
                languageEntryValueBox.Focus();
                languageEntryValueBox.SelectAll();
                e.Handled = true;
            }
        };

        languageEntryValueBox = new TextBox
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Language entry value"
        };
        var buttonPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var saveButton = new Button { Text = "&Save entry", AutoSize = true };
        saveButton.Click += delegate { SaveSelectedLanguageEntry(); };
        buttonPanel.Controls.Add(saveButton);

        layout.Controls.Add(filePanel, 0, 0);
        layout.Controls.Add(languageEntryList, 0, 1);
        layout.Controls.Add(new Label { Text = "Value:", AutoSize = true, Dock = DockStyle.Fill }, 0, 2);
        layout.Controls.Add(languageEntryValueBox, 0, 3);
        layout.Controls.Add(buttonPanel, 0, 4);

        languageEditorFileBox.SelectedIndexChanged += delegate { LoadLanguageEditorEntries(); };
        if (languageEditorFileBox.Items.Count > 0)
        {
            languageEditorFileBox.SelectedIndex = 0;
        }

        return layout;
    }

    private void LoadLanguageEditorEntries()
    {
        if (languageEditorFileBox == null || languageEntryList == null)
        {
            return;
        }

        languageEntryList.Items.Clear();
        var choice = languageEditorFileBox.SelectedItem as LanguageChoice;
        if (choice == null || string.IsNullOrWhiteSpace(choice.FullPath) || !System.IO.File.Exists(choice.FullPath))
        {
            return;
        }

        foreach (var key in SensorReadoutForm.ReadLanguageFile(choice.FullPath).Keys.OrderBy(LanguageEntrySortKey))
        {
            languageEntryList.Items.Add(new LanguageEntryChoice { Key = key, Label = FriendlyLanguageEntryLabel(key) });
        }
        if (languageEntryList.Items.Count > 0)
        {
            languageEntryList.SelectedIndex = 0;
        }
    }

    private void LoadSelectedLanguageEntryValue()
    {
        var choice = languageEditorFileBox.SelectedItem as LanguageChoice;
        var entry = languageEntryList.SelectedItem as LanguageEntryChoice;
        var key = entry == null ? "" : entry.Key;
        if (choice == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(choice.FullPath))
        {
            languageEntryValueBox.Text = "";
            return;
        }

        var values = SensorReadoutForm.ReadLanguageFile(choice.FullPath);
        string value;
        languageEntryValueBox.Text = values.TryGetValue(key, out value) ? value : "";
    }

    private void SaveSelectedLanguageEntry()
    {
        var choice = languageEditorFileBox.SelectedItem as LanguageChoice;
        var entry = languageEntryList.SelectedItem as LanguageEntryChoice;
        var key = entry == null ? "" : entry.Key;
        if (choice == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(choice.FullPath))
        {
            return;
        }

        SensorReadoutForm.UpdateLanguageFileValue(choice.FullPath, key, languageEntryValueBox.Text);
        if (key.Equals("speech.startupActive", StringComparison.OrdinalIgnoreCase))
        {
            startupSpeechMessageBox.Text = languageEntryValueBox.Text;
        }
    }

    private void CreateNewLanguageFile()
    {
        var requested = PromptForText(this, SensorReadoutForm.L("ui.New language file", "New language file"), SensorReadoutForm.L("ui.File name:", "File name:"), "MyLanguage.txt");
        var fileName = NormalizeNewLanguageFileName(requested);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var folder = SensorReadoutForm.GetLanguagesFolderPath();
        var path = System.IO.Path.Combine(folder, fileName);
        if (System.IO.File.Exists(path))
        {
            MessageBox.Show(this, SensorReadoutForm.L("message.languageFileExists", "That language file already exists."), SensorReadoutForm.L("ui.New language file", "New language file"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            var englishPath = System.IO.Path.Combine(folder, "English.txt");
            if (System.IO.File.Exists(englishPath))
            {
                System.IO.File.Copy(englishPath, path);
            }
            else
            {
                System.IO.File.WriteAllLines(path, new[] { "language.name=" + System.IO.Path.GetFileNameWithoutExtension(fileName) });
            }

            SensorReadoutForm.UpdateLanguageFileValue(path, "language.name", System.IO.Path.GetFileNameWithoutExtension(fileName));
            var choice = new LanguageChoice
            {
                FileName = fileName,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(fileName),
                FullPath = path
            };
            languageEditorFileBox.Items.Add(choice);
            languageEditorFileBox.SelectedItem = choice;
            if (!languageBox.Items.Cast<LanguageChoice>().Any(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                languageBox.Items.Add(choice);
            }

            var owner = Owner as SensorReadoutForm;
            if (owner != null)
            {
                owner.RefreshLanguagesNow();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, SensorReadoutForm.L("ui.New language file", "New language file"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FriendlyLanguageEntryLabel(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        if (key.Equals("language.name", StringComparison.OrdinalIgnoreCase))
        {
            return "Language: Display name";
        }
        if (key.Equals("manual.file", StringComparison.OrdinalIgnoreCase))
        {
            return "Manual: file name only, no folder path";
        }
        if (key.Equals("number.decimalSeparator", StringComparison.OrdinalIgnoreCase))
        {
            return "Number: Decimal separator";
        }
        if (key.Equals("speech.startupActive", StringComparison.OrdinalIgnoreCase))
        {
            return "Spoken: Startup message";
        }
        if (key.Equals("app.title", StringComparison.OrdinalIgnoreCase))
        {
            return "Interface: App title";
        }
        if (key.StartsWith("type.", StringComparison.OrdinalIgnoreCase))
        {
            return "Section: " + key.Substring("type.".Length);
        }
        if (key.StartsWith("group.", StringComparison.OrdinalIgnoreCase))
        {
            return "Group: " + key.Substring("group.".Length);
        }
        if (key.StartsWith("reading.", StringComparison.OrdinalIgnoreCase))
        {
            return "Reading: " + key.Substring("reading.".Length);
        }
        if (key.StartsWith("message.", StringComparison.OrdinalIgnoreCase))
        {
            return "Message: " + SplitCamelKey(key.Substring("message.".Length));
        }
        if (key.StartsWith("a11y.", StringComparison.OrdinalIgnoreCase))
        {
            return "Spoken: " + key.Substring("a11y.".Length);
        }
        if (key.StartsWith("ui.", StringComparison.OrdinalIgnoreCase))
        {
            return "Interface: " + key.Substring("ui.".Length);
        }

        return "Other: " + key;
    }

    private static string LanguageEntrySortKey(string key)
    {
        if (key.StartsWith("language.", StringComparison.OrdinalIgnoreCase)) return "00|" + key;
        if (key.StartsWith("manual.", StringComparison.OrdinalIgnoreCase)) return "01|" + key;
        if (key.StartsWith("speech.", StringComparison.OrdinalIgnoreCase)) return "02|" + key;
        if (key.StartsWith("type.", StringComparison.OrdinalIgnoreCase)) return "03|" + key;
        if (key.StartsWith("group.", StringComparison.OrdinalIgnoreCase)) return "04|" + key;
        if (key.StartsWith("reading.", StringComparison.OrdinalIgnoreCase)) return "05|" + key;
        if (key.StartsWith("ui.", StringComparison.OrdinalIgnoreCase)) return "06|" + key;
        if (key.StartsWith("a11y.", StringComparison.OrdinalIgnoreCase)) return "07|" + key;
        if (key.StartsWith("message.", StringComparison.OrdinalIgnoreCase)) return "08|" + key;
        if (key.StartsWith("number.", StringComparison.OrdinalIgnoreCase)) return "09|" + key;
        return "99|" + key;
    }

    private static string SplitCamelKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var result = new System.Text.StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            if (i > 0 && char.IsUpper(ch) && char.IsLower(key[i - 1]))
            {
                result.Append(' ');
            }
            result.Append(ch);
        }

        return result.ToString();
    }

    private static string NormalizeNewLanguageFileName(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return "";
        }

        var fileName = System.IO.Path.GetFileName(requested.Trim());
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".txt";
        }

        return fileName;
    }

    private static string PromptForText(IWin32Window owner, string title, string label, string initialValue)
    {
        using (var dialog = new Form())
        {
            dialog.Text = title;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(460, 150);
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var textBox = new TextBox { Text = initialValue ?? "", Dock = DockStyle.Fill };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var okButton = new Button { Text = SensorReadoutForm.L("ui.OK", "OK"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = SensorReadoutForm.L("ui.Cancel", "Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(textBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;
            return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text : null;
        }
    }
}
