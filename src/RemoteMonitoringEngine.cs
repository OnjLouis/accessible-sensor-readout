using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

internal sealed class RemotePublishState
{
    public RemoteMachineSnapshot LastSnapshot;
    public int DeltasSinceSnapshot;
    public DateTime LastHeartbeatUtc;
    public DateTime LastAttemptUtc;
}

internal static class RemoteMonitoringEngine
{
    private const int SnapshotIntervalDeltas = 120;
    private const int MaximumProcessedFanCommandIds = 8192;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    private static readonly object ProcessedFanCommandLock = new object();
    private static readonly Dictionary<string, DateTime> ProcessedFanCommandIds = new Dictionary<string, DateTime>(StringComparer.Ordinal);

    internal static bool IsPublishDue(RemoteConnectionSetting connection, RemotePublishState state, DateTime nowUtc)
    {
        if (connection == null || state == null)
        {
            return false;
        }

        var lastAttemptUtc = state.LastAttemptUtc;
        if (lastAttemptUtc == DateTime.MinValue || nowUtc < lastAttemptUtc)
        {
            return true;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(2, Math.Min(300, connection.PollIntervalSeconds)));
        return nowUtc - lastAttemptUtc >= interval;
    }

    internal static bool TryBeginPublish(RemoteConnectionSetting connection, RemotePublishState state, DateTime nowUtc)
    {
        if (!IsPublishDue(connection, state, nowUtc))
        {
            return false;
        }
        state.LastAttemptUtc = nowUtc;
        return true;
    }

    public static RemoteMachineSnapshot Publish(
        RemoteConnectionSetting connection,
        RemotePublishState state,
        IList<SensorRow> rows,
        string machineId,
        string machineName,
        string appVersion,
        string memoryUnitMode,
        string storageUnitMode,
        string transferUnitMode,
        string machineWriteToken,
        IEnumerable<RemoteFanProfileDescriptor> fanProfiles = null)
    {
        if (connection == null || state == null)
        {
            throw new ArgumentNullException(connection == null ? "connection" : "state");
        }
        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        var spaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
        var client = new RemoteServerClient(connection.ServerUrl, token, machineWriteToken);

        if (state.LastSnapshot == null)
        {
            var index = client.GetMachineIndex(spaceId);
            var existing = (index.Machines ?? new List<RemoteMachineIndexEntry>())
                .FirstOrDefault(item => string.Equals(item.MachineId, machineId, StringComparison.Ordinal));
            var sequence = existing == null ? 1 : Math.Max(1, existing.LatestSequence + 1);
            var snapshot = RemoteSnapshotCodec.CreateSnapshot(rows, machineId, machineName, appVersion, sequence, memoryUnitMode, storageUnitMode, transferUnitMode, fanProfiles);
            client.PutSnapshot(spaceId, machineId, sequence, RemotePayloadCrypto.Encrypt(snapshot, password));
            state.LastSnapshot = snapshot;
            state.DeltasSinceSnapshot = 0;
            state.LastHeartbeatUtc = DateTime.UtcNow;
            return snapshot;
        }

        var current = RemoteSnapshotCodec.CreateSnapshot(
            rows,
            machineId,
            machineName,
            appVersion,
            state.LastSnapshot.Sequence + 1,
            memoryUnitMode,
            storageUnitMode,
            transferUnitMode,
            fanProfiles);
        var delta = RemoteSnapshotCodec.CreateDelta(state.LastSnapshot, current);
        var changedCount = delta.ChangedRows.Count + delta.RemovedRowKeys.Count;
        var metadataChanged = !string.Equals(state.LastSnapshot.AppVersion, current.AppVersion, StringComparison.Ordinal) ||
            !string.Equals(state.LastSnapshot.MachineName, current.MachineName, StringComparison.Ordinal) ||
            !string.Equals(state.LastSnapshot.MemoryUnitMode, current.MemoryUnitMode, StringComparison.Ordinal) ||
            !string.Equals(state.LastSnapshot.StorageUnitMode, current.StorageUnitMode, StringComparison.Ordinal) ||
            !string.Equals(state.LastSnapshot.TransferUnitMode, current.TransferUnitMode, StringComparison.Ordinal);
        if (changedCount == 0 && delta.RowOrder.Count == 0 && !delta.FanProfilesChanged && !metadataChanged)
        {
            if (DateTime.UtcNow - state.LastHeartbeatUtc >= HeartbeatInterval)
            {
                client.PostHeartbeat(spaceId, machineId);
                state.LastHeartbeatUtc = DateTime.UtcNow;
            }
            return state.LastSnapshot;
        }

        var compact = state.DeltasSinceSnapshot >= SnapshotIntervalDeltas ||
            changedCount > Math.Max(100, current.Rows.Count / 2);
        try
        {
            if (compact)
            {
                client.PutSnapshot(spaceId, machineId, current.Sequence, RemotePayloadCrypto.Encrypt(current, password));
                state.DeltasSinceSnapshot = 0;
            }
            else
            {
                client.PostDelta(spaceId, machineId, current.Sequence, RemotePayloadCrypto.Encrypt(delta, password));
                state.DeltasSinceSnapshot++;
            }
        }
        catch (RemoteServerException error)
        {
            if (error.StatusCode != HttpStatusCode.Conflict && (int)error.StatusCode != 428)
            {
                throw;
            }
            var index = client.GetMachineIndex(spaceId);
            var existing = (index.Machines ?? new List<RemoteMachineIndexEntry>())
                .FirstOrDefault(item => string.Equals(item.MachineId, machineId, StringComparison.Ordinal));
            current.Sequence = existing == null ? Math.Max(1, current.Sequence) : Math.Max(1, existing.LatestSequence + 1);
            client.PutSnapshot(spaceId, machineId, current.Sequence, RemotePayloadCrypto.Encrypt(current, password));
            state.DeltasSinceSnapshot = 0;
        }

        state.LastSnapshot = current;
        state.LastHeartbeatUtc = DateTime.UtcNow;
        return current;
    }

    public static List<RemoteMachineDescriptor> ListMachines(RemoteConnectionSetting connection)
    {
        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        var spaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
        var client = new RemoteServerClient(connection.ServerUrl, token);
        var index = client.GetMachineIndex(spaceId);
        if (index == null || index.ProtocolVersion != 1)
        {
            throw new InvalidOperationException("The remote server returned an unsupported computer index.");
        }
        var result = new List<RemoteMachineDescriptor>();
        Exception firstLoadError = null;
        foreach (var entry in index.Machines ?? new List<RemoteMachineIndexEntry>())
        {
            try
            {
                var snapshot = LoadMachine(connection, entry.MachineId);
                if (!string.Equals(snapshot.MachineId, entry.MachineId, StringComparison.Ordinal))
                {
                    continue;
                }
                result.Add(new RemoteMachineDescriptor
                {
                    MachineId = entry.MachineId,
                    MachineName = snapshot.MachineName,
                    AppVersion = snapshot.AppVersion,
                    LastSeenUtc = UnixMillisecondsToUtc(entry.LastSeenUnixMs),
                    LatestSequence = Math.Max(snapshot.Sequence, entry.LatestSequence),
                    FanProfiles = snapshot.FanProfiles == null
                        ? new List<RemoteFanProfileDescriptor>()
                        : snapshot.FanProfiles.Select(profile => new RemoteFanProfileDescriptor { Id = profile.Id, Name = profile.Name }).ToList()
                });
            }
            catch (Exception error)
            {
                if (firstLoadError == null) firstLoadError = error;
            }
        }
        if ((index.Machines ?? new List<RemoteMachineIndexEntry>()).Count > 0 && result.Count == 0)
        {
            throw new InvalidOperationException("No remote computers could be decrypted. Check the monitoring password and server connection.", firstLoadError);
        }
        return result.OrderBy(item => item.MachineName).ThenBy(item => item.MachineId).ToList();
    }

    public static RemoteMachineSnapshot LoadMachine(RemoteConnectionSetting connection, string machineId)
    {
        return LoadMachine(connection, machineId, null);
    }

    public static RemoteMachineSnapshot LoadMachine(RemoteConnectionSetting connection, string machineId, RemoteMachineSnapshot currentSnapshot)
    {
        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        var spaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
        var client = new RemoteServerClient(connection.ServerUrl, token);
        RemoteMachineSnapshot snapshot = null;
        if (currentSnapshot != null)
        {
            RemoteSnapshotCodec.ValidateSnapshot(currentSnapshot);
            if (string.Equals(currentSnapshot.MachineId, machineId, StringComparison.Ordinal))
            {
                snapshot = currentSnapshot;
            }
        }

        if (snapshot == null)
        {
            snapshot = LoadCompleteSnapshot(client, spaceId, machineId, password);
        }

        RemoteDeltaEnvelopeList deltas;
        try
        {
            deltas = client.GetDeltas(spaceId, machineId, snapshot.Sequence);
            ValidateDeltaEnvelopeList(deltas, snapshot.Sequence);
            if (deltas.SnapshotSequence > snapshot.Sequence)
            {
                snapshot = LoadCompleteSnapshot(client, spaceId, machineId, password);
                deltas = client.GetDeltas(spaceId, machineId, snapshot.Sequence);
                ValidateDeltaEnvelopeList(deltas, snapshot.Sequence);
            }
        }
        catch (RemoteServerException error)
        {
            if ((int)error.StatusCode != 428) throw;
            snapshot = LoadCompleteSnapshot(client, spaceId, machineId, password);
            deltas = client.GetDeltas(spaceId, machineId, snapshot.Sequence);
            ValidateDeltaEnvelopeList(deltas, snapshot.Sequence);
        }
        var expectedSequence = snapshot.Sequence + 1;
        foreach (var envelope in (deltas.Deltas ?? new List<RemoteDeltaEnvelope>()).OrderBy(item => item.Sequence))
        {
            if (envelope == null || envelope.Sequence != expectedSequence)
            {
                throw new InvalidDataException("The remote server returned an incomplete or out-of-order update chain.");
            }
            var encryptedDelta = Convert.FromBase64String(envelope.Payload ?? "");
            var delta = RemotePayloadCrypto.Decrypt<RemoteMachineDelta>(encryptedDelta, password);
            snapshot = RemoteSnapshotCodec.ApplyDelta(snapshot, delta);
            expectedSequence++;
        }
        if (snapshot.Sequence != deltas.LatestSequence)
        {
            throw new InvalidDataException("The remote server did not return every required update.");
        }
        return snapshot;
    }

    private static void ValidateDeltaEnvelopeList(RemoteDeltaEnvelopeList deltas, long currentSequence)
    {
        if (deltas == null || deltas.ProtocolVersion != 1 || deltas.SnapshotSequence < 1 ||
            deltas.LatestSequence < deltas.SnapshotSequence || deltas.LatestSequence < currentSequence ||
            (deltas.Deltas != null && deltas.Deltas.Count > 64))
        {
            throw new InvalidDataException("The remote server returned invalid update metadata.");
        }
    }

    private static RemoteMachineSnapshot LoadCompleteSnapshot(RemoteServerClient client, string spaceId, string machineId, string password)
    {
        long snapshotSequence;
        long latestSequence;
        var encryptedSnapshot = client.GetSnapshot(spaceId, machineId, out snapshotSequence, out latestSequence);
        var snapshot = RemotePayloadCrypto.Decrypt<RemoteMachineSnapshot>(encryptedSnapshot, password);
        RemoteSnapshotCodec.ValidateSnapshot(snapshot);
        if (!string.Equals(snapshot.MachineId, machineId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote server returned data for a different machine.");
        }
        if (snapshotSequence < 1 || latestSequence < snapshotSequence || snapshot.Sequence != snapshotSequence)
        {
            throw new InvalidDataException("The remote server returned inconsistent snapshot metadata.");
        }
        return snapshot;
    }

    private static DateTime UnixMillisecondsToUtc(long value)
    {
        try
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(Math.Max(0, value));
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public static List<RemoteFanProfileDescriptor> CreateFanProfileDescriptors(IEnumerable<FanProfileSetting> profiles)
    {
        var result = new List<RemoteFanProfileDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles ?? Enumerable.Empty<FanProfileSetting>())
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Name) || profile.Actions == null || profile.Actions.Count == 0) continue;
            var canonical = profile.Name.Trim() + "\n" + string.Join("\n", profile.Actions
                .Where(action => action != null && !string.IsNullOrWhiteSpace(action.FanControlKey))
                .OrderBy(action => action.FanControlKey, StringComparer.OrdinalIgnoreCase)
                .Select(action => (action.FanControlKey ?? "").Trim().ToLowerInvariant() + "|" + (action.Manual ? "manual" : "automatic") + "|" + Math.Max(0, Math.Min(100, action.Percent)))
                .ToArray());
            string id;
            using (var sha = SHA256.Create())
            {
                id = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            if (ids.Add(id)) result.Add(new RemoteFanProfileDescriptor { Id = id, Name = profile.Name.Trim() });
        }
        return result;
    }

    public static string FanProfileId(FanProfileSetting profile)
    {
        var descriptor = CreateFanProfileDescriptors(new[] { profile }).FirstOrDefault();
        return descriptor == null ? "" : descriptor.Id;
    }

    public static void SendFanProfileCommand(
        RemoteConnectionSetting connection,
        string targetMachineId,
        string requestingMachineId,
        RemoteFanProfileDescriptor profile)
    {
        if (connection == null || profile == null || string.IsNullOrWhiteSpace(targetMachineId) || string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new ArgumentException("A remote computer and fan profile are required.");
        }
        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        var commandId = RemotePayloadCrypto.CreateRandomId();
        var command = new RemoteFanProfileCommand
        {
            CommandId = commandId,
            TargetMachineId = targetMachineId,
            RequestedByMachineId = requestingMachineId ?? "",
            FanProfileId = profile.Id,
            FanProfileName = profile.Name ?? "",
            CreatedUtc = DateTime.UtcNow.ToString("o")
        };
        var client = new RemoteServerClient(connection.ServerUrl, token);
        client.PostCommand(RemotePayloadCrypto.DeriveSpaceId(token, password), targetMachineId, commandId, RemotePayloadCrypto.Encrypt(command, password));
    }

    public static void SendViewerPresence(
        RemoteConnectionSetting connection,
        string targetMachineId,
        string viewerMachineId,
        string viewerMachineName,
        string sessionId,
        string action,
        int lifetimeSeconds)
    {
        if (connection == null || string.IsNullOrWhiteSpace(targetMachineId) ||
            string.IsNullOrWhiteSpace(viewerMachineId) || string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A remote computer and viewer session are required.");
        }

        action = (action ?? "").Trim().ToLowerInvariant();
        if (action != "connected" && action != "heartbeat" && action != "disconnected")
        {
            throw new ArgumentException("The remote viewer action is not supported.");
        }

        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        var now = DateTime.UtcNow;
        // A live session owns one queue slot. Heartbeats replace the previous
        // connected/heartbeat payload instead of filling the target's command queue.
        var commandId = action == "disconnected" ? sessionId + "D" : sessionId;
        var command = new RemoteViewerPresenceCommand
        {
            CommandId = commandId,
            TargetMachineId = targetMachineId,
            ViewerMachineId = viewerMachineId,
            ViewerMachineName = (viewerMachineName ?? "").Trim(),
            SessionId = sessionId,
            Action = action,
            CreatedUtc = now.ToString("o"),
            ExpiresUtc = now.AddSeconds(Math.Max(15, Math.Min(120, lifetimeSeconds))).ToString("o")
        };
        var client = new RemoteServerClient(connection.ServerUrl, token);
        client.PostCommand(RemotePayloadCrypto.DeriveSpaceId(token, password), targetMachineId, commandId, RemotePayloadCrypto.Encrypt(command, password));
    }

    public static void RemoveMachine(RemoteConnectionSetting connection, string machineId, string machineWriteToken)
    {
        if (connection == null || string.IsNullOrWhiteSpace(machineId)) throw new ArgumentException("A remote computer is required.");
        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        new RemoteServerClient(connection.ServerUrl, token, machineWriteToken).DeleteMachine(RemotePayloadCrypto.DeriveSpaceId(token, password), machineId);
    }

    public static RemoteReceivedCommands ReadAndAcknowledgeCommands(
        RemoteConnectionSetting connection,
        string machineId,
        string machineWriteToken,
        bool acceptFanProfiles)
    {
        var token = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedAccessToken);
        var password = RemotePayloadCrypto.UnprotectSecret(connection.ProtectedPassword);
        var spaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
        var client = new RemoteServerClient(connection.ServerUrl, token, machineWriteToken);
        var envelopes = client.GetCommands(spaceId, machineId);
        var result = new RemoteReceivedCommands();
        RemoteFanProfileCommand newestCommand = null;
        var newestCreatedUtc = DateTime.MinValue;
        foreach (var envelope in (envelopes.Commands ?? new List<RemoteCommandEnvelope>()).Take(32))
        {
            RemoteFanProfileCommand accepted = null;
            RemoteViewerPresenceCommand acceptedPresence = null;
            var acceptedCreatedUtc = DateTime.MinValue;
            try
            {
                var payload = Convert.FromBase64String(envelope.Payload ?? "");
                var command = RemotePayloadCrypto.Decrypt<RemoteFanProfileCommand>(payload, password);
                DateTime createdUtc;
                if (acceptFanProfiles && command != null && string.Equals(command.Format, "SensorReadoutRemoteFanProfileCommand", StringComparison.Ordinal) &&
                    command.ProtocolVersion == 1 && string.Equals(command.CommandId, envelope.CommandId, StringComparison.Ordinal) &&
                    ValidRemoteCommandId(command.CommandId) &&
                    string.Equals(command.TargetMachineId, machineId, StringComparison.Ordinal) &&
                    DateTime.TryParse(command.CreatedUtc, out createdUtc) &&
                    createdUtc.ToUniversalTime() >= DateTime.UtcNow.AddMinutes(-5) && createdUtc.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(1))
                {
                    accepted = command;
                    acceptedCreatedUtc = createdUtc.ToUniversalTime();
                }

                if (accepted == null)
                {
                    var presence = RemotePayloadCrypto.Decrypt<RemoteViewerPresenceCommand>(payload, password);
                    DateTime presenceCreatedUtc;
                    DateTime presenceExpiresUtc;
                    var presenceAction = presence == null ? "" : (presence.Action ?? "").Trim().ToLowerInvariant();
                    if (presence != null && string.Equals(presence.Format, "SensorReadoutRemoteViewerPresence", StringComparison.Ordinal) &&
                        presence.ProtocolVersion == 1 && string.Equals(presence.CommandId, envelope.CommandId, StringComparison.Ordinal) &&
                        string.Equals(presence.TargetMachineId, machineId, StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(presence.ViewerMachineId) && !string.IsNullOrWhiteSpace(presence.SessionId) &&
                        (presenceAction == "connected" || presenceAction == "heartbeat" || presenceAction == "disconnected") &&
                        DateTime.TryParse(presence.CreatedUtc, out presenceCreatedUtc) &&
                        DateTime.TryParse(presence.ExpiresUtc, out presenceExpiresUtc) &&
                        presenceCreatedUtc.ToUniversalTime() >= DateTime.UtcNow.AddMinutes(-3) &&
                        presenceCreatedUtc.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(1) &&
                        presenceExpiresUtc.ToUniversalTime() >= DateTime.UtcNow.AddSeconds(-30) &&
                        presenceExpiresUtc.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(3))
                    {
                        presence.Action = presenceAction;
                        acceptedPresence = presence;
                    }
                }
            }
            catch
            {
            }

            try
            {
                client.DeleteCommand(spaceId, machineId, envelope.CommandId);
                if (accepted != null && RememberProcessedFanCommand(spaceId, machineId, accepted.CommandId, acceptedCreatedUtc) &&
                    acceptedCreatedUtc >= newestCreatedUtc)
                {
                    newestCommand = accepted;
                    newestCreatedUtc = acceptedCreatedUtc;
                }
                if (acceptedPresence != null)
                {
                    result.ViewerPresenceCommands.Add(acceptedPresence);
                }
            }
            catch { }
        }
        if (newestCommand != null)
        {
            result.FanProfileCommands.Add(newestCommand);
        }
        return result;
    }

    private static bool RememberProcessedFanCommand(string spaceId, string machineId, string commandId, DateTime createdUtc)
    {
        var nowUtc = DateTime.UtcNow;
        var key = ProcessedFanCommandKey(spaceId, machineId, commandId);
        lock (ProcessedFanCommandLock)
        {
            foreach (var expired in ProcessedFanCommandIds.Where(item => item.Value <= nowUtc).Select(item => item.Key).ToList())
            {
                ProcessedFanCommandIds.Remove(expired);
            }
            if (ProcessedFanCommandIds.ContainsKey(key)) return false;
            while (ProcessedFanCommandIds.Count >= MaximumProcessedFanCommandIds)
            {
                var oldest = ProcessedFanCommandIds.OrderBy(item => item.Value).First();
                ProcessedFanCommandIds.Remove(oldest.Key);
            }
            ProcessedFanCommandIds[key] = createdUtc.ToUniversalTime().AddMinutes(6);
            return true;
        }
    }

    private static string ProcessedFanCommandKey(string spaceId, string machineId, string commandId)
    {
        using (var sha = SHA256.Create())
        {
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes((spaceId ?? "") + "\n" + (machineId ?? "") + "\n" + (commandId ?? ""))));
        }
    }

    private static bool ValidRemoteCommandId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length >= 32 && value.Length <= 128 &&
            value.All(character => char.IsLetterOrDigit(character) || character == '_' || character == '-');
    }

    public static List<RemoteFanProfileCommand> ReadAndAcknowledgeFanProfileCommands(RemoteConnectionSetting connection, string machineId, string machineWriteToken)
    {
        return ReadAndAcknowledgeCommands(connection, machineId, machineWriteToken, true).FanProfileCommands;
    }
}
