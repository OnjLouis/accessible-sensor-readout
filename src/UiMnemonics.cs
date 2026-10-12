using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

internal static class UiMnemonics
{
    private sealed class Caption
    {
        internal string Text;
        internal Action<string> SetText;
        internal char FixedKey;
    }

    internal static void ForMenu(ToolStripItemCollection items)
    {
        Normalize(items.Cast<ToolStripItem>()
            .Where(item => !(item is ToolStripSeparator) && !IsUserText(item.Tag))
            .Select(item => new Caption { Text = item.Text, SetText = text => item.Text = text }).ToList());
    }

    private static bool IsUserText(object tag)
    {
        return tag is SpokenHotKeySetting || tag is FanProfileSetting || tag is DeviceFilter ||
            tag is PlugInHelpLink || tag is LanguageChoice;
    }

    internal static void ForControls(Control.ControlCollection controls, Control.ControlCollection sharedControls = null)
    {
        var captions = new List<Caption>();
        CollectControls(controls, captions);
        var reserved = new HashSet<char>();
        if (sharedControls != null)
        {
            var shared = new List<Caption>();
            CollectControls(sharedControls, shared);
            foreach (var caption in shared)
            {
                var key = caption.FixedKey != '\0' ? caption.FixedKey : Mnemonic(caption.Text);
                if (key != '\0') reserved.Add(key);
            }
        }
        Normalize(captions, reserved);
    }

    private static void CollectControls(Control.ControlCollection controls, List<Caption> captions)
    {
        foreach (Control control in controls)
        {
            if (control is TabControl) continue;
            if (control is ButtonBase || control is Label)
            {
                var shortcut = control as ShortcutButton;
                var keys = shortcut == null ? Keys.None : shortcut.ShortcutKeys;
                var code = keys & Keys.KeyCode;
                var fixedKey = (keys & Keys.Alt) != 0 && (keys & Keys.Control) == 0
                    ? code >= Keys.A && code <= Keys.Z ? (char)('A' + (code - Keys.A))
                    : code >= Keys.D0 && code <= Keys.D9 ? (char)('0' + (code - Keys.D0)) : '\0'
                    : '\0';
                captions.Add(new Caption { Text = control.Text, SetText = text => control.Text = text, FixedKey = fixedKey });
            }
            CollectControls(control.Controls, captions);
        }
    }

    private static void Normalize(List<Caption> captions, IEnumerable<char> reserved = null)
    {
        // Explicit shortcut buttons keep their keys. Preserve other preferred keys
        // before assigning alternatives, so a collision cannot steal a later key.
        var used = new HashSet<char>(captions.Where(c => c.FixedKey != '\0').Select(c => c.FixedKey));
        if (reserved != null) used.UnionWith(reserved);
        var pending = new List<Caption>();
        foreach (var caption in captions)
        {
            var key = Mnemonic(caption.Text);
            if (caption.FixedKey != '\0')
            {
                if (key != '\0' && key != caption.FixedKey) SetMnemonic(caption, caption.FixedKey);
                continue;
            }
            if (key != '\0' && !used.Add(key)) pending.Add(caption);
        }
        foreach (var caption in pending)
        {
            var label = PlainLabel(caption.Text.Split('\t')[0]);
            var key = label.Select(char.ToUpperInvariant).FirstOrDefault(c => c >= 'A' && c <= 'Z' && !used.Contains(c));
            if (key == '\0') key = label.Select(char.ToUpperInvariant).FirstOrDefault(c => char.IsLetterOrDigit(c) && !used.Contains(c));
            if (key != '\0') used.Add(key);
            SetMnemonic(caption, key);
        }
    }

    private static char Mnemonic(string text)
    {
        text = text ?? "";
        for (var i = 0; i < text.Length - 1 && text[i] != '\t'; i++)
        {
            if (text[i] != '&') continue;
            if (text[i + 1] == '&') { i++; continue; }
            return char.ToUpperInvariant(text[i + 1]);
        }
        return '\0';
    }

    private static string PlainLabel(string text)
    {
        return (text ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
    }

    private static void SetMnemonic(Caption caption, char key)
    {
        var parts = (caption.Text ?? "").Split(new[] { '\t' }, 2);
        var label = PlainLabel(parts[0]);
        var position = key == '\0' ? -1 : label.ToUpperInvariant().IndexOf(key);
        var output = "";
        for (var i = 0; i < label.Length; i++)
        {
            if (i == position) output += "&";
            output += label[i] == '&' ? "&&" : label[i].ToString();
        }
        caption.SetText(output + (parts.Length > 1 ? "\t" + parts[1] : ""));
    }
}
