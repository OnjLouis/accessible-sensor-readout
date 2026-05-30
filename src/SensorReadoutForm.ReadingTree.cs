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
        yield return new DeviceFilter { Key = "type|Audio", DisplayName = T("type.Audio", "Audio"), Type = "Audio" };
        yield return new DeviceFilter { Key = "type|Display", DisplayName = T("type.Display", "Display"), Type = "Display" };
        yield return new DeviceFilter { Key = "type|Devices", DisplayName = T("type.Devices", "Devices"), Type = "Devices" };
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

        var expansionMode = NormalizeReadingTreeExpansionMode(settings == null ? "" : settings.ReadingTreeExpansionMode);
        if (!string.IsNullOrWhiteSpace(lastAppliedReadingTreeExpansionMode) &&
            !string.Equals(lastAppliedReadingTreeExpansionMode, expansionMode, StringComparison.OrdinalIgnoreCase))
        {
            readingTreeExpansionInitialized = false;
            lastReadingTreeShapeSignature = "";
            lastAppliedReadingTreeExpansionMode = expansionMode;
        }
        else if (string.IsNullOrWhiteSpace(lastAppliedReadingTreeExpansionMode))
        {
            lastAppliedReadingTreeExpansionMode = expansionMode;
        }

        var selectedKey = readingTree.SelectedNode == null ? "" : readingTree.SelectedNode.Name;
        var filter = deviceList.SelectedItem as DeviceFilter;
        var filterKey = filter == null ? "" : filter.Key ?? "";
        var wasPlaceholderOnly = readingTree.Nodes.Count == 1 && string.Equals(readingTree.Nodes[0].Name, "empty", StringComparison.Ordinal);
        var resetExpansion = !readingTreeExpansionInitialized || !string.Equals(lastReadingTreeFilterKey, filterKey, StringComparison.Ordinal) || wasPlaceholderOnly;
        var expandAll = resetExpansion && ShouldExpandReadingTreeOnReset();
        var expandedKeys = resetExpansion ? new HashSet<string>() : GetExpandedNodeKeys(readingTree.Nodes);
        var rows = ApplyFilter(latestRows, filter)
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ShortHardwareName(r.Hardware))
            .ThenBy(r => CleanSensorName(r.Name))
            .ToList();

        var items = BuildReadingTree(rows, filter);
        if (!reportViewMode)
        {
            items = FilterHiddenReadingItems(items);
        }
        var signature = TreeSignature(items);
        var shapeSignature = TreeShapeSignature(items);
        if (string.Equals(lastReadingTreeFilterKey, filterKey, StringComparison.Ordinal) &&
            string.Equals(lastReadingTreeSignature, signature, StringComparison.Ordinal))
        {
            if (resetExpansion)
            {
                ApplyReadingTreeExpansion(readingTree.Nodes, expandedKeys, expandAll);
                lastReadingTreeFilterKey = filterKey;
                readingTreeExpansionInitialized = true;
            }
            return;
        }

        if (!resetExpansion && string.Equals(lastReadingTreeShapeSignature, shapeSignature, StringComparison.Ordinal))
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

            ApplyReadingTreeExpansion(readingTree.Nodes, expandedKeys, expandAll);
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
            var noMeterText = T("status.noMeterForSelectedReading", "No meter for selected reading.");
            if (lastSelectedMeterValue != -1 || !string.Equals(lastSelectedMeterLabel, noMeterText, StringComparison.Ordinal))
            {
                selectedMeterProgressBar.Value = 0;
                selectedMeterProgressBar.AccessibleName = T("a11y.Selected meter", "Selected meter");
                selectedMeterProgressBar.AccessibleDescription = T("a11y.Selected reading is not a percentage meter", "Selected reading is not a percentage meter");
                selectedMeterValueLabel.Text = noMeterText;
                lastSelectedMeterValue = -1;
                lastSelectedMeterLabel = noMeterText;
            }

            return;
        }

        var percent = ClampPercent(ExtractPercent(row));
        var value = (int)Math.Round(percent);
        var label = MeterLabel(row);
        var changed = value != lastSelectedMeterValue || !string.Equals(label, lastSelectedMeterLabel, StringComparison.Ordinal);
        if (changed)
        {
            selectedMeterProgressBar.Value = value;
            selectedMeterProgressBar.AccessibleName = label + ", " + value + " percent";
            selectedMeterProgressBar.AccessibleDescription = label + ", " + value + " percent";
            selectedMeterValueLabel.Text = label + ": " + value + "%";
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
                var textChanged = false;
                if (node.Text != item.Text)
                {
                    node.Text = item.Text;
                    textChanged = true;
                }

                var existingRow = node.Tag as SensorRow;
                if (textChanged || (existingRow == null) != (item.Row == null))
                {
                    node.Tag = item.Row;
                }

                if (textChanged || string.IsNullOrWhiteSpace(node.ToolTipText) != (item.Row == null || item.Row.Details == null || item.Row.Details.Count == 0))
                {
                    var detailsHint = GetTreeNodeDetailsHint(item);
                    if (!string.Equals(node.ToolTipText, detailsHint, StringComparison.Ordinal))
                    {
                        node.ToolTipText = detailsHint;
                    }
                }
            }

            UpdateTreeNodes(node.Nodes, itemByKey);
        }
    }

    private void AnnounceSelectedReadingDetailsAvailability()
    {
        if (detailsAvailabilityAnnouncementTimer != null)
        {
            detailsAvailabilityAnnouncementTimer.Stop();
        }

        pendingDetailsAvailabilityAnnouncementKey = "";
        if (readingTree == null || readingTree.SelectedNode == null || !readingTree.ContainsFocus)
        {
            return;
        }

        var row = readingTree.SelectedNode.Tag as SensorRow;
        if (row == null || row.Details == null || row.Details.Count == 0)
        {
            return;
        }

        var nodeKey = string.IsNullOrWhiteSpace(readingTree.SelectedNode.Name)
            ? readingTree.SelectedNode.FullPath
            : readingTree.SelectedNode.Name;
        var now = DateTime.UtcNow;
        if (string.Equals(lastDetailsAvailabilityAnnouncementKey, nodeKey, StringComparison.Ordinal) &&
            now - lastDetailsAvailabilityAnnouncementUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        pendingDetailsAvailabilityAnnouncementKey = nodeKey;
        if (detailsAvailabilityAnnouncementTimer != null)
        {
            detailsAvailabilityAnnouncementTimer.Start();
        }
    }

    private void SpeakPendingDetailsAvailabilityAnnouncement()
    {
        if (readingTree == null || readingTree.SelectedNode == null || !readingTree.ContainsFocus || string.IsNullOrWhiteSpace(pendingDetailsAvailabilityAnnouncementKey))
        {
            pendingDetailsAvailabilityAnnouncementKey = "";
            return;
        }

        var currentKey = string.IsNullOrWhiteSpace(readingTree.SelectedNode.Name)
            ? readingTree.SelectedNode.FullPath
            : readingTree.SelectedNode.Name;
        if (!string.Equals(currentKey, pendingDetailsAvailabilityAnnouncementKey, StringComparison.Ordinal))
        {
            pendingDetailsAvailabilityAnnouncementKey = "";
            return;
        }

        var row = readingTree.SelectedNode.Tag as SensorRow;
        if (row == null || row.Details == null || row.Details.Count == 0)
        {
            pendingDetailsAvailabilityAnnouncementKey = "";
            return;
        }

        lastDetailsAvailabilityAnnouncementKey = currentKey;
        lastDetailsAvailabilityAnnouncementUtc = DateTime.UtcNow;
        pendingDetailsAvailabilityAnnouncementKey = "";

        string error;
        var message = T("a11y.Has details", "Has Details.");
        if (!ScreenReaderOutput.TrySpeakPolite(message, out error))
        {
            LogMessage("Debug", "Details availability hint was not spoken. " + error);
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

    private bool ShouldExpandReadingTreeOnReset()
    {
        var mode = NormalizeReadingTreeExpansionMode(settings == null ? "" : settings.ReadingTreeExpansionMode);
        if (string.Equals(mode, ReadingTreeExpansionCollapsed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(mode, ReadingTreeExpansionRemember, StringComparison.OrdinalIgnoreCase))
        {
            return settings == null || settings.ReadingTreeLastExpanded;
        }

        return true;
    }

    private void ApplyReadingTreeExpansion(TreeNodeCollection nodes, HashSet<string> expandedKeys, bool expandAll)
    {
        suppressReadingTreeExpansionTracking = true;
        try
        {
            ApplyExpandedNodeKeys(nodes, expandedKeys, expandAll);
        }
        finally
        {
            suppressReadingTreeExpansionTracking = false;
        }
    }

    private void TrackReadingTreeExpansionAction(bool expanded)
    {
        if (suppressReadingTreeExpansionTracking || settings == null)
        {
            return;
        }

        if (settings.ReadingTreeLastExpanded == expanded)
        {
            return;
        }

        settings.ReadingTreeLastExpanded = expanded;
        SaveSettings(settings);
    }

    private void CaptureReadingExpansionBeforeHide()
    {
        hiddenReadingExpandedKeys = readingTree == null
            ? new HashSet<string>()
            : GetExpandedNodeKeys(readingTree.Nodes);
    }

    private void RestoreReadingExpansionAfterShow()
    {
        if (readingTree == null || hiddenReadingExpandedKeys == null || hiddenReadingExpandedKeys.Count == 0)
        {
            return;
        }

        readingTree.BeginUpdate();
        try
        {
            ApplyReadingTreeExpansion(readingTree.Nodes, hiddenReadingExpandedKeys, false);
            if (readingTree.SelectedNode != null)
            {
                readingTree.SelectedNode.EnsureVisible();
            }
        }
        finally
        {
            readingTree.EndUpdate();
        }
    }

    private void ExpandAllReadings()
    {
        if (readingTree == null || readingTree.Nodes.Count == 0)
        {
            return;
        }

        readingTree.BeginUpdate();
        try
        {
            suppressReadingTreeExpansionTracking = true;
            readingTree.ExpandAll();
        }
        finally
        {
            suppressReadingTreeExpansionTracking = false;
            readingTree.EndUpdate();
        }
        TrackReadingTreeExpansionAction(true);
        readingTreeExpansionInitialized = true;
        statusLabel.Text = T("status.Expanded all readings.", "Expanded all readings.");
        readingTree.Focus();
    }

    private void CollapseAllReadings()
    {
        if (readingTree == null || readingTree.Nodes.Count == 0)
        {
            return;
        }

        readingTree.BeginUpdate();
        try
        {
            suppressReadingTreeExpansionTracking = true;
            readingTree.CollapseAll();
            if (readingTree.SelectedNode != null)
            {
                readingTree.SelectedNode.EnsureVisible();
            }
        }
        finally
        {
            suppressReadingTreeExpansionTracking = false;
            readingTree.EndUpdate();
        }
        TrackReadingTreeExpansionAction(false);
        readingTreeExpansionInitialized = true;
        statusLabel.Text = T("status.Collapsed all readings.", "Collapsed all readings.");
        readingTree.Focus();
    }

    private List<ReadingTreeItem> BuildReadingTree(List<SensorRow> rows, DeviceFilter filter)
    {
        if (rows.Count == 0)
        {
            var loadingText = EmptyCategoryMessage(filter == null ? "" : filter.Type);
            if (filter != null && !string.IsNullOrWhiteSpace(filter.Type))
            {
                loadingText = DisplayTypeName(filter.Type) + ": " + loadingText;
            }

            return new List<ReadingTreeItem> { new ReadingTreeItem { Key = "empty", Text = loadingText } };
        }

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var typeItem = new ReadingTreeItem { Key = "type|" + filter.Type, Text = DisplayTypeName(filter.Type) };
            var summaryItem = BuildCategorySummaryItem(filter.Type, rows);
            if (filter.Type == "Performance")
            {
                AddPerformanceGroups(typeItem, rows);
                InsertCategorySummary(typeItem, summaryItem);
                return typeItem.Children;
            }

            if (filter.Type == "Temperature")
            {
                AddTemperatureGroups(typeItem, rows);
                InsertCategorySummary(typeItem, summaryItem);
                return typeItem.Children;
            }

            if (filter.Type == "USB")
            {
                AddUsbGroups(typeItem, rows);
                InsertCategorySummary(typeItem, summaryItem);
                return typeItem.Children;
            }

            if (filter.Type == "Audio")
            {
                AddAudioGroups(typeItem, rows);
                InsertCategorySummary(typeItem, summaryItem);
                return typeItem.Children;
            }

            if (filter.Type == "Devices")
            {
                AddDeviceInventoryGroups(typeItem, rows);
                InsertCategorySummary(typeItem, summaryItem);
                return typeItem.Children;
            }

            AddHardwareGroups(typeItem, rows);
            InsertCategorySummary(typeItem, summaryItem);
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

    private string EmptyCategoryMessage(string type)
    {
        if (reportViewMode)
        {
            return T("message.staticReportCategoryEmpty", "This static report does not contain readings for this category.");
        }

        type = type ?? "";
        if (type.Equals("Fan", StringComparison.OrdinalIgnoreCase))
        {
            return T("message.noFanReadingsYet", "No fan readings are visible yet. Some systems need LibreHardwareMonitor, PawnIO, or a laptop Plug-In, and some hardware does not expose fan sensors to Windows.");
        }
        if (type.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return T("message.noTemperatureReadingsYet", "No temperature readings are visible yet. Sensor Readout will show them when LibreHardwareMonitor, Core Temp, Windows, or an enabled hardware Plug-In exposes them.");
        }
        if (type.Equals("SMART", StringComparison.OrdinalIgnoreCase))
        {
            return T("message.noSmartReadingsYet", "No SMART readings are visible yet. Some drives, USB bridges, RAID controllers, or Windows storage drivers hide SMART data.");
        }
        if (type.Equals("Network", StringComparison.OrdinalIgnoreCase))
        {
            return T("message.noNetworkReadingsYet", "No network readings are visible yet. Check that Windows exposes an active network adapter; Wi-Fi details require a Wi-Fi adapter and Windows WLAN data.");
        }
        if (type.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            return T("message.noUsbReadingsYet", "No USB readings are visible yet. Press F5 after plugging or unplugging hardware so Sensor Readout can rebuild the USB inventory.");
        }
        if (type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
        {
            return T("message.noBatteryReadingsYet", "No battery readings are visible yet. Desktop systems and some battery drivers do not expose battery data to Windows.");
        }
        return T("message.refreshingInBackground", "Readings will appear here as the background refresh completes.");
    }

    private static void InsertCategorySummary(ReadingTreeItem typeItem, ReadingTreeItem summaryItem)
    {
        if (typeItem == null || summaryItem == null)
        {
            return;
        }

        typeItem.Children.Insert(0, summaryItem);
    }

    private ReadingTreeItem BuildCategorySummaryItem(string type, List<SensorRow> rows)
    {
        if (string.IsNullOrWhiteSpace(type) || rows == null || rows.Count == 0)
        {
            return null;
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hardwareCount = rows.Select(r => ShortHardwareName(r.Hardware)).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AddDetail(details, "Category", DisplayTypeName(type));
        AddDetail(details, "Reading count", rows.Count.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, "Device or group count", hardwareCount.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, "Numeric readings", rows.Count(r => r.Value.HasValue).ToString(CultureInfo.InvariantCulture));
        AddDetail(details, "Rows with details", rows.Count(r => r.Details != null && r.Details.Count > 0).ToString(CultureInfo.InvariantCulture));

        if (type.Equals("Network", StringComparison.OrdinalIgnoreCase))
        {
            AddDetail(details, "Network adapters up", rows.Count(r => string.Equals(r.Name, "Status", StringComparison.OrdinalIgnoreCase) && string.Equals(r.DisplayValue, "Up", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
            AddDetail(details, "WiFi connected rows", rows.Count(r => string.Equals(r.Name, "Wi-Fi connected", StringComparison.OrdinalIgnoreCase) && r.Value.HasValue && r.Value.Value >= 1).ToString(CultureInfo.InvariantCulture));
        }
        else if (type.Equals("Devices", StringComparison.OrdinalIgnoreCase))
        {
            AddDetail(details, "Non-working devices", rows.Count(r => string.Equals(r.Hardware, "Non-working devices", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
        }
        else if (type.Equals("SMART", StringComparison.OrdinalIgnoreCase))
        {
            AddDetail(details, "Drives or storage groups", hardwareCount.ToString(CultureInfo.InvariantCulture));
        }
        else if (type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
        {
            var charge = rows.FirstOrDefault(r => r.Value.HasValue && (r.DisplayValue ?? "").Contains("%"));
            AddDetail(details, "Current charge", charge == null ? "" : FormatValue(charge));
        }

        var display = string.Format(
            T("ui.Category summary count", "{0} {1} across {2} {3}"),
            rows.Count.ToString(CultureInfo.InvariantCulture),
            rows.Count == 1 ? T("ui.reading", "reading") : T("ui.readings", "readings"),
            hardwareCount.ToString(CultureInfo.InvariantCulture),
            hardwareCount == 1 ? T("ui.group", "group") : T("ui.groups", "groups"));
        return new ReadingTreeItem
        {
            Key = "category-summary|" + type,
            Text = T("ui.Category summary", "Category summary") + ": " + display,
            Row = new SensorRow
            {
                Type = type,
                Hardware = T("ui.Category summary", "Category summary"),
                Name = T("ui.Category summary", "Category summary"),
                DisplayValue = display,
                Source = "Sensor Readout",
                Details = details
            }
        };
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
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
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
            AddOverviewGroups(overviewItem, overviewRows);
            parent.Children.Add(overviewItem);
        }

        var dataSourceRows = rows
            .Where(IsDataSourceSummaryRow)
            .ToList();
        if (dataSourceRows.Count > 0)
        {
            var dataSourcesItem = new ReadingTreeItem { Key = "performance|data-sources", Text = T("ui.Data sources", "Data sources") };
            AddReadingRows(dataSourcesItem, dataSourceRows);
            parent.Children.Add(dataSourcesItem);
        }

        var systemRows = rows
            .Where(r => IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware))
            .ToList();
        if (systemRows.Count > 0)
        {
            var systemItem = new ReadingTreeItem { Key = "performance|system", Text = T("group.CPU and memory", "CPU and memory") };
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
                if (string.Equals(hardwareGroup.Key, "CPU", StringComparison.OrdinalIgnoreCase))
                {
                    AddCpuPerformanceGroups(hardwareItem, hardwareGroup);
                }
                else
                {
                    AddReadingRows(hardwareItem, hardwareGroup);
                }

                systemItem.Children.Add(hardwareItem);
            }

            parent.Children.Add(systemItem);
        }

        var graphicsRows = rows
            .Where(IsGpuPerformanceRow)
            .ToList();
        if (graphicsRows.Count > 0)
        {
            var graphicsItem = new ReadingTreeItem { Key = "performance|graphics", Text = T("group.Graphics", "Graphics") };
            AddHardwareGroups(graphicsItem, graphicsRows);
            parent.Children.Add(graphicsItem);
        }

        var printerRows = rows
            .Where(IsPrinterPerformanceRow)
            .ToList();
        if (printerRows.Count > 0)
        {
            var printerItem = new ReadingTreeItem { Key = "performance|printers", Text = T("group.Printers", "Printers") };
            AddPrinterGroups(printerItem, printerRows);
            parent.Children.Add(printerItem);
        }

        var storageRows = rows
            .Where(r => !IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware) && !IsDataSourceSummaryRow(r) && !IsGpuPerformanceRow(r) && !IsPrinterPerformanceRow(r))
            .ToList();
        if (storageRows.Count > 0)
        {
            var storageItem = new ReadingTreeItem { Key = "performance|storage", Text = T("group.Storage", "Storage") };
            AddHardwareGroups(storageItem, storageRows);
            parent.Children.Add(storageItem);
        }
    }

    private static bool IsDataSourceSummaryRow(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(row.Identifier) &&
            row.Identifier.StartsWith("data-source/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hardware = ShortHardwareName(row.Hardware);
        return string.Equals(hardware, T("ui.Data sources", "Data sources"), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware, "Data sources", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPrinterGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var printerGroups = rows
            .GroupBy(PrinterGroupName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issueGroups = printerGroups
            .Where(g => PrinterHasIssue(g))
            .ToList();
        if (issueGroups.Count > 0)
        {
            var issuesItem = new ReadingTreeItem
            {
                Key = "performance|printer-issues",
                Text = T("group.Printer issues", "Printer issues")
            };
            foreach (var issueGroup in issueGroups)
            {
                AddPrinterGroup(issuesItem, issueGroup, true);
            }
            parent.Children.Add(issuesItem);
        }

        foreach (var printerGroup in printerGroups)
        {
            AddPrinterGroup(parent, printerGroup, false);
        }
    }

    private static void AddPrinterGroup(ReadingTreeItem parent, IGrouping<string, SensorRow> printerGroup, bool issueSummaryOnly)
    {
        var printerName = printerGroup.Key;
        var printerItem = new ReadingTreeItem
        {
            Key = (issueSummaryOnly ? "performance|printer-issue|" : "performance|printer|") + StableDeviceInventoryKey(printerName),
            Text = printerName
        };
        AddReadingRows(printerItem, issueSummaryOnly ? PrinterIssueRows(printerGroup) : printerGroup);
        parent.Children.Add(printerItem);
    }

    private static IEnumerable<SensorRow> PrinterIssueRows(IEnumerable<SensorRow> rows)
    {
        return (rows ?? Enumerable.Empty<SensorRow>())
            .Where(IsPrinterIssueRow)
            .ToList();
    }

    private static bool PrinterHasIssue(IEnumerable<SensorRow> rows)
    {
        return (rows ?? Enumerable.Empty<SensorRow>()).Any(IsPrinterIssueRow);
    }

    private static bool IsPrinterIssueRow(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        var name = CleanSensorName(row.Name);
        var value = (row.DisplayValue ?? "").Trim();
        if (name.Equals("Offline", StringComparison.OrdinalIgnoreCase))
        {
            return value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        if (name.Equals("Jobs queued", StringComparison.OrdinalIgnoreCase))
        {
            double count;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out count) && count > 0;
        }

        if (name.Equals("Error state", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Other", StringComparison.OrdinalIgnoreCase);
        }

        if (name.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Extended status", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !value.Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Other", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsPrinterPerformanceRow(SensorRow row)
    {
        return row != null && string.Equals(row.Type, "Performance", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(row.Hardware, "Printers", StringComparison.OrdinalIgnoreCase) ||
            (row.Hardware ?? "").StartsWith("Printer: ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGpuPerformanceRow(SensorRow row)
    {
        if (row == null || !string.Equals(row.Type, "Performance", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(row.Hardware, "GPU", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Hardware, "GPU memory", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrinterGroupName(SensorRow row)
    {
        var name = DetailValue(row, "Printer name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var hardware = row == null ? "" : row.Hardware ?? "";
        if (hardware.StartsWith("Printer: ", StringComparison.OrdinalIgnoreCase))
        {
            return hardware.Substring("Printer: ".Length).Trim();
        }

        return string.IsNullOrWhiteSpace(hardware)
            ? T("group.Printers", "Printers")
            : ShortHardwareName(hardware);
    }

    private static void AddOverviewGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var rowList = (rows ?? Enumerable.Empty<SensorRow>()).ToList();
        AddOverviewGroup(parent, rowList, "overview|system", T("group.System", "System"), IsOverviewSystemRow);
        AddOverviewGroup(parent, rowList, "overview|windows", T("group.Windows", "Windows"), IsOverviewWindowsRow);
        AddOverviewGroup(parent, rowList, "overview|firmware-board", T("group.Firmware and board", "Firmware and board"), IsOverviewFirmwareBoardRow);
        AddOverviewGroup(parent, rowList, "overview|graphics", T("group.Graphics", "Graphics"), IsOverviewGraphicsRow);
        AddOverviewGroup(parent, rowList, "overview|printer-summary", T("group.Printer summary", "Printer summary"), IsOverviewPrinterSummaryRow);
        AddOverviewGroup(parent, rowList, "overview|accessibility", T("group.Accessibility", "Accessibility"), IsOverviewAccessibilityRow);

        var grouped = new HashSet<SensorRow>(rowList.Where(r =>
            IsOverviewSystemRow(r) ||
            IsOverviewWindowsRow(r) ||
            IsOverviewFirmwareBoardRow(r) ||
            IsOverviewGraphicsRow(r) ||
            IsOverviewPrinterSummaryRow(r) ||
            IsOverviewAccessibilityRow(r)));
        var otherRows = rowList.Where(r => !grouped.Contains(r)).ToList();
        if (otherRows.Count > 0)
        {
            AddOverviewGroup(parent, otherRows, "overview|other", T("group.Other", "Other"), r => true);
        }
    }

    private static void AddOverviewGroup(ReadingTreeItem parent, IEnumerable<SensorRow> rows, string key, string text, Func<SensorRow, bool> predicate)
    {
        var groupRows = rows.Where(predicate).ToList();
        if (groupRows.Count == 0)
        {
            return;
        }

        var item = new ReadingTreeItem { Key = key, Text = text };
        AddReadingRows(item, groupRows);
        parent.Children.Add(item);
    }

    private static bool IsOverviewSystemRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.Equals("System uptime", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System model", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewWindowsRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Secure Boot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewFirmwareBoardRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.StartsWith("BIOS ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Baseboard ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SMBIOS version", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Embedded controller version", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewGraphicsRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.IndexOf("adapter RAM", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.EndsWith(" BIOS", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("graphics BIOS", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("driver date", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("driver version", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsOverviewPrinterSummaryRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.Equals("Printer count", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Default printer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewAccessibilityRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.Equals("Screen reader output", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Detected screen readers", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("High contrast", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Sticky Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Toggle Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Filter Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Show sounds", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Audio descriptions", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTemperatureGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        AddTypedHardwareGroup(parent, rows, "temperature|system", T("group.System", "System"), IsSystemTemperatureRow);
        AddTypedHardwareGroup(parent, rows, "temperature|graphics", T("group.Graphics", "Graphics"), IsGraphicsTemperatureRow);
        AddTypedHardwareGroup(parent, rows, "temperature|storage", T("group.Storage", "Storage"), IsStorageTemperatureRow);

        var grouped = new HashSet<SensorRow>(rows.Where(r => IsSystemTemperatureRow(r) || IsGraphicsTemperatureRow(r) || IsStorageTemperatureRow(r)));
        var otherRows = rows.Where(r => !grouped.Contains(r)).ToList();
        if (otherRows.Count > 0)
        {
            AddTypedHardwareGroup(parent, otherRows, "temperature|other", T("group.Other", "Other"), r => true);
        }
    }

    private static void AddTypedHardwareGroup(ReadingTreeItem parent, IEnumerable<SensorRow> rows, string key, string text, Func<SensorRow, bool> predicate)
    {
        var groupRows = rows.Where(predicate).ToList();
        if (groupRows.Count == 0)
        {
            return;
        }

        var item = new ReadingTreeItem { Key = key, Text = text };
        AddHardwareGroups(item, groupRows);
        parent.Children.Add(item);
    }

    private static bool IsSystemTemperatureRow(SensorRow row)
    {
        var hardware = ShortHardwareName(row == null ? "" : row.Hardware);
        return IsCpuHardwareName(hardware) ||
            hardware.IndexOf("motherboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("system", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("chipset", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGraphicsTemperatureRow(SensorRow row)
    {
        var hardware = ShortHardwareName(row == null ? "" : row.Hardware);
        return hardware.IndexOf("gpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("geforce", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("intel graphics", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsStorageTemperatureRow(SensorRow row)
    {
        var hardware = ShortHardwareName(row == null ? "" : row.Hardware);
        return hardware.IndexOf("ssd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("hdd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("samsung", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("seagate", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("western digital", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("wd ", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("crucial", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("micron", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("sandisk", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("kingston", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("toshiba", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("m371", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("ct", StringComparison.OrdinalIgnoreCase) == 0;
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

    private static void AddAudioGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var deviceGroup in rows
            .GroupBy(r => AudioDeviceGroupName(r))
            .OrderBy(g => AudioDeviceSortIndex(g.Key))
            .ThenBy(g => g.Key))
        {
            var deviceItem = new ReadingTreeItem
            {
                Key = "audio|device|" + deviceGroup.Key,
                Text = deviceGroup.Key
            };

            var deviceRows = deviceGroup
                .Where(r => string.Equals(CleanSensorName(r.Name), "Device", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (deviceRows.Count > 0)
            {
                AddReadingRows(deviceItem, deviceRows);
            }

            AddAudioDirectionGroup(deviceItem, deviceGroup, "Playback");
            AddAudioDirectionGroup(deviceItem, deviceGroup, "Recording");

            var otherRows = deviceGroup
                .Where(r => !string.Equals(CleanSensorName(r.Name), "Device", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(AudioEndpointDirection(r), "Playback", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(AudioEndpointDirection(r), "Recording", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (otherRows.Count > 0)
            {
                var otherItem = new ReadingTreeItem { Key = "audio|device|" + deviceGroup.Key + "|other", Text = T("group.Other", "Other") };
                AddReadingRows(otherItem, otherRows);
                deviceItem.Children.Add(otherItem);
            }

            parent.Children.Add(deviceItem);
        }
    }

    private static void AddAudioDirectionGroup(ReadingTreeItem deviceItem, IEnumerable<SensorRow> rows, string direction)
    {
        var directionRows = rows
            .Where(r => string.Equals(AudioEndpointDirection(r), direction, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => AudioEndpointSortIndex(r))
            .ThenBy(r => AudioEndpointName(r))
            .ToList();
        if (directionRows.Count == 0)
        {
            return;
        }

        var directionItem = new ReadingTreeItem
        {
            Key = "audio|device|" + deviceItem.Text + "|" + direction.ToLowerInvariant(),
            Text = T("group.Audio " + direction.ToLowerInvariant(), direction)
        };
        AddReadingRows(directionItem, directionRows);
        deviceItem.Children.Add(directionItem);
    }

    private static void AddDeviceInventoryGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var group in rows
            .GroupBy(r => ShortHardwareName(r.Hardware))
            .OrderBy(g => DeviceInventoryGroupSortIndex(g.Key))
            .ThenBy(g => g.Key))
        {
            var groupItem = new ReadingTreeItem
            {
                Key = "devices|group|" + group.Key,
                Text = group.Key
            };

            foreach (var deviceGroup in group
                .GroupBy(DeviceInventoryDeviceKey)
                .OrderBy(g => DeviceInventoryDeviceName(g)))
            {
                var deviceRow = DeviceInventorySummaryRow(deviceGroup);
                var deviceName = DeviceInventoryDeviceName(deviceRow);
                var deviceItem = new ReadingTreeItem
                {
                    Key = "devices|device|" + group.Key + "|" + deviceGroup.Key,
                    Text = deviceName,
                    Row = deviceRow
                };
                groupItem.Children.Add(deviceItem);
            }

            parent.Children.Add(groupItem);
        }
    }

    private static string DeviceInventoryDeviceName(SensorRow row)
    {
        if (row != null && row.Details != null)
        {
            string value;
            if (row.Details.TryGetValue("Device name", out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return row == null || string.IsNullOrWhiteSpace(row.Hardware) ? T("group.Other", "Other") : ShortHardwareName(row.Hardware);
    }

    private static string DeviceInventoryDeviceName(IEnumerable<SensorRow> rows)
    {
        var list = (rows ?? Enumerable.Empty<SensorRow>()).Where(r => r != null).ToList();
        foreach (var row in list)
        {
            var name = DeviceInventoryDeviceName(row);
            if (!string.IsNullOrWhiteSpace(name) && name != ShortHardwareName(row.Hardware))
            {
                return name;
            }
        }

        var first = list.FirstOrDefault();
        return first == null ? T("group.Other", "Other") : DeviceInventoryDeviceName(first);
    }

    private static string DeviceInventoryDeviceKey(SensorRow row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.Identifier))
        {
            return "";
        }

        if (row.Identifier.StartsWith("device/", StringComparison.OrdinalIgnoreCase) &&
            row.Identifier.IndexOf('/', "device/".Length) < 0)
        {
            return row.Identifier;
        }

        var lastSeparator = row.Identifier.LastIndexOf('/');
        return lastSeparator > 0 ? row.Identifier.Substring(0, lastSeparator) : row.Identifier;
    }

    private static SensorRow DeviceInventorySummaryRow(IEnumerable<SensorRow> rows)
    {
        var list = (rows ?? Enumerable.Empty<SensorRow>()).Where(r => r != null).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count == 1 && list[0].Details != null && list[0].Details.Count > 0)
        {
            return list[0];
        }

        var first = list[0];
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in list)
        {
            if (row.Details != null)
            {
                foreach (var detail in row.Details)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && !string.IsNullOrWhiteSpace(detail.Value) && !details.ContainsKey(detail.Key))
                    {
                        details[detail.Key] = detail.Value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(row.DisplayValue) && !details.ContainsKey(row.Name))
            {
                details[row.Name] = row.DisplayValue;
            }
        }

        var deviceName = DeviceInventoryDeviceName(list);
        return new SensorRow
        {
            Type = "Devices",
            Hardware = first.Hardware,
            Name = deviceName,
            Identifier = DeviceInventoryDeviceKey(first),
            DisplayValue = first.DisplayValue,
            Source = first.Source,
            Details = details
        };
    }

    private static int DeviceInventoryGroupSortIndex(string group)
    {
        if (DeviceInventoryGroupEquals(group, "group.Device nonworking", "Non-working devices")) return 0;
        if (DeviceInventoryGroupEquals(group, "group.Device PCI and system", "PCI and system devices")) return 1;
        if (DeviceInventoryGroupEquals(group, "group.Device storage", "Storage devices and controllers")) return 2;
        if (DeviceInventoryGroupEquals(group, "group.Device USB", "USB devices and controllers")) return 3;
        if (DeviceInventoryGroupEquals(group, "group.Device network", "Network devices")) return 4;
        if (DeviceInventoryGroupEquals(group, "group.Device audio", "Audio devices")) return 5;
        if (DeviceInventoryGroupEquals(group, "group.Device display", "Display devices")) return 6;
        if (DeviceInventoryGroupEquals(group, "group.Device input", "Input devices")) return 7;
        if (DeviceInventoryGroupEquals(group, "group.Device bluetooth", "Bluetooth")) return 8;
        if (DeviceInventoryGroupEquals(group, "group.Device imaging", "Cameras and imaging")) return 9;
        if (DeviceInventoryGroupEquals(group, "group.Device printers", "Printers")) return 10;
        if (DeviceInventoryGroupEquals(group, "group.Device security", "Security devices")) return 11;
        return 99;
    }

    private static bool DeviceInventoryGroupEquals(string group, string key, string fallback)
    {
        return string.Equals(group, fallback, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(group, T(key, fallback), StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCpuPerformanceGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        AddNamedReadingGroup(parent, rows, "cpu|identity", T("group.CPU identity", "Identity"), new[]
        {
            "CPU name",
            "CPU vendor",
            "CPU architecture",
            "CPU socket",
            "CPU processor ID"
        });
        AddNamedReadingGroup(parent, rows, "cpu|cores-clocks", T("group.CPU cores and clocks", "Cores and clocks"), new[]
        {
            "CPU usage",
            "CPU cores",
            "CPU threads",
            "CPU current clock",
            "CPU max clock"
        });
        AddNamedReadingGroup(parent, rows, "cpu|features", T("group.CPU features", "Features"), new[]
        {
            "CPU instruction sets",
            "CPU virtualization extensions",
            "CPU virtualization enabled in firmware",
            "CPU hardware VM memory translation (SLAT/EPT/NPT)",
            "CPU data execution prevention"
        });
        AddNamedReadingGroup(parent, rows, "cpu|cache", T("group.CPU cache", "Cache"), new[]
        {
            "CPU L1 cache",
            "CPU L2 cache",
            "CPU L3 cache"
        });

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CPU name",
            "CPU vendor",
            "CPU architecture",
            "CPU socket",
            "CPU processor ID",
            "CPU usage",
            "CPU cores",
            "CPU threads",
            "CPU current clock",
            "CPU max clock",
            "CPU instruction sets",
            "CPU virtualization extensions",
            "CPU virtualization enabled in firmware",
            "CPU hardware VM memory translation (SLAT/EPT/NPT)",
            "CPU data execution prevention",
            "CPU L1 cache",
            "CPU L2 cache",
            "CPU L3 cache"
        };

        var remaining = rows.Where(r => !handled.Contains(CleanSensorName(r.Name))).ToList();
        if (remaining.Count > 0)
        {
            AddNamedReadingGroup(parent, remaining, "cpu|other", T("group.Other", "Other"), remaining.Select(r => CleanSensorName(r.Name)).ToArray());
        }
    }

    private static void AddNamedReadingGroup(ReadingTreeItem parent, IEnumerable<SensorRow> rows, string key, string text, string[] names)
    {
        var wanted = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
        var groupRows = rows.Where(r => wanted.Contains(CleanSensorName(r.Name))).ToList();
        if (groupRows.Count == 0)
        {
            return;
        }

        var item = new ReadingTreeItem { Key = key, Text = text };
        AddReadingRows(item, groupRows);
        parent.Children.Add(item);
    }

    private static string AudioDeviceGroupName(SensorRow row)
    {
        if (row == null)
        {
            return T("group.Audio devices", "Audio devices");
        }

        var deviceName = DetailValue(row, "Device name");
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            return deviceName;
        }

        var hardware = ShortHardwareName(row.Hardware);
        return string.IsNullOrWhiteSpace(hardware) ? T("group.Audio devices", "Audio devices") : hardware;
    }

    private static int AudioDeviceSortIndex(string name)
    {
        return string.Equals(name, T("group.Audio devices", "Audio devices"), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static string AudioEndpointDirection(SensorRow row)
    {
        return DetailValue(row, "Endpoint direction");
    }

    private static string AudioEndpointName(SensorRow row)
    {
        var name = DetailValue(row, "Name");
        return string.IsNullOrWhiteSpace(name) ? ShortHardwareName(row == null ? "" : row.Hardware) : name;
    }

    private static int AudioEndpointSortIndex(SensorRow row)
    {
        var name = AudioEndpointName(row);
        if (name.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("speaker", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 0;
        }

        if (name.IndexOf("spdif", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("s/pdif", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 2;
        }

        return 1;
    }

    private static string DetailValue(SensorRow row, string key)
    {
        if (row == null || row.Details == null || string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        string value;
        return row.Details.TryGetValue(key, out value) ? value ?? "" : "";
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
                Text = IsDeviceSummaryType(row.Type) ? ShortHardwareName(row.Hardware) + ": " + FormatValue(row) : DisplayReadingName(row.Name) + ": " + FormatValue(row),
                Row = row
            });
        }
    }

    private static bool IsDeviceSummaryType(string type)
    {
        return string.Equals(type, "USB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Display", StringComparison.OrdinalIgnoreCase);
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
        var node = new TreeNode(item.Text) { Name = item.Key, Tag = item.Row, ToolTipText = GetTreeNodeDetailsHint(item) };
        foreach (var child in item.Children)
        {
            node.Nodes.Add(CreateTreeNode(child));
        }

        return node;
    }

    private static string GetTreeNodeDetailsHint(ReadingTreeItem item)
    {
        return item != null && item.Row != null && item.Row.Details != null && item.Row.Details.Count > 0
            ? T("a11y.Has details", "Has Details.")
            : "";
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

        if (type == "Audio")
        {
            return 7;
        }

        if (type == "Display")
        {
            return 8;
        }

        if (type == "Devices")
        {
            return 9;
        }

        if (type == "Battery")
        {
            return 4;
        }

        return 10;
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

        if (type == "Audio")
        {
            return T("type.Audio", "Audio");
        }

        if (type == "Display")
        {
            return T("type.Display", "Display");
        }

        if (type == "Devices")
        {
            return T("type.Devices", "Devices");
        }

        return string.IsNullOrWhiteSpace(type) ? T("type.Readings", "Readings") : type;
    }

    public static int ReadingSortIndex(string name)
    {
        var clean = CleanSensorName(name);
        if (clean.Equals("System uptime", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Model", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU usage", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("CPU name", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("CPU vendor", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU architecture", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU socket", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("CPU processor ID", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("CPU cores", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU threads", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU current clock", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("CPU max clock", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("CPU instruction sets", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("CPU virtualization extensions", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("CPU virtualization enabled in firmware", StringComparison.OrdinalIgnoreCase)) return 2;
        if (clean.Equals("CPU hardware VM memory translation (SLAT/EPT/NPT)", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("CPU data execution prevention", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("BIOS vendor", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("BIOS version", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("BIOS date", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Health", StringComparison.OrdinalIgnoreCase)) return 0;
        if (clean.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) return 1;
        if (clean.Equals("Memory total", StringComparison.OrdinalIgnoreCase)) return 3;
        if (clean.Equals("Memory used", StringComparison.OrdinalIgnoreCase)) return 4;
        if (clean.Equals("Memory used size", StringComparison.OrdinalIgnoreCase)) return 5;
        if (clean.Equals("Memory available", StringComparison.OrdinalIgnoreCase)) return 6;
        if (clean.Equals("Paging file total", StringComparison.OrdinalIgnoreCase)) return 7;
        if (clean.Equals("Paging file used", StringComparison.OrdinalIgnoreCase)) return 8;
        if (clean.Equals("Paging file free", StringComparison.OrdinalIgnoreCase)) return 9;
        if (clean.Equals("Data read", StringComparison.OrdinalIgnoreCase)) return 10;
        if (clean.Equals("Data written", StringComparison.OrdinalIgnoreCase)) return 11;
        if (clean.Equals("Read rate", StringComparison.OrdinalIgnoreCase)) return 12;
        if (clean.Equals("Write rate", StringComparison.OrdinalIgnoreCase)) return 13;
        if (clean.Equals("Read activity", StringComparison.OrdinalIgnoreCase)) return 14;
        if (clean.Equals("Write activity", StringComparison.OrdinalIgnoreCase)) return 15;
        if (clean.Equals("Total activity", StringComparison.OrdinalIgnoreCase)) return 16;
        if (clean.Equals("Total space", StringComparison.OrdinalIgnoreCase)) return 20;
        if (clean.Equals("Used space", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Space used", StringComparison.OrdinalIgnoreCase)) return 21;
        if (clean.Equals("Free space", StringComparison.OrdinalIgnoreCase)) return 22;
        if (clean.Equals("Size", StringComparison.OrdinalIgnoreCase)) return 23;
        if (clean.Equals("Status", StringComparison.OrdinalIgnoreCase)) return 30;
        if (clean.Equals("IP address", StringComparison.OrdinalIgnoreCase)) return 31;
        if (clean.Equals("Link speed", StringComparison.OrdinalIgnoreCase)) return 32;
        if (clean.Equals("Receive rate", StringComparison.OrdinalIgnoreCase)) return 33;
        if (clean.Equals("Send rate", StringComparison.OrdinalIgnoreCase)) return 34;
        if (clean.Equals("Data received", StringComparison.OrdinalIgnoreCase)) return 35;
        if (clean.Equals("Data sent", StringComparison.OrdinalIgnoreCase)) return 36;
        if (clean.Equals("Wi-Fi network", StringComparison.OrdinalIgnoreCase)) return 37;
        if (clean.Equals("Wi-Fi signal strength", StringComparison.OrdinalIgnoreCase)) return 38;
        if (clean.Equals("Wi-Fi signal RSSI", StringComparison.OrdinalIgnoreCase)) return 39;
        if (clean.Equals("Wi-Fi channel", StringComparison.OrdinalIgnoreCase)) return 40;
        if (clean.Equals("Wi-Fi frequency", StringComparison.OrdinalIgnoreCase)) return 41;
        if (clean.Equals("Wi-Fi radio type", StringComparison.OrdinalIgnoreCase)) return 42;
        if (clean.Equals("Wi-Fi receive link speed", StringComparison.OrdinalIgnoreCase)) return 43;
        if (clean.Equals("Wi-Fi transmit link speed", StringComparison.OrdinalIgnoreCase)) return 44;
        if (clean.Equals("Wi-Fi security", StringComparison.OrdinalIgnoreCase)) return 45;
        if (clean.Equals("Wi-Fi authentication", StringComparison.OrdinalIgnoreCase)) return 46;
        if (clean.Equals("Wi-Fi cipher", StringComparison.OrdinalIgnoreCase)) return 47;
        if (clean.Equals("Device", StringComparison.OrdinalIgnoreCase)) return 60;
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
            .Replace("Uptime", "System uptime")
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
