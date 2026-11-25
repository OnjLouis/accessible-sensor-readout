using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm
{
    internal static DialogResult ConfirmSpokenHotKeyImport(IWin32Window owner, IEnumerable<SpokenHotKeySetting> profiles)
    {
        if (!profiles.Any(p => p != null && !string.IsNullOrWhiteSpace(p.HotKey)))
        {
            return DialogResult.No;
        }

        return MessageBox.Show(owner,
            L("message.Restore spoken hotkey shortcuts on this computer? Choose Yes to assign available shortcuts, No to import without assignments, or Cancel to stop the import.",
                "Restore spoken hotkey shortcuts on this computer? Choose Yes to assign available shortcuts, No to import without assignments, or Cancel to stop the import."),
            L("ui.Import spoken hotkeys", "Import spoken hotkeys"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
    }

    internal SpokenHotKeyImport CreateSpokenHotKeyImport(bool replacingProfiles)
    {
        var releasable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (replacingProfiles)
        {
            foreach (var profile in registeredSpokenHotKeys.Values)
            {
                releasable.Add(NormalizeHotKeyText(profile.HotKey));
            }
        }

        return new SpokenHotKeyImport(settings, replacingProfiles,
            key => releasable.Contains(key) || CanRegisterImportedHotKey(key));
    }

    internal sealed class SpokenHotKeyImport
    {
        private readonly HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, bool> isAvailable;
        private readonly List<string> unavailable = new List<string>();
        private int assigned;

        internal SpokenHotKeyImport(AppSettings settings, bool replacingProfiles, Func<string, bool> isAvailable)
        {
            this.isAvailable = isAvailable;
            reserved.Add(NormalizeHotKeyText(settings.ShowHideHotKey));
            reserved.Add(NormalizeHotKeyText(settings.SpeakTrayHotKey));
            reserved.Add(NormalizeHotKeyText(settings.DiskActivityHotKey));
            foreach (var profile in settings.FanProfiles ?? new List<FanProfileSetting>())
            {
                if (profile != null) reserved.Add(NormalizeHotKeyText(profile.HotKey));
            }

            if (!replacingProfiles)
            {
                foreach (var profile in settings.SpokenHotKeys ?? new List<SpokenHotKeySetting>())
                {
                    if (profile != null) reserved.Add(NormalizeHotKeyText(profile.HotKey));
                }
            }
        }

        internal string Assign(string profileName, string requested, int position, bool hasReadings)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                return "";
            }

            var key = NormalizeHotKeyText(requested);
            if (hasReadings && position < MaxGlobalHotKeyProfiles && key.Length > 0 && !reserved.Contains(key) && isAvailable(key))
            {
                reserved.Add(key);
                assigned++;
                return key;
            }

            unavailable.Add(profileName + " (" + (key.Length > 0 ? key : requested.Trim()) + ")");
            return "";
        }

        internal string Summary()
        {
            var summary = L("message.Restored spoken shortcuts:", "Restored spoken shortcuts:") + " " + assigned + ".";
            if (unavailable.Count > 0)
            {
                summary += Environment.NewLine + L("message.Could not restore these shortcuts:", "Could not restore these shortcuts:") +
                    Environment.NewLine + string.Join(Environment.NewLine, unavailable);
            }
            return summary;
        }
    }
}
