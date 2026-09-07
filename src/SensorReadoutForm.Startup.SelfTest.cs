using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void SelfTestStartupOwnership()
    {
        var savedStartup = settings.RunAtStartup;
        try
        {
            settings.RunAtStartup = true;
            using (var preferences = new PreferencesForm(settings, latestRows, LoadLanguageChoices(), "Startup and Install"))
            {
                var checkbox = (CheckBox)typeof(PreferencesForm).GetField("runAtStartupCheckBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(preferences);
                Require(!checkbox.Checked, "Copied configuration claimed Windows startup for the isolated test copy.");
                Require(!preferences.StartupRegistrationChanged, "Opening Preferences requested a startup change.");
                SetPrivateField(preferences, "loadingPreferences", false);
                InvokePrivate(preferences, "SaveLivePreferences");
                Require(!preferences.StartupRegistrationChanged && !settings.RunAtStartup, "Saving unrelated preferences retained copied startup authority.");

                // Keep this UI test away from real desktop-shortcut creation and key prompts.
                SetPrivateField(preferences, "loadingPreferences", true);
                checkbox.Checked = true;
                Require(!preferences.StartupRegistrationChanged, "Loading the checkbox marked it as a user action.");
                SetPrivateField(preferences, "loadingPreferences", false);
                checkbox.Checked = false;
                Require(preferences.StartupRegistrationChanged, "Explicitly disabling startup was not recorded.");
            }

            var target = @"C:\Apps\Sensor Readout\Sensor Readout.exe";
            var other = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Other copy", "Sensor Readout.exe");
            var xml = BuildStartupTaskXml(target, "--minimized", @"C:\Apps\Sensor Readout");
            Require(StartupTaskTargetsExecutable(xml, target, true), "Owned startup task was not recognised.");
            Require(!StartupTaskTargetsExecutable(xml, other, true), "Another copy's task was claimed.");
            var disabled = xml.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>");
            Require(!StartupTaskTargetsExecutable(disabled, target, true), "Disabled task appeared enabled.");
            Require(StartupTaskTargetsExecutable(disabled, target, false), "Disabled owned task could not be removed.");
            Require(!StartupTaskTargetsExecutable("not XML", target, true), "Invalid task XML was accepted.");
            Require(!StartupTaskTargetsExecutable(xml.Replace("<LogonTrigger>", "<TimeTrigger>").Replace("</LogonTrigger>", "</TimeTrigger>"), target, true), "Non-logon task appeared as Windows startup.");
            Require(!StartupTaskTargetsExecutable(xml.Replace("</Actions>", "<Exec><Command>other.exe</Command></Exec></Actions>"), target, false), "A multi-action task was claimed for deletion.");
            Require(StartupCommandTargetsExecutable("\"" + target + "\" --minimized", target), "Quoted legacy startup command was not recognised.");
            Require(StartupCommandTargetsExecutable(target + " --minimized", target), "Unquoted legacy startup command was not recognised.");
            Require(!StartupCommandTargetsExecutable(target + ".old --minimized", target), "A different executable sharing the prefix was claimed.");
            Require(!StartupCommandTargetsExecutable("\"" + other + "\"", target), "Another legacy startup target was claimed.");
            Require(!StartupCommandTargetsExecutable("", ""), "Empty startup paths matched.");

            Require(PlanStartupRegistrationChange(false, false, false, false) == StartupRegistrationChange.None, "Uninstalling an unregistered copy modified startup.");
            Require(PlanStartupRegistrationChange(false, true, false, false) == StartupRegistrationChange.DeleteTask, "Removing this copy affected another copy's legacy entries.");
            Require(PlanStartupRegistrationChange(false, false, true, true) == (StartupRegistrationChange.DeleteRun | StartupRegistrationChange.DeleteShortcut), "Legacy removal affected another copy's task.");
            var changes = PlanStartupRegistrationChange(true, false, false, false);
            var operations = new List<string>();
            ApplyStartupRegistrationChange(changes, delegate { operations.Add("create"); }, delegate { operations.Add("delete task"); }, delegate { operations.Add("delete run"); }, delegate { operations.Add("delete shortcut"); });
            Require(string.Join(",", operations) == "create,delete run,delete shortcut", "Startup replacement deletes the task before creating its replacement.");
            operations.Clear();
            var creationFailed = false;
            try
            {
                ApplyStartupRegistrationChange(changes, delegate { throw new InvalidOperationException("simulated failure"); }, delegate { operations.Add("delete task"); }, delegate { operations.Add("delete run"); }, delegate { operations.Add("delete shortcut"); });
            }
            catch (InvalidOperationException) { creationFailed = true; }
            Require(creationFailed && operations.Count == 0, "Failed startup creation destroyed previous registrations.");
        }
        finally
        {
            settings.RunAtStartup = savedStartup;
        }
    }
}
