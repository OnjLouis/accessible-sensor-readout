using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private bool ShowSelectedReadingDetails()
    {
        var row = GetSelectedReadingRow();
        if (row == null || row.Details == null || row.Details.Count == 0)
        {
            return false;
        }

        using (var dialog = new Form())
        {
            dialog.Text = ShortHardwareName(row.Hardware) + " details";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(760, 520);
            dialog.MinimumSize = new Size(520, 360);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowNodeToolTips = true,
                AccessibleName = T("a11y.Details", "Details"),
                AccessibleDescription = T("a11y.Details grouped by topic. Expand a group to review fields. Press F3 to find, F4 to review text, Control C to copy, Control Shift C to copy only values, Control M to copy matching lines, or Escape to close.", "Details grouped by topic. Expand a group to review fields. Press F3 to find, F4 to review text, Control C to copy, Control Shift C to copy only values, Control M to copy matching lines, or Escape to close.")
            };
            PopulateDetailsTree(tree, row.Details);
            var windowsSettingsTarget = GetRelatedWindowsSettingsTarget(row);
            var opensFileLocation = windowsSettingsTarget != null && !string.IsNullOrWhiteSpace(windowsSettingsTarget.FilePath);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var closeButton = CreateCloseButton();
            var openWindowsSettingButton = windowsSettingsTarget == null
                ? null
                : new ShortcutButton
                {
                    Text = opensFileLocation ? T("ui.Open file &location...", "Open file &location...") : T("ui.Open &Windows setting...", "Open &Windows setting..."),
                    AutoSize = true,
                    ShortcutText = opensFileLocation ? "Alt+L" : "Alt+W",
                    ShortcutKeys = opensFileLocation ? Keys.L : Keys.W,
                    AccessibleName = opensFileLocation ? T("a11y.Open related file location", "Open related file location") : T("a11y.Open related Windows setting", "Open related Windows setting"),
                    AccessibleDescription = opensFileLocation ? T("a11y.Opens the folder containing the executable for this task.", "Opens the folder containing the executable for this task.") : T("a11y.Opens the Windows Settings page related to this reading.", "Opens the Windows Settings page related to this reading.")
                };
            var copyButton = new Button { Text = T("ui.&Copy", "&Copy"), AutoSize = true };
            var copyValueButton = new Button { Text = T("ui.Copy &value only", "Copy &value only"), AutoSize = true };
            var copyMatchingButton = new Button { Text = T("ui.Copy &matching...", "Copy &matching..."), AutoSize = true };
            var collapseAllButton = new Button { Text = T("ui.C&ollapse all", "C&ollapse all"), AutoSize = true };
            var expandAllButton = new Button { Text = T("ui.&Expand all", "&Expand all"), AutoSize = true };
            closeButton.Click += delegate { dialog.Close(); };
            if (openWindowsSettingButton != null)
            {
                openWindowsSettingButton.Click += delegate { OpenWindowsSettingsTarget(windowsSettingsTarget); };
            }
            copyButton.Click += delegate { CopyDetailsTree(tree); };
            copyValueButton.Click += delegate { CopyDetailsTreeValueOnly(tree); };
            copyMatchingButton.Click += delegate { CopyMatchingDetailsTreeLines(tree); };
            collapseAllButton.Click += delegate { CollapseDetailsTree(tree); };
            expandAllButton.Click += delegate { ExpandDetailsTree(tree); };
            buttons.Controls.Add(closeButton);
            if (openWindowsSettingButton != null)
            {
                buttons.Controls.Add(openWindowsSettingButton);
            }
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(copyValueButton);
            buttons.Controls.Add(copyMatchingButton);
            buttons.Controls.Add(collapseAllButton);
            buttons.Controls.Add(expandAllButton);

            tree.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (HandleDetailsTreeKey(tree, e))
                {
                    return;
                }
            };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (tree.Focused && HandleDetailsTreeKey(tree, e))
                {
                    return;
                }

                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            dialog.Controls.Add(tree);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate
            {
                if (tree.Nodes.Count > 0)
                {
                    tree.SelectedNode = tree.Nodes[0];
                }
                tree.Focus();
            };
            dialog.ShowDialog(this);
        }

        return true;
    }

    private sealed class WindowsSettingsTarget
    {
        public string Uri;
        public string FilePath;
        public string Name;
    }

    private bool CanOpenSelectedWindowsSetting()
    {
        return GetRelatedWindowsSettingsTarget(GetSelectedReadingRow()) != null;
    }

    private bool OpenSelectedWindowsSetting()
    {
        var target = GetRelatedWindowsSettingsTarget(GetSelectedReadingRow());
        if (target == null)
        {
            System.Media.SystemSounds.Beep.Play();
            statusLabel.Text = T("status.Select a reading with a related location.", "Select a reading with a related location.");
            return false;
        }

        OpenWindowsSettingsTarget(target);
        return true;
    }

    private static WindowsSettingsTarget GetRelatedWindowsSettingsTarget(SensorRow row)
    {
        if (row == null)
        {
            return null;
        }

        if (IsSafeWindowsSettingsUri(row.WindowsSettingsUri))
        {
            return new WindowsSettingsTarget { Uri = row.WindowsSettingsUri, Name = "Windows setting" };
        }

        var type = row.Type ?? "";
        var hardware = row.Hardware ?? "";
        var name = CleanSensorName(row.Name);
        var combined = (type + " " + hardware + " " + name).Trim();
        var isDeviceInventory = string.Equals(type, "Devices", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(type, "Tasks", StringComparison.OrdinalIgnoreCase))
        {
            string executablePath;
            if (row.Details != null && row.Details.TryGetValue("Executable path", out executablePath) && File.Exists(executablePath))
            {
                return new WindowsSettingsTarget { FilePath = executablePath, Name = "File location" };
            }
        }

        if (name.Equals("Audio descriptions", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Show sounds", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsTarget("ms-settings:easeofaccess-audio", "Accessibility audio");
        }

        if (name.Equals("Closed captions", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsTarget("ms-settings:easeofaccess-closedcaptioning", "Closed captions");
        }

        if (name.Equals("High contrast", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsTarget("ms-settings:easeofaccess-highcontrast", "High contrast");
        }

        if (name.Equals("Sticky Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Toggle Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Filter Keys", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsTarget("ms-settings:easeofaccess-keyboard", "Accessibility keyboard");
        }

        if (name.Equals("Screen reader output", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Detected screen readers", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsTarget("ms-settings:easeofaccess-narrator", "Narrator");
        }

        if (string.Equals(type, "Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, "bluetooth")))
        {
            return SettingsTarget("ms-settings:bluetooth", "Bluetooth");
        }

        if (ContainsAny(combined, "printer", "print queue") &&
            (isDeviceInventory || string.Equals(type, "Performance/Overview", StringComparison.OrdinalIgnoreCase)))
        {
            return SettingsTarget("ms-settings:printers", "Printers and scanners");
        }

        if (string.Equals(type, "USB", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, " usb ")))
        {
            return SettingsTarget("ms-settings:usb", "USB");
        }

        if (string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, "audio", "speaker", "microphone", "endpoint")))
        {
            return SettingsTarget("ms-settings:sound", "Sound");
        }

        if (string.Equals(type, "Display", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, "display", "monitor", "graphics", "gpu")))
        {
            return SettingsTarget("ms-settings:display", "Display");
        }

        if (string.Equals(type, "Network", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, "wi-fi", "wifi", "ethernet", "network", "public ip")))
        {
            return SettingsTarget("ms-settings:network", "Network and internet");
        }

        if (string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, "battery", "power supply", "charger")))
        {
            return SettingsTarget("ms-settings:powersleep", "Power and battery");
        }

        if (string.Equals(type, "SMART", StringComparison.OrdinalIgnoreCase) ||
            (isDeviceInventory && ContainsAny(combined, "disk", "drive", "bitlocker", "storage")))
        {
            return SettingsTarget("ms-settings:storagesense", "Storage");
        }

        if (isDeviceInventory && ContainsAny(combined, "camera", "webcam", "imaging"))
        {
            return SettingsTarget("ms-settings:camera", "Camera");
        }

        if (ContainsAny(combined, "startup"))
        {
            return SettingsTarget("ms-settings:startupapps", "Startup apps");
        }

        if (ContainsAny(combined, "windows update", "update"))
        {
            return SettingsTarget("ms-settings:windowsupdate", "Windows Update");
        }

        return null;
    }

    private static WindowsSettingsTarget SettingsTarget(string uri, string name)
    {
        return new WindowsSettingsTarget { Uri = uri, Name = name };
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        if (string.IsNullOrWhiteSpace(text) || terms == null)
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (!string.IsNullOrWhiteSpace(term) && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeWindowsSettingsUri(string uri)
    {
        return !string.IsNullOrWhiteSpace(uri) &&
            uri.Trim().StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenWindowsSettingsTarget(WindowsSettingsTarget target)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(target.FilePath) && File.Exists(target.FilePath))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + target.FilePath + "\"", UseShellExecute = true });
                statusLabel.Text = T("status.Opened file location.", "Opened file location.");
                return;
            }

            if (!IsSafeWindowsSettingsUri(target.Uri))
            {
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = target.Uri, UseShellExecute = true });
            statusLabel.Text = T("status.Opened related Windows setting.", "Opened related Windows setting.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not open related location", "Could not open related location"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExpandDetailsTree(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var selected = tree.SelectedNode;
        tree.BeginUpdate();
        try
        {
            tree.ExpandAll();
            if (selected != null)
            {
                tree.SelectedNode = selected;
                selected.EnsureVisible();
            }
        }
        finally
        {
            tree.EndUpdate();
        }
    }

    private void CollapseDetailsTree(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var selected = tree.SelectedNode;
        tree.BeginUpdate();
        try
        {
            tree.CollapseAll();
            if (selected != null)
            {
                tree.SelectedNode = selected;
                selected.EnsureVisible();
            }
        }
        finally
        {
            tree.EndUpdate();
        }
    }

    private bool HandleDetailsTreeKey(TreeView tree, KeyEventArgs e)
    {
        if (tree == null || e == null)
        {
            return false;
        }

        if (e.Control && e.KeyCode == Keys.C)
        {
            if (e.Shift)
            {
                CopyDetailsTreeValueOnly(tree);
            }
            else
            {
                CopyDetailsTree(tree);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (e.Control && !e.Shift && e.KeyCode == Keys.M)
        {
            CopyMatchingDetailsTreeLines(tree);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (e.Control && e.Shift && e.KeyCode == Keys.Right)
        {
            ExpandDetailsTree(tree);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (e.Control && e.Shift && e.KeyCode == Keys.Left)
        {
            CollapseDetailsTree(tree);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (e.KeyCode == Keys.F3)
        {
            ShowDetailsTreeSearchDialog(tree);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (e.KeyCode == Keys.F4)
        {
            ShowDetailsTreeTextReview(tree);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        if (e.KeyCode == Keys.Enter)
        {
            var node = tree.SelectedNode;
            if (node != null && node.Nodes.Count > 0)
            {
                if (node.IsExpanded)
                {
                    node.Collapse();
                }
                else
                {
                    node.Expand();
                }
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            return true;
        }

        return false;
    }

    private void ShowDetailsTreeSearchDialog(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var nodes = new List<TreeNode>();
        foreach (TreeNode node in tree.Nodes)
        {
            AddDetailTreeSearchNodes(node, nodes);
        }

        var selected = ShowSearchDialog(
            this,
            L("ui.Find detail", "Find detail"),
            L("ui.Search details:", "Search details:"),
            nodes.Cast<object>(),
            delegate(object item) { return ((TreeNode)item).Text; },
            delegate(object item) { return GetDetailTreeNodePath((TreeNode)item); }) as TreeNode;

        if (selected == null)
        {
            tree.Focus();
            return;
        }

        ExpandDetailTreeParents(selected);
        tree.SelectedNode = selected;
        selected.EnsureVisible();
        tree.Focus();
    }

    private void ShowDetailsTreeTextReview(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var selected = tree.SelectedNode;
        if (selected == null)
        {
            return;
        }

        var lines = new List<string>();
        AppendDetailTreeLines(selected, lines, 0);
        if (lines.Count == 0)
        {
            return;
        }

        using (var dialog = new Form())
        {
            dialog.Text = L("ui.Review text", "Review text");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(720, 360);
            dialog.MinimumSize = new Size(420, 220);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var text = string.Join(Environment.NewLine, lines.ToArray());
            var textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Text = text,
                AccessibleName = L("a11y.Review text", "Review text"),
                AccessibleDescription = L("a11y.Read-only text for the selected detail or detail group.", "Read-only text for the selected detail or detail group.")
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var closeButton = CreateCloseButton();
            var copyButton = new Button { Text = L("ui.&Copy", "&Copy"), AutoSize = true };
            closeButton.Click += delegate { dialog.Close(); };
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
                    e.SuppressKeyPress = true;
                }
            };
            dialog.Shown += delegate
            {
                textBox.Focus();
                textBox.Select(0, 0);
            };

            dialog.Controls.Add(textBox);
            dialog.Controls.Add(buttons);
            dialog.CancelButton = closeButton;
            dialog.ShowDialog(this);
        }

        tree.Focus();
    }

    private static void AddDetailTreeSearchNodes(TreeNode node, List<TreeNode> nodes)
    {
        if (node == null || nodes == null)
        {
            return;
        }

        nodes.Add(node);
        foreach (TreeNode child in node.Nodes)
        {
            AddDetailTreeSearchNodes(child, nodes);
        }
    }

    private static string GetDetailTreeNodePath(TreeNode node)
    {
        if (node == null)
        {
            return "";
        }

        var parts = new List<string>();
        var current = node;
        while (current != null)
        {
            parts.Add(current.Text);
            current = current.Parent;
        }

        parts.Reverse();
        return string.Join(" ", parts.ToArray());
    }

    private static void ExpandDetailTreeParents(TreeNode node)
    {
        var current = node == null ? null : node.Parent;
        while (current != null)
        {
            current.Expand();
            current = current.Parent;
        }
    }

    private sealed class DetailTreePath
    {
        public string[] Groups;
        public string Label;
        public int SortIndex;
        public bool ExpandByDefault;
    }

    private sealed class DetailTreeNodeInfo
    {
        public bool IsLeaf;
        public int SortIndex;
    }

    private static void PopulateDetailsTree(TreeView tree, Dictionary<string, string> details)
    {
        if (tree == null || details == null)
        {
            return;
        }

        var groups = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in details
            .Select(p => new { Pair = p, Path = GetDetailTreePath(p.Key) })
            .OrderBy(p => p.Path.SortIndex)
            .ThenBy(p => UsbDetailSortIndex(p.Pair.Key))
            .ThenBy(p => p.Pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var nodes = tree.Nodes;
            var key = "";
            TreeNode parent = null;
            foreach (var group in pair.Path.Groups)
            {
                key = key.Length == 0 ? group : key + "\u001f" + group;
                TreeNode groupNode;
                if (!groups.TryGetValue(key, out groupNode))
                {
                    groupNode = new TreeNode(group)
                    {
                        ToolTipText = group,
                        Tag = new DetailTreeNodeInfo { IsLeaf = false, SortIndex = pair.Path.SortIndex }
                    };
                    nodes.Add(groupNode);
                    groups[key] = groupNode;
                }

                parent = groupNode;
                nodes = groupNode.Nodes;
            }

            var value = pair.Pair.Value ?? "";
            var label = string.IsNullOrWhiteSpace(pair.Path.Label) ? pair.Pair.Key : pair.Path.Label.Trim();
            var text = string.IsNullOrWhiteSpace(value) ? label : label + ": " + value;
            var leaf = new TreeNode(text)
            {
                ToolTipText = text,
                Tag = new DetailTreeNodeInfo { IsLeaf = true, SortIndex = pair.Path.SortIndex }
            };
            nodes.Add(leaf);
            if (parent == null && pair.Path.ExpandByDefault)
            {
                leaf.Expand();
            }
        }

        foreach (TreeNode node in tree.Nodes)
        {
            ExpandDefaultDetailGroups(node);
        }
    }

    private static void ExpandDefaultDetailGroups(TreeNode node)
    {
        if (node == null)
        {
            return;
        }

        if (ShouldExpandDetailGroup(node.Text))
        {
            node.Expand();
            foreach (TreeNode child in node.Nodes)
            {
                ExpandDefaultDetailGroups(child);
            }
        }
    }

    private static bool ShouldExpandDetailGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Equals("WMI", StringComparison.OrdinalIgnoreCase) || name.Equals("Registry", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static DetailTreePath GetDetailTreePath(string field)
    {
        field = (field ?? "").Trim();
        var path = new DetailTreePath
        {
            Groups = new[] { "Summary" },
            Label = field,
            SortIndex = 0,
            ExpandByDefault = true
        };

        if (field.Length == 0)
        {
            return path;
        }

        Match match;
        match = Regex.Match(field, @"^Partition\s+(\d+)\s+volume\s+(\d+)\s+(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            path.Groups = new[] { "Partitions", "Partition " + match.Groups[1].Value, "Volume " + match.Groups[2].Value };
            path.Label = ToDetailLabel(match.Groups[3].Value);
            path.SortIndex = 320 + SafeParseInt(match.Groups[1].Value);
            return path;
        }

        match = Regex.Match(field, @"^Partition\s+(\d+)\s+(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            path.Groups = new[] { "Partitions", "Partition " + match.Groups[1].Value };
            path.Label = ToDetailLabel(match.Groups[2].Value);
            path.SortIndex = 300 + SafeParseInt(match.Groups[1].Value);
            return path;
        }

        match = Regex.Match(field, @"^CPU cache\s+(\d+)\s+WMI\s+(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            path.Groups = new[] { "WMI", "CPU cache " + match.Groups[1].Value };
            path.Label = ToDetailLabel(match.Groups[2].Value);
            path.SortIndex = 910;
            return path;
        }

        match = Regex.Match(field, @"^(.+?)\s+WMI\s+(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            path.Groups = new[] { "WMI", ToDetailLabel(match.Groups[1].Value) };
            path.Label = ToDetailLabel(match.Groups[2].Value);
            path.SortIndex = 900 + WmiDetailSortOffset(match.Groups[1].Value);
            return path;
        }

        DetailTreePath prefixedPath;
        if (TryPrefixDetailPath(field, "Signed driver ", "Driver", 100, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Driver ", "Driver", 100, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "PCI ", "PCI", 120, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "USB ", "USB", 130, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Network ", "Network", 160, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "WiFi ", "Network", 165, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Audio ", "Audio", 170, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Endpoint registry raw property ", "Endpoint registry", 175, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Endpoint ", "Endpoint", 172, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Display ", "Display", 180, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Monitor ", "Display", 185, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Battery ", "Battery", 200, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Physical disk ", "Physical disk", 260, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Volume ", "Volumes", 340, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Windows ", "Windows", 360, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "OS ", "Windows", 365, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Licensing ", "Windows", 370, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "BIOS ", "Firmware", 390, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Secure Boot ", "Firmware", 395, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "TPM ", "Firmware", 400, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Baseboard ", "Motherboard", 420, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Motherboard ", "Motherboard", 420, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Chassis ", "Motherboard", 430, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Memory ", "Memory", 450, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Registry ", "Registry", 950, out prefixedPath)) return prefixedPath;
        if (TryPrefixDetailPath(field, "Raw Windows ", "Raw Windows fields", 980, out prefixedPath)) return prefixedPath;

        return path;
    }

    private static bool TryPrefixDetailPath(string field, string prefix, string group, int sortIndex, out DetailTreePath path)
    {
        path = null;
        if (!field.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = new DetailTreePath
        {
            Groups = new[] { group },
            Label = ToDetailLabel(field.Substring(prefix.Length)),
            SortIndex = sortIndex,
            ExpandByDefault = ShouldExpandDetailGroup(group)
        };
        return true;
    }

    private static int SafeParseInt(string text)
    {
        int value;
        return int.TryParse(text, out value) ? value : 0;
    }

    private static int WmiDetailSortOffset(string source)
    {
        source = source ?? "";
        if (source.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)) return 1;
        if (source.StartsWith("Baseboard", StringComparison.OrdinalIgnoreCase) || source.StartsWith("Chassis", StringComparison.OrdinalIgnoreCase)) return 2;
        if (source.StartsWith("Memory", StringComparison.OrdinalIgnoreCase)) return 3;
        if (source.StartsWith("BIOS", StringComparison.OrdinalIgnoreCase) || source.StartsWith("TPM", StringComparison.OrdinalIgnoreCase)) return 4;
        if (source.StartsWith("Display", StringComparison.OrdinalIgnoreCase) || source.StartsWith("Monitor", StringComparison.OrdinalIgnoreCase)) return 5;
        if (source.StartsWith("Network", StringComparison.OrdinalIgnoreCase)) return 6;
        if (source.StartsWith("Battery", StringComparison.OrdinalIgnoreCase)) return 7;
        return 50;
    }

    private static string ToDetailLabel(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            return text;
        }

        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static int UsbDetailSortIndex(string field)
    {
        if (field.Equals("Name", StringComparison.OrdinalIgnoreCase)) return 0;
        if (field.Equals("Type", StringComparison.OrdinalIgnoreCase)) return 1;
        if (field.Equals("Vendor", StringComparison.OrdinalIgnoreCase)) return 2;
        if (field.Equals("Connected", StringComparison.OrdinalIgnoreCase)) return 3;
        if (field.Equals("Speed", StringComparison.OrdinalIgnoreCase)) return 4;
        if (field.Equals("Power", StringComparison.OrdinalIgnoreCase)) return 5;
        if (field.Equals("Safe to unplug", StringComparison.OrdinalIgnoreCase)) return 6;
        if (field.Equals("Drive letters", StringComparison.OrdinalIgnoreCase)) return 7;
        if (field.Equals("Network adapter", StringComparison.OrdinalIgnoreCase)) return 8;
        if (field.Equals("MAC address", StringComparison.OrdinalIgnoreCase)) return 9;
        if (field.Equals("MAC vendor", StringComparison.OrdinalIgnoreCase)) return 10;
        if (field.Equals("Storage hardware ID", StringComparison.OrdinalIgnoreCase)) return 11;
        if (field.Equals("Storage hardware ID format", StringComparison.OrdinalIgnoreCase)) return 12;
        if (field.Equals("Storage OUI vendor", StringComparison.OrdinalIgnoreCase)) return 13;
        if (field.Equals("VID/PID", StringComparison.OrdinalIgnoreCase)) return 14;
        if (field.Equals("USB ID vendor", StringComparison.OrdinalIgnoreCase)) return 15;
        if (field.Equals("USB ID product", StringComparison.OrdinalIgnoreCase)) return 16;
        if (field.Equals("Network PNP device", StringComparison.OrdinalIgnoreCase)) return 97;
        return 100;
    }

    private void CopyDetailsTree(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var lines = new List<string>();
        if (tree.SelectedNode != null)
        {
            AppendDetailTreeLines(tree.SelectedNode, lines, 0);
        }
        else
        {
            foreach (TreeNode node in tree.Nodes)
            {
                AppendDetailTreeLines(node, lines, 0);
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        statusLabel.Text = string.Format(T("status.Copied detail lines to clipboard.", "Copied {0} detail line{1} to clipboard."), lines.Count, lines.Count == 1 ? "" : "s");
    }

    private void CopyDetailsTreeValueOnly(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var lines = new List<string>();
        if (tree.SelectedNode != null)
        {
            AppendDetailTreeValueLines(tree.SelectedNode, lines);
        }
        else
        {
            foreach (TreeNode node in tree.Nodes)
            {
                AppendDetailTreeValueLines(node, lines);
            }
        }

        if (lines.Count == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        statusLabel.Text = string.Format(T("status.Copied detail values to clipboard.", "Copied {0} detail value{1} to clipboard."), lines.Count, lines.Count == 1 ? "" : "s");
    }

    private void CopyMatchingDetailsTreeLines(TreeView tree)
    {
        if (tree == null)
        {
            return;
        }

        var query = PromptForSingleLineText(
            this,
            T("ui.Copy matching details", "Copy matching details"),
            T("ui.Search text:", "Search text:"),
            "");
        var terms = (query ?? "")
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();
        if (terms.Length == 0)
        {
            tree.Focus();
            return;
        }

        var matches = new List<TreeNode>();
        foreach (TreeNode node in tree.Nodes)
        {
            AddMatchingDetailNodes(node, terms, matches);
        }

        var lines = new List<string>();
        foreach (var match in matches)
        {
            AppendDetailTreeLines(match, lines, 0);
        }

        if (lines.Count == 0)
        {
            statusLabel.Text = T("status.No matching details found.", "No matching details found.");
            tree.Focus();
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, lines.Distinct().ToArray()));
        statusLabel.Text = string.Format(T("status.Copied matching detail lines:", "Copied matching detail lines: {0}."), lines.Count);
        AnnounceCopiedToClipboard();
        tree.Focus();
    }

    private static void AddMatchingDetailNodes(TreeNode node, string[] terms, List<TreeNode> matches)
    {
        if (node == null || terms == null || matches == null)
        {
            return;
        }

        var text = GetDetailTreeNodePath(node).ToUpperInvariant();
        if (terms.All(term => text.IndexOf(term.ToUpperInvariant(), StringComparison.Ordinal) >= 0))
        {
            matches.Add(node);
        }

        foreach (TreeNode child in node.Nodes)
        {
            AddMatchingDetailNodes(child, terms, matches);
        }
    }

    private static string PromptForSingleLineText(IWin32Window owner, string title, string label, string initialValue)
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
            var okButton = new Button { Text = L("ui.&OK", "&OK"), DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = L("ui.&Cancel", "&Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(textBox, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;
            dialog.Shown += delegate { textBox.Focus(); textBox.SelectAll(); };
            return dialog.ShowDialog(owner) == DialogResult.OK ? textBox.Text : "";
        }
    }

    private static void AppendDetailTreeLines(TreeNode node, List<string> lines, int depth)
    {
        if (node == null || lines == null)
        {
            return;
        }

        lines.Add(new string(' ', Math.Max(0, depth) * 2) + node.Text);
        foreach (TreeNode child in node.Nodes)
        {
            AppendDetailTreeLines(child, lines, depth + 1);
        }
    }

    private static void AppendDetailTreeValueLines(TreeNode node, List<string> lines)
    {
        if (node == null || lines == null)
        {
            return;
        }

        if (node.Nodes.Count == 0)
        {
            var value = TextAfterFirstColon(node.Text);
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add(value);
            }
            return;
        }

        foreach (TreeNode child in node.Nodes)
        {
            AppendDetailTreeValueLines(child, lines);
        }
    }
}
