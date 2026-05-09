using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private void UpdateDeviceList()
    {
        var filters = BuildFilters(latestRows).ToList();
        if (filters.Count > 0 && !filters.Any(f => f.Key == selectedFilterKey))
        {
            selectedFilterKey = filters[0].Key;
        }

        var currentSignature = deviceList.Items
            .Cast<DeviceFilter>()
            .Select(f => f.Key + "=" + f.DisplayName)
            .ToList();
        var newSignature = filters.Select(f => f.Key + "=" + f.DisplayName).ToList();

        if (currentSignature.SequenceEqual(newSignature))
        {
            if (deviceList.SelectedItem == null && deviceList.Items.Count > 0)
            {
                var selectedIndex = filters.FindIndex(f => f.Key == selectedFilterKey);
                deviceList.SelectedIndex = Math.Max(0, selectedIndex);
            }
            return;
        }

        deviceList.BeginUpdate();
        try
        {
            deviceList.Items.Clear();
            foreach (var filter in filters)
            {
                deviceList.Items.Add(filter);
            }

            var selectedIndex = filters.FindIndex(f => f.Key == selectedFilterKey);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                selectedFilterKey = filters.Count > 0 ? filters[0].Key : "type|Temperature";
            }

            if (filters.Count > 0)
            {
                deviceList.SelectedIndex = selectedIndex;
            }
        }
        finally
        {
            deviceList.EndUpdate();
        }
    }

    private static IEnumerable<DeviceFilter> BuildFilters(List<SensorRow> rows)
    {
        yield return new DeviceFilter { Key = "type|Performance", DisplayName = T("type.Performance", "Performance/Overview"), Type = "Performance" };
        yield return new DeviceFilter { Key = "type|Temperature", DisplayName = T("type.Temperature", "Temperatures"), Type = "Temperature" };
        yield return new DeviceFilter { Key = "type|Fan", DisplayName = T("type.Fan", "Fans"), Type = "Fan" };
        yield return new DeviceFilter { Key = "type|SMART", DisplayName = T("type.SMART", "SMART"), Type = "SMART" };
        yield return new DeviceFilter { Key = "type|Network", DisplayName = T("type.Network", "Network"), Type = "Network" };
        yield return new DeviceFilter { Key = "type|USB", DisplayName = T("type.USB", "USB"), Type = "USB" };
        if (rows != null && rows.Any(r => string.Equals(r.Type, "Battery", StringComparison.OrdinalIgnoreCase)))
        {
            yield return new DeviceFilter { Key = "type|Battery", DisplayName = T("type.Battery", "Battery"), Type = "Battery" };
        }
    }

    private void UpdateReadingList()
    {
        UpdateReadingList(null);
    }

    private void UpdateReadingList(string preferredFallbackKey)
    {
        if (readingTree == null)
        {
            return;
        }

        var selectedKey = readingTree.SelectedNode == null ? "" : readingTree.SelectedNode.Name;
        var filter = deviceList.SelectedItem as DeviceFilter;
        var filterKey = filter == null ? "" : filter.Key ?? "";
        var expandAll = !readingTreeExpansionInitialized || !string.Equals(lastReadingTreeFilterKey, filterKey, StringComparison.Ordinal);
        var expandedKeys = expandAll ? new HashSet<string>() : GetExpandedNodeKeys(readingTree.Nodes);
        var rows = ApplyFilter(latestRows, filter)
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .ToList();

        var items = BuildReadingTree(rows, filter);
        items = FilterHiddenReadingItems(items);
        var signature = TreeSignature(items);
        var shapeSignature = TreeShapeSignature(items);
        if (string.Equals(lastReadingTreeSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(lastReadingTreeShapeSignature, shapeSignature, StringComparison.Ordinal))
        {
            UpdateTreeNodes(readingTree.Nodes, BuildTreeItemMap(items));
            lastReadingTreeSignature = signature;
            UpdateSelectedMeterProgress();
            return;
        }

        readingTree.BeginUpdate();
        try
        {
            readingTree.Nodes.Clear();
            foreach (var item in items)
            {
                readingTree.Nodes.Add(CreateTreeNode(item));
            }

            ApplyExpandedNodeKeys(readingTree.Nodes, expandedKeys, expandAll);
            var selectedNode = FindTreeNode(readingTree.Nodes, selectedKey);
            if (selectedNode == null && !string.IsNullOrWhiteSpace(preferredFallbackKey))
            {
                selectedNode = FindTreeNode(readingTree.Nodes, preferredFallbackKey);
            }

            if (selectedNode == null && readingTree.Nodes.Count > 0)
            {
                selectedNode = readingTree.Nodes[0];
            }

            if (selectedNode != null)
            {
                readingTree.SelectedNode = selectedNode;
            }

            lastReadingTreeSignature = signature;
            lastReadingTreeShapeSignature = shapeSignature;
            lastReadingTreeFilterKey = filterKey;
            readingTreeExpansionInitialized = true;
            UpdateSelectedMeterProgress();
        }
        finally
        {
            readingTree.EndUpdate();
        }
    }

    private static string FindHideFallbackKey(TreeNode node)
    {
        if (node == null)
        {
            return "";
        }

        var next = NextVisibleTreeNode(node);
        if (next != null && !string.IsNullOrWhiteSpace(next.Name))
        {
            return next.Name;
        }

        var previous = PreviousVisibleTreeNode(node);
        if (previous != null && !string.IsNullOrWhiteSpace(previous.Name))
        {
            return previous.Name;
        }

        return node.Parent == null ? "" : node.Parent.Name;
    }

    private static TreeNode NextVisibleTreeNode(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        if (node.Nodes.Count > 0)
        {
            return node.Nodes[0];
        }

        var current = node;
        while (current != null)
        {
            var sibling = NextSibling(current);
            if (sibling != null)
            {
                return sibling;
            }

            current = current.Parent;
        }

        return null;
    }

    private static TreeNode PreviousVisibleTreeNode(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var sibling = PreviousSibling(node);
        if (sibling != null)
        {
            while (sibling.IsExpanded && sibling.Nodes.Count > 0)
            {
                sibling = sibling.Nodes[sibling.Nodes.Count - 1];
            }

            return sibling;
        }

        return node.Parent;
    }

    private static TreeNode NextSibling(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var siblings = node.Parent == null ? node.TreeView.Nodes : node.Parent.Nodes;
        var index = node.Index + 1;
        return index >= 0 && index < siblings.Count ? siblings[index] : null;
    }

    private static TreeNode PreviousSibling(TreeNode node)
    {
        if (node == null)
        {
            return null;
        }

        var siblings = node.Parent == null ? node.TreeView.Nodes : node.Parent.Nodes;
        var index = node.Index - 1;
        return index >= 0 && index < siblings.Count ? siblings[index] : null;
    }

    private void UpdateSelectedMeterProgress()
    {
        if (selectedMeterProgressBar == null || selectedMeterValueLabel == null)
        {
            return;
        }

        var row = GetSelectedReadingRow();
        if (row == null || !IsMeterRow(row))
        {
            selectedMeterProgressBar.Value = 0;
            selectedMeterProgressBar.AccessibleName = T("a11y.Selected meter", "Selected meter");
            selectedMeterProgressBar.AccessibleDescription = T("a11y.Selected reading is not a percentage meter", "Selected reading is not a percentage meter");
            selectedMeterValueLabel.Text = T("status.noMeterForSelectedReading", "No meter for selected reading.");
            lastSelectedMeterValue = -1;
            lastSelectedMeterLabel = "";
            return;
        }

        var percent = ClampPercent(ExtractPercent(row));
        var value = (int)Math.Round(percent);
        var label = MeterLabel(row);
        var changed = value != lastSelectedMeterValue || !string.Equals(label, lastSelectedMeterLabel, StringComparison.Ordinal);
        selectedMeterProgressBar.Value = value;
        selectedMeterProgressBar.AccessibleName = label + ", " + value + " percent";
        selectedMeterProgressBar.AccessibleDescription = label + ", " + value + " percent";
        selectedMeterValueLabel.Text = label + ": " + value + "%";
        if (changed)
        {
            lastSelectedMeterValue = value;
            lastSelectedMeterLabel = label;
            selectedMeterProgressBar.NotifyAccessibleValueChanged();
        }
    }

    private static string MeterLabel(SensorRow row)
    {
        var name = CleanSensorName(row.Name);
        var hardware = ShortHardwareName(row.Hardware);
        if (string.IsNullOrWhiteSpace(hardware) || hardware.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return DisplayReadingName(name);
        }

        return hardware + ", " + DisplayReadingName(name);
    }

    private static bool IsMeterRow(SensorRow row)
    {
        if (row == null || row.Type == "Fan Control")
        {
            return false;
        }

        var percent = ExtractPercent(row);
        return percent.HasValue && percent.Value >= 0 && percent.Value <= 100;
    }

    private static float ClampPercent(float? value)
    {
        if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
        {
            return 0;
        }

        return Math.Max(0, Math.Min(100, value.Value));
    }

    private static float? ExtractPercent(SensorRow row)
    {
        if (row == null)
        {
            return null;
        }

        var text = row.DisplayValue ?? "";
        var parsed = ExtractPercent(text);
        if (parsed.HasValue)
        {
            return parsed.Value;
        }

        if (row.Value.HasValue && IsImplicitPercentRow(row))
        {
            return row.Value.Value;
        }

        return null;
    }

    private static bool IsImplicitPercentRow(SensorRow row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.Name))
        {
            return false;
        }

        var name = CleanSensorName(row.Name);
        return name.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("activity", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("space used", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("used space", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("free space", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("life remaining", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("wear", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("load", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float? ExtractPercent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var percentIndex = text.IndexOf('%');
        if (percentIndex < 0)
        {
            return null;
        }

        var start = percentIndex - 1;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '.' || text[start] == ',' || text[start] == '-'))
        {
            start--;
        }

        var number = text.Substring(start + 1, percentIndex - start - 1).Trim().Replace(',', '.');
        float value;
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : (float?)null;
    }

    private static Dictionary<string, ReadingTreeItem> BuildTreeItemMap(IEnumerable<ReadingTreeItem> items)
    {
        var map = new Dictionary<string, ReadingTreeItem>();
        AddTreeItemMap(items, map);
        return map;
    }

    private static void AddTreeItemMap(IEnumerable<ReadingTreeItem> items, Dictionary<string, ReadingTreeItem> map)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Key))
            {
                map[item.Key] = item;
            }

            AddTreeItemMap(item.Children, map);
        }
    }

    private static void UpdateTreeNodes(TreeNodeCollection nodes, Dictionary<string, ReadingTreeItem> itemByKey)
    {
        foreach (TreeNode node in nodes)
        {
            ReadingTreeItem item;
            if (!string.IsNullOrWhiteSpace(node.Name) && itemByKey.TryGetValue(node.Name, out item))
            {
                if (node.Text != item.Text)
                {
                    node.Text = item.Text;
                }

                node.Tag = item.Row;
            }

            UpdateTreeNodes(node.Nodes, itemByKey);
        }
    }

    private static HashSet<string> GetExpandedNodeKeys(TreeNodeCollection nodes)
    {
        var keys = new HashSet<string>();
        AddExpandedNodeKeys(nodes, keys);
        return keys;
    }

    private static void AddExpandedNodeKeys(TreeNodeCollection nodes, HashSet<string> keys)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded && !string.IsNullOrWhiteSpace(node.Name))
            {
                keys.Add(node.Name);
            }

            AddExpandedNodeKeys(node.Nodes, keys);
        }
    }

    private static void ApplyExpandedNodeKeys(TreeNodeCollection nodes, HashSet<string> expandedKeys, bool expandAll)
    {
        foreach (TreeNode node in nodes)
        {
            if (expandAll || expandedKeys.Contains(node.Name))
            {
                node.Expand();
            }
            else
            {
                node.Collapse();
            }

            ApplyExpandedNodeKeys(node.Nodes, expandedKeys, expandAll);
        }
    }

    private static List<ReadingTreeItem> BuildReadingTree(List<SensorRow> rows, DeviceFilter filter)
    {
        if (rows.Count == 0)
        {
            var loadingText = T("message.refreshingInBackground", "Readings will appear here as the background refresh completes.");
            if (filter != null && !string.IsNullOrWhiteSpace(filter.Type))
            {
                loadingText = DisplayTypeName(filter.Type) + ": " + loadingText;
            }

            return new List<ReadingTreeItem> { new ReadingTreeItem { Key = "empty", Text = loadingText } };
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var typeItem = new ReadingTreeItem { Key = "type|" + filter.Type, Text = DisplayTypeName(filter.Type) };
            if (filter.Type == "Performance")
            {
                AddPerformanceGroups(typeItem, rows);
                return typeItem.Children;
            }

            if (filter.Type == "USB")
            {
                AddUsbGroups(typeItem, rows);
                return typeItem.Children;
            }

            AddHardwareGroups(typeItem, rows);
            return typeItem.Children;
        }

        if (!string.IsNullOrWhiteSpace(filter.Hardware))
        {
            var hardwareItem = new ReadingTreeItem { Key = "hardware|" + filter.Hardware, Text = ShortHardwareName(filter.Hardware) };
            AddReadingRows(hardwareItem, rows);
            return new List<ReadingTreeItem> { hardwareItem };
        }

        var root = new ReadingTreeItem { Key = "readings", Text = T("type.Readings", "Readings") };
        AddReadingRows(root, rows);
        return new List<ReadingTreeItem> { root };
    }

    private List<ReadingTreeItem> FilterHiddenReadingItems(IEnumerable<ReadingTreeItem> items)
    {
        var hidden = new HashSet<string>(settings.HiddenReadingKeys ?? new List<string>());
        return items
            .Select(item => FilterHiddenReadingItem(item, hidden))
            .Where(item => item != null)
            .ToList();
    }

    private static ReadingTreeItem FilterHiddenReadingItem(ReadingTreeItem item, HashSet<string> hidden)
    {
        if (hidden.Contains(item.Key))
        {
            return null;
        }

        var copy = new ReadingTreeItem { Key = item.Key, Text = item.Text, Row = item.Row };
        foreach (var child in item.Children)
        {
            var filtered = FilterHiddenReadingItem(child, hidden);
            if (filtered != null)
            {
                copy.Children.Add(filtered);
            }
        }

        if (copy.Row == null && copy.Children.Count == 0)
        {
            return null;
        }

        return copy;
    }

    private static void AddHardwareGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var hardwareGroup in rows
            .GroupBy(r => ShortHardwareName(r.Hardware))
            .OrderBy(g => g.Key))
        {
            var hardwareItem = new ReadingTreeItem
            {
                Key = "hardware|" + parent.Key + "|" + hardwareGroup.Key,
                Text = hardwareGroup.Key
            };
            AddReadingRows(hardwareItem, hardwareGroup);
            parent.Children.Add(hardwareItem);
        }
    }

    private static void AddPerformanceGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var overviewRows = rows
            .Where(r => string.Equals(r.Hardware, "Overview", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overviewRows.Count > 0)
        {
            var overviewItem = new ReadingTreeItem { Key = "performance|overview", Text = T("group.Overview", "Overview") };
            AddReadingRows(overviewItem, overviewRows);
            parent.Children.Add(overviewItem);
        }

        var systemRows = rows
            .Where(r => IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware))
            .ToList();
        if (systemRows.Count > 0)
        {
            var systemItem = new ReadingTreeItem { Key = "performance|system", Text = T("group.System", "System") };
            foreach (var hardwareGroup in systemRows
                .GroupBy(r => ShortHardwareName(r.Hardware))
                .OrderBy(g => PerformanceHardwareSortIndex(g.Key))
                .ThenBy(g => g.Key))
            {
                var hardwareItem = new ReadingTreeItem
                {
                    Key = "hardware|performance|system|" + hardwareGroup.Key,
                    Text = hardwareGroup.Key
                };
                AddReadingRows(hardwareItem, hardwareGroup);
                systemItem.Children.Add(hardwareItem);
            }

            parent.Children.Add(systemItem);
        }

        var storageRows = rows
            .Where(r => !IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware))
            .ToList();
        if (storageRows.Count > 0)
        {
            var storageItem = new ReadingTreeItem { Key = "performance|storage", Text = T("group.Storage", "Storage") };
            AddHardwareGroups(storageItem, storageRows);
            parent.Children.Add(storageItem);
        }
    }

    private static void AddUsbGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var devices = rows
            .Where(r => !IsUsbHubOrController(r))
            .ToList();
        if (devices.Count > 0)
        {
            var devicesItem = new ReadingTreeItem { Key = "usb|devices", Text = T("group.USB devices", "Connected devices") };
            AddReadingRows(devicesItem, devices);
            parent.Children.Add(devicesItem);
        }

        var hubs = rows
            .Where(IsUsbHubOrController)
            .ToList();
        if (hubs.Count > 0)
        {
            var hubsItem = new ReadingTreeItem { Key = "usb|hubs", Text = T("group.USB hubs", "Hubs and controllers") };
            AddReadingRows(hubsItem, hubs);
            parent.Children.Add(hubsItem);
        }
    }

    private static bool IsUsbHubOrController(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        var name = (row.Hardware + " " + row.DisplayValue).ToLowerInvariant();
        return name.IndexOf("hub") >= 0 || name.IndexOf("controller") >= 0 || name.IndexOf("host") >= 0;
    }

    private static bool IsSystemPerformanceHardware(string hardware)
    {
        return string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewHardware(string hardware)
    {
        return string.Equals(hardware, "Overview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "BIOS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "GPU", StringComparison.OrdinalIgnoreCase);
    }

    private static int PerformanceHardwareSortIndex(string hardware)
    {
        if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static void AddReadingRows(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var row in rows
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ReadingSortIndex(r.Name))
            .ThenBy(r => CleanSensorName(r.Name)))
        {
            parent.Children.Add(new ReadingTreeItem
            {
                Key = "row|" + RowSettingsKey(row),
                Text = row.Type == "USB" ? ShortHardwareName(row.Hardware) + ": " + FormatValue(row) : DisplayReadingName(row.Name) + ": " + FormatValue(row),
                Row = row
            });
        }
    }

    private static string DisplayReadingName(string name)
    {
        var clean = CleanSensorName(name);
        var translated = T("reading." + clean, clean);
        if (!string.Equals(translated, clean, StringComparison.Ordinal))
        {
            return translated;
        }

        if (clean.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase))
        {
            return T("reading.Temperature #", "Temperature #") + clean.Substring("Temperature #".Length);
        }

        return translated;
    }

    private static TreeNode CreateTreeNode(ReadingTreeItem item)
    {
        var node = new TreeNode(item.Text) { Name = item.Key, Tag = item.Row };
        foreach (var child in item.Children)
        {
            node.Nodes.Add(CreateTreeNode(child));
        }

        return node;
    }

    private static TreeNode FindTreeNode(TreeNodeCollection nodes, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (TreeNode node in nodes)
        {
            if (string.Equals(node.Name, key, StringComparison.Ordinal))
            {
                return node;
            }

            var found = FindTreeNode(node.Nodes, key);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static string TreeSignature(IEnumerable<ReadingTreeItem> items)
    {
        return string.Join("\n", items.Select(TreeSignature).ToArray());
    }

    private static string TreeSignature(ReadingTreeItem item)
    {
        return item.Key + "=" + item.Text + "\n" + string.Join("\n", item.Children.Select(TreeSignature).ToArray());
    }

    private static string TreeShapeSignature(IEnumerable<ReadingTreeItem> items)
    {
        return string.Join("\n", items.Select(TreeShapeSignature).ToArray());
    }

    private static string TreeShapeSignature(ReadingTreeItem item)
    {
        return item.Key + "\n" + string.Join("\n", item.Children.Select(TreeShapeSignature).ToArray());
    }

    private static IEnumerable<SensorRow> ApplyFilter(IEnumerable<SensorRow> rows, DeviceFilter filter)
    {
        if (filter == null)
        {
            return rows.Where(r => r.Type == "Temperature");
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            return rows.Where(r => r.Type == filter.Type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Hardware))
        {
            return rows.Where(r => r.Type != "Fan Control" && r.Type != "Performance" && r.Hardware == filter.Hardware);
        }

        return rows;
    }

    public static int TypeSortIndex(string type)
    {
        if (type == "Performance")
        {
            return 0;
        }

        if (type == "Temperature")
        {
            return 1;
        }

        if (type == "Fan")
        {
            return 2;
        }

        if (type == "SMART")
        {
            return 3;
        }

        if (type == "Network")
        {
            return 5;
        }

        if (type == "USB")
        {
            return 6;
        }

        if (type == "Battery")
        {
            return 4;
        }

        return 7;
    }

    public static string DisplayTypeName(string type)
    {
        if (type == "Temperature")
        {
            return T("type.Temperature", "Temperatures");
        }

        if (type == "Fan")
        {
            return T("type.Fan", "Fans");
        }

        if (type == "SMART")
        {
            return T("type.SMART", "SMART");
        }

        if (type == "Performance")
        {
            return T("type.Performance", "Performance/Overview");
        }

        if (type == "Network")
        {
            return T("type.Network", "Network");
        }

        if (type == "Battery")
        {
            return T("type.Battery", "Battery");
        }

        if (type == "USB")
        {
            return T("type.USB", "USB");
        }

        return string.IsNullOrWhiteSpace(type) ? T("type.Readings", "Readings") : type;
    }

    public static int ReadingSortIndex(string name)
    {
        var clean = CleanSensorName(name);
        if (clean.Equals("Uptime", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Model", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("BIOS vendor", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("BIOS version", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("BIOS date", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Health", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU usage", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("Memory total", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("Memory used", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("Memory used size", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Memory available", StringComparison.OrdinalIgnoreCase)) return 6;
        if (clean.Equals("Data read", StringComparison.OrdinalIgnoreCase)) return 10;
        if (clean.Equals("Data written", StringComparison.OrdinalIgnoreCase)) return 11;
        if (clean.Equals("Read rate", StringComparison.OrdinalIgnoreCase)) return 12;
        if (clean.Equals("Write rate", StringComparison.OrdinalIgnoreCase)) return 13;
        if (clean.Equals("Read activity", StringComparison.OrdinalIgnoreCase)) return 14;
        if (clean.Equals("Write activity", StringComparison.OrdinalIgnoreCase)) return 15;
        if (clean.Equals("Total activity", StringComparison.OrdinalIgnoreCase)) return 16;
        if (clean.Equals("Total space", StringComparison.OrdinalIgnoreCase)) return 20;
        if (clean.Equals("Space used", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Free space", StringComparison.OrdinalIgnoreCase)) return 22;
        if (clean.Equals("Used space", StringComparison.OrdinalIgnoreCase)) return 23;
        if (clean.Equals("Size", StringComparison.OrdinalIgnoreCase)) return 23;
        if (clean.Equals("Status", StringComparison.OrdinalIgnoreCase)) return 30;
        if (clean.Equals("IP address", StringComparison.OrdinalIgnoreCase)) return 31;
        if (clean.Equals("Link speed", StringComparison.OrdinalIgnoreCase)) return 32;
        if (clean.Equals("Receive rate", StringComparison.OrdinalIgnoreCase)) return 33;
        if (clean.Equals("Send rate", StringComparison.OrdinalIgnoreCase)) return 34;
        if (clean.Equals("Data received", StringComparison.OrdinalIgnoreCase)) return 35;
        if (clean.Equals("Data sent", StringComparison.OrdinalIgnoreCase)) return 36;
        if (clean.Equals("Device", StringComparison.OrdinalIgnoreCase)) return 40;
        return 100;
    }

    public static string ShortHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return NormalizeHardwareName(hardware);
    }

    private static string NormalizeHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        return NormalizeStorageHardwareName(hardware.Trim());
    }

    private static string NormalizeStorageHardwareName(string hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware))
        {
            return "Unknown device";
        }

        var value = hardware.Trim();
        if (value.StartsWith("USB ", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4).Trim();
        }

        value = ReplaceIgnoreCase(value, "SCSI Disk Device", "").Trim();
        while (value.IndexOf("  ", StringComparison.Ordinal) >= 0)
        {
            value = value.Replace("  ", " ");
        }

        return value;
    }

    private static string ReplaceIgnoreCase(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue))
        {
            return value;
        }

        var index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            value = value.Substring(0, index) + newValue + value.Substring(index + oldValue.Length);
            index = value.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    public static string CleanSensorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed sensor";
        }

        return name
            .Replace("Core (Tctl/Tdie)", "CPU package")
            .Replace("CCD1 (Tdie)", "CCD1 Tdie");
    }

    private static string FormatValue(SensorRow row)
    {
        if (!row.Value.HasValue)
        {
            return row.DisplayValue ?? "";
        }

        if (row.Type == "Temperature")
        {
            return FormatTemperature(row.Value.Value);
        }

        if (!string.IsNullOrWhiteSpace(row.DisplayValue))
        {
            return row.DisplayValue;
        }

        if (row.Type == "Fan")
        {
            return FormatNumber(Math.Round(row.Value.Value, 0), "0") + " RPM";
        }

        if (row.Type == "SMART")
        {
            return FormatNumber(Math.Round(row.Value.Value, 1), "0.0");
        }
        return FormatNumber(Math.Round(row.Value.Value, 1), "0.0");
    }

    private static string FormatTemperature(float celsius)
    {
        var unit = NormalizeTemperatureUnit(activeTemperatureUnit);
        var celsiusText = FormatNumber(Math.Round(celsius, 1), "0.0") + " C";
        var fahrenheitText = FormatNumber(Math.Round((celsius * 9.0 / 5.0) + 32.0, 1), "0.0") + " F";
        if (unit == "F")
        {
            return fahrenheitText;
        }
        if (unit == "CF")
        {
            return celsiusText + " / " + fahrenheitText;
        }
        if (unit == "FC")
        {
            return fahrenheitText + " / " + celsiusText;
        }

        return celsiusText;
    }

    private static string HtmlEncode(string value)
    {
        if (value == null)
        {
            return "";
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

}
