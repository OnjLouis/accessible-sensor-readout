using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class PreferencesForm : Form
{
    private void AddSelectedTrayChoice()
    {
        var item = trayAvailableList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus("Select an available reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        if (traySelectedList.Items.Count >= SensorReadoutForm.MaxTrayStatusReadings)
        {
            SetTraySelectionStatus("The notification area can show up to eight readings.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = trayAvailableList.SelectedIndex;
        trayAvailableList.Items.Remove(item);
        item.ShowSpeechPreview = true;
        traySelectedList.Items.Add(item);
        traySelectedList.SelectedItem = item;
        if (trayAvailableList.Items.Count > 0)
        {
            trayAvailableList.SelectedIndex = Math.Max(0, Math.Min(index, trayAvailableList.Items.Count - 1));
        }
        SetTraySelectionStatus("Added " + item + " to tray order.");
        SaveLivePreferences();
    }

    private void RemoveSelectedTrayChoice()
    {
        var item = traySelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus("Select a tray reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var index = traySelectedList.SelectedIndex;
        traySelectedList.Items.Remove(item);
        item.ShowSpeechPreview = false;
        AddAvailableTrayChoiceSorted(item);
        if (traySelectedList.Items.Count > 0)
        {
            traySelectedList.SelectedIndex = Math.Max(0, Math.Min(index, traySelectedList.Items.Count - 1));
        }
        trayAvailableList.SelectedItem = item;
        SetTraySelectionStatus("Removed " + item + " from tray order.");
        SaveLivePreferences();
    }

    private void MoveSelectedTrayChoice(int direction)
    {
        var index = traySelectedList.SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= traySelectedList.Items.Count)
        {
            SetTraySelectionStatus("Cannot move the selected tray reading further.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var item = traySelectedList.Items[index];
        traySelectedList.Items.RemoveAt(index);
        traySelectedList.Items.Insert(target, item);
        traySelectedList.SelectedIndex = target;
        SetTraySelectionStatus("Moved " + item + (direction < 0 ? " up." : " down."));
        SaveLivePreferences();
    }

    private void TrayAvailableListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F3)
        {
            ShowPreferenceListSearch(trayAvailableList, SensorReadoutForm.L("ui.Find reading", "Find reading"));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Right)
        {
            AddSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void TraySelectedListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelectedTrayChoice(false);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.X)
        {
            CopySelectedTrayChoice(true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.V)
        {
            PasteTrayChoices();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F2)
        {
            RenameSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Left)
        {
            RemoveSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelectedTrayChoice();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveSelectedTrayChoice(-1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveSelectedTrayChoice(1);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void RenameSelectedTrayChoice()
    {
        var item = traySelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus("Select a tray reading first.");
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        RenameSpeechLabel(item, SetTraySelectionStatus);
    }

    private void ResetSelectedTrayChoiceLabel()
    {
        var item = traySelectedList.SelectedItem as TrayItemChoice;
        if (item == null)
        {
            SetTraySelectionStatus(SensorReadoutForm.L("status.Select a tray reading first.", "Select a tray reading first."));
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        readingSpeechLabels.Remove(item.Key);
        RefreshSpeechPreviewLists();
        SaveLivePreferences();
        SetTraySelectionStatus(SensorReadoutForm.L("ui.Reset spoken label for", "Reset spoken label for") + " " + item + ".");
    }

    private void CopySelectedTrayChoice(bool cut)
    {
        var item = traySelectedList == null ? null : traySelectedList.SelectedItem as TrayItemChoice;
        if (item == null || string.IsNullOrWhiteSpace(item.Key))
        {
            SetTraySelectionStatus(SensorReadoutForm.L("status.Select a tray reading first.", "Select a tray reading first."));
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        spokenReadingClipboardKeys.Clear();
        spokenReadingClipboardKeys.Add(item.Key);
        if (cut)
        {
            RemoveSelectedTrayChoice();
            SetTraySelectionStatus(string.Format(SensorReadoutForm.L("status.Cut reading.", "Cut {0}."), item));
        }
        else
        {
            SetTraySelectionStatus(string.Format(SensorReadoutForm.L("status.Copied reading.", "Copied {0}."), item));
        }
    }

    private void PasteTrayChoices()
    {
        if (spokenReadingClipboardKeys.Count == 0)
        {
            SetTraySelectionStatus(SensorReadoutForm.L("status.No copied readings.", "No copied readings."));
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var added = 0;
        TrayItemChoice lastAdded = null;
        foreach (var key in spokenReadingClipboardKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || ContainsTrayChoice(traySelectedList, key))
            {
                continue;
            }

            if (traySelectedList.Items.Count >= SensorReadoutForm.MaxTrayStatusReadings)
            {
                break;
            }

            var item = TakeTrayChoiceByKey(trayAvailableList, key);
            if (item == null)
            {
                var row = RowForKey(key);
                item = row == null
                    ? TrayItemChoice.Unresolved(key, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames)
                    : new TrayItemChoice(row, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames);
            }

            item.ShowSpeechPreview = true;
            traySelectedList.Items.Add(item);
            lastAdded = item;
            added++;
        }

        if (added == 0)
        {
            var message = traySelectedList.Items.Count >= SensorReadoutForm.MaxTrayStatusReadings
                ? SensorReadoutForm.L("status.Tray reading limit reached.", "The notification area can show up to eight readings.")
                : SensorReadoutForm.L("status.Copied readings are already in tray.", "Copied readings are already in the notification area status.");
            SetTraySelectionStatus(message);
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        if (lastAdded != null)
        {
            traySelectedList.SelectedItem = lastAdded;
        }

        SaveLivePreferences();
        SetTraySelectionStatus(string.Format(
            added == 1
                ? SensorReadoutForm.L("status.Pasted one reading into tray.", "Pasted {0} reading into notification area status.")
                : SensorReadoutForm.L("status.Pasted readings into tray.", "Pasted {0} readings into notification area status."),
            added));
    }

    private void ShowPreferenceListSearch(ListBox list, string title)
    {
        if (list == null || list.Items.Count == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }

        var choices = list.Items.Cast<object>().ToList();
        var selected = SensorReadoutForm.ShowSearchDialog(
            this,
            title,
            SensorReadoutForm.L("ui.Search:", "Search:"),
            choices,
            delegate(object item) { return item == null ? "" : item.ToString(); },
            delegate(object item) { return PreferenceListSearchText(item); });
        if (selected == null)
        {
            list.Focus();
            return;
        }

        var index = list.Items.IndexOf(selected);
        if (index >= 0)
        {
            list.SelectedIndex = index;
            list.TopIndex = Math.Max(0, index - 2);
        }

        list.Focus();
    }

    private static string PreferenceListSearchText(object item)
    {
        var trayChoice = item as TrayItemChoice;
        if (trayChoice != null)
        {
            return trayChoice.Type + " " + trayChoice.Hardware + " " + trayChoice.Name + " " + trayChoice.Key;
        }

        var fanChoice = item as FanControlChoice;
        if (fanChoice != null)
        {
            return fanChoice.Hardware + " " + fanChoice.Name + " " + fanChoice.Action + " " + fanChoice.Key;
        }

        return item == null ? "" : item.ToString();
    }

    private void AttachIncrementalListSearch(ListBox list)
    {
        if (list == null)
        {
            return;
        }

        listSearchStates[list] = new ListSearchState();
        list.KeyDown += IncrementalListSearchKeyDown;
    }

    private void AttachListReorderDragDrop(ListBox list, Action saveAfterReorder)
    {
        if (list == null)
        {
            return;
        }

        list.AllowDrop = true;
        var dragIndex = -1;
        var dragStart = Point.Empty;
        list.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                dragIndex = -1;
                return;
            }

            var source = sender as ListBox;
            var index = source == null ? -1 : source.IndexFromPoint(e.Location);
            if (source == null || index < 0 || index >= source.Items.Count)
            {
                return;
            }

            source.SelectedIndex = index;
            dragIndex = index;
            dragStart = e.Location;
        };
        list.MouseMove += delegate(object sender, MouseEventArgs e)
        {
            var source = sender as ListBox;
            if (source == null || dragIndex < 0 || (e.Button & MouseButtons.Left) == 0)
            {
                return;
            }

            var dragSize = SystemInformation.DragSize;
            var dragBounds = new Rectangle(
                dragStart.X - dragSize.Width / 2,
                dragStart.Y - dragSize.Height / 2,
                dragSize.Width,
                dragSize.Height);
            if (dragBounds.Contains(e.Location))
            {
                return;
            }

            var index = dragIndex;
            dragIndex = -1;
            source.DoDragDrop(new ListDragState(source, index), DragDropEffects.Move);
        };
        list.MouseUp += delegate { dragIndex = -1; };
        list.DragEnter += delegate(object sender, DragEventArgs e)
        {
            var state = e.Data == null ? null : e.Data.GetData(typeof(ListDragState)) as ListDragState;
            e.Effect = state != null && state.Source == sender ? DragDropEffects.Move : DragDropEffects.None;
        };
        list.DragDrop += delegate(object sender, DragEventArgs e)
        {
            var targetList = sender as ListBox;
            var state = e.Data == null ? null : e.Data.GetData(typeof(ListDragState)) as ListDragState;
            if (targetList == null || state == null || state.Source != targetList)
            {
                return;
            }

            var point = targetList.PointToClient(new Point(e.X, e.Y));
            var target = targetList.IndexFromPoint(point);
            if (target < 0)
            {
                target = targetList.Items.Count - 1;
            }

            if (MoveListItem(targetList, state.Index, target))
            {
                if (saveAfterReorder != null)
                {
                    saveAfterReorder();
                }
            }
        };
    }

    private static bool MoveListItem(ListBox list, int fromIndex, int toIndex)
    {
        if (list == null || fromIndex < 0 || fromIndex >= list.Items.Count || toIndex < 0 || toIndex >= list.Items.Count || fromIndex == toIndex)
        {
            return false;
        }

        var checkedList = list as CheckedListBox;
        var wasChecked = checkedList != null && checkedList.GetItemChecked(fromIndex);
        var item = list.Items[fromIndex];
        list.Items.RemoveAt(fromIndex);
        list.Items.Insert(toIndex, item);
        if (checkedList != null)
        {
            checkedList.SetItemChecked(toIndex, wasChecked);
        }
        list.SelectedIndex = toIndex;
        return true;
    }

    private sealed class ListDragState
    {
        public readonly ListBox Source;
        public readonly int Index;

        public ListDragState(ListBox source, int index)
        {
            Source = source;
            Index = index;
        }
    }

    private void IncrementalListSearchKeyDown(object sender, KeyEventArgs e)
    {
        var list = sender as ListBox;
        if (list == null || e.Control || e.Alt || list.Items.Count == 0)
        {
            return;
        }

        var searchChar = SearchCharFromKeyEvent(e);
        if (searchChar == '\0')
        {
            return;
        }

        ListSearchState state;
        if (!listSearchStates.TryGetValue(list, out state))
        {
            state = new ListSearchState();
            listSearchStates[list] = state;
        }

        var now = DateTime.UtcNow;
        if ((now - state.LastKey).TotalMilliseconds > 1000)
        {
            state.Text = "";
        }

        state.LastKey = now;
        state.Text += searchChar;
        if (!SelectListSearchMatch(list, state.Text) && state.Text.Length > 1)
        {
            state.Text = searchChar.ToString();
            SelectListSearchMatch(list, state.Text);
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private static char SearchCharFromKeyEvent(KeyEventArgs e)
    {
        if (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
        {
            return char.ToLowerInvariant((char)('a' + (e.KeyCode - Keys.A)));
        }

        if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
        {
            return (char)('0' + (e.KeyCode - Keys.D0));
        }

        if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
        {
            return (char)('0' + (e.KeyCode - Keys.NumPad0));
        }

        if (e.KeyCode == Keys.Space)
        {
            return ' ';
        }

        return '\0';
    }

    private static bool SelectListSearchMatch(ListBox list, string searchText)
    {
        if (list == null || string.IsNullOrWhiteSpace(searchText))
        {
            return false;
        }

        var start = Math.Max(0, list.SelectedIndex);
        var normalizedSearch = NormalizeListSearchText(searchText);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var offset = pass == 0 ? 1 : 0; offset <= list.Items.Count; offset++)
            {
                var index = (start + offset) % list.Items.Count;
                var text = NormalizeListSearchText(Convert.ToString(list.Items[index]));
                if (text.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase) ||
                    text.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    list.SelectedIndex = index;
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeListSearchText(string text)
    {
        return (text ?? "")
            .Replace(" - ", " ")
            .Replace(": ", " ")
            .Replace(":", " ")
            .Trim();
    }

    private void SetTraySelectionStatus(string message)
    {
        if (traySelectionStatusLabel != null)
        {
            traySelectionStatusLabel.Text = SensorReadoutForm.TranslateUiText(message);
        }
    }

    private void UpdateTraySelectionStatus()
    {
        if (traySelectionStatusLabel == null || traySelectedList == null)
        {
            return;
        }

        SetTraySelectionStatus(SensorReadoutForm.L("ui.Tray order has", "Tray order has") + " " + traySelectedList.Items.Count + " " + SensorReadoutForm.L("ui.of 8 readings.", "of 8 readings."));
    }

    public void UpdateSensorRows(List<SensorRow> latestRows)
    {
        if (latestRows == null)
        {
            return;
        }

        var newRows = latestRows
            .Where(SensorReadoutForm.IsSelectableReadoutRow)
            .OrderBy(r => SensorReadoutForm.TypeSortIndex(r.Type))
            .ThenBy(r => r.Hardware)
            .ThenBy(r => r.Name)
            .ToList();
        var newSignature = BuildRowsSignature(newRows);
        latestSensorRows.Clear();
        latestSensorRows.AddRange(latestRows.Where(r => r != null));
        var newFanControlRows = BuildFanProfileFanControlRows(latestRows);
        var newFanControlSignature = BuildRowsSignature(newFanControlRows);
        if (string.Equals(newSignature, rowsSignature, StringComparison.Ordinal) &&
            string.Equals(newFanControlSignature, fanControlRowsSignature, StringComparison.Ordinal))
        {
            return;
        }

        var previousLoading = loadingPreferences;
        loadingPreferences = true;
        try
        {
            rows.Clear();
            rows.AddRange(newRows);
            rowsSignature = newSignature;
            fanControlRows.Clear();
            fanControlRows.AddRange(newFanControlRows);
            fanControlRowsSignature = newFanControlSignature;

            PopulateTrayReadingLists(CurrentTrayItemKeys());
            PopulateSpokenReadingLists(SelectedSpokenHotKey(), IsNotificationAreaStatusSelected());
            PopulateFanProfileLists(SelectedFanProfile());
            PopulateAlarmReadings();
            UpdateTraySelectionStatus();
            UpdateSpokenSelectionStatus();
            UpdateFanProfileStatus();
        }
        finally
        {
            loadingPreferences = previousLoading;
        }
    }

    private static string BuildRowsSignature(IEnumerable<SensorRow> sourceRows)
    {
        return string.Join("|", (sourceRows ?? Enumerable.Empty<SensorRow>())
            .Select(r => SensorReadoutForm.RowSettingsKey(r))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .OrderBy(k => k)
            .ToArray());
    }

    private void PopulateTrayReadingLists(List<string> selectedKeys)
    {
        if (trayAvailableList == null || traySelectedList == null)
        {
            return;
        }

        var selectedAvailableKey = SelectedTrayChoiceKey(trayAvailableList);
        var selectedTrayKey = SelectedTrayChoiceKey(traySelectedList);
        trayAvailableList.Items.Clear();
        traySelectedList.Items.Clear();
        var keys = selectedKeys ?? new List<string>();
        var choices = rows
            .Select(r => new TrayItemChoice(r, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames))
            .OrderBy(i => i.Hardware)
            .ThenBy(i => SensorReadoutForm.ReadingSortIndex(i.Name))
            .ThenBy(i => i.Name)
            .ThenBy(i => i.Type)
            .ToList();

        foreach (var key in keys)
        {
            var selectedTrayChoice = choices.FirstOrDefault(i => i.Key == key);
            if (selectedTrayChoice != null && !ContainsTrayChoice(traySelectedList, selectedTrayChoice.Key))
            {
                selectedTrayChoice.ShowSpeechPreview = true;
                traySelectedList.Items.Add(selectedTrayChoice);
            }
            else if (selectedTrayChoice == null && !ContainsTrayChoice(traySelectedList, key))
            {
                var unresolved = TrayItemChoice.Unresolved(key, readingSpeechLabels, ShouldPreviewSpeechWithDeviceNames);
                unresolved.ShowSpeechPreview = true;
                traySelectedList.Items.Add(unresolved);
            }
        }

        foreach (var item in choices)
        {
            if (!ContainsTrayChoice(traySelectedList, item.Key))
            {
                trayAvailableList.Items.Add(item);
            }
        }

        if (trayAvailableList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(trayAvailableList, selectedAvailableKey);
        }
        if (traySelectedList.Items.Count > 0)
        {
            SelectTrayChoiceByKey(traySelectedList, selectedTrayKey);
        }
    }

    private static string SelectedTrayChoiceKey(ListBox list)
    {
        var choice = list == null ? null : list.SelectedItem as TrayItemChoice;
        return choice == null ? "" : choice.Key;
    }

    private static void SelectTrayChoiceByKey(ListBox list, string key)
    {
        if (list == null || list.Items.Count == 0)
        {
            return;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            var choice = list.Items[i] as TrayItemChoice;
            if (choice != null && string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                list.SelectedIndex = i;
                return;
            }
        }

        list.SelectedIndex = 0;
    }

    private void AddAvailableTrayChoiceSorted(TrayItemChoice choice)
    {
        var insertIndex = 0;
        while (insertIndex < trayAvailableList.Items.Count &&
            TrayItemChoice.Compare((TrayItemChoice)trayAvailableList.Items[insertIndex], choice) <= 0)
        {
            insertIndex++;
        }

        trayAvailableList.Items.Insert(insertIndex, choice);
    }

    private static bool ContainsTrayChoice(ListBox list, string key)
    {
        return list.Items.Cast<TrayItemChoice>().Any(i => i.Key == key);
    }

    private List<string> CurrentTrayItemKeys()
    {
        return new List<string>(liveSettings.TrayItemKeys ?? new List<string>())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Take(SensorReadoutForm.MaxTrayStatusReadings)
            .ToList();
    }
}
