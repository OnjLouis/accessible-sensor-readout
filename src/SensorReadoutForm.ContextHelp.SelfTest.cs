using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm
{
    private void SelfTestContextHelp()
    {
        SelfTestProcessWatchLocation();
        Require(ContextHelp.IsHelpKey(Keys.F1), "Plain F1 must open help.");
        Require(!ContextHelp.IsHelpKey(Keys.Control | Keys.Shift | Keys.F1), "Modified F1 must not open plain-F1 help.");
        var bypass = typeof(PreferencesForm).GetMethod("ShouldBypassHotKeyCapture", BindingFlags.NonPublic | BindingFlags.Static);
        Require((bool)bypass.Invoke(null, new object[] { Keys.F1 }), "Plain F1 must bypass preferences hotkey capture.");
        Require(!(bool)bypass.Invoke(null, new object[] { Keys.Control | Keys.Shift | Keys.F1 }), "Ctrl+Shift+F1 must remain capturable.");
        var capture = typeof(PreferencesForm).GetMethod("HandleHotKeyBoxCommandKey", BindingFlags.NonPublic | BindingFlags.Static);
        using (var box = new TextBox { Text = "Ctrl+Shift+F12" })
        {
            Require(!(bool)capture.Invoke(null, new object[] { box, Keys.F1 }), "F1 must pass through the preference capture handler.");
            Require(box.Text == "Ctrl+Shift+F12", "F1 changed the saved hotkey.");
            Require((bool)capture.Invoke(null, new object[] { box, Keys.Control | Keys.Shift | Keys.F1 }), "Supported modified F1 was not captured.");
            Require(box.Text == "Ctrl+Shift+F1", "Modified F1 captured the wrong hotkey.");
        }
        using (var box = CreateInlineHotKeyBox("Ctrl+Shift+F12", "Spoken hotkey"))
        {
            Require(string.IsNullOrEmpty(box.AccessibleDescription), "Hotkey capture instructions belong in F1, not normal focus announcements.");
            var keyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            keyDown.Invoke(box, new object[] { new KeyEventArgs(Keys.F1) });
            Require(box.Text == "Ctrl+Shift+F12", "Inline hotkey capture changed its value on plain F1.");
            keyDown.Invoke(box, new object[] { new KeyEventArgs(Keys.Control | Keys.Shift | Keys.F1) });
            Require(box.Text == "Ctrl+Shift+F1", "Inline capture lost Ctrl+Shift+F1 support.");
        }

        var previousLanguage = activeLanguage;
        try
        {
            foreach (var choice in LoadLanguageChoices())
            {
                ActivateLanguage(choice.FileName);
                var entries = LoadHelpEntries(Path.Combine(GetLanguagesFolderPath(), choice.FileName));
                foreach (var entry in ControlHelpCatalog.EnglishEntries())
                    Require(entries.ContainsKey(entry.Key) && !string.IsNullOrWhiteSpace(entries[entry.Key]), "Missing control-help translation: " + choice.FileName + ": " + entry.Key);

                using (var category = new ListBox { AccessibleName = L("a11y.Reading section", "Reading section") })
                {
                    string name;
                    var text = ControlHelpCatalog.Describe(category, out name);
                    Require(text == entries["help.Categories"], "Translated category label did not find its stable help topic: " + choice.FileName);
                }
                using (var button = new Button { Text = L("ui.Remove &profile", "Remove &profile") })
                {
                    string name;
                    Require(ControlHelpCatalog.Describe(button, out name) == entries["help.Remove"], "Translated button did not find its help topic: " + choice.FileName);
                }
                using (var secret = new TextBox { Text = "private-help-test-value", UseSystemPasswordChar = true })
                {
                    ControlHelpCatalog.Bind(secret, "Credentials", "Monitoring password");
                    using (var dialog = ContextHelp.CreateDialog(secret, null))
                    {
                        var text = FindControls<TextBox>(dialog.Controls).Single();
                        Require(text.Text == entries["help.Credentials"], "Credential help must explain the field.");
                        Require(!dialog.Text.Contains(secret.Text) && !text.Text.Contains(secret.Text), "Help exposed entered private text.");
                        Require(text.ReadOnly && text.Multiline && text.TabStop && !dialog.ShowInTaskbar, "Help text is not an accessible owned-dialog surface.");
                        Require(dialog.CancelButton != null, "Escape must be able to close control help.");
                        RequireUniqueControlMnemonics("Control help " + choice.FileName, dialog.Controls);
                    }
                }
                SelfTestPreferenceHelpCoverage(choice.FileName);
                using (var tree = new TreeView())
                {
                    string name;
                    Require(ControlHelpCatalog.Describe(tree, out name) == entries["help.Tree"],
                        "An unknown tree must not promise feature-specific shortcuts: " + choice.FileName);
                }
                foreach (var pair in new Dictionary<string, string>
                {
                    { "Details", "Details" }, { "Review text", "TextReview" }, { "Report comparison results", "TextReview" },
                    { "Network tools results", "Details" }, { "Running processes", "Processes" },
                    { "Search results", "Search" }, { "Fan control target", "FanControls" },
                    { "Settings transfer categories", "SettingsTransfer" }, { "Hotkey and tray destinations", "HotkeyDestination" },
                    { "Also delete Config, Logs, and Reports", "UninstallData" },
                    { "Audio latency test duration in seconds", "Latency" }, { "Audio latency history graph", "Latency" },
                    { "Watch duration", "WatchDuration" }, { "Address other computers use", "Address" }
                })
                {
                    using (var control = new TextBox { AccessibleName = L("a11y." + pair.Key, pair.Key) })
                    {
                        string name;
                        Require(ControlHelpCatalog.Describe(control, out name) == entries["help." + pair.Value],
                            "Tool instructions must remain in translated F1 help: " + choice.FileName + ": " + pair.Key);
                    }
                }
            }
        }
        finally { activeLanguage = previousLanguage; }

        using (var preferences = new PreferencesForm(JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(settings)), latestRows, LoadLanguageChoices(), "Hotkeys"))
        {
            foreach (var box in FindControls<TextBox>(preferences.Controls).Where(b => b.ReadOnly && b.AccessibleName != null && b.AccessibleName.IndexOf("hotkey", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                string name;
                Require(ControlHelpCatalog.Describe(box, out name) == L("help.Hotkey", ControlHelpCatalog.EnglishEntries()["help.Hotkey"]), "A preferences hotkey field has no F1-specific help.");
            }
        }
        SelfTestOwnedHelpRouting();
    }

    private void SelfTestProcessWatchLocation()
    {
        var listType = typeof(SensorReadoutForm).GetNestedType("ProcessWatchListBox", BindingFlags.NonPublic);
        Require(listType != null, "The process watcher needs its own Alt+Enter file-location handler.");
        var opened = new List<string>();
        using (var list = (ListBox)Activator.CreateInstance(listType, new object[] { new Action<string>(opened.Add) }))
        {
            var path = Application.ExecutablePath;
            list.Items.Add(new ProcessWatchTarget { Name = "Test process", Path = path });
            list.Items.Add(new ProcessWatchTarget { Name = "Unavailable process", Path = "" });
            list.SelectedIndex = 0;
            var command = listType.GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            var message = Message.Create(IntPtr.Zero, 0x0104, new IntPtr((int)Keys.Enter), IntPtr.Zero);
            Require((bool)command.Invoke(list, new object[] { message, Keys.Alt | Keys.Enter }), "Watcher Alt+Enter must be handled without starting a watch.");
            Require(opened.Count == 1 && opened[0] == path, "Watcher Alt+Enter must open the selected executable's location.");
            Require(!(bool)command.Invoke(list, new object[] { message, Keys.Enter }), "Plain Enter must remain available to start the configured watch.");
            var menu = list.ContextMenuStrip;
            Require(menu != null && menu.Items.Count == 1, "Watcher file location needs a mouse/context-menu action too.");
            var opening = typeof(ContextMenuStrip).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic);
            opening.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
            var item = (ToolStripMenuItem)menu.Items[0];
            Require(item.Enabled && item.ShortcutKeys == (Keys.Alt | Keys.Enter), "Watcher menu must expose the available Alt+Enter action.");
            item.PerformClick();
            Require(opened.Count == 2 && opened[1] == path, "Watcher menu and shortcut must use the same selected path.");
            list.SelectedIndex = 1;
            opening.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
            Require(!item.Enabled, "Watcher location must be unavailable when Windows supplies no executable path.");
            command.Invoke(list, new object[] { message, Keys.Alt | Keys.Enter });
            Require(opened.Count == 2, "Unavailable process must not reuse the previous process location.");
            list.SelectedIndex = 0;
            list.Enabled = false;
            command.Invoke(list, new object[] { message, Keys.Alt | Keys.Enter });
            Require(opened.Count == 2, "Disabled watcher selection must not open a location.");
        }
    }

    private void SelfTestPreferenceHelpCoverage(string languageFile)
    {
        var clone = JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(settings));
        clone.LanguageFile = languageFile;
        using (var preferences = new PreferencesForm(clone, latestRows, LoadLanguageChoices(), "Hotkeys"))
        {
            var genericKeys = new[] { "TextField", "Choice", "Number", "Toggle", "List", "Tree", "Command" };
            var english = ControlHelpCatalog.EnglishEntries();
            var genericHelp = new HashSet<string>(genericKeys.Select(key => L("help." + key, english["help." + key])));
            var uncovered = new List<string>();
            foreach (var control in FindControls<Control>(preferences.Controls).Where(c => c is ButtonBase || c is ComboBox || c is NumericUpDown || c is TextBoxBase || c is ListBox))
            {
                string name;
                if (genericHelp.Contains(ControlHelpCatalog.Describe(control, out name))) uncovered.Add(control.AccessibleName ?? (control is ButtonBase ? control.Text : control.GetType().Name));
                Require(string.IsNullOrEmpty(control.AccessibleDescription) ||
                    System.Text.RegularExpressions.Regex.IsMatch(control.AccessibleDescription, @"^Alt\+[1-4]$"),
                    "Preferences repeats instructions outside F1 (" + languageFile + "): " + control.AccessibleName);
                var shortcut = control as ShortcutButton;
                if (shortcut != null && !string.IsNullOrEmpty(shortcut.ShortcutText))
                    Require(shortcut.AccessibilityObject.KeyboardShortcut == shortcut.ShortcutText, "Concise controls must retain their accessible keyboard shortcut.");
            }
            Require(uncovered.Count == 0, "Preferences controls need specific help (" + languageFile + "): " + string.Join("; ", uncovered));
        }
    }

    private delegate bool HelpKeyMessageFilter(ref Message message, Keys? modifiers);

    private void SelfTestOwnedHelpRouting()
    {
        using (var owner = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(500, 300) })
        using (var nested = new Form { Owner = owner, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = owner.Location, Size = owner.Size })
        using (var helper = new ContextHelp(owner, delegate { }))
        using (var box = CreateInlineHotKeyBox("Ctrl+Shift+F12", "Spoken hotkey"))
        using (var closeTimer = new Timer { Interval = 100 })
        {
            // Synthetic messages must not inherit modifiers held on the user's desktop.
            var filter = (HelpKeyMessageFilter)Delegate.CreateDelegate(typeof(HelpKeyMessageFilter), helper,
                typeof(ContextHelp).GetMethod("FilterKeyMessage", BindingFlags.Instance | BindingFlags.NonPublic));
            nested.Controls.Add(box);
            nested.Show();
            box.Focus();
            var sawHelp = false;
            Exception failure = null;
            closeTimer.Tick += delegate
            {
                closeTimer.Stop();
                var help = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Owner == nested);
                if (help == null) return;
                sawHelp = true;
                try
                {
                    var text = FindControls<TextBox>(help.Controls).Single();
                    Require(text.Focused, "Help must initially focus its readable text.");
                    var repeat = Message.Create(text.Handle, 0x0100, new IntPtr((int)Keys.F1), new IntPtr(1L << 30));
                    Require(filter(ref repeat, Keys.None), "Held F1 repeat must be consumed without nesting help.");
                    Require(Application.OpenForms.Cast<Form>().Count(f => f.Owner == help) == 0, "Held F1 opened nested help.");
                }
                catch (Exception ex) { failure = ex; }
                finally { help.Close(); }
            };
            var unrelated = Message.Create(box.Handle, 0x0046, new IntPtr(long.MaxValue), IntPtr.Zero);
            Require(!helper.PreFilterMessage(ref unrelated), "Help must ignore non-keyboard messages safely.");
            var press = Message.Create(box.Handle, 0x0100, new IntPtr((int)Keys.F1), IntPtr.Zero);
            Require(!filter(ref press, Keys.Control | Keys.Shift), "Modified F1 must pass through without opening control help.");
            closeTimer.Start();
            Require(filter(ref press, Keys.None), "F1 in an owned nested hotkey dialog was not handled.");
            closeTimer.Stop();
            Require(sawHelp, "F1 did not open help in the nested dialog.");
            if (failure != null) throw failure;
            Require(box.Text == "Ctrl+Shift+F12", "Opening help changed an inline hotkey.");
            Require(box.Focused, "Closing help did not return focus to the hotkey field.");
            var release = Message.Create(box.Handle, 0x0101, new IntPtr((int)Keys.F1), IntPtr.Zero);
            Require(filter(ref release, Keys.None), "The F1 release must not reach the capture field.");
            nested.Close();
        }
    }

    private static Dictionary<string, string> LoadHelpEntries(string path)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line.StartsWith("help.", StringComparison.Ordinal)) continue;
            Require(!entries.ContainsKey(line.Substring(0, separator)), "Duplicate control-help language key.");
            entries.Add(line.Substring(0, separator), line.Substring(separator + 1));
        }
        return entries;
    }
}
