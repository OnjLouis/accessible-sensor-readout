using System;
using System.Collections.Generic;
using System.Linq;

internal static class RemoteSnapshotCodec
{
    private const int MaximumRows = 20000;
    private const int MaximumDetailsPerRow = 256;
    private const int MaximumTextLength = 65536;

    public static RemoteMachineSnapshot CreateSnapshot(
        IEnumerable<SensorRow> rows,
        string machineId,
        string machineName,
        string appVersion,
        long sequence,
        string memoryUnitMode,
        string storageUnitMode,
        string transferUnitMode,
        IEnumerable<RemoteFanProfileDescriptor> fanProfiles = null)
    {
        return new RemoteMachineSnapshot
        {
            AppVersion = appVersion ?? "",
            MachineId = machineId ?? "",
            MachineName = machineName ?? "",
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Sequence = sequence,
            MemoryUnitMode = memoryUnitMode ?? "",
            StorageUnitMode = storageUnitMode ?? "",
            TransferUnitMode = transferUnitMode ?? "",
            Rows = CreateRemoteRows(rows),
            FanProfiles = CloneFanProfiles(fanProfiles)
        };
    }

    public static RemoteMachineDelta CreateDelta(RemoteMachineSnapshot previous, RemoteMachineSnapshot current)
    {
        ValidateSnapshot(previous);
        ValidateSnapshot(current);
        if (!string.Equals(previous.MachineId, current.MachineId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cannot create a remote difference between different machines.");
        }

        var previousByKey = previous.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var currentByKey = current.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var changed = new List<RemoteSensorRow>();
        foreach (var row in current.Rows)
        {
            RemoteSensorRow oldRow;
            if (!previousByKey.TryGetValue(row.Key, out oldRow) || !RowsEqual(oldRow, row))
            {
                changed.Add(Clone(row));
            }
        }

        var removed = previous.Rows
            .Select(r => r.Key)
            .Where(key => !currentByKey.ContainsKey(key))
            .ToList();
        var oldOrder = previous.Rows.Select(r => r.Key).ToList();
        var newOrder = current.Rows.Select(r => r.Key).ToList();
        var fanProfilesChanged = !FanProfilesEqual(previous.FanProfiles, current.FanProfiles);
        return new RemoteMachineDelta
        {
            MachineId = current.MachineId,
            AppVersion = current.AppVersion,
            MachineName = current.MachineName,
            GeneratedUtc = current.GeneratedUtc,
            MemoryUnitMode = current.MemoryUnitMode,
            StorageUnitMode = current.StorageUnitMode,
            TransferUnitMode = current.TransferUnitMode,
            BaseSequence = previous.Sequence,
            Sequence = current.Sequence,
            ChangedRows = changed,
            RemovedRowKeys = removed,
            RowOrder = oldOrder.SequenceEqual(newOrder, StringComparer.Ordinal) ? new List<string>() : newOrder,
            FanProfilesChanged = fanProfilesChanged,
            FanProfiles = fanProfilesChanged ? CloneFanProfiles(current.FanProfiles) : new List<RemoteFanProfileDescriptor>()
        };
    }

    public static RemoteMachineSnapshot ApplyDelta(RemoteMachineSnapshot snapshot, RemoteMachineDelta delta)
    {
        ValidateSnapshot(snapshot);
        ValidateDelta(delta);
        if (!string.Equals(snapshot.MachineId, delta.MachineId, StringComparison.Ordinal) || snapshot.Sequence != delta.BaseSequence)
        {
            throw new InvalidOperationException("The remote difference does not continue from the loaded snapshot.");
        }

        var rows = snapshot.Rows.Select(Clone).ToList();
        var byKey = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        foreach (var key in delta.RemovedRowKeys ?? new List<string>())
        {
            byKey.Remove(key);
        }
        foreach (var row in delta.ChangedRows ?? new List<RemoteSensorRow>())
        {
            byKey[row.Key] = Clone(row);
        }

        var ordered = new List<RemoteSensorRow>();
        var requestedOrder = delta.RowOrder != null && delta.RowOrder.Count > 0
            ? delta.RowOrder
            : rows.Select(r => r.Key).Concat((delta.ChangedRows ?? new List<RemoteSensorRow>()).Select(r => r.Key)).ToList();
        var addedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in requestedOrder)
        {
            RemoteSensorRow row;
            if (byKey.TryGetValue(key, out row) && addedKeys.Add(key))
            {
                ordered.Add(row);
            }
        }
        foreach (var row in byKey.Values)
        {
            if (addedKeys.Add(row.Key)) ordered.Add(row);
        }

        var result = new RemoteMachineSnapshot
        {
            AppVersion = delta.AppVersion,
            MachineId = snapshot.MachineId,
            MachineName = delta.MachineName,
            GeneratedUtc = delta.GeneratedUtc,
            Sequence = delta.Sequence,
            MemoryUnitMode = delta.MemoryUnitMode,
            StorageUnitMode = delta.StorageUnitMode,
            TransferUnitMode = delta.TransferUnitMode,
            Rows = ordered,
            FanProfiles = delta.FanProfilesChanged ? CloneFanProfiles(delta.FanProfiles) : CloneFanProfiles(snapshot.FanProfiles)
        };
        ValidateSnapshot(result);
        return result;
    }

    public static List<SensorRow> ToSensorRows(RemoteMachineSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        return snapshot.Rows.Select(row => new SensorRow
        {
            Type = row.Type,
            Hardware = row.Hardware,
            Name = row.Name,
            Identifier = row.Identifier,
            Value = row.Value,
            DisplayValue = row.DisplayValue,
            Source = row.Source,
            Details = row.Details == null ? null : new Dictionary<string, string>(row.Details)
        }).ToList();
    }

    public static void ValidateSnapshot(RemoteMachineSnapshot snapshot)
    {
        if (snapshot == null || !string.Equals(snapshot.Format, "SensorReadoutRemoteSnapshot", StringComparison.Ordinal) || snapshot.ProtocolVersion != 1)
        {
            throw new InvalidOperationException("The remote machine snapshot is not supported.");
        }
        if (!ValidRemoteId(snapshot.MachineId) || snapshot.Sequence < 1)
        {
            throw new InvalidOperationException("The remote machine snapshot has an invalid identity or sequence.");
        }
        ValidateMetadata(snapshot.AppVersion, snapshot.MachineName, snapshot.GeneratedUtc, snapshot.MemoryUnitMode, snapshot.StorageUnitMode, snapshot.TransferUnitMode);
        snapshot.Rows = snapshot.Rows ?? new List<RemoteSensorRow>();
        snapshot.FanProfiles = snapshot.FanProfiles ?? new List<RemoteFanProfileDescriptor>();
        if (snapshot.Rows.Count > MaximumRows)
        {
            throw new InvalidOperationException("The remote machine snapshot contains too many rows.");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in snapshot.Rows)
        {
            ValidateRow(row);
            if (!keys.Add(row.Key))
            {
                throw new InvalidOperationException("The remote machine snapshot contains duplicate row identities.");
            }
        }
        ValidateFanProfiles(snapshot.FanProfiles);
    }

    private static void ValidateDelta(RemoteMachineDelta delta)
    {
        if (delta == null || !string.Equals(delta.Format, "SensorReadoutRemoteDelta", StringComparison.Ordinal) || delta.ProtocolVersion != 1 ||
            !ValidRemoteId(delta.MachineId) || delta.BaseSequence < 1 || delta.Sequence != delta.BaseSequence + 1)
        {
            throw new InvalidOperationException("The remote machine difference is not supported or is out of sequence.");
        }
        ValidateMetadata(delta.AppVersion, delta.MachineName, delta.GeneratedUtc, delta.MemoryUnitMode, delta.StorageUnitMode, delta.TransferUnitMode);
        delta.ChangedRows = delta.ChangedRows ?? new List<RemoteSensorRow>();
        delta.RemovedRowKeys = delta.RemovedRowKeys ?? new List<string>();
        delta.RowOrder = delta.RowOrder ?? new List<string>();
        delta.FanProfiles = delta.FanProfiles ?? new List<RemoteFanProfileDescriptor>();
        if (delta.ChangedRows.Count > MaximumRows || delta.RemovedRowKeys.Count > MaximumRows || delta.RowOrder.Count > MaximumRows)
        {
            throw new InvalidOperationException("The remote machine difference contains too many rows.");
        }
        var changedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in delta.ChangedRows)
        {
            ValidateRow(row);
            if (!changedKeys.Add(row.Key)) throw new InvalidOperationException("The remote machine difference contains duplicate changed-row identities.");
        }
        ValidateKeyList(delta.RemovedRowKeys, "removed-row");
        ValidateKeyList(delta.RowOrder, "row-order");
        ValidateFanProfiles(delta.FanProfiles);
    }

    private static List<RemoteSensorRow> CreateRemoteRows(IEnumerable<SensorRow> rows)
    {
        var result = new List<RemoteSensorRow>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows ?? Enumerable.Empty<SensorRow>())
        {
            if (row == null || result.Count >= MaximumRows)
            {
                continue;
            }
            var baseKey = StableBaseKey(row);
            int count;
            counts.TryGetValue(baseKey, out count);
            count++;
            counts[baseKey] = count;
            result.Add(new RemoteSensorRow
            {
                Key = count == 1 ? baseKey : baseKey + "#" + count,
                Type = Limit(row.Type),
                Hardware = Limit(row.Hardware),
                Name = Limit(row.Name),
                Identifier = Limit(row.Identifier),
                Value = row.Value,
                DisplayValue = Limit(row.DisplayValue),
                Source = Limit(row.Source),
                Details = row.Details == null
                    ? null
                    : row.Details
                        .OrderBy(item => item.Key, StringComparer.Ordinal)
                        .Take(MaximumDetailsPerRow)
                        .ToDictionary(item => Limit(item.Key), item => Limit(item.Value), StringComparer.Ordinal)
            });
        }
        return result;
    }

    private static string StableBaseKey(SensorRow row)
    {
        return string.Join("|", new[]
        {
            EscapeKeyPart(row.Type),
            EscapeKeyPart(row.Hardware),
            EscapeKeyPart(row.Name),
            EscapeKeyPart(row.Identifier)
        });
    }

    private static string EscapeKeyPart(string value)
    {
        return (value ?? "").Replace("%", "%25").Replace("|", "%7C").Replace("#", "%23");
    }

    private static string Limit(string value)
    {
        var text = value ?? "";
        return text.Length <= MaximumTextLength ? text : text.Substring(0, MaximumTextLength);
    }

    private static void ValidateRow(RemoteSensorRow row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.Key) || row.Key.Length > MaximumTextLength)
        {
            throw new InvalidOperationException("A remote sensor row has an invalid identity.");
        }
        if ((row.Details == null ? 0 : row.Details.Count) > MaximumDetailsPerRow)
        {
            throw new InvalidOperationException("A remote sensor row contains too many details.");
        }
        foreach (var text in new[] { row.Type, row.Hardware, row.Name, row.Identifier, row.DisplayValue, row.Source })
        {
            if ((text ?? "").Length > MaximumTextLength)
            {
                throw new InvalidOperationException("A remote sensor row contains an oversized value.");
            }
        }
        foreach (var detail in row.Details ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(detail.Key) || detail.Key.Length > MaximumTextLength || (detail.Value ?? "").Length > MaximumTextLength)
            {
                throw new InvalidOperationException("A remote sensor row contains an invalid or oversized detail.");
            }
        }
    }

    private static void ValidateMetadata(params string[] values)
    {
        foreach (var text in values ?? new string[0])
        {
            if ((text ?? "").Length > MaximumTextLength)
            {
                throw new InvalidOperationException("The remote machine payload contains oversized metadata.");
            }
        }
    }

    private static void ValidateKeyList(IEnumerable<string> values, string description)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in values ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumTextLength || !keys.Add(key))
            {
                throw new InvalidOperationException("The remote machine difference contains an invalid or duplicate " + description + " identity.");
            }
        }
    }

    private static bool ValidRemoteId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length >= 32 && value.Length <= 128 &&
            value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-');
    }

    private static bool RowsEqual(RemoteSensorRow left, RemoteSensorRow right)
    {
        if (left == null || right == null) return left == right;
        if (!string.Equals(left.Key, right.Key, StringComparison.Ordinal) ||
            !string.Equals(left.Type, right.Type, StringComparison.Ordinal) ||
            !string.Equals(left.Hardware, right.Hardware, StringComparison.Ordinal) ||
            !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            !string.Equals(left.Identifier, right.Identifier, StringComparison.Ordinal) ||
            left.Value != right.Value ||
            !string.Equals(left.DisplayValue, right.DisplayValue, StringComparison.Ordinal) ||
            !string.Equals(left.Source, right.Source, StringComparison.Ordinal))
        {
            return false;
        }

        var leftDetails = left.Details ?? new Dictionary<string, string>();
        var rightDetails = right.Details ?? new Dictionary<string, string>();
        if (leftDetails.Count != rightDetails.Count) return false;
        foreach (var item in leftDetails)
        {
            string value;
            if (!rightDetails.TryGetValue(item.Key, out value) || !string.Equals(item.Value, value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static RemoteSensorRow Clone(RemoteSensorRow row)
    {
        return new RemoteSensorRow
        {
            Key = row.Key,
            Type = row.Type,
            Hardware = row.Hardware,
            Name = row.Name,
            Identifier = row.Identifier,
            Value = row.Value,
            DisplayValue = row.DisplayValue,
            Source = row.Source,
            Details = row.Details == null ? null : new Dictionary<string, string>(row.Details)
        };
    }

    private static List<RemoteFanProfileDescriptor> CloneFanProfiles(IEnumerable<RemoteFanProfileDescriptor> profiles)
    {
        return (profiles ?? Enumerable.Empty<RemoteFanProfileDescriptor>())
            .Where(profile => profile != null && !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(profile.Name))
            .Take(100)
            .Select(profile => new RemoteFanProfileDescriptor { Id = Limit(profile.Id), Name = Limit(profile.Name) })
            .ToList();
    }

    private static bool FanProfilesEqual(IList<RemoteFanProfileDescriptor> left, IList<RemoteFanProfileDescriptor> right)
    {
        var first = left ?? new List<RemoteFanProfileDescriptor>();
        var second = right ?? new List<RemoteFanProfileDescriptor>();
        if (first.Count != second.Count) return false;
        for (var index = 0; index < first.Count; index++)
        {
            if (!string.Equals(first[index].Id, second[index].Id, StringComparison.Ordinal) ||
                !string.Equals(first[index].Name, second[index].Name, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static void ValidateFanProfiles(IList<RemoteFanProfileDescriptor> profiles)
    {
        if (profiles == null || profiles.Count > 100) throw new InvalidOperationException("The remote machine contains too many fan profiles.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 512 || !ids.Add(profile.Id))
            {
                throw new InvalidOperationException("The remote machine contains an invalid fan profile.");
            }
        }
    }
}
