using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

internal sealed class EmbeddedRemoteServer : IDisposable
{
    private const int MaximumConcurrentRequests = 16;
    internal static readonly TimeSpan HeaderWaitTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan EntityBodyTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpListener listener;
    private readonly RemoteRelayStore store;
    private readonly string accessToken;
    private readonly Action<string> log;
    private readonly SemaphoreSlim requestSlots = new SemaphoreSlim(MaximumConcurrentRequests, MaximumConcurrentRequests);
    private Thread listenerThread;
    private volatile bool stopping;

    public EmbeddedRemoteServer(int port, string dataPath, string accessToken, Action<string> log)
        : this(port, dataPath, accessToken, log, "+")
    {
    }

    internal EmbeddedRemoteServer(int port, string dataPath, string accessToken, Action<string> log, string listenHost)
    {
        if (port < 1024 || port > 65535)
        {
            throw new ArgumentOutOfRangeException("port");
        }
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Trim().Length < 32 || accessToken.Trim().Length > 4096)
        {
            throw new ArgumentException("An embedded server access token between 32 and 4096 characters is required.", "accessToken");
        }
        this.accessToken = accessToken.Trim();
        this.log = log;
        store = new RemoteRelayStore(dataPath, RemotePayloadCrypto.MaximumEnvelopeBytes);
        listener = new HttpListener();
        TryConfigureListenerTimeouts(listener);
        listener.Prefixes.Add("http://" + (string.IsNullOrWhiteSpace(listenHost) ? "+" : listenHost.Trim()) + ":" + port + "/");
    }

    internal static bool TryConfigureListenerTimeouts(HttpListener target)
    {
        if (target == null) return false;
        try
        {
            target.TimeoutManager.HeaderWait = HeaderWaitTimeout;
            target.TimeoutManager.EntityBody = EntityBodyTimeout;
            target.TimeoutManager.IdleConnection = IdleConnectionTimeout;
            target.TimeoutManager.DrainEntityBody = TimeSpan.FromSeconds(5);
            target.TimeoutManager.MinSendBytesPerSecond = 1024;
            return true;
        }
        catch (PlatformNotSupportedException) { return false; }
        catch (NotImplementedException) { return false; }
        catch (HttpListenerException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void Start()
    {
        listener.Start();
        listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "Sensor Readout remote server" };
        listenerThread.Start();
    }

    public void Stop()
    {
        stopping = true;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        if (listenerThread != null && listenerThread.IsAlive && Thread.CurrentThread != listenerThread)
        {
            try { listenerThread.Join(3000); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void ListenLoop()
    {
        while (!stopping)
        {
            try
            {
                var context = listener.GetContext();
                if (!requestSlots.Wait(0))
                {
                    WriteText(context, 503, "Server is busy");
                    try { context.Response.Close(); } catch { }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Handle(context); }
                    finally { requestSlots.Release(); }
                });
            }
            catch (HttpListenerException)
            {
                if (!stopping) Log("Embedded remote server listener stopped unexpectedly.");
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception error)
            {
                Log("Embedded remote server accept failed: " + error.GetType().Name + ": " + error.Message);
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var request = context.Request;
            var path = request.Url == null ? "/" : request.Url.AbsolutePath;
            if (string.Equals(path, "/api/v1/health", StringComparison.Ordinal) && (request.HttpMethod == "GET" || request.HttpMethod == "HEAD"))
            {
                WriteJson(context, 200, new { Name = "Sensor Readout Server", ProtocolVersion = 1 });
                return;
            }
            if (!Authorized(request))
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer");
                WriteText(context, 401, "Unauthorized");
                return;
            }

            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5 && IsApiPrefix(parts) && string.Equals(parts[4], "machines", StringComparison.Ordinal) && request.HttpMethod == "GET")
            {
                WriteJson(context, 200, new { ProtocolVersion = 1, Machines = store.ListMachines(parts[3]) });
                return;
            }
            if ((parts.Length != 6 && parts.Length != 7 && parts.Length != 8) || !IsApiPrefix(parts) || !string.Equals(parts[4], "machines", StringComparison.Ordinal))
            {
                WriteText(context, 404, "Not found");
                return;
            }

            var spaceId = parts[3];
            var machineId = parts[5];
            if (parts.Length == 6 && request.HttpMethod == "DELETE")
            {
                store.DeleteMachine(spaceId, machineId, MachineWriteToken(request));
                WriteJson(context, 200, new { Deleted = true });
                return;
            }
            if (parts.Length == 6)
            {
                WriteText(context, 405, "Method not allowed");
                return;
            }
            var action = parts[6];
            if (action == "commands" && parts.Length == 7 && request.HttpMethod == "GET")
            {
                WriteJson(context, 200, new { ProtocolVersion = 1, Commands = store.GetCommands(spaceId, machineId, MachineWriteToken(request)) });
                return;
            }
            if (action == "commands" && parts.Length == 8 && request.HttpMethod == "POST")
            {
                store.PutCommand(spaceId, machineId, parts[7], ReadPayload(request));
                WriteJson(context, 201, new { Accepted = true });
                return;
            }
            if (action == "commands" && parts.Length == 8 && request.HttpMethod == "DELETE")
            {
                store.DeleteCommand(spaceId, machineId, parts[7], MachineWriteToken(request));
                WriteJson(context, 200, new { Deleted = true });
                return;
            }
            if (parts.Length != 7)
            {
                WriteText(context, 404, "Not found");
                return;
            }
            if (action == "snapshot" && request.HttpMethod == "PUT")
            {
                var sequence = PositiveQueryLong(request, "sequence");
                var metadata = store.PutSnapshot(spaceId, machineId, sequence, ReadPayload(request), MachineWriteToken(request));
                WriteJson(context, 200, metadata);
                return;
            }
            if (action == "snapshot" && (request.HttpMethod == "GET" || request.HttpMethod == "HEAD"))
            {
                RemoteRelayMetadata metadata;
                var payload = store.GetSnapshot(spaceId, machineId, out metadata);
                context.Response.Headers["X-SR-Snapshot-Sequence"] = metadata.SnapshotSequence.ToString();
                context.Response.Headers["X-SR-Latest-Sequence"] = metadata.LatestSequence.ToString();
                WriteBytes(context, 200, payload, "application/octet-stream");
                return;
            }
            if (action == "deltas" && request.HttpMethod == "POST")
            {
                var sequence = PositiveQueryLong(request, "sequence");
                WriteJson(context, 200, store.AppendDelta(spaceId, machineId, sequence, ReadPayload(request), MachineWriteToken(request)));
                return;
            }
            if (action == "deltas" && request.HttpMethod == "GET")
            {
                var after = NonnegativeQueryLong(request, "after");
                RemoteRelayMetadata metadata;
                var deltas = store.GetDeltas(spaceId, machineId, after, out metadata);
                WriteJson(context, 200, new
                {
                    ProtocolVersion = 1,
                    SnapshotSequence = metadata.SnapshotSequence,
                    LatestSequence = metadata.LatestSequence,
                    Deltas = deltas
                });
                return;
            }
            if (action == "heartbeat" && request.HttpMethod == "POST")
            {
                WriteJson(context, 200, store.Heartbeat(spaceId, machineId, MachineWriteToken(request)));
                return;
            }
            WriteText(context, 405, "Method not allowed");
        }
        catch (RemoteRelayConflict error)
        {
            context.Response.Headers["X-SR-Latest-Sequence"] = error.LatestSequence.ToString();
            WriteText(context, 409, "Remote sequence changed");
        }
        catch (RemoteRelaySnapshotRequired)
        {
            WriteText(context, 428, "A complete snapshot is required");
        }
        catch (RemoteRelayUnauthorized)
        {
            WriteText(context, 403, "The computer publishing credential was rejected");
        }
        catch (RemoteRelayCapacityExceeded)
        {
            WriteText(context, 507, "The embedded remote server storage limit has been reached");
        }
        catch (FileNotFoundException)
        {
            WriteText(context, 404, "Not found");
        }
        catch (InvalidDataException error)
        {
            WriteText(context, 400, error.Message);
        }
        catch (Exception error)
        {
            Log("Embedded remote server request failed: " + error);
            WriteText(context, 500, "Server request failed");
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        var supplied = request.Headers["Authorization"] ?? "";
        var accepted = ConstantTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes("Bearer " + accessToken));
        if (!accepted)
        {
            Log("Embedded remote server rejected a request with an invalid access token.");
        }
        return accepted;
    }

    private byte[] ReadPayload(HttpListenerRequest request)
    {
        if (request.ContentLength64 < 1 || request.ContentLength64 > RemotePayloadCrypto.MaximumEnvelopeBytes)
        {
            throw new InvalidDataException("Encrypted payload is empty or too large.");
        }
        var output = new byte[checked((int)request.ContentLength64)];
        var offset = 0;
        while (offset < output.Length)
        {
            var count = request.InputStream.Read(output, offset, output.Length - offset);
            if (count <= 0) throw new InvalidDataException("Encrypted payload ended unexpectedly.");
            offset += count;
        }
        return output;
    }

    private static string MachineWriteToken(HttpListenerRequest request)
    {
        return (request.Headers["X-SR-Machine-Token"] ?? "").Trim();
    }

    private static bool IsApiPrefix(string[] parts)
    {
        return parts.Length >= 5 && parts[0] == "api" && parts[1] == "v1" && parts[2] == "spaces";
    }

    private static long PositiveQueryLong(HttpListenerRequest request, string name)
    {
        var value = NonnegativeQueryLong(request, name);
        if (value < 1) throw new InvalidDataException(name + " must be positive.");
        return value;
    }

    private static long NonnegativeQueryLong(HttpListenerRequest request, string name)
    {
        long value;
        if (!long.TryParse(request.QueryString[name], out value) || value < 0)
        {
            throw new InvalidDataException(name + " must be a non-negative number.");
        }
        return value;
    }

    private static void WriteJson(HttpListenerContext context, int status, object value)
    {
        WriteBytes(context, status, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None)), "application/json; charset=utf-8");
    }

    private static void WriteText(HttpListenerContext context, int status, string value)
    {
        WriteBytes(context, status, Encoding.UTF8.GetBytes(value ?? ""), "text/plain; charset=utf-8");
    }

    private static void WriteBytes(HttpListenerContext context, int status, byte[] value, string contentType)
    {
        if (context.Response.OutputStream == null) return;
        var body = value ?? new byte[0];
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.LongLength;
        if (context.Request.HttpMethod != "HEAD" && body.Length > 0)
        {
            context.Response.OutputStream.Write(body, 0, body.Length);
        }
    }

    private static bool ConstantTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
        return difference == 0;
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }
}

internal sealed class RemoteRelayStore
{
    private const string SpaceDirectoryName = "S";
    private const string MachineDirectoryName = "M";
    private const string CommandDirectoryName = "C";
    private const int MaximumDeltasPerMachine = 64;
    private const long MaximumDeltaBytesPerMachine = 16L * 1024 * 1024;
    private const long MaximumBufferedDeltaBytes = 64L * 1024 * 1024;
    private const long ActivityPersistIntervalMilliseconds = 60L * 60 * 1000;
    private const int MaximumMachinesPerSpace = 128;
    private const int DefaultMaximumMachinesTotal = 256;
    private const int DefaultMaximumSpaces = 32;
    private const long DefaultMaximumStoredBytes = 512L * 1024 * 1024;
    private const int MaximumCommandsPerMachine = 32;
    private const int MaximumCommandBytes = 64 * 1024;
    private const string SnapshotFileName = "snapshot.bin";
    private const string MetadataFileName = "metadata.json";
    private const string PendingSnapshotFileName = "snapshot.next";
    private const string PendingMetadataFileName = "metadata.next";
    private const string CheckpointMarkerFileName = "checkpoint.pending";
    private readonly string root;
    private readonly int maximumPayloadBytes;
    private readonly long maximumStoredBytes;
    private readonly int maximumMachinesTotal;
    private readonly int maximumSpaces;
    private readonly object[] machineLocks = Enumerable.Range(0, 64).Select(index => new object()).ToArray();
    private readonly object storageGuard = new object();
    private readonly Dictionary<string, List<RemoteRelayPendingDelta>> pendingDeltas = new Dictionary<string, List<RemoteRelayPendingDelta>>(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteRelayMetadata> volatileMetadata = new Dictionary<string, RemoteRelayMetadata>(StringComparer.Ordinal);
    private long storedBytes;
    private long bufferedDeltaBytes;
    private int storedMachineCount;
    private int storedSpaceCount;

    public RemoteRelayStore(string root, int maximumPayloadBytes)
        : this(root, maximumPayloadBytes, DefaultMaximumStoredBytes, DefaultMaximumMachinesTotal, DefaultMaximumSpaces)
    {
    }

    internal RemoteRelayStore(string root, int maximumPayloadBytes, long maximumStoredBytes, int maximumMachinesTotal, int maximumSpaces)
    {
        if (maximumPayloadBytes < 1 || maximumStoredBytes < 1 || maximumMachinesTotal < 1 || maximumSpaces < 1)
        {
            throw new ArgumentOutOfRangeException("maximumStoredBytes", "Remote relay capacity limits must be positive.");
        }
        this.root = Path.GetFullPath(root);
        this.maximumPayloadBytes = maximumPayloadBytes;
        this.maximumStoredBytes = maximumStoredBytes;
        this.maximumMachinesTotal = maximumMachinesTotal;
        this.maximumSpaces = maximumSpaces;
        Directory.CreateDirectory(this.root);
        RecoverPendingCheckpoints();
        InitializeStorageUsage();
    }

    public List<RemoteMachineIndexEntry> ListMachines(string spaceId)
    {
        ValidateId(spaceId);
        var path = Path.Combine(root, SpaceDirectoryName, StorageComponent(spaceId), MachineDirectoryName);
        if (!Directory.Exists(path)) return new List<RemoteMachineIndexEntry>();
        var output = new List<RemoteMachineIndexEntry>();
        lock (storageGuard)
        {
            foreach (var directory in Directory.GetDirectories(path))
            {
                RemoteRelayMetadata metadata;
                try { metadata = EffectiveMetadata(spaceId, directory); }
                catch (InvalidDataException) { continue; }
                var machineId = metadata.MachineId;
                if (!ValidId(machineId) || !File.Exists(Path.Combine(directory, SnapshotFileName))) continue;
                output.Add(new RemoteMachineIndexEntry
                {
                    MachineId = machineId,
                    SnapshotSequence = metadata.SnapshotSequence,
                    LatestSequence = metadata.LatestSequence,
                    LastSeenUnixMs = metadata.LastSeenUnixMs
                });
            }
        }
        return output.OrderBy(item => item.MachineId).ToList();
    }

    public RemoteRelayMetadata PutSnapshot(string spaceId, string machineId, long sequence, byte[] payload, string machineWriteToken)
    {
        ValidatePayload(payload);
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                var snapshotPath = Path.Combine(directory, SnapshotFileName);
                var metadataPath = Path.Combine(directory, MetadataFileName);
                var newMachine = !File.Exists(snapshotPath);
                if (!newMachine && !File.Exists(metadataPath))
                {
                    throw new InvalidDataException("Remote machine ownership metadata is missing.");
                }
                var stableMetadata = LoadMetadata(directory);
                VerifyStoredIdentity(stableMetadata, spaceId, machineId);
                VerifyMachineWriteToken(stableMetadata, machineWriteToken, newMachine && string.IsNullOrWhiteSpace(stableMetadata.MachineWriteTokenHash));
                var metadata = EffectiveMetadata(spaceId, machineId, directory);
                if (string.IsNullOrWhiteSpace(metadata.MachineWriteTokenHash)) metadata.MachineWriteTokenHash = stableMetadata.MachineWriteTokenHash;
                var machinesInSpace = CountStoredMachinesInSpace(spaceId);
                var spaceHadMachines = machinesInSpace > 0;
                if (newMachine && (storedMachineCount >= maximumMachinesTotal || machinesInSpace >= MaximumMachinesPerSpace))
                {
                    throw new RemoteRelayCapacityExceeded();
                }
                if (newMachine && !spaceHadMachines && storedSpaceCount >= maximumSpaces)
                {
                    throw new RemoteRelayCapacityExceeded();
                }
                if (metadata.LatestSequence > sequence) throw new RemoteRelayConflict(metadata.LatestSequence);
                metadata.SnapshotSequence = sequence;
                metadata.LatestSequence = sequence;
                metadata.LastSeenUnixMs = UtcNowMilliseconds();
                metadata.DeltaCount = 0;
                metadata.DeltaBytes = 0;
                metadata.SpaceId = spaceId;
                metadata.MachineId = machineId;
                CommitCheckpoint(directory, payload, metadata);

                var deltaPath = Path.Combine(directory, "Deltas");
                if (Directory.Exists(deltaPath))
                {
                    foreach (var file in Directory.GetFiles(deltaPath, "*.bin"))
                    {
                        long deltaSequence;
                        if (long.TryParse(Path.GetFileNameWithoutExtension(file), out deltaSequence) && deltaSequence <= sequence)
                        {
                            DeleteFileBounded(file);
                        }
                    }
                }
                ClearPending(spaceId, machineId);
                if (newMachine)
                {
                    storedMachineCount++;
                    if (!spaceHadMachines) storedSpaceCount++;
                }
                return metadata;
            }
        }
    }

    public byte[] GetSnapshot(string spaceId, string machineId, out RemoteRelayMetadata metadata)
    {
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                var path = Path.Combine(directory, SnapshotFileName);
                if (!File.Exists(path)) throw new FileNotFoundException();
                var payload = File.ReadAllBytes(path);
                ValidatePayload(payload);
                metadata = EffectiveMetadata(spaceId, machineId, directory);
                VerifyStoredIdentity(metadata, spaceId, machineId);
                return payload;
            }
        }
    }

    public RemoteRelayMetadata AppendDelta(string spaceId, string machineId, long sequence, byte[] payload, string machineWriteToken)
    {
        ValidatePayload(payload);
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                if (!File.Exists(Path.Combine(directory, "snapshot.bin"))) throw new RemoteRelaySnapshotRequired();
                var stableMetadata = LoadMetadata(directory);
                VerifyStoredIdentity(stableMetadata, spaceId, machineId);
                VerifyMachineWriteToken(stableMetadata, machineWriteToken, false);
                var metadata = EffectiveMetadata(spaceId, machineId, directory);
                if (metadata.DeltaCount >= MaximumDeltasPerMachine || metadata.DeltaBytes + payload.LongLength > MaximumDeltaBytesPerMachine) throw new RemoteRelaySnapshotRequired();
                if (sequence != metadata.LatestSequence + 1) throw new RemoteRelayConflict(metadata.LatestSequence);
                if (bufferedDeltaBytes + payload.LongLength > MaximumBufferedDeltaBytes) throw new RemoteRelaySnapshotRequired();
                var key = MachineKey(spaceId, machineId);
                List<RemoteRelayPendingDelta> pending;
                if (!pendingDeltas.TryGetValue(key, out pending))
                {
                    pending = new List<RemoteRelayPendingDelta>();
                    pendingDeltas[key] = pending;
                }
                pending.Add(new RemoteRelayPendingDelta { Sequence = sequence, Payload = payload });
                bufferedDeltaBytes += payload.LongLength;
                metadata.LatestSequence = sequence;
                metadata.LastSeenUnixMs = UtcNowMilliseconds();
                metadata.DeltaCount++;
                metadata.DeltaBytes += payload.LongLength;
                volatileMetadata[key] = CloneMetadata(metadata);
                PersistActivityIfDue(directory, metadata);
                return metadata;
            }
        }
    }

    public List<RemoteDeltaEnvelope> GetDeltas(string spaceId, string machineId, long after, out RemoteRelayMetadata metadata)
    {
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                metadata = EffectiveMetadata(spaceId, machineId, directory);
                VerifyStoredIdentity(metadata, spaceId, machineId);
                if (after < metadata.SnapshotSequence) throw new RemoteRelaySnapshotRequired();
                var output = new List<RemoteDeltaEnvelope>();
                var deltaPath = Path.Combine(directory, "Deltas");
                if (Directory.Exists(deltaPath))
                {
                    foreach (var file in Directory.GetFiles(deltaPath, "*.bin").OrderBy(item => item, StringComparer.Ordinal))
                    {
                        long sequence;
                        if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out sequence) || sequence <= after) continue;
                        var payload = File.ReadAllBytes(file);
                        ValidatePayload(payload);
                        output.Add(new RemoteDeltaEnvelope { Sequence = sequence, Payload = Convert.ToBase64String(payload) });
                    }
                }
                List<RemoteRelayPendingDelta> pending;
                if (pendingDeltas.TryGetValue(MachineKey(spaceId, machineId), out pending))
                {
                    output.AddRange(pending.Where(item => item.Sequence > after).Select(item => new RemoteDeltaEnvelope
                    {
                        Sequence = item.Sequence,
                        Payload = Convert.ToBase64String(item.Payload)
                    }));
                }
                return output.OrderBy(item => item.Sequence).ToList();
            }
        }
    }

    public RemoteRelayMetadata Heartbeat(string spaceId, string machineId, string machineWriteToken)
    {
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                if (!File.Exists(Path.Combine(directory, "snapshot.bin"))) throw new RemoteRelaySnapshotRequired();
                var stableMetadata = LoadMetadata(directory);
                VerifyStoredIdentity(stableMetadata, spaceId, machineId);
                VerifyMachineWriteToken(stableMetadata, machineWriteToken, false);
                var metadata = EffectiveMetadata(spaceId, machineId, directory);
                metadata.LastSeenUnixMs = UtcNowMilliseconds();
                volatileMetadata[MachineKey(spaceId, machineId)] = CloneMetadata(metadata);
                PersistActivityIfDue(directory, metadata);
                return metadata;
            }
        }
    }

    public void PutCommand(string spaceId, string machineId, string commandId, byte[] payload)
    {
        ValidateId(commandId);
        if (payload == null || payload.Length < 1 || payload.Length > MaximumCommandBytes) throw new InvalidDataException("Encrypted command is empty or too large.");
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                if (!File.Exists(Path.Combine(directory, "snapshot.bin"))) throw new RemoteRelaySnapshotRequired();
                var commandsPath = Path.Combine(directory, CommandDirectoryName);
                Directory.CreateDirectory(commandsPath);
                if (!File.Exists(Path.Combine(commandsPath, StorageComponent(commandId) + ".bin")) && Directory.GetFiles(commandsPath, "*.bin").Length >= MaximumCommandsPerMachine)
                {
                    throw new InvalidDataException("The remote command queue is full.");
                }
                AtomicWriteBounded(Path.Combine(commandsPath, StorageComponent(commandId) + ".bin"), payload);
                AtomicWriteBounded(Path.Combine(commandsPath, StorageComponent(commandId) + ".id"), Encoding.UTF8.GetBytes(commandId));
            }
        }
    }

    public List<RemoteCommandEnvelope> GetCommands(string spaceId, string machineId, string machineWriteToken)
    {
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            if (!File.Exists(Path.Combine(directory, "snapshot.bin"))) throw new RemoteRelaySnapshotRequired();
            VerifyMachineWriteToken(LoadMetadata(directory), machineWriteToken, false);
            var commandsPath = Path.Combine(directory, CommandDirectoryName);
            if (!Directory.Exists(commandsPath)) return new List<RemoteCommandEnvelope>();
            var output = new List<RemoteCommandEnvelope>();
            foreach (var file in Directory.GetFiles(commandsPath, "*.bin").OrderBy(path => File.GetCreationTimeUtc(path)))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var idPath = Path.Combine(commandsPath, stem + ".id");
                if (!File.Exists(idPath)) continue;
                var commandId = File.ReadAllText(idPath).Trim();
                if (!ValidId(commandId)) continue;
                var payload = File.ReadAllBytes(file);
                if (payload.Length < 1 || payload.Length > MaximumCommandBytes) continue;
                output.Add(new RemoteCommandEnvelope { CommandId = commandId, Payload = Convert.ToBase64String(payload) });
            }
            return output.Take(MaximumCommandsPerMachine).ToList();
        }
    }

    public void DeleteCommand(string spaceId, string machineId, string commandId, string machineWriteToken)
    {
        ValidateId(commandId);
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                VerifyMachineWriteToken(LoadMetadata(directory), machineWriteToken, false);
                var stem = Path.Combine(directory, CommandDirectoryName, StorageComponent(commandId));
                DeleteFileBounded(stem + ".bin");
                DeleteFileBounded(stem + ".id");
            }
        }
    }

    public void DeleteMachine(string spaceId, string machineId, string machineWriteToken)
    {
        var directory = MachineDirectory(spaceId, machineId);
        lock (MachineLock(spaceId, machineId))
        {
            lock (storageGuard)
            {
                if (!Directory.Exists(directory)) throw new FileNotFoundException();
                VerifyMachineWriteToken(LoadMetadata(directory), machineWriteToken, false);
                var hadSnapshot = File.Exists(Path.Combine(directory, "snapshot.bin"));
                ClearPending(spaceId, machineId);
                DeleteDirectoryBounded(directory);
                if (hadSnapshot && storedMachineCount > 0) storedMachineCount--;
                if (hadSnapshot && CountStoredMachinesInSpace(spaceId) == 0 && storedSpaceCount > 0) storedSpaceCount--;
            }
        }
    }

    private string MachineDirectory(string spaceId, string machineId)
    {
        ValidateId(spaceId);
        ValidateId(machineId);
        return Path.Combine(root, SpaceDirectoryName, StorageComponent(spaceId), MachineDirectoryName, StorageComponent(machineId));
    }

    private object MachineLock(string spaceId, string machineId)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(spaceId + "/" + machineId));
            var slot = (hash[0] | (hash[1] << 8)) % machineLocks.Length;
            return machineLocks[slot];
        }
    }

    private RemoteRelayMetadata LoadMetadata(string directory)
    {
        var path = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(path))
        {
            if (File.Exists(Path.Combine(directory, SnapshotFileName)))
            {
                throw new InvalidDataException("Remote machine ownership metadata is missing.");
            }
            return new RemoteRelayMetadata();
        }
        try
        {
            var metadata = JsonConvert.DeserializeObject<RemoteRelayMetadata>(File.ReadAllText(path));
            ValidateStoredMetadata(metadata);
            return metadata;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidDataException("Remote machine ownership metadata is unreadable.", error);
        }
    }

    private static void ValidateStoredMetadata(RemoteRelayMetadata metadata)
    {
        if (metadata == null || metadata.ProtocolVersion != 1 || !ValidId(metadata.SpaceId) || !ValidId(metadata.MachineId) ||
            metadata.SnapshotSequence < 1 || metadata.LatestSequence < metadata.SnapshotSequence ||
            metadata.DeltaCount < 0 || metadata.DeltaCount > MaximumDeltasPerMachine ||
            metadata.DeltaBytes < 0 || metadata.DeltaBytes > MaximumDeltaBytesPerMachine ||
            !ValidMachineWriteTokenHash(metadata.MachineWriteTokenHash))
        {
            throw new InvalidDataException("Remote machine ownership metadata is missing or invalid.");
        }
    }

    private static bool ValidMachineWriteTokenHash(string value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && Convert.FromBase64String(value).Length == 32;
        }
        catch
        {
            return false;
        }
    }

    private static string MachineKey(string spaceId, string machineId)
    {
        return spaceId + "/" + machineId;
    }

    private RemoteRelayMetadata EffectiveMetadata(string spaceId, string machineId, string directory)
    {
        RemoteRelayMetadata metadata;
        return volatileMetadata.TryGetValue(MachineKey(spaceId, machineId), out metadata) ? CloneMetadata(metadata) : LoadMetadata(directory);
    }

    private RemoteRelayMetadata EffectiveMetadata(string spaceId, string directory)
    {
        var metadata = LoadMetadata(directory);
        if (!ValidId(metadata.MachineId)) return metadata;
        return EffectiveMetadata(spaceId, metadata.MachineId, directory);
    }

    private void ClearPending(string spaceId, string machineId)
    {
        var key = MachineKey(spaceId, machineId);
        List<RemoteRelayPendingDelta> pending;
        if (pendingDeltas.TryGetValue(key, out pending))
        {
            bufferedDeltaBytes = Math.Max(0, bufferedDeltaBytes - pending.Sum(item => item.Payload == null ? 0 : item.Payload.LongLength));
            pendingDeltas.Remove(key);
        }
        volatileMetadata.Remove(key);
    }

    private void PersistActivityIfDue(string directory, RemoteRelayMetadata effective)
    {
        var stable = LoadMetadata(directory);
        if (effective.LastSeenUnixMs - stable.LastSeenUnixMs < ActivityPersistIntervalMilliseconds) return;
        stable.LastSeenUnixMs = effective.LastSeenUnixMs;
        SaveMetadata(directory, stable);
    }

    private static RemoteRelayMetadata CloneMetadata(RemoteRelayMetadata source)
    {
        return new RemoteRelayMetadata
        {
            ProtocolVersion = source.ProtocolVersion,
            SpaceId = source.SpaceId,
            MachineId = source.MachineId,
            SnapshotSequence = source.SnapshotSequence,
            LatestSequence = source.LatestSequence,
            LastSeenUnixMs = source.LastSeenUnixMs,
            DeltaCount = source.DeltaCount,
            DeltaBytes = source.DeltaBytes,
            MachineWriteTokenHash = source.MachineWriteTokenHash
        };
    }

    private void InitializeStorageUsage()
    {
        storedBytes = CalculateDirectoryBytes(root);
        var snapshots = Directory.GetFiles(root, "snapshot.bin", SearchOption.AllDirectories);
        storedMachineCount = snapshots.Length;
        var spaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            var machineDirectory = Path.GetDirectoryName(snapshot);
            var machinesDirectory = string.IsNullOrWhiteSpace(machineDirectory) ? null : Path.GetDirectoryName(machineDirectory);
            var spaceDirectory = string.IsNullOrWhiteSpace(machinesDirectory) ? null : Path.GetDirectoryName(machinesDirectory);
            if (!string.IsNullOrWhiteSpace(spaceDirectory)) spaces.Add(spaceDirectory);
        }
        storedSpaceCount = spaces.Count;
    }

    private int CountStoredMachinesInSpace(string spaceId)
    {
        var machinesPath = Path.Combine(root, SpaceDirectoryName, StorageComponent(spaceId), MachineDirectoryName);
        if (!Directory.Exists(machinesPath)) return 0;
        return Directory.GetDirectories(machinesPath).Count(directory => File.Exists(Path.Combine(directory, "snapshot.bin")));
    }

    private void SaveMetadata(string directory, RemoteRelayMetadata metadata)
    {
        metadata.ProtocolVersion = 1;
        AtomicWriteBounded(Path.Combine(directory, MetadataFileName), SerializeMetadata(metadata));
    }

    private static byte[] SerializeMetadata(RemoteRelayMetadata metadata)
    {
        return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(metadata, Formatting.Indented));
    }

    private void CommitCheckpoint(string directory, byte[] snapshot, RemoteRelayMetadata metadata)
    {
        metadata.ProtocolVersion = 1;
        ValidateStoredMetadata(metadata);
        var pendingSnapshot = Path.Combine(directory, PendingSnapshotFileName);
        var pendingMetadata = Path.Combine(directory, PendingMetadataFileName);
        var marker = Path.Combine(directory, CheckpointMarkerFileName);

        if (File.Exists(marker)) CompleteCheckpointBounded(directory);
        DeleteFileBounded(pendingSnapshot);
        DeleteFileBounded(pendingMetadata);

        try
        {
            AtomicWriteBounded(pendingSnapshot, snapshot);
            AtomicWriteBounded(pendingMetadata, SerializeMetadata(metadata));
            AtomicWriteBounded(marker, new byte[] { 1 });
            CompleteCheckpointBounded(directory);
        }
        catch (Exception checkpointError)
        {
            if (File.Exists(marker))
            {
                try
                {
                    CompleteCheckpointBounded(directory);
                    return;
                }
                catch (Exception recoveryError)
                {
                    throw new AggregateException("The remote machine checkpoint failed and could not be recovered safely.", checkpointError, recoveryError);
                }
            }

            DeleteFileBounded(pendingSnapshot);
            DeleteFileBounded(pendingMetadata);
            throw;
        }
    }

    private void CompleteCheckpointBounded(string directory)
    {
        PromoteStagedFileBounded(Path.Combine(directory, PendingSnapshotFileName), Path.Combine(directory, SnapshotFileName));
        PromoteStagedFileBounded(Path.Combine(directory, PendingMetadataFileName), Path.Combine(directory, MetadataFileName));
        LoadMetadata(directory);
        DeleteFileBounded(Path.Combine(directory, CheckpointMarkerFileName));
    }

    private void PromoteStagedFileBounded(string stagedPath, string destinationPath)
    {
        if (!File.Exists(stagedPath))
        {
            if (!File.Exists(destinationPath)) throw new InvalidDataException("A pending remote machine checkpoint is incomplete.");
            return;
        }

        var oldLength = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
        if (File.Exists(destinationPath))
        {
            File.Replace(stagedPath, destinationPath, null);
        }
        else
        {
            File.Move(stagedPath, destinationPath);
        }
        storedBytes = Math.Max(0, storedBytes - oldLength);
    }

    private void RecoverPendingCheckpoints()
    {
        foreach (var marker in Directory.GetFiles(root, CheckpointMarkerFileName, SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(marker);
            PromoteStagedFile(Path.Combine(directory, PendingSnapshotFileName), Path.Combine(directory, SnapshotFileName));
            PromoteStagedFile(Path.Combine(directory, PendingMetadataFileName), Path.Combine(directory, MetadataFileName));
            LoadMetadata(directory);
            File.Delete(marker);
        }

        foreach (var name in new[] { PendingSnapshotFileName, PendingMetadataFileName })
        {
            foreach (var orphan in Directory.GetFiles(root, name, SearchOption.AllDirectories))
            {
                File.Delete(orphan);
            }
        }
    }

    private static void PromoteStagedFile(string stagedPath, string destinationPath)
    {
        if (!File.Exists(stagedPath))
        {
            if (!File.Exists(destinationPath)) throw new InvalidDataException("A pending remote machine checkpoint is incomplete.");
            return;
        }
        if (File.Exists(destinationPath)) File.Replace(stagedPath, destinationPath, null);
        else File.Move(stagedPath, destinationPath);
    }

    private void ValidatePayload(byte[] payload)
    {
        if (payload == null || payload.Length < 1 || payload.Length > maximumPayloadBytes)
        {
            throw new InvalidDataException("Encrypted payload is empty or too large.");
        }
    }

    private void AtomicWriteBounded(string path, byte[] value)
    {
        lock (storageGuard)
        {
            var oldLength = File.Exists(path) ? new FileInfo(path).Length : 0;
            var projected = storedBytes - oldLength + (value == null ? 0 : value.LongLength);
            if (projected > maximumStoredBytes) throw new RemoteRelayCapacityExceeded();
            AtomicWrite(path, value);
            storedBytes = Math.Max(0, projected);
        }
    }

    private void DeleteFileBounded(string path)
    {
        lock (storageGuard)
        {
            if (!File.Exists(path)) return;
            long length;
            try { length = new FileInfo(path).Length; }
            catch { length = 0; }
            try
            {
                File.Delete(path);
                storedBytes = Math.Max(0, storedBytes - length);
            }
            catch
            {
            }
        }
    }

    private void DeleteDirectoryBounded(string path)
    {
        lock (storageGuard)
        {
            var length = CalculateDirectoryBytes(path);
            Directory.Delete(path, true);
            storedBytes = Math.Max(0, storedBytes - length);
        }
    }

    private static long CalculateDirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { }
        }
        return total;
    }

    private static void AtomicWrite(string path, byte[] value)
    {
        var directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        // Keep the staging name short because portable installs may already be
        // close to the legacy Windows path limit. It remains random and local
        // to the destination directory so File.Replace stays atomic.
        string temp;
        do
        {
            temp = Path.Combine(directory, ".t-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }
        while (File.Exists(temp));
        try
        {
            File.WriteAllBytes(temp, value);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void ValidateId(string value)
    {
        if (!ValidId(value)) throw new InvalidDataException("Invalid remote identifier.");
    }

    private static bool ValidId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length >= 32 && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
    }

    private static string StorageComponent(string value)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var shortened = new byte[12];
            Buffer.BlockCopy(hash, 0, shortened, 0, shortened.Length);
            return Convert.ToBase64String(shortened).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    private static void VerifyStoredIdentity(RemoteRelayMetadata metadata, string spaceId, string machineId)
    {
        if (metadata == null) return;
        if ((!string.IsNullOrWhiteSpace(metadata.SpaceId) && !string.Equals(metadata.SpaceId, spaceId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(metadata.MachineId) && !string.Equals(metadata.MachineId, machineId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("A remote storage identifier collision was detected.");
        }
    }

    private static void VerifyMachineWriteToken(RemoteRelayMetadata metadata, string supplied, bool allowRegistration)
    {
        var token = (supplied ?? "").Trim();
        if (token.Length < 32 || token.Length > 4096) throw new RemoteRelayUnauthorized();
        string hash;
        using (var sha = SHA256.Create())
        {
            hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
        }
        if (string.IsNullOrWhiteSpace(metadata.MachineWriteTokenHash) && allowRegistration)
        {
            metadata.MachineWriteTokenHash = hash;
            return;
        }
        if (!ConstantTimeStringEquals(metadata.MachineWriteTokenHash, hash)) throw new RemoteRelayUnauthorized();
    }

    private static bool ConstantTimeStringEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? "");
        var rightBytes = Encoding.UTF8.GetBytes(right ?? "");
        if (leftBytes.Length != rightBytes.Length) return false;
        var difference = 0;
        for (var index = 0; index < leftBytes.Length; index++) difference |= leftBytes[index] ^ rightBytes[index];
        return difference == 0;
    }

    private static long UtcNowMilliseconds()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }
}

internal sealed class RemoteRelayMetadata
{
    public int ProtocolVersion = 1;
    public string SpaceId;
    public string MachineId;
    public long SnapshotSequence;
    public long LatestSequence;
    public long LastSeenUnixMs;
    public int DeltaCount;
    public long DeltaBytes;
    public string MachineWriteTokenHash;
}

internal sealed class RemoteRelayPendingDelta
{
    public long Sequence;
    public byte[] Payload;
}

internal sealed class RemoteRelayConflict : Exception
{
    public RemoteRelayConflict(long latestSequence) { LatestSequence = latestSequence; }
    public long LatestSequence { get; private set; }
}

internal sealed class RemoteRelaySnapshotRequired : Exception
{
}

internal sealed class RemoteRelayUnauthorized : Exception
{
}

internal sealed class RemoteRelayCapacityExceeded : Exception
{
}
