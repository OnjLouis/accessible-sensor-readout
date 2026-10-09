using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class ControlHelpCatalog
{
    private sealed class Topic
    {
        internal readonly string Key;
        internal readonly string Text;
        internal readonly string[] Names;
        internal Topic(string key, string text, params string[] names) { Key = key; Text = text; Names = names; }
    }

    private sealed class Binding
    {
        internal string Key;
        internal string Name;
    }

    private static readonly ConditionalWeakTable<Control, Binding> Bindings = new ConditionalWeakTable<Control, Binding>();
    private static readonly Topic[] Topics =
    {
        new Topic("Categories", "Choose a category, then press Tab to move to its readings. Type several letters to find a category. Delete hides it; Ctrl+Up or Ctrl+Down changes its position and its number shortcut.", "Reading section"),
        new Topic("CategoryVisibility", "Checked categories appear in the main view. Space changes visibility; Ctrl+Up and Ctrl+Down change their order. Main-window number shortcuts follow this visible order. Hidden categories are still collected for reports and diagnostics.", "Visible categories", "Show category", "Hide category", "Show", "Hide"),
        new Topic("Readings", "Browse readings with the arrow keys. Right and Left expand or collapse groups. Enter opens Details; Alt+Enter opens a related Windows setting or file location when available. F3 searches, Ctrl+C copies, and Delete hides the selected reading.", "Readings"),
        new Topic("Details", "Browse the selected reading's details. The values are read-only. Expand or collapse groups with Right and Left. F3 searches, F4 opens readable text, Ctrl+C copies, Ctrl+Shift+C copies only values, Ctrl+M copies matching information, and Escape closes the dialog.", "Details", "Network tools results", "Live audio latency readings"),
        new Topic("TextReview", "Review the read-only text with standard text-navigation keys. Ctrl+A selects all text; Ctrl+C copies selected text. The Copy button copies the whole text. Escape closes this window.", "Report comparison results", "Review text"),
        new Topic("Refresh", "Automatic refresh collects new readings at the configured interval. Background collection may be reduced when no hotkeys, alarms, curves, or logging need frequent readings. In the main window, F5 requests a fresh collection.", "Auto refresh", "Refresh interval in seconds", "Refresh while focused", "Pause automatic updates"),
        new Topic("Units", "Choose how values are displayed. Classic uses a 1024 scale with KB, MB and GB; Binary IEC uses the same scale with KiB, MiB and GiB; Decimal SI uses a 1000 scale. Memory, storage sizes and transfer rates have separate choices. This does not change the underlying reading.", "Memory size units", "Storage size units", "Transfer rate units", "Memory and GPU memory units", "Transfer rate and transfer total units"),
        new Topic("Temperature", "Choose Celsius, Fahrenheit, or both for displayed temperatures. This changes presentation, not the underlying sensor or a saved fan curve's Celsius temperatures.", "Temperature unit"),
        new Topic("Decimal", "Choose the decimal separator for displayed values, or use the selected language's default. This does not change numeric values.", "Decimal separator"),
        new Topic("Language", "Choose the interface language. Missing translations fall back to English. The full manual uses the matching installed language where available.", "Language"),
        new Topic("Expansion", "Choose whether categories start expanded, collapsed, or remember your last expand-all or collapse-all choice. This is one shared choice, not a separate saved state for every row.", "Reading tree expansion", "Expand when switching categories", "Keep collapsed when switching categories", "Remember my last expand or collapse action"),
        new Topic("Tray", "Choose whether Sensor Readout remains available in the notification area and how much of the configured status is shown in its tooltip. The notification-area profile is edited on the Hotkeys tab.", "Show status in notification area", "Show as many readings as possible in notification area tooltip"),
        new Topic("Startup", "Choose whether this copy runs when you sign in to Windows and whether it starts minimized. Startup registration changes only when you explicitly change its setting; copying another computer's configuration does not enable startup.", "Run at Windows startup", "Start minimized to notification area"),
        new Topic("Shortcut", "Create or remove a desktop shortcut for this copy of Sensor Readout. This does not change your readings or profiles.", "Create desktop shortcut"),
        new Topic("Install", "Install copies the portable app to the Windows programs folder for your account and preserves your data. Uninstall removes installed app files while keeping Config, Logs and Reports. Portable use does not require installation.", "Install location", "Install to this PC", "Uninstall from this PC"),
        new Topic("UninstallData", "Leave this unchecked to keep settings, logs, and saved reports after uninstalling.", "Also delete Config, Logs, and Reports"),
        new Topic("SettingsReset", "Delete the saved settings and restart with defaults after confirmation. Logs and reports are kept. Export any profiles you want to retain before using this command.", "Delete all settings"),
        new Topic("Updates", "Choose automatic update checks, confirmation, or quiet installation. Updates preserve your settings and verify the package before installing it. Help > Check for updates remains available for a manual check.", "Check for updates", "Install updates quietly", "Show update install confirmation"),
        new Topic("Sound", "Choose a sound from the Sounds folder, or no sound. Where preview is available, changing the selection plays it. The associated checkbox controls whether the sound is used.", "Update available sound", "Diagnostic start sound", "Diagnostic complete sound", "Alarm sound", "Fan profile sound", "Startup sound", "Shutdown sound", "Process watch completion sound", "Audio latency completion sound", "Remote viewer notification sound"),
        new Topic("Message", "Enter the words to announce for this event. This is separate from the profile or alarm name. The associated speech checkbox controls whether the message is spoken.", "Startup speech message", "Alarm spoken message", "Fan profile spoken message", "Process watch stopped message", "Audio latency completion message"),
        new Topic("Speech", "Enable or disable the associated spoken announcement. Explicit spoken hotkeys can still be used. Spoken and visual feedback preferences control category announcements and optional visual hotkey feedback.", "Speak startup message", "Speak when fan profile changes", "Speak when process watch stops", "Speak when watch stops", "Speak when test stops", "Speak diagnostic progress", "Speak with screen reader"),
        new Topic("CategorySpeech", "Choose Full, Brief, or Off for category announcements. Brief keeps the category name and shortcut without the longer explanation. Windows speech without a screen reader has its own opt-in checkbox.", "Category speech", "Speak categories through Windows speech"),
        new Topic("DeviceNames", "Include device names in spoken reading feedback to distinguish similar sensors. Turning this off makes announcements shorter. Custom spoken reading labels can still identify the device.", "Include device names in spoken feedback"),
        new Topic("Visual", "Show spoken hotkey results in a floating window, choose its screen position and timeout. This works with or without a screen reader and should not take focus from the app you are using.", "Show visual spoken hotkey feedback", "Visual spoken feedback placement", "Visual spoken feedback timeout seconds"),
        new Topic("Hotkey", "Press a key combination with at least two modifiers, such as Ctrl+Shift+F1. Unsafe Windows shortcuts are rejected. Plain F1 opens help without changing this field. Tab moves on, Escape closes the dialog, and Backspace or Delete clears the key.", "Show/hide hotkey", "Speak tray status hotkey", "Spoken hotkey", "Fan profile hotkey", "Configure show/hide hotkey"),
        new Topic("HotkeyProfiles", "Choose a spoken hotkey profile to edit its name, key and ordered readings. Notification area status is a permanent, non-removable profile. Alt+2 focuses this list; Alt+3 and Alt+4 focus available and selected readings.", "Hotkey profiles"),
        new Topic("HotkeyDestination", "Choose a spoken hotkey or notification-area status as the destination for the selected reading. Enter or the Add/Remove button adds or removes that reading and saves the change. New spoken hotkey creates another destination.", "Hotkey and tray destinations"),
        new Topic("ReadingSelection", "Choose available readings and add them with Ctrl+Right. In the selected list, Ctrl+Left removes, Ctrl+Up or Ctrl+Down reorders, and Ctrl+C, Ctrl+X or Ctrl+V copies, cuts or pastes readings between profiles. These choices determine the spoken order.", "Available notification area readings", "Available readings for spoken hotkey", "Readings spoken by this hotkey", "Selected notification area readings in display order", "Add reading", "Remove reading", "Cut", "Paste"),
        new Topic("Name", "Choose a meaningful name for this profile, alarm or connection. It helps you find the item again; a separate spoken-message or reading-label setting controls custom announcements.", "Spoken hotkey name", "Fan profile name", "Alarm name", "Name"),
        new Topic("Skip", "Skip missing or unavailable readings when this profile is spoken. Turning this off can help diagnose a sensor that is not returning data.", "Skip unavailable notification area readings", "Skip unavailable readings for this spoken hotkey"),
        new Topic("DoublePress", "Choose the double-press interval for copying spoken hotkey results to the clipboard. Two distinct presses are required; holding a key does not count as another press.", "Double-press hotkey copies"),
        new Topic("FanProfiles", "Choose a fan profile and the controls it changes. A profile can request a supported manual percentage or return controls to automatic. Hardware and plug-in capabilities determine what can actually be applied. Ctrl+Right adds the selected control; Ctrl+Left or Delete removes it, and Ctrl+Up or Ctrl+Down changes its order.", "Fan profiles", "Available fan controls", "Fan controls in this profile", "Profile fan actions", "Fan profile action", "Fan profile percent", "Toggle fan profile back to automatic"),
        new Topic("FanControls", "Choose a supported fan control and a manual percentage or mode, then use Manual adjust to apply it. Selected auto returns that control to automatic; All fans reset returns all supported controls to automatic. F2 focuses the label field. Editing a percentage alone does not apply it.", "Fan control target", "Fan manual percentage"),
        new Topic("FanCurve", "Choose the fan control and temperature source, then define low, high and emergency temperatures and percentages. Temperatures are stored in Celsius. Enable the curve only after checking that the selected hardware supports control and the settings are safe.", "Low fan percent", "High fan percent", "Emergency fan percent", "Low temperature, Celsius", "High temperature, Celsius", "Emergency temperature, Celsius", "Minimum fan percent change"),
        new Topic("StoppedFans", "Include stopped, hidden or unpopulated fan controls in this list. This can reveal a control you want to configure; it does not start or change the fan by itself.", "Show stopped or hidden fan controls", "Show stopped or unpopulated fan headers", "Show stopped"),
        new Topic("Alarms", "Choose an alarm to edit its reading, condition and threshold. An enabled alarm can speak a custom message, play a sound, or both. Check the selected unit before setting the threshold.", "Alarms", "Alarm reading", "Alarm condition", "Alarm threshold", "Alarm threshold unit"),
        new Topic("Cooldown", "Cooldown limits how soon an alarm may sound again. With Repeat after cooldown off, an active condition warns once until a valid non-matching reading clears it. Missing readings do not re-arm the alarm.", "Alarm cooldown seconds", "Repeat after cooldown"),
        new Topic("Presets", "Choose a starter profile or alarm, then review its readings, thresholds and hotkey for this computer. Presets are starting points, not a guarantee that every sensor exists.", "Spoken hotkey presets", "Alarm presets", "Presets"),
        new Topic("Removal", "Choose whether profile removal asks for confirmation. Hidden readings and categories can be restored from Hidden items; removing a profile is different from hiding a reading.", "Confirm fan profile removal", "Confirm spoken hotkey profile removal", "Do not show this message again"),
        new Topic("Hidden", "Checked items are hidden. Uncheck an item to show it again. Hiding affects the main view, not collection for reports or diagnostics, and hiding a mirrored hotkey reading does not remove it from its spoken profile.", "Hidden readings and categories", "Show selected", "Show all"),
        new Topic("Plugins", "Enable only plug-ins needed for this computer. Disabled plug-ins are not loaded. Plug-ins are trusted code, so install them only from sources you trust. Fan-control support depends on the plug-in and hardware.", "Installed Plug-Ins"),
        new Topic("Logging", "Choose how much diagnostic information is logged. Debug is useful temporarily for investigation but produces more output. Crash logging is attempted even when routine logging is off. Logs may include hardware and diagnostic details.", "Logging level"),
        new Topic("LanguageEditor", "Choose a language file and entry, then edit its translation. F3 searches entries. Preserve placeholders such as {0} and shortcut markers. Control-help entries use help. keys and can be translated here too. F2 moves from the entries list to the value field.", "Language file to edit", "Language entries", "Language entry value", "Save entry", "Reload", "Open folder"),
        new Topic("Processes", "Choose a running process. Names, role, start time and parent help distinguish similar entries. Enter starts the configured watch. Alt+Enter or the context menu opens its file location when available. Watching records resource counters, not private app contents.", "Running processes"),
        new Topic("WatchDuration", "Set the duration and unit for process watching. Zero means run until stopped. Start closes the dialog and watches in the background; Ctrl+Shift+W stops it and saves the report automatically.", "Watch duration", "Watch duration unit"),
        new Topic("WatchFormat", "Choose HTML for a readable, table-based report or CSV for further analysis. The report is saved automatically in Reports when watching stops. The next watch reuses these settings.", "Process watch report format"),
        new Topic("Latency", "Measure DPC, interrupt and hard-page-fault activity while reproducing an audio problem. No trace runs until you start. Use the live monitor for current readings or review the automatically saved HTML report after stopping. Zero duration runs until stopped. Closing the live monitor does not stop the diagnostic; reopen it from Options. Ctrl+Shift+D stops the diagnostic and saves its report.", "Audio latency diagnostic explanation", "Live audio latency monitor explanation", "Audio latency history graph", "Audio latency test duration in seconds", "Open live audio latency monitor when test starts"),
        new Topic("Network", "Enter an IP address or host name, then run the lookup. DNS and optional ping help investigate connectivity. Online metadata is requested only for public addresses; local and private addresses are not sent to the lookup service. This is not a speed test or port scan.", "Address or host name", "Include ping test"),
        new Topic("Servers", "Choose a saved server connection, then refresh its computers or view one. Remote readings replace the current view. After viewing a remote computer, Ctrl+R in the main window returns to this computer. Removing a saved connection is not deleting another person's computer.", "Remote servers", "Computers on selected server", "View computer", "Refresh computers", "Remove server", "Enable this server", "Add server", "Edit server"),
        new Topic("Address", "Enter the server address, including http:// or https:// and a port when needed. Use an address the other computer can reach. Localhost refers to the computer making the connection, not automatically to the hosting computer.", "Server address", "Address other computers use"),
        new Topic("HostPort", "Choose the listening port for the built-in server. Other computers must use the same port in the server address. Firewall, network and VPN configuration determine whether they can reach it."),
        new Topic("Credentials", "The access token authorizes server access; the monitoring password encrypts the shared readings. Computers sharing a monitoring space need the same password. Keep credentials and connection files private. Help never displays the entered credential.", "Access token", "Monitoring password", "Generate password"),
        new Topic("Host", "Enable the built-in server on this computer, or save a connection file for another computer. Choose an address reachable from that computer and keep the file private. Hosting and publishing readings are separate choices.", "Host this computer", "Save host connection", "Share this computer"),
        new Topic("RemoteFan", "Allow remote use of saved fan profiles only if you want this server connection to control supported fans on this computer. A remote client can request saved profiles, not arbitrary fan settings.", "Allow remote saved fan profiles", "Run fan profile", "Remote fan profiles"),
        new Topic("Viewer", "Choose speech or a sound when a remote viewer connects or disconnects. These notifications help you notice access to this computer. Preview checks the chosen feedback without creating a real viewer connection.", "Announce viewer changes with speech", "Preview change speech"),
        new Topic("Stats", "Review the exact anonymous community-statistics payload before uploading. Nothing is submitted until you choose Upload. Statistics omit identifying report data; a full diagnostic report contains more detail and should be shared deliberately.", "Community stats payload preview", "Community stats privacy summary", "Upload"),
        new Topic("Diagnostics", "Diagnostics collect reports and logs for troubleshooting. The completed ZIP is saved in Reports. Review it before sharing because full diagnostics can contain computer, device and path information.", "Support report instructions", "Play diagnostic sounds", "Save support report"),
        new Topic("Tips", "Show or review short hints about Sensor Readout. Turning off startup tips does not disable help or the manual.", "Show tips on startup", "Hints and tips"),
        new Topic("CopyAction", "Copy the selected information to the clipboard. Copy value only omits its label; Copy matching collects related matching information. Copying does not change the reading or profile.", "Copy", "Copy value only", "Copy matching", "Copy help"),
        new Topic("Reorder", "Move the selected item up or down in its list. Ctrl+Up and Ctrl+Down provide the same action where available. The new position determines the order used by that list or profile.", "Move up", "Move down", "Up", "Down"),
        new Topic("Remove", "Remove the selected item from this list or profile. This is not the same as hiding a main reading. Review any confirmation before accepting; the permanent notification-area profile cannot be removed.", "Remove", "Remove profile", "Delete"),
        new Topic("Clear", "Clear this field or selection. Clearing a hotkey disables that key assignment; it does not delete the entire profile.", "Clear"),
        new Topic("Reset", "Restore the defaults for the selected setting or profile. Review the resulting readings and labels before relying on them.", "Reset to defaults", "Reset labels", "Reset default", "Reset this", "Reset"),
        new Topic("Label", "Choose the label used for this reading or fan. A custom reading label controls what the spoken hotkey says. Renaming does not change the underlying device or sensor.", "Fan label", "Rename", "Rename reading", "Save label for selected fan control"),
        new Topic("New", "Create a new item for this feature, then choose its name and settings. A new fan profile does not change fans until applied, and a new alarm should be reviewed before enabling it.", "New", "Add selected", "Add"),
        new Topic("Enabled", "Enable or disable the selected alarm, fan curve or connection. Enabling a fan curve can change supported fan controls; check its temperature source and settings first.", "Enabled"),
        new Topic("Start", "Start the configured test or process watch. It runs in the background and saves its report automatically when stopped. Check its duration and completion feedback before starting.", "Start"),
        new Topic("Stop", "Stop the active test or process watch and save its report. Closing its setup dialog is not a request to stop an already-running watch.", "Stop"),
        new Topic("RefreshList", "Reload the available items or results for this feature. This does not apply a fan setting or start a process watch.", "Refresh list", "Refresh", "Run"),
        new Topic("Search", "Search the available readings or the current view. Type words to narrow matching labels and details, then use the result list to choose an item. Enter selects the highlighted result; Escape cancels the search.", "Search", "Search text", "Search results", "Find", "Find reading", "Find fan control", "Find language entry"),
        new Topic("SettingsTransfer", "Choose which settings categories to include when exporting or importing. Checked categories are included. Review the resulting profiles and key assignments before using them on another computer.", "Settings transfer categories"),
        new Topic("Import", "Choose a file to import using this command. Review the imported configuration before using it. Profiles are matched to available readings; plug-ins are left disabled until you enable them yourself.", "Import", "Import connection", "Import Plug-In", "Import from ZIP"),
        new Topic("Location", "Open the related Windows setting or the selected process file location in Explorer when available. This does not change a setting or run the selected program.", "Open Windows setting", "Open file location", "Open related Windows setting", "Open related file location"),
        new Topic("Expand", "Expand or collapse the displayed tree groups. This changes the view, not the collected readings.", "Expand all", "Collapse all"),
        new Topic("Preview", "Preview the selected sound or spoken feedback. This does not apply a fan setting or start a diagnostic.", "Preview sound", "Preview speech"),
        new Topic("Apply", "Apply the selected fan request now. This can change hardware behaviour on supported devices. Check the selected control, mode and percentage first.", "Apply now", "Apply manual percentage or mode to selected fan control", "Set all visible fan controls to maximum or performance mode", "Set all visible fan controls to 75 percent or performance mode"),
        new Topic("Automatic", "Return the selected fan control, or all controls named by this command, to the hardware's automatic control.", "Return selected fan control to automatic", "Return all fan controls to automatic"),
        new Topic("Tabs", "Choose a preferences tab, then press Tab to reach its controls. Ctrl+number selects a preferences tab; Alt+number shortcuts apply only inside the relevant tab.", "Preferences", "TabControl"),
        new Topic("CloseDialog", "Close this dialog and return to its previous window. Preferences saves changes as they are made; closing a process-watch setup without starting it does not start a watch.", "Close"),
        new Topic("Accept", "Accept this dialog's choices and return to the previous window. Review the selected action before accepting.", "OK", "Save"),
        new Topic("Cancel", "Cancel this dialog's pending action and return to the previous window. This does not undo changes already saved from Preferences.", "Cancel"),
        new Topic("TextField", "Enter text in this field. Tab moves to the next control. F1 opens help without changing the entered text. The full manual explains the surrounding feature."),
        new Topic("Choice", "Choose an option with the arrow keys, or open the list with Alt+Down. Tab moves to the next control. The full manual explains the surrounding feature."),
        new Topic("Number", "Enter a number or use the arrow keys to change it within the allowed range. Tab moves to the next control. The full manual explains the surrounding feature."),
        new Topic("Toggle", "Press Space or click to turn this option on or off. Tab moves to the next control. The full manual explains the surrounding feature."),
        new Topic("List", "Use the arrow keys to select an item. In a checked list, Space changes whether the item is checked. Tab moves to the next control. The full manual explains the surrounding feature."),
        new Topic("Tree", "Use the arrow keys to select an item. Right and Left expand or collapse groups. Tab moves to the next control. The full manual explains the surrounding feature."),
        new Topic("Command", "Press Space, Enter or click to activate this command. Tab moves to the next control. The full manual explains the surrounding feature."),
        new Topic("Information", "This area displays information. It does not change a sensor or setting. Use the full manual for the surrounding feature."),
        new Topic("Window", "Press F1 while a control has focus for help about that control. The Manual button opens the full guide for the selected language. Escape closes help and returns to the original control.")
    };

    internal static IDictionary<string, string> EnglishEntries()
    {
        return Topics.ToDictionary(t => "help." + t.Key, t => t.Text);
    }

    private static readonly Dictionary<string, Topic> Aliases = BuildAliases();

    private static Dictionary<string, Topic> BuildAliases()
    {
        var aliases = new Dictionary<string, Topic>(StringComparer.OrdinalIgnoreCase);
        foreach (var topic in Topics)
            foreach (var name in topic.Names) aliases[Normalize(name)] = topic;
        return aliases;
    }

    private static Topic FindTopic(string label)
    {
        if (label.Length == 0) return null;
        Topic topic;
        if (Aliases.TryGetValue(label, out topic)) return topic;
        foreach (var key in SensorReadoutForm.ControlHelpLabelKeys(label))
            if (Aliases.TryGetValue(Normalize(key), out topic)) return topic;
        return null;
    }

    internal static void BindKnown(Control control, string originalName, string originalText)
    {
        Binding existing;
        if (Bindings.TryGetValue(control, out existing)) return;
        var name = originalName;
        if (string.IsNullOrWhiteSpace(name) && control is ButtonBase) name = originalText;
        var topic = FindTopic(Normalize(name));
        if (topic != null) Bind(control, topic.Key, name);
    }

    internal static void Bind(Control control, string topic, string name = null)
    {
        Bindings.Remove(control);
        Bindings.Add(control, new Binding { Key = topic, Name = name });
    }

    internal static string Describe(Control control, out string name)
    {
        // NumericUpDown and editable combos focus a private child; describe the public control.
        while (control != null && control.Parent != null && (control.Parent is NumericUpDown || control.Parent is ComboBox)) control = control.Parent;
        Binding binding;
        Topic topic = null;
        name = SensorReadoutForm.L("help.Control", "Control");
        if (control != null && Bindings.TryGetValue(control, out binding))
        {
            topic = Topics.FirstOrDefault(t => t.Key == binding.Key);
            if (!string.IsNullOrWhiteSpace(binding.Name)) name = Normalize(SensorReadoutForm.L("a11y." + binding.Name, SensorReadoutForm.TranslateUiText(binding.Name)));
            if (!string.IsNullOrWhiteSpace(control.AccessibleName)) name = Normalize(control.AccessibleName);
            else if (control is ButtonBase) name = Normalize(control.Text);
        }
        if (topic == null && control != null)
        {
            var label = Normalize(control.AccessibleName);
            if (label.Length == 0 && (control is ButtonBase || control is TabControl)) label = Normalize(control.Text);
            topic = FindTopic(label);
            if (topic != null) name = label;
        }
        if (topic == null)
        {
            if (control != null)
            {
                var label = Normalize(control.AccessibleName);
                if (label.Length == 0 && control is ButtonBase) label = Normalize(control.Text);
                if (SensorReadoutForm.ControlHelpLabelKeys(label).Any()) name = label;
                var description = control.AccessibleDescription ?? "";
                if (description.Length > 12 && SensorReadoutForm.ControlHelpLabelKeys(Normalize(description)).Any()) return description;
            }
            var key = control is TabControl ? "Tabs" : control is NumericUpDown ? "Number" : control is ComboBox ? "Choice" :
                control is CheckBox || control is RadioButton ? "Toggle" : control is ListBox ? "List" : control is TreeView ? "Tree" :
                control is TextBoxBase && !((TextBoxBase)control).ReadOnly ? "TextField" : control is ButtonBase ? "Command" : control == null ? "Window" : "Information";
            topic = Topics.First(t => t.Key == key);
        }
        return SensorReadoutForm.L("help." + topic.Key, topic.Text);
    }

    internal static string Normalize(string text)
    {
        return (text ?? "").Split('\t')[0].Replace("&", "").Replace(",", "").Trim().TrimEnd(':', '.', ' ');
    }
}
