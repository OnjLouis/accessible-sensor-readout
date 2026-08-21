using System;
using System.Collections.Generic;

public sealed class RemoteConnectionSetting
{
    public string Id = "";
    public string Name = "";
    public string ServerUrl = "";
    public string ProtectedAccessToken = "";
    public string ProtectedPassword = "";
    public bool PublishThisComputer = false;
    public bool AllowRemoteFanProfiles = false;
    public bool AnnounceRemoteViewers = true;
    public string RemoteViewerSoundFile = "";
    public bool Enabled = true;
    public int PollIntervalSeconds = 5;
    public bool IsEmbeddedHostConnection = false;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? ServerUrl : Name;
    }
}

public sealed class RemoteConnectionDocument
{
    public string Format = "SensorReadoutRemoteConnection";
    public int ProtocolVersion = 1;
    public string ServerUrl = "";
    public string Token = "";
}

public sealed class RemoteServerHealth
{
    public string Name = "";
    public string Version = "";
    public int ProtocolVersion;
}

public sealed class RemoteMachineSnapshot
{
    public string Format = "SensorReadoutRemoteSnapshot";
    public int ProtocolVersion = 1;
    public string AppVersion = "";
    public string MachineId = "";
    public string MachineName = "";
    public string GeneratedUtc = "";
    public long Sequence;
    public string MemoryUnitMode = "";
    public string StorageUnitMode = "";
    public string TransferUnitMode = "";
    public List<RemoteSensorRow> Rows = new List<RemoteSensorRow>();
    public List<RemoteFanProfileDescriptor> FanProfiles = new List<RemoteFanProfileDescriptor>();
}

public sealed class RemoteMachineDelta
{
    public string Format = "SensorReadoutRemoteDelta";
    public int ProtocolVersion = 1;
    public string MachineId = "";
    public string AppVersion = "";
    public string MachineName = "";
    public string GeneratedUtc = "";
    public string MemoryUnitMode = "";
    public string StorageUnitMode = "";
    public string TransferUnitMode = "";
    public long BaseSequence;
    public long Sequence;
    public List<RemoteSensorRow> ChangedRows = new List<RemoteSensorRow>();
    public List<string> RemovedRowKeys = new List<string>();
    public List<string> RowOrder = new List<string>();
    public bool FanProfilesChanged;
    public List<RemoteFanProfileDescriptor> FanProfiles = new List<RemoteFanProfileDescriptor>();
}

public sealed class RemoteFanProfileDescriptor
{
    public string Id = "";
    public string Name = "";

    public override string ToString() { return Name ?? ""; }
}

public sealed class RemoteFanProfileCommand
{
    public string Format = "SensorReadoutRemoteFanProfileCommand";
    public int ProtocolVersion = 1;
    public string CommandId = "";
    public string TargetMachineId = "";
    public string RequestedByMachineId = "";
    public string FanProfileId = "";
    public string FanProfileName = "";
    public string CreatedUtc = "";
}

public sealed class RemoteViewerPresenceCommand
{
    public string Format = "SensorReadoutRemoteViewerPresence";
    public int ProtocolVersion = 1;
    public string CommandId = "";
    public string TargetMachineId = "";
    public string ViewerMachineId = "";
    public string ViewerMachineName = "";
    public string SessionId = "";
    public string Action = "";
    public string CreatedUtc = "";
    public string ExpiresUtc = "";
}

public sealed class RemoteReceivedCommands
{
    public List<RemoteFanProfileCommand> FanProfileCommands = new List<RemoteFanProfileCommand>();
    public List<RemoteViewerPresenceCommand> ViewerPresenceCommands = new List<RemoteViewerPresenceCommand>();
}

public sealed class RemoteCommandEnvelopeList
{
    public int ProtocolVersion;
    public List<RemoteCommandEnvelope> Commands = new List<RemoteCommandEnvelope>();
}

public sealed class RemoteCommandEnvelope
{
    public string CommandId = "";
    public string Payload = "";
}

public sealed class RemoteSensorRow
{
    public string Key = "";
    public string Type = "";
    public string Hardware = "";
    public string Name = "";
    public string Identifier = "";
    public float? Value;
    public string DisplayValue = "";
    public string Source = "";
    public Dictionary<string, string> Details;
}

public sealed class RemoteMachineIndex
{
    public int ProtocolVersion;
    public List<RemoteMachineIndexEntry> Machines = new List<RemoteMachineIndexEntry>();
}

public sealed class RemoteMachineIndexEntry
{
    public string MachineId = "";
    public long SnapshotSequence;
    public long LatestSequence;
    public long LastSeenUnixMs;
}

public sealed class RemoteDeltaEnvelopeList
{
    public int ProtocolVersion;
    public long SnapshotSequence;
    public long LatestSequence;
    public List<RemoteDeltaEnvelope> Deltas = new List<RemoteDeltaEnvelope>();
}

public sealed class RemoteDeltaEnvelope
{
    public long Sequence;
    public string Payload = "";
}

public sealed class RemoteMachineDescriptor
{
    public string MachineId = "";
    public string MachineName = "";
    public string AppVersion = "";
    public DateTime LastSeenUtc;
    public long LatestSequence;
    public List<RemoteFanProfileDescriptor> FanProfiles = new List<RemoteFanProfileDescriptor>();

    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(MachineName) ? MachineId : MachineName;
        var version = string.IsNullOrWhiteSpace(AppVersion) ? "" : " - Sensor Readout " + AppVersion;
        return name + version;
    }
}
