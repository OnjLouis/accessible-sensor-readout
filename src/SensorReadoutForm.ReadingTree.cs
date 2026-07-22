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

    private IEnumerable<DeviceFilter> BuildFilters(List<SensorRow> rows)
    {
        var hidden = new HashSet<string>(settings == null ? new List<string>() : settings.HiddenCategoryKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var available = DefaultCategoryChoices()
            .ToDictionary(c => c.Key, c => c, StringComparer.OrdinalIgnoreCase);
        var order = settings == null || settings.CategoryOrderKeys == null || settings.CategoryOrderKeys.Count == 0
            ? DefaultCategoryChoices().Select(c => c.Key).ToList()
            : settings.CategoryOrderKeys.Concat(DefaultCategoryChoices().Select(c => c.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var yielded = false;
        foreach (var key in order)
        {
            CategoryChoice choice;
            if (!available.TryGetValue(key, out choice) || hidden.Contains(key))
            {
                continue;
            }

            yielded = true;
            yield return new DeviceFilter { Key = choice.Key, DisplayName = DisplayTypeName(choice.Type), Type = choice.Type };
        }

        if (!yielded && available.ContainsKey("type|Performance"))
        {
            yield return new DeviceFilter { Key = "type|Performance", DisplayName = DisplayTypeName("Performance"), Type = "Performance" };
        }
    }

    public static List<CategoryChoice> DefaultCategoryChoices()
    {
        return new List<CategoryChoice>
        {
            new CategoryChoice { Key = "type|Performance", DisplayName = T("type.Performance", "Performance/Overview"), Type = "Performance" },
            new CategoryChoice { Key = "type|Temperature", DisplayName = T("type.Temperature", "Temperatures"), Type = "Temperature" },
            new CategoryChoice { Key = "type|Fan", DisplayName = T("type.Fan", "Fans"), Type = "Fan" },
            new CategoryChoice { Key = "type|SMART", DisplayName = T("type.SMART", "SMART"), Type = "SMART" },
            new CategoryChoice { Key = "type|Network", DisplayName = T("type.Network", "Network"), Type = "Network" },
            new CategoryChoice { Key = "type|Bluetooth", DisplayName = T("type.Bluetooth", "Bluetooth"), Type = "Bluetooth" },
            new CategoryChoice { Key = "type|Tasks", DisplayName = T("type.Tasks", "Tasks"), Type = "Tasks" },
            new CategoryChoice { Key = "type|Spoken Hotkeys", DisplayName = T("type.Spoken Hotkeys", "Spoken Hotkeys"), Type = "Spoken Hotkeys" },
            new CategoryChoice { Key = "type|USB", DisplayName = T("type.USB", "USB"), Type = "USB" },
            new CategoryChoice { Key = "type|Audio", DisplayName = T("type.Audio", "Audio"), Type = "Audio" },
            new CategoryChoice { Key = "type|Display", DisplayName = T("type.Display", "Display"), Type = "Display" },
            new CategoryChoice { Key = "type|Battery", DisplayName = T("type.Battery", "Battery"), Type = "Battery" },
            new CategoryChoice { Key = "type|Devices", DisplayName = T("type.Devices", "Devices"), Type = "Devices" },
            new CategoryChoice { Key = "type|Firmware Security", DisplayName = T("type.Firmware Security", "Firmware Security"), Type = "Firmware Security" }
        };
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
        var rows = ApplyFilter(latestRows, filter).ToList();
        if (filter == null || !string.Equals(filter.Type, "Spoken Hotkeys", StringComparison.OrdinalIgnoreCase))
        {
            rows = rows
                .OrderBy(r => TypeSortIndex(r.Type))
                .ThenBy(r => ShortHardwareName(r.Hardware))
                .ThenBy(r => CleanSensorName(r.Name))
                .ToList();
        }

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
        var previousSuppressExpansionTracking = suppressReadingTreeExpansionTracking;
        suppressReadingTreeExpansionTracking = true;
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
            suppressReadingTreeExpansionTracking = previousSuppressExpansionTracking;
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
                selectedMeterProgressBar.SetVisualState(MeterProgressBar.NormalState);
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
            selectedMeterProgressBar.SetVisualState(MeterVisualState(row, percent));
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

                node.Tag = item.Row;

                if (textChanged || !string.Equals(node.ToolTipText, GetTreeNodeDetailsHint(item), StringComparison.Ordinal))
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
        if (row == null || !HasDetailsOrWindowsSetting(row))
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
        if (row == null || !HasDetailsOrWindowsSetting(row))
        {
            pendingDetailsAvailabilityAnnouncementKey = "";
            return;
        }

        lastDetailsAvailabilityAnnouncementKey = currentKey;
        lastDetailsAvailabilityAnnouncementUtc = DateTime.UtcNow;
        pendingDetailsAvailabilityAnnouncementKey = "";

        string error;
        var message = GetTreeNodeDetailsHint(new ReadingTreeItem { Row = row });
        if (!ScreenReaderOutput.TrySpeakPoliteForActiveScreenReader(message, out error))
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
        var previousSuppressExpansionTracking = suppressReadingTreeExpansionTracking;
        suppressReadingTreeExpansionTracking = true;
        try
        {
            ApplyExpandedNodeKeys(nodes, expandedKeys, expandAll);
        }
        finally
        {
            suppressReadingTreeExpansionTracking = previousSuppressExpansionTracking;
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

            if (filter.Type == "Spoken Hotkeys")
            {
                AddSpokenHotKeyGroups(typeItem, rows);
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

        var emptyLead = T("message.noDataCurrentlyAvailableForCategory", "No data currently available for this category.");
        var hideHint = " " + T("message.hideEmptyCategoryHint", "You can hide this category from Preferences if you do not want it in the category list.");
        type = type ?? "";
        if (type.Equals("Fan", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noFanReadingsYet", "Some systems need LibreHardwareMonitor, PawnIO, or a laptop Plug-In, and some hardware does not expose fan sensors to Windows.") + hideHint;
        }
        if (type.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noTemperatureReadingsYet", "Sensor Readout will show temperature readings when LibreHardwareMonitor, Core Temp, Windows, or an enabled hardware Plug-In exposes them.") + hideHint;
        }
        if (type.Equals("SMART", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noSmartReadingsYet", "Some drives, USB bridges, RAID controllers, or Windows storage drivers hide SMART data.") + hideHint;
        }
        if (type.Equals("Network", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noNetworkReadingsYet", "Check that Windows exposes an active network adapter; Wi-Fi details require a Wi-Fi adapter and Windows WLAN data.") + hideHint;
        }
        if (type.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noBluetoothReadingsYet", "Check that Windows exposes a Bluetooth radio or paired Bluetooth devices.") + hideHint;
        }
        if (type.Equals("USB", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noUsbReadingsYet", "Press F5 after plugging or unplugging hardware so Sensor Readout can rebuild the USB inventory.") + hideHint;
        }
        if (type.Equals("Battery", StringComparison.OrdinalIgnoreCase))
        {
            return emptyLead + " " + T("message.noBatteryReadingsYet", "Desktop systems and some battery drivers do not expose battery data to Windows.") + hideHint;
        }
        return emptyLead + " " + T("message.refreshingInBackground", "Readings will appear here as the background refresh completes.") + hideHint;
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

        if (copy.Row == null && copy.Children.Count == 0 && !string.Equals(copy.Key, "empty", StringComparison.Ordinal))
        {
            return null;
        }

        return copy;
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
        if (item == null || item.Row == null)
        {
            return "";
        }

        var hasDetails = item.Row.Details != null && item.Row.Details.Count > 0;
        var relatedTarget = GetRelatedWindowsSettingsTarget(item.Row);
        var hasRelatedTarget = relatedTarget != null;
        var hasFileLocation = relatedTarget != null && !string.IsNullOrWhiteSpace(relatedTarget.FilePath);
        if (hasDetails && hasFileLocation)
        {
            return T("a11y.Has details and file location", "Has Details. Has file location.");
        }

        if (hasDetails && hasRelatedTarget)
        {
            return T("a11y.Has details and Windows setting", "Has Details. Has Windows setting.");
        }

        if (hasDetails)
        {
            return T("a11y.Has details", "Has Details.");
        }

        if (hasFileLocation)
        {
            return T("a11y.Has file location", "Has file location.");
        }

        return hasRelatedTarget
            ? T("a11y.Has Windows setting", "Has Windows setting.")
            : "";
    }

    private static bool HasDetailsOrWindowsSetting(SensorRow row)
    {
        return row != null &&
            ((row.Details != null && row.Details.Count > 0) ||
             GetRelatedWindowsSettingsTarget(row) != null);
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

}
