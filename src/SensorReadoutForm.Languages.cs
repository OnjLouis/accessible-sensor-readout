using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void RefreshLanguageChoices(bool force)
    {
        var signature = BuildLanguageFolderSignature();
        if (!force && string.Equals(signature, languageFolderSignature, StringComparison.Ordinal))
        {
            return;
        }

        languageFolderSignature = signature;
        languageChoices = LoadLanguageChoices();
        LogMessage("Debug", "Loaded " + languageChoices.Count + " language file" + (languageChoices.Count == 1 ? "" : "s") + " from " + GetLanguagesFolderPath() + ": " + string.Join(", ", languageChoices.Select(c => c.FileName).ToArray()));
    }

    private void CheckLanguageFolderChanged()
    {
        var oldSignature = languageFolderSignature;
        RefreshLanguageChoices(false);
        if (!string.Equals(oldSignature, languageFolderSignature, StringComparison.Ordinal))
        {
            BuildLanguageMenu();
            LoadSelectedLanguage();
            ApplyLanguage();
        }
    }

    private void BuildLanguageMenu()
    {
        if (languageMenuItem == null)
        {
            return;
        }

        languageMenuItem.DropDownItems.Clear();
        foreach (var choice in UserSelectableLanguageChoices(languageChoices))
        {
            var item = new ToolStripMenuItem(choice.DisplayName)
            {
                Tag = choice,
                Checked = string.Equals(settings.LanguageFile ?? "", choice.FileName ?? "", StringComparison.OrdinalIgnoreCase)
            };
            item.Click += delegate(object sender, EventArgs e)
            {
                var menuItem = sender as ToolStripMenuItem;
                var selected = menuItem == null ? null : menuItem.Tag as LanguageChoice;
                if (selected != null)
                {
                    SetLanguage(selected.FileName);
                }
            };
            languageMenuItem.DropDownItems.Add(item);
        }

        languageMenuItem.DropDownItems.Add(new ToolStripSeparator());
        languageMenuItem.DropDownItems.Add(T("ui.Open languages folder", "Open languages folder"), null, delegate { OpenLanguagesFolder(); });
    }

    private static IEnumerable<LanguageChoice> UserSelectableLanguageChoices(IEnumerable<LanguageChoice> choices)
    {
        var seenEnglish = false;
        foreach (var choice in choices ?? new List<LanguageChoice>())
        {
            var isEnglish = string.Equals(choice.DisplayName ?? "", "English", StringComparison.OrdinalIgnoreCase);
            if (isEnglish)
            {
                if (seenEnglish)
                {
                    continue;
                }

                seenEnglish = true;
            }

            yield return choice;
        }
    }

    private void SetLanguage(string fileName)
    {
        settings.LanguageFile = SanitizeLanguageFileName(fileName);
        settings.LanguagePreferenceInitialized = true;
        SaveSettings(settings);
        LoadSelectedLanguage();
        BuildLanguageMenu();
        ApplyLanguage();
        statusLabel.Text = string.Format(T("status.Language set to.", "Language set to {0}."), activeLanguage.DisplayName);
    }

    private void LoadSelectedLanguage()
    {
        ActivateLanguage(settings.LanguageFile);
    }

    public static void ActivateLanguage(string fileName)
    {
        activeLanguage = LoadLanguage(fileName);
    }

    public void ReloadLanguageFromSettings()
    {
        LoadSelectedLanguage();
        BuildLanguageMenu();
        ApplyLanguage();
    }

    public void RefreshLanguagesNow()
    {
        RefreshLanguageChoices(true);
        BuildLanguageMenu();
    }

    private void ApplyLanguage()
    {
        UpdateWindowTitle();
        ApplyLanguageToControls(Controls);
        ApplyLanguageToToolStripItems(menuStrip.Items);
        if (trayIcon != null && trayIcon.ContextMenuStrip != null)
        {
            ApplyLanguageToToolStripItems(trayIcon.ContextMenuStrip.Items);
        }
        if (deviceList != null && deviceList.ContextMenuStrip != null)
        {
            ApplyLanguageToToolStripItems(deviceList.ContextMenuStrip.Items);
        }
        ApplyAccessibleLanguage();
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        UpdateDeviceList();
        UpdateReadingList();
        UpdateTrayStatus();
    }

    private void ApplyAccessibleLanguage()
    {
        if (pauseCheckBox != null)
        {
            pauseCheckBox.AccessibleName = T("a11y.Pause automatic updates", "Pause automatic updates");
        }
        if (deviceList != null)
        {
            deviceList.AccessibleName = T("a11y.Reading section", "Reading section");
            deviceList.AccessibleDescription = T("a11y.Choose a category on the left, then review its readings and details on the right.", "Choose a category on the left, then review its readings and details on the right.");
        }
        if (readingTree != null)
        {
            readingTree.AccessibleName = T("a11y.Readings", "Readings");
            readingTree.AccessibleDescription = T("a11y.Current readings grouped by category or device", "Current readings grouped by category or device");
        }
        if (selectedMeterProgressBar != null)
        {
            selectedMeterProgressBar.AccessibleName = T("a11y.Selected meter", "Selected meter");
            selectedMeterProgressBar.AccessibleDescription = T("a11y.Selected meter value", "Selected meter value");
        }
    }

    private void ApplyLanguageToControls(Control.ControlCollection controls)
    {
        foreach (Control control in controls)
        {
            ApplyLanguageToControl(control);
            ApplyLanguageToControls(control.Controls);
        }
    }

    private void ApplyLanguageToControl(Control control)
    {
        if (control == null || string.IsNullOrWhiteSpace(control.Text))
        {
            return;
        }

        string original;
        if (!originalUiText.TryGetValue(control, out original))
        {
            original = control.Text;
            originalUiText[control] = original;
        }

        control.Text = TranslateUiText(original);
    }

    private void ApplyLanguageToToolStripItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            ApplyLanguageToToolStripItem(item);
            var dropDownItem = item as ToolStripDropDownItem;
            if (dropDownItem != null)
            {
                ApplyLanguageToToolStripItems(dropDownItem.DropDownItems);
            }
        }
    }

    private void ApplyLanguageToToolStripItem(ToolStripItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Text))
        {
            return;
        }

        string original;
        if (!originalUiText.TryGetValue(item, out original))
        {
            original = item.Text;
            originalUiText[item] = original;
        }

        item.Text = TranslateUiText(original);
    }

    public static string TranslateUiText(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return original;
        }

        var tab = original.IndexOf('\t');
        var label = tab >= 0 ? original.Substring(0, tab) : original;
        var shortcut = tab >= 0 ? original.Substring(tab) : "";
        var translated = T("ui." + label, label);
        if (string.Equals(translated, label, StringComparison.Ordinal) && label.IndexOf('&') >= 0)
        {
            translated = T("ui." + StripMenuMnemonic(label), label);
        }

        return PreserveMenuMnemonic(label, translated) + shortcut;
    }

    private static string StripMenuMnemonic(string text)
    {
        return (text ?? "").Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
    }

    private static string PreserveMenuMnemonic(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translated) || translated.IndexOf('&') >= 0)
        {
            return translated;
        }

        var index = original.IndexOf('&');
        if (index < 0 || index >= original.Length - 1)
        {
            return translated;
        }

        var mnemonic = char.ToUpperInvariant(original[index + 1]);
        for (var i = 0; i < translated.Length; i++)
        {
            if (char.ToUpperInvariant(translated[i]) == mnemonic)
            {
                return translated.Substring(0, i) + "&" + translated.Substring(i);
            }
        }

        return "&" + translated;
    }

    public static string L(string key, string fallback)
    {
        return T(key, fallback);
    }

    public static string NormalizeTemperatureUnit(string unit)
    {
        if (string.Equals(unit, "F", StringComparison.OrdinalIgnoreCase)) return "F";
        if (string.Equals(unit, "CF", StringComparison.OrdinalIgnoreCase)) return "CF";
        if (string.Equals(unit, "FC", StringComparison.OrdinalIgnoreCase)) return "FC";
        return "C";
    }

    public static int TemperatureUnitIndex(string unit)
    {
        var normalized = NormalizeTemperatureUnit(unit);
        if (normalized == "F") return 1;
        if (normalized == "CF") return 2;
        if (normalized == "FC") return 3;
        return 0;
    }

    public static string TemperatureUnitFromIndex(int index)
    {
        if (index == 1) return "F";
        if (index == 2) return "CF";
        if (index == 3) return "FC";
        return "C";
    }

    private static string TemperatureUnitDisplayName(string unit)
    {
        var normalized = NormalizeTemperatureUnit(unit);
        if (normalized == "F") return T("ui.Fahrenheit (F)", "Fahrenheit (F)");
        if (normalized == "CF") return T("ui.Celsius, then Fahrenheit", "Celsius, then Fahrenheit");
        if (normalized == "FC") return T("ui.Fahrenheit, then Celsius", "Fahrenheit, then Celsius");
        return T("ui.Celsius (C)", "Celsius (C)");
    }

    private void OpenLanguagesFolder()
    {
        var folder = GetLanguagesFolderPath();
        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            Process.Start(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("message.Could not open languages folder:", "Could not open languages folder:") + " " + ex.Message, "Sensor Readout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static List<LanguageChoice> LoadLanguageChoices()
    {
        var choices = new List<LanguageChoice>();

        try
        {
            var folder = GetLanguagesFolderPath();
            if (!System.IO.Directory.Exists(folder))
            {
                return choices;
            }

            foreach (var path in System.IO.Directory.GetFiles(folder, "*.*")
                .Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => string.Equals(System.IO.Path.GetFileName(p), DefaultLanguageFileName, StringComparison.OrdinalIgnoreCase) ? "" : p))
            {
                var fileName = System.IO.Path.GetFileName(path);
                var catalog = LoadLanguage(fileName);
                if (!choices.Any(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    choices.Add(new LanguageChoice { FileName = fileName, DisplayName = catalog.DisplayName, FullPath = path });
                }
            }
        }
        catch
        {
        }

        return choices;
    }

    public static LanguageCatalog LoadLanguage(string fileName)
    {
        fileName = SanitizeLanguageFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = DefaultLanguageFileName;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = System.IO.Path.Combine(GetLanguagesFolderPath(), fileName);
            if (!System.IO.File.Exists(path))
            {
                return LoadFallbackEnglishLanguage();
            }

            values = ReadLanguageFile(path);
            if (values.Count == 0 && !string.Equals(fileName, DefaultLanguageFileName, StringComparison.OrdinalIgnoreCase))
            {
                return LoadFallbackEnglishLanguage();
            }
        }
        catch
        {
            return LoadFallbackEnglishLanguage();
        }

        string displayName;
        if (!values.TryGetValue("language.name", out displayName) || string.IsNullOrWhiteSpace(displayName))
        {
            displayName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        }

        return new LanguageCatalog(fileName, displayName, values);
    }

    private static LanguageCatalog LoadFallbackEnglishLanguage()
    {
        try
        {
            var path = System.IO.Path.Combine(GetLanguagesFolderPath(), DefaultLanguageFileName);
            var values = ReadLanguageFile(path);
            if (values.Count > 0)
            {
                string displayName;
                if (!values.TryGetValue("language.name", out displayName) || string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = "English";
                }

                return new LanguageCatalog(DefaultLanguageFileName, displayName, values);
            }
        }
        catch
        {
        }

        return LanguageCatalog.English();
    }

    private static string BuildLanguageFolderSignature()
    {
        try
        {
            var folder = GetLanguagesFolderPath();
            if (!System.IO.Directory.Exists(folder))
            {
                return "";
            }

            return string.Join("|", System.IO.Directory.GetFiles(folder, "*.*")
                .Where(p => p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p)
                .Select(p => System.IO.Path.GetFileName(p) + ":" + System.IO.File.GetLastWriteTimeUtc(p).Ticks + ":" + new System.IO.FileInfo(p).Length)
                .ToArray());
        }
        catch
        {
            return "";
        }
    }

    public static Dictionary<string, string> ReadLanguageFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                return values;
            }

            foreach (var rawLine in System.IO.File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, equals).Trim();
                var value = line.Substring(equals + 1).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value.Replace("\\n", Environment.NewLine);
                }
            }
        }
        catch
        {
        }

        return values;
    }

    public static void UpdateLanguageFileValue(string path, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var lines = System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
        var replacement = key.Trim() + "=" + (value ?? "").Replace(Environment.NewLine, "\\n");
        var updated = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#") || line.StartsWith(";") || line.IndexOf('=') <= 0)
            {
                continue;
            }

            var existingKey = line.Substring(0, line.IndexOf('=')).Trim();
            if (existingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = replacement;
                updated = true;
                break;
            }
        }

        if (!updated)
        {
            lines.Add(replacement);
        }

        System.IO.File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
    }

    public static void OpenLanguagesFolderStatic(IWin32Window owner)
    {
        var folder = GetLanguagesFolderPath();
        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
            }

            Process.Start(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, T("message.Could not open languages folder:", "Could not open languages folder:") + " " + ex.Message, "Sensor Readout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static string GetLanguagesFolderPath()
    {
        return GetKnownFolderPath("Langs", "langs");
    }

    private static string GetDocsFolderPath()
    {
        return GetKnownFolderPath("Docs", "docs");
    }

    private static string GetKnownFolderPath(string canonicalName, string legacyName)
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            var actual = System.IO.Directory.Exists(baseDirectory)
                ? System.IO.Directory.GetDirectories(baseDirectory)
                    .FirstOrDefault(p => string.Equals(System.IO.Path.GetFileName(p), canonicalName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (!string.IsNullOrWhiteSpace(actual) &&
                !string.Equals(System.IO.Path.GetFileName(actual), canonicalName, StringComparison.Ordinal))
            {
                TryNormalizeFolderCase(actual, canonicalName);
            }
        }
        catch
        {
        }

        var canonical = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, canonicalName);
        if (System.IO.Directory.Exists(canonical))
        {
            return canonical;
        }

        var legacy = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, legacyName);
        if (System.IO.Directory.Exists(legacy))
        {
            TryNormalizeFolderCase(legacy, canonicalName);
            if (System.IO.Directory.Exists(canonical))
            {
                return canonical;
            }

            return legacy;
        }

        return canonical;
    }

    private static void TryNormalizeFolderCase(string existingPath, string canonicalName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(existingPath) || !System.IO.Directory.Exists(existingPath))
            {
                return;
            }

            var parent = System.IO.Directory.GetParent(existingPath);
            if (parent == null)
            {
                return;
            }

            var canonicalPath = System.IO.Path.Combine(parent.FullName, canonicalName);
            if (string.Equals(System.IO.Path.GetFileName(existingPath), canonicalName, StringComparison.Ordinal))
            {
                return;
            }

            var tempPath = System.IO.Path.Combine(parent.FullName, canonicalName + "_case_tmp");
            if (System.IO.Directory.Exists(tempPath))
            {
                if (!System.IO.Directory.Exists(canonicalPath))
                {
                    System.IO.Directory.Move(tempPath, canonicalPath);
                    return;
                }

                TryDeleteEmptyDirectory(tempPath);
                return;
            }

            System.IO.Directory.Move(existingPath, tempPath);
            System.IO.Directory.Move(tempPath, canonicalPath);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                System.IO.Directory.Exists(path) &&
                !System.IO.Directory.EnumerateFileSystemEntries(path).Any())
            {
                System.IO.Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeLanguageFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        fileName = System.IO.Path.GetFileName(fileName.Trim());
        return fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? fileName : "";
    }
}
