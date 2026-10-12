using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm
{
    private void SelfTestLocalizedMenusAndDialogs()
    {
        SelfTestMnemonicAllocation();
        var previousLanguage = activeLanguage;
        var previousProfiles = settings.SpokenHotKeys;
        var mnemonicErrors = new List<string>();
        var helpKeys = new[] {
            "Help for &focused control", "Manual", "Check for updates...", "Version history...", "Project on GitHub",
            "Contact", "Donate", "Install Core Temp support...", "Install prerequisites...", "Run initial setup wizard...",
            "Hints and tips...", "Check accessibility setup...", "Run diagnostics...", "Prepare support report",
            "Share a&nonymous community stats...", "About Sensor Readout"
        };
        try
        {
            settings.SpokenHotKeys = new List<SpokenHotKeySetting> { new SpokenHotKeySetting { Name = "Manual", HotKey = "Ctrl+Shift+F8" } };
            foreach (var choice in LoadLanguageChoices().Concat(new[] { new LanguageChoice { FileName = "English.txt" } }))
            {
                ActivateLanguage(choice.FileName);
                ApplyLanguage();
                var oldHelpItem = helpMenu.DropDownItems[0];
                var oldHelpDisposed = false;
                oldHelpItem.Disposed += delegate { oldHelpDisposed = true; };
                BuildHelpMenu();
                BuildHotkeysMenu();
                var viewMenu = (ToolStripMenuItem)menuStrip.Items[2];
                RebuildViewMenu(viewMenu);
                Require(oldHelpDisposed && oldHelpItem.Owner == null, "Rebuilt Help items must be disposed: " + choice.FileName);
                var helpItems = helpMenu.DropDownItems.OfType<ToolStripMenuItem>().Where(item => !(item.Tag is PlugInHelpLink)).ToList();
                Require(helpItems.Count == helpKeys.Length, "Unexpected Help command count.");
                for (var i = 0; i < helpKeys.Length; i++)
                {
                    var expected = L("ui." + helpKeys[i], "missing-menu-translation");
                    Require(expected != "missing-menu-translation", "Missing Help key: " + choice.FileName + ": " + helpKeys[i]);
                    if (helpKeys[i] == "Prepare support report" && !expected.EndsWith("...", StringComparison.Ordinal)) expected += "...";
                    Require(MenuLabelForLanguageTest(helpItems[i].Text) == StripMenuMnemonic(expected), "Reopened Help label was not translated: " + choice.FileName + ": " + helpKeys[i]);
                }
                Require(MenuLabelForLanguageTest(viewMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.ShortcutKeys == Keys.F5).Text) == StripMenuMnemonic(L("ui.Refresh now", "Refresh now")), "Reopened View refresh label was not translated.");
                Require(MenuLabelForLanguageTest(viewMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.Right)).Text) == StripMenuMnemonic(L("ui.&Expand all", "Expand all")), "Reopened View expand label was not translated.");
                Require(MenuLabelForLanguageTest(hotkeysSpokenHotKeyMenuItem.Text) == StripMenuMnemonic(L("ui.Add/remove current reading from hotkey or tray...", "missing")), "Reopened Hotkeys command was not translated.");
                Require(MenuLabelForLanguageTest(treeSpokenHotKeyMenuItem.Text) == StripMenuMnemonic(L("ui.Add/remove from hotkey or tray...", "missing")), "Reading context-menu hotkey command was not translated.");
                var fileMenu = (ToolStripMenuItem)menuStrip.Items[0];
                Require(MenuLabelForLanguageTest(fileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.M)).Text) == StripMenuMnemonic(L("ui.Compare reports", "Compare reports")) + "...", "Compare reports ellipsis lookup failed.");
                Require(MenuLabelForLanguageTest(fileMenu.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.A)).Text) == StripMenuMnemonic(L("ui.Save anonymized report", "Save anonymized report")) + "...", "Save anonymized report ellipsis lookup failed.");
                var spokenMenu = hotkeysMenu.DropDownItems.OfType<ToolStripMenuItem>().First(item => item.DropDownItems.Count > 0);
                Require(spokenMenu.DropDownItems[0].Text.StartsWith("Manual:", StringComparison.Ordinal), "User profile names must not be translated.");

                using (var about = CreateAboutDialog())
                {
                    var text = FindControls<TextBox>(about.Controls).Single();
                    Require(about.Text == StripMenuMnemonic(L("ui.About Sensor Readout", "About Sensor Readout")), "About title was not translated.");
                    Require(text.AccessibleName == L("a11y.About Sensor Readout text", "About Sensor Readout text"), "About accessible name was not translated.");
                    foreach (var key in new[] { "Created by Codex.", "Ideas by Andre Louis.", "Contributor thanks", "Bundled and referenced components:", "Bundled components list" })
                        Require(text.Text.Contains(L("ui." + key, "missing-about-translation")), "About text was not translated: " + key);
                    Require(text.Text.Contains(ProjectUrl) && text.Text.Contains(AppVersion), "About lost the project URL or version.");
                    try { RequireUniqueControlMnemonics("Localized About " + choice.FileName, about.Controls); }
                    catch (InvalidOperationException error) { mnemonicErrors.Add(error.Message); }
                }
                using (var preferences = new PreferencesForm(JsonConvert.DeserializeObject<AppSettings>(JsonConvert.SerializeObject(settings)), latestRows, LoadLanguageChoices(), "General"))
                {
                    var lists = FindControls<ListBox>(preferences.Controls).ToList();
                    Require(lists.Any(control => control.AccessibleName == L("a11y.Hotkey profiles", "missing")), "Hotkey profile list accessible name was not translated.");
                    Require(lists.Any(control => control.AccessibleName == L("a11y.Hidden readings and categories", "missing")), "Hidden-items list accessible name was not translated.");
                    Require(FindControls<Label>(preferences.Controls).Any(control => control.Text == L("ui.Windows programs folder for this user", "missing")), "Installation location was not translated.");
                    foreach (var page in FindControls<TabControl>(preferences.Controls).SelectMany(tabs => tabs.TabPages.Cast<TabPage>()))
                    {
                        try { RequireUniqueControlMnemonics("Localized Preferences " + choice.FileName + ": " + page.Text, page.Controls); }
                        catch (InvalidOperationException error) { mnemonicErrors.Add(error.Message); }
                    }
                }
                foreach (var menu in menuStrip.Items.OfType<ToolStripMenuItem>())
                {
                    // Run the real opening handlers without showing a desktop menu.
                    typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(menu, new object[] { EventArgs.Empty });
                    try { RequireUniqueMenuMnemonics("Localized menu " + choice.FileName + ": " + menu.Text, menu.DropDownItems); }
                    catch (InvalidOperationException error) { mnemonicErrors.Add(error.Message); }
                }
                foreach (var context in new[] { readingTree.ContextMenuStrip, deviceList.ContextMenuStrip })
                {
                    typeof(ToolStripDropDownMenu).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(context, new object[] { new System.ComponentModel.CancelEventArgs() });
                    try { RequireUniqueMenuMnemonics("Localized context menu " + choice.FileName, context.Items); }
                    catch (InvalidOperationException error) { mnemonicErrors.Add(error.Message); }
                }
            }
            for (var i = 0; i < 20; i++)
            {
                var oldItem = helpMenu.DropDownItems[0];
                var disposed = false;
                oldItem.Disposed += delegate { disposed = true; };
                BuildHelpMenu();
                Require(disposed && oldItem.Owner == null, "Repeated Help rebuild retained an undisposed item.");
            }
            Require(mnemonicErrors.Count == 0, string.Join(" | ", mnemonicErrors.ToArray()));
        }
        finally
        {
            activeLanguage = previousLanguage;
            settings.SpokenHotKeys = previousProfiles;
            ApplyLanguage();
            BuildHelpMenu();
            BuildHotkeysMenu();
        }
    }

    private static string MenuLabelForLanguageTest(string text)
    {
        return StripMenuMnemonic((text ?? "").Split('\t')[0]);
    }

    private static void SelfTestMnemonicAllocation()
    {
        using (var shared = new Panel())
        using (var page = new Panel())
        {
            var close = new Button { Text = "&Close" };
            shared.Controls.Add(close);
            var import = new ShortcutButton { Text = "&Import", ShortcutKeys = Keys.Alt | Keys.I, ShortcutText = "Alt+I" };
            var include = new CheckBox { Text = "&Include names" };
            var color = new CheckBox { Text = "&Color" };
            var literal = new Button { Text = "&Info && labels" };
            page.Controls.Add(import);
            page.Controls.Add(include);
            page.Controls.Add(color);
            page.Controls.Add(literal);
            UiMnemonics.ForControls(page.Controls, shared.Controls);
            Require(import.ShortcutKeys == (Keys.Alt | Keys.I) && import.AccessibilityObject.KeyboardShortcut == "Alt+I", "Explicit keyboard shortcuts must not change.");
            Require(StripMenuMnemonic(literal.Text) == "Info & labels", "Literal ampersands must remain literal.");
            foreach (var control in new ButtonBase[] { import, include, color, literal })
            {
                char key;
                Require(TryGetControlMnemonicKey(control, out key), "A translated bundled command lost its accelerator.");
                Require(key != 'C', "A tab command must not steal the shared Close accelerator.");
            }
            RequireUniqueControlMnemonics("Mnemonic allocation", page.Controls);
            var texts = page.Controls.Cast<Control>().Select(control => control.Text).ToArray();
            UiMnemonics.ForControls(page.Controls, shared.Controls);
            Require(texts.SequenceEqual(page.Controls.Cast<Control>().Select(control => control.Text)), "Mnemonic allocation must be stable on repeated application.");
        }
    }
}
