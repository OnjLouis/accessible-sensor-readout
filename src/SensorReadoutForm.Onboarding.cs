using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void ShowStartupOnboardingIfNeeded()
    {
        if (IsDisposed || reportViewMode || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        if (!settings.InitialSetupWizardDismissed &&
            (string.IsNullOrWhiteSpace(settings.ShowHideHotKey) || string.IsNullOrWhiteSpace(settings.SpeakTrayHotKey)))
        {
            ShowInitialSetupWizard(true);
            return;
        }

        if (settings.ShowTipsOnStartup)
        {
            ShowTipsDialog(true);
        }
    }

    private void ShowInitialSetupWizard(bool firstRun)
    {
        using (var dialog = new Form())
        {
            dialog.Text = string.Format("{0} {1} - {2}", "Sensor Readout", AppVersion, T("ui.Initial setup", "Initial setup"));
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(620, 360);
            dialog.MinimumSize = new Size(520, 300);
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new TextBox
            {
                Text = T("message.Initial setup intro", "Welcome to Sensor Readout. Here, you can set two of the most important global hotkeys to make Sensor Readout useful to you. Press Tab or Shift+Tab to move through this dialog. Changes are saved as soon as you enter them, and you can change them later in Preferences."),
                ReadOnly = true,
                Multiline = true,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                Height = 88,
                TabStop = true,
                TabIndex = 0,
                AccessibleName = T("a11y.Initial setup instructions", "Initial setup instructions")
            };

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var showHideLabel = new Label { Text = T("ui.Show/hide hotkey", "Show/hide hotkey"), AutoSize = true, Dock = DockStyle.Fill };
            var speakLabel = new Label { Text = T("ui.Speak tray status hotkey", "Speak tray status hotkey"), AutoSize = true, Dock = DockStyle.Fill };
            var showHideBox = CreateSetupHotKeyBox(settings.ShowHideHotKey, T("a11y.Show/hide hotkey", "Show/hide hotkey"));
            var speakBox = CreateSetupHotKeyBox(settings.SpeakTrayHotKey, T("a11y.Speak tray status hotkey", "Speak tray status hotkey"));
            showHideBox.TabIndex = 1;
            speakBox.TabIndex = 2;
            var hintsBox = new CheckBox
            {
                Text = T("ui.Show tips on startup", "Show &tips on startup"),
                Checked = settings.ShowTipsOnStartup,
                AutoSize = true,
                TabIndex = 3,
                AccessibleName = T("a11y.Show tips on startup", "Show tips on startup")
            };
            var note = new Label
            {
                Text = T("message.Initial setup note", "Tip: a show/hide hotkey is strongly recommended if you start minimized to the notification area."),
                AutoSize = true,
                Dock = DockStyle.Fill
            };

            grid.Controls.Add(showHideLabel, 0, 0);
            grid.Controls.Add(showHideBox, 1, 0);
            grid.Controls.Add(speakLabel, 0, 1);
            grid.Controls.Add(speakBox, 1, 1);
            grid.Controls.Add(hintsBox, 1, 2);
            grid.Controls.Add(note, 0, 3);
            grid.SetColumnSpan(note, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = CreateCloseButton();
            closeButton.Text = T("ui.Close", "&Close");
            var prefsButton = new Button { Text = T("ui.Open &Preferences", "Open &Preferences"), AutoSize = true };
            prefsButton.TabIndex = 4;
            closeButton.TabIndex = 5;
            Action saveSetup = delegate
            {
                var showHide = NormalizeHotKeyText(showHideBox.Text);
                var speak = NormalizeHotKeyText(speakBox.Text);
                if (!ValidateSetupHotKeys(showHide, speak))
                {
                    return;
                }

                settings.ShowHideHotKey = showHide;
                settings.SpeakTrayHotKey = speak;
                settings.ShowTipsOnStartup = hintsBox.Checked;
                SaveSettings(settings);
                RegisterGlobalHotKeys();
                BuildHotkeysMenu();
                UpdateTrayStatus();
                statusLabel.Text = T("status.Initial setup saved.", "Initial setup saved.");
            };

            closeButton.Click += delegate
            {
                saveSetup();
                settings.InitialSetupWizardDismissed = true;
                SaveSettings(settings);
                dialog.Close();
            };
            prefsButton.Click += delegate
            {
                saveSetup();
                settings.InitialSetupWizardDismissed = true;
                SaveSettings(settings);
                dialog.Close();
                ShowPreferences("Hotkeys");
            };
            showHideBox.TextChanged += delegate { saveSetup(); };
            speakBox.TextChanged += delegate { saveSetup(); };
            hintsBox.CheckedChanged += delegate { saveSetup(); };

            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(prefsButton);
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(grid, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate
            {
                dialog.BeginInvoke(new Action(delegate
                {
                    dialog.ActiveControl = intro;
                    intro.Focus();
                    intro.Select(0, 0);
                }));
            };
            dialog.ShowDialog(this);
        }

        if (firstRun && settings.ShowTipsOnStartup)
        {
            ShowTipsDialog(true);
        }
    }

    private bool ValidateSetupHotKeys(string showHide, string speak)
    {
        if (!string.IsNullOrWhiteSpace(showHide) && string.IsNullOrWhiteSpace(NormalizeHotKeyText(showHide)))
        {
            MessageBox.Show(this, T("message.Show/hide hotkey is not valid.", "Show/hide hotkey is not valid."), T("ui.Initial setup", "Initial setup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(speak) && string.IsNullOrWhiteSpace(NormalizeHotKeyText(speak)))
        {
            MessageBox.Show(this, T("message.Speak tray hotkey is not valid.", "Speak tray status hotkey is not valid."), T("ui.Initial setup", "Initial setup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(showHide) && string.Equals(showHide, speak, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, T("message.Choose different hotkeys.", "Choose different hotkeys for show/hide and speak tray status."), T("ui.Initial setup", "Initial setup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private static TextBox CreateSetupHotKeyBox(string hotKey, string accessibleName)
    {
        var box = new SetupHotKeyTextBox
        {
            Text = NormalizeHotKeyText(hotKey),
            ReadOnly = true,
            Dock = DockStyle.Fill,
            AccessibleName = accessibleName,
            AccessibleDescription = L("a11y.Press a key combination with at least two modifiers to assign it. Use Backspace or Delete to clear it.", "Press a key combination with at least two modifiers to assign it. Use Backspace or Delete to clear it.")
        };
        return box;
    }

    private sealed class SetupHotKeyTextBox : TextBox
    {
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Tab || key == Keys.Escape || ((keyData & Keys.Alt) == Keys.Alt && key == Keys.F4))
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (key == Keys.Back || key == Keys.Delete)
            {
                Text = "";
                return true;
            }

            if (IsModifierOnlyHotKeyData(keyData))
            {
                return true;
            }

            var text = HotKeyTextFromKeyData(keyData);
            if (!string.IsNullOrWhiteSpace(text))
            {
                Text = text;
                SelectAll();
            }
            else
            {
                System.Media.SystemSounds.Beep.Play();
            }

            return true;
        }
    }

    private void ShowTipsDialog(bool startupTip)
    {
        var tips = BuildTips();
        if (tips.Count == 0)
        {
            return;
        }

        using (var dialog = new Form())
        {
            dialog.Text = startupTip ? T("ui.Sensor Readout tip", "Sensor Readout tip") : T("ui.Hints and tips", "Hints and tips");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(720, 420);
            dialog.MinimumSize = new Size(520, 320);
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = T("a11y.Hints and tips", "Hints and tips") };
            foreach (var tip in tips)
            {
                list.Items.Add(tip);
            }

            var showTipsBox = new CheckBox
            {
                Text = T("ui.Show tips on startup", "Show &tips on startup"),
                Checked = settings.ShowTipsOnStartup,
                AutoSize = true,
                AccessibleName = T("a11y.Show tips on startup", "Show tips on startup")
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = CreateCloseButton();
            var randomButton = new Button { Text = T("ui.&Random tip", "&Random tip"), AutoSize = true };
            closeButton.Click += delegate { dialog.Close(); };
            randomButton.Click += delegate
            {
                if (list.Items.Count <= 1)
                {
                    return;
                }

                var random = new Random();
                var next = list.SelectedIndex;
                while (next == list.SelectedIndex)
                {
                    next = random.Next(list.Items.Count);
                }
                list.SelectedIndex = next;
            };
            showTipsBox.CheckedChanged += delegate
            {
                settings.ShowTipsOnStartup = showTipsBox.Checked;
                SaveSettings(settings);
            };

            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(randomButton);
            layout.Controls.Add(list, 0, 0);
            layout.Controls.Add(showTipsBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate
            {
                list.SelectedIndex = startupTip ? Math.Abs(DateTime.Now.DayOfYear) % tips.Count : 0;
                list.Focus();
            };
            dialog.FormClosed += delegate
            {
                settings.ShowTipsOnStartup = showTipsBox.Checked;
                SaveSettings(settings);
            };
            dialog.ShowDialog(this);
        }
    }

    private static List<string> BuildTips()
    {
        return new List<string>
        {
            L("tip.Show/hide hotkey", "Set a show/hide hotkey so Sensor Readout is easy to recover if it starts minimized or Windows hides the notification area icon."),
            L("tip.Speak tray status", "Set a speak tray status hotkey for a quick spoken summary without opening the main window."),
            L("tip.F3 search", "Press F3 to search across categories. Type immediately, then tab to the result list."),
            L("tip.F4 review", "Press F4 on a reading to review its text in a read-only edit box, useful when a screen reader pronunciation is unclear."),
            L("tip.Details", "Press Enter or choose Details on rows with more information to see deeper Windows, driver, device, storage, or network data."),
            L("tip.Quick hotkey assignment", "Press Ctrl+Shift+H on a reading to add or remove it from notification area status or a spoken hotkey profile."),
            L("tip.Support report", "Use Help > Prepare support report to create a diagnostic ZIP with the report and summary files needed for troubleshooting."),
            L("tip.Anonymized reports", "Use File > Save anonymized report before sharing a report publicly; private identifiers such as IP and MAC addresses are masked."),
            L("tip.Report comparison", "Use File > Compare reports to compare two reports from before and after a hardware, driver, or settings change."),
            L("tip.Plug-ins", "Laptop-specific plug-ins can expose extra fan and temperature data on supported Dell, Huawei, Lenovo, MSI, Asus, HP, OMEN, Victus, and Framework systems."),
            L("tip.Tasks category", "The Tasks category shows compact highest-CPU, highest-memory, highest-GPU, and highest-GPU-memory process summaries without turning Sensor Readout into a full task manager."),
            L("tip.Spoken hotkeys category", "The Spoken Hotkeys category gives visual users one place to review what each configured spoken hotkey currently says."),
            L("tip.Category order", "Use Preferences > Categories, or Delete and Ctrl+Up/Ctrl+Down from the main category list, to hide or reorder whole categories."),
            L("tip.NVIDIA SMI memory", "On NVIDIA systems, NVIDIA SMI memory readings may better match vendor tools than Windows GPU memory counters because they use driver-reported accounting."),
            L("tip.Starter hotkeys", "Useful spoken hotkey starters include system status, memory status, C drive activity, network status, Tasks summary, and uptime."),
            L("tip.Hotkey examples", "Ctrl+Shift+Function keys are a practical pattern for spoken hotkeys because they are easy to group and unlikely to conflict with normal app commands.")
        };
    }

    private void ResetAllSettingsAndRestart(IWin32Window owner)
    {
        var result = MessageBox.Show(owner,
            T("message.Delete all settings confirmation", "Delete all Sensor Readout settings and restart with defaults? Logs and reports will be kept."),
            T("ui.Delete all settings", "Delete all settings"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var configFolder = GetConfigFolderPath();
            if (Directory.Exists(configFolder))
            {
                Directory.Delete(configFolder, true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, T("message.Could not delete settings:", "Could not delete settings:") + " " + ex.Message, T("ui.Delete all settings", "Delete all settings"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            Process.Start(Application.ExecutablePath);
        }
        catch
        {
        }

        Environment.Exit(0);
    }
}
