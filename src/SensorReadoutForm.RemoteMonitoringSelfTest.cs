using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm
{
    private void SelfTestRemoteMonitoringCrypto()
    {
        Require(!new RemoteConnectionSetting().PublishThisComputer, "New remote connections must not share this computer until the user explicitly opts in.");
        Require(remoteComputersMenuItem != null && remoteComputersMenuItem.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.R), "Remote computers lost its Control Shift R shortcut.");
        Require(remoteComputersMenuItem.Text.IndexOf("Ctrl+Shift+R", StringComparison.OrdinalIgnoreCase) >= 0, "Remote computers Options item does not expose Control Shift R.");
        Require(returnToLiveReadingsMenuItem != null && returnToLiveReadingsMenuItem.ShortcutKeys == (Keys.Control | Keys.R), "Return to this computer lost its Control R shortcut.");
        var previousReportViewMode = reportViewMode;
        var previousRemoteViewMode = remoteViewMode;
        try
        {
            reportViewMode = false;
            remoteViewMode = true;
            UpdateReportViewMenuState();
            Require(returnToLiveReadingsMenuItem.Available && returnToLiveReadingsMenuItem.Enabled, "Return to this computer was not available during remote viewing.");
            Require(returnToLiveReadingsMenuItem.Text.IndexOf(T("ui.Return to this computer", "Return to this computer"), StringComparison.OrdinalIgnoreCase) >= 0, "Remote viewing did not label the return command for this computer.");
            Require(returnToLiveReadingsMenuItem.Text.IndexOf("Ctrl+R", StringComparison.OrdinalIgnoreCase) >= 0, "Return to this computer did not display Control R in the File menu.");
        }
        finally
        {
            reportViewMode = previousReportViewMode;
            remoteViewMode = previousRemoteViewMode;
            UpdateReportViewMenuState();
        }
        SelfTestRemoteRefreshExpansionPreservation();
        SelfTestRemoteConnectionImportPath();
        const string password = "self-test remote password";
        const string token = "self-test server token";
        var generatedPassword = RemotePayloadCrypto.CreateMonitoringPassword();
        var secondGeneratedPassword = RemotePayloadCrypto.CreateMonitoringPassword();
        Require(generatedPassword.Length == 32, "Generated monitoring password did not have the expected length.");
        Require(generatedPassword.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_'), "Generated monitoring password contained characters that are difficult to transfer safely.");
        Require(!string.Equals(generatedPassword, secondGeneratedPassword, StringComparison.Ordinal), "Generated monitoring passwords were unexpectedly identical.");
        var snapshot = new RemoteMachineSnapshot
        {
            AppVersion = AppVersion,
            MachineId = RemotePayloadCrypto.CreateRandomId(),
            MachineName = "SELF-TEST",
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Sequence = 7,
            MemoryUnitMode = ByteUnitBinary,
            StorageUnitMode = ByteUnitDecimal,
            TransferUnitMode = ByteUnitClassic,
            Rows = new List<RemoteSensorRow>
            {
                new RemoteSensorRow
                {
                    Key = "Performance|CPU|CPU usage|self-test",
                    Type = "Performance",
                    Hardware = "CPU",
                    Name = "CPU usage",
                    Identifier = "self-test",
                    Value = 12.5f,
                    DisplayValue = "12.5%",
                    Source = "Self-test",
                    Details = new Dictionary<string, string> { { "Provider", "Self-test" } }
                }
            }
        };

        var encrypted = RemotePayloadCrypto.Encrypt(snapshot, password);
        Require(encrypted[9] == 2, "Remote snapshot did not use the current efficient encryption format.");
        Require(encrypted.Length > 64 && encrypted.Length <= RemotePayloadCrypto.MaximumEnvelopeBytes, "Remote snapshot encryption produced an invalid envelope size.");
        var decrypted = RemotePayloadCrypto.Decrypt<RemoteMachineSnapshot>(encrypted, password);
        Require(string.Equals(decrypted.MachineId, snapshot.MachineId, StringComparison.Ordinal), "Remote snapshot machine identity did not round-trip.");
        Require(decrypted.Sequence == snapshot.Sequence, "Remote snapshot sequence did not round-trip.");
        Require(decrypted.Rows != null && decrypted.Rows.Count == 1 && string.Equals(decrypted.Rows[0].DisplayValue, "12.5%", StringComparison.Ordinal), "Remote snapshot rows did not round-trip.");

        var tampered = (byte[])encrypted.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        var rejectedTamper = false;
        try
        {
            RemotePayloadCrypto.Decrypt<RemoteMachineSnapshot>(tampered, password);
        }
        catch (CryptographicException)
        {
            rejectedTamper = true;
        }
        Require(rejectedTamper, "Remote payload authentication did not reject altered data.");

        var rejectedWrongPassword = false;
        try
        {
            RemotePayloadCrypto.Decrypt<RemoteMachineSnapshot>(encrypted, "wrong password");
        }
        catch (CryptographicException)
        {
            rejectedWrongPassword = true;
        }
        Require(rejectedWrongPassword, "Remote payload authentication did not reject the wrong password.");

        var firstSpaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
        var secondSpaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
        var otherSpaceId = RemotePayloadCrypto.DeriveSpaceId(token, "other password");
        var otherTokenSpaceId = RemotePayloadCrypto.DeriveSpaceId(token + " changed", password);
        Require(string.Equals(firstSpaceId, secondSpaceId, StringComparison.Ordinal), "Remote space identity was not deterministic.");
        Require(!string.Equals(firstSpaceId, otherSpaceId, StringComparison.Ordinal), "Different remote passwords produced the same space identity.");
        Require(!string.Equals(firstSpaceId, otherTokenSpaceId, StringComparison.Ordinal), "Different server tokens produced the same remote space identity.");
        Require(string.Equals(firstSpaceId, "jf1jzmFOeE9sxSJ-iM55V7omDFQ-H_J-6M6rLTsnuMo", StringComparison.Ordinal), "Remote space identity no longer matches the Python PBKDF2-HMAC-SHA1 compatibility vector.");
        Require(firstSpaceId.Length >= 32, "Remote space identity is too short.");

        var rejectedShortPassword = false;
        try { RemotePayloadCrypto.DeriveSpaceId(token, "short"); }
        catch (ArgumentException) { rejectedShortPassword = true; }
        Require(rejectedShortPassword, "Remote monitoring accepted a password shorter than the documented minimum.");

        string normalizedServerUrl;
        foreach (var acceptedUrl in new[]
        {
            "http://127.0.0.1:48673",
            "http://192.0.2.1:48673/",
            "https://[2001:db8::1]:48673/",
            "https://sensors.example.test/srrelay"
        })
        {
            Require(RemoteServerClient.TryNormalizeServerUrl(acceptedUrl, out normalizedServerUrl), "Remote monitoring rejected a valid server URL: " + acceptedUrl);
            Require(normalizedServerUrl.EndsWith("/", StringComparison.Ordinal), "Remote monitoring did not normalize a server URL with a trailing slash.");
            new RemoteServerClient(normalizedServerUrl, "self-test-http-access-token-00000001");
        }
        foreach (var rejectedUrl in new[]
        {
            "",
            "server.example.test:48673",
            "ftp://server.example.test/",
            "https://user:password@server.example.test/",
            "https://server.example.test/?token=secret",
            "https://server.example.test/#fragment",
            "http://0.0.0.0:48673/",
            "http://[::]:48673/",
            "http://server.example.test/\r\nX-Test: injected"
        })
        {
            Require(!RemoteServerClient.TryNormalizeServerUrl(rejectedUrl, out normalizedServerUrl), "Remote monitoring accepted an unsafe or incomplete server URL: " + rejectedUrl);
        }
        Require(RemoteServerClient.ReadBounded(new MemoryStream(new byte[8]), 8).Length == 8, "Remote response limiting rejected a response exactly at its limit.");
        var oversizedResponseRejected = false;
        try { RemoteServerClient.ReadBounded(new MemoryStream(new byte[9]), 8); }
        catch (InvalidDataException) { oversizedResponseRejected = true; }
        Require(oversizedResponseRejected, "Remote response limiting accepted a response beyond its safety limit.");
        SelfTestRemoteHealthResponses();
        string normalizedExportUrl;
        string exportError;
        Require(RemoteHostConnectionExportDialog.TryNormalizeExportUrl("100.64.1.2", 48673, out normalizedExportUrl, out exportError) &&
            string.Equals(normalizedExportUrl, "http://100.64.1.2:48673/", StringComparison.Ordinal), "Remote export did not add the listening port to a VPN address.");
        Require(RemoteHostConnectionExportDialog.TryNormalizeExportUrl("https://sensors.example.test/srrelay", 48673, out normalizedExportUrl, out exportError) &&
            string.Equals(normalizedExportUrl, "https://sensors.example.test/srrelay/", StringComparison.Ordinal), "Remote export did not preserve an HTTPS reverse-proxy path.");
        Require(!RemoteHostConnectionExportDialog.TryNormalizeExportUrl("http://127.0.0.1:48673/", 48673, out normalizedExportUrl, out exportError), "Remote export accepted a loopback address that another computer cannot reach.");
        Require(!RemoteHostConnectionExportDialog.TryNormalizeExportUrl("http://0.0.0.0:48673/", 48673, out normalizedExportUrl, out exportError), "Remote export accepted a wildcard listening address.");
        Require(RemoteHostConnectionExportDialog.CandidateUrls(48673, "http://127.0.0.1:48673/").All(item => item.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) < 0), "Remote export offered a loopback address.");

        var cadenceConnection = new RemoteConnectionSetting { PollIntervalSeconds = 5 };
        var cadenceState = new RemotePublishState();
        var cadenceStart = DateTime.UtcNow;
        Require(RemoteMonitoringEngine.TryBeginPublish(cadenceConnection, cadenceState, cadenceStart), "The first remote publication was not immediately due.");
        Require(!RemoteMonitoringEngine.TryBeginPublish(cadenceConnection, cadenceState, cadenceStart.AddSeconds(2)), "Remote publication ignored its configured interval.");
        Require(RemoteMonitoringEngine.TryBeginPublish(cadenceConnection, cadenceState, cadenceStart.AddSeconds(5)), "Remote publication did not become due at its configured interval.");
        cadenceState.LastAttemptUtc = cadenceStart.AddMinutes(1);
        Require(RemoteMonitoringEngine.IsPublishDue(cadenceConnection, cadenceState, cadenceStart), "Remote publication did not recover from a backward system-clock change.");

        var protectedSecret = RemotePayloadCrypto.ProtectSecret(password);
        Require(!string.IsNullOrWhiteSpace(protectedSecret) && protectedSecret.IndexOf(password, StringComparison.Ordinal) < 0, "Remote password was not protected in local settings.");
        Require(string.Equals(RemotePayloadCrypto.UnprotectSecret(protectedSecret), password, StringComparison.Ordinal), "Protected remote password did not round-trip.");

        var previous = RemoteSnapshotCodec.CreateSnapshot(
            new[]
            {
                new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 10, DisplayValue = "10%", Source = "Self-test" },
                new SensorRow { Type = "Network", Hardware = "Ethernet", Name = "Receive rate", Identifier = "rx", Value = 1, DisplayValue = "1 KB/s", Source = "Self-test" }
            },
            snapshot.MachineId,
            "SELF-TEST",
            AppVersion,
            10,
            ByteUnitBinary,
            ByteUnitBinary,
            ByteUnitBinary);
        var current = RemoteSnapshotCodec.CreateSnapshot(
            new[]
            {
                new SensorRow { Type = "Network", Hardware = "Ethernet", Name = "Receive rate", Identifier = "rx", Value = 2, DisplayValue = "2 KB/s", Source = "Self-test" },
                new SensorRow { Type = "Temperature", Hardware = "CPU", Name = "CPU package", Identifier = "temp", Value = 50, DisplayValue = "50 C", Source = "Self-test" }
            },
            snapshot.MachineId,
            "SELF-TEST",
            AppVersion,
            11,
            ByteUnitBinary,
            ByteUnitBinary,
            ByteUnitBinary);
        var delta = RemoteSnapshotCodec.CreateDelta(previous, current);
        Require(delta.ChangedRows.Count == 2, "Remote difference did not include changed and added rows.");
        Require(delta.RemovedRowKeys.Count == 1, "Remote difference did not include a removed row.");
        Require(delta.RowOrder.Count == 2, "Remote difference did not preserve changed row order.");
        var applied = RemoteSnapshotCodec.ApplyDelta(previous, delta);
        Require(applied.Sequence == 11 && applied.Rows.Count == 2, "Remote difference did not produce the expected snapshot.");
        Require(string.Equals(applied.Rows[0].Name, "Receive rate", StringComparison.Ordinal) && string.Equals(applied.Rows[0].DisplayValue, "2 KB/s", StringComparison.Ordinal), "Remote difference did not preserve the source row order and value.");
        Require(string.Equals(applied.Rows[1].Name, "CPU package", StringComparison.Ordinal), "Remote difference did not append the new row in source order.");

        var metadataOnly = RemoteSnapshotCodec.CreateSnapshot(
            RemoteSnapshotCodec.ToSensorRows(applied),
            snapshot.MachineId,
            "RENAMED-SELF-TEST",
            "6.0.1",
            12,
            ByteUnitDecimal,
            ByteUnitClassic,
            ByteUnitDecimal,
            new[] { new RemoteFanProfileDescriptor { Id = "profile-id", Name = "Performance" } });
        var metadataDelta = RemoteSnapshotCodec.CreateDelta(applied, metadataOnly);
        Require(metadataDelta.ChangedRows.Count == 0 && metadataDelta.RemovedRowKeys.Count == 0, "Remote metadata-only change unexpectedly rewrote sensor rows.");
        Require(metadataDelta.FanProfilesChanged, "Remote fan-profile-only change was not detected.");
        var metadataApplied = RemoteSnapshotCodec.ApplyDelta(applied, metadataDelta);
        Require(metadataApplied.MachineName == "RENAMED-SELF-TEST" && metadataApplied.AppVersion == "6.0.1", "Remote difference did not update machine metadata.");
        Require(metadataApplied.MemoryUnitMode == ByteUnitDecimal && metadataApplied.StorageUnitMode == ByteUnitClassic && metadataApplied.TransferUnitMode == ByteUnitDecimal, "Remote difference did not update unit preferences.");
        Require(metadataApplied.FanProfiles.Count == 1 && metadataApplied.FanProfiles[0].Name == "Performance", "Remote difference did not update available fan profiles.");

        var empty = RemoteSnapshotCodec.CreateSnapshot(
            new SensorRow[0], snapshot.MachineId, "RENAMED-SELF-TEST", "6.0.1", 13,
            ByteUnitDecimal, ByteUnitClassic, ByteUnitDecimal, metadataApplied.FanProfiles);
        var emptyApplied = RemoteSnapshotCodec.ApplyDelta(metadataApplied, RemoteSnapshotCodec.CreateDelta(metadataApplied, empty));
        Require(emptyApplied.Rows.Count == 0, "An empty remote collection did not clear stale sensor rows.");

        var invalidMetadata = RemoteSnapshotCodec.CreateSnapshot(new SensorRow[0], snapshot.MachineId, "SELF-TEST", AppVersion, 20,
            ByteUnitBinary, ByteUnitBinary, ByteUnitBinary);
        invalidMetadata.MachineName = new string('x', 65537);
        var rejectedOversizedMetadata = false;
        try { RemoteSnapshotCodec.ValidateSnapshot(invalidMetadata); }
        catch (InvalidOperationException) { rejectedOversizedMetadata = true; }
        Require(rejectedOversizedMetadata, "Remote snapshot validation accepted oversized metadata.");

        var duplicateDelta = RemoteSnapshotCodec.CreateDelta(previous, current);
        duplicateDelta.ChangedRows.Add(duplicateDelta.ChangedRows[0]);
        var rejectedDuplicateDelta = false;
        try { RemoteSnapshotCodec.ApplyDelta(previous, duplicateDelta); }
        catch (InvalidOperationException) { rejectedDuplicateDelta = true; }
        Require(rejectedDuplicateDelta, "Remote difference validation accepted duplicate changed-row identities.");
    }

    private void SelfTestRemoteConnectionImportPath()
    {
        var root = Path.Combine(GetConfigFolderPath(), "R-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var mappedRoot = Path.Combine(root, "MappedRoot");
        var relativePath = Path.Combine("SR connection 100% \u00e9", "sensor readout connection.srconnection");
        var mappedFile = Path.Combine(mappedRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(mappedFile));
        try
        {
            var expected = new RemoteConnectionDocument
            {
                ServerUrl = "https://sensors.example.test/srrelay",
                Token = "  mapped-drive-self-test-token-0000000000000000000000000000  "
            };
            File.WriteAllText(mappedFile, JsonConvert.SerializeObject(expected), new UTF8Encoding(true));

            var resolverCalledForLocalFile = false;
            var directPath = RemoteMonitoringDialog.ResolveConnectionFilePath(mappedFile, delegate(char driveLetter)
            {
                resolverCalledForLocalFile = true;
                return "";
            });
            Require(string.Equals(directPath, mappedFile, StringComparison.OrdinalIgnoreCase) && !resolverCalledForLocalFile,
                "An accessible connection file unnecessarily consulted mapped-drive state.");

            var mappedInput = @"Q:\" + relativePath;
            RemoteConnectionDocument parsed;
            string validationError;
            Require(RemoteMonitoringDialog.TryReadConnectionDocument(
                    mappedInput,
                    delegate(char driveLetter) { return driveLetter == 'Q' ? mappedRoot : ""; },
                    out parsed,
                    out validationError),
                "A valid connection file on an inaccessible mapped drive did not import: " + validationError);
            Require(parsed != null &&
                    string.Equals(parsed.Format, "SensorReadoutRemoteConnection", StringComparison.Ordinal) &&
                    parsed.ProtocolVersion == 1 &&
                    string.Equals(parsed.ServerUrl, "https://sensors.example.test/srrelay/", StringComparison.Ordinal) &&
                    string.Equals(parsed.Token, expected.Token.Trim(), StringComparison.Ordinal),
                "A mapped-drive connection document did not preserve and normalize its import schema.");

            var missingMappedPath = RemoteMonitoringDialog.ResolveConnectionFilePath(@"R:\missing\connection.srconnection", delegate(char driveLetter) { return ""; });
            Require(string.Equals(missingMappedPath, Path.GetFullPath(@"R:\missing\connection.srconnection"), StringComparison.OrdinalIgnoreCase),
                "An unavailable mapping produced an unrelated fallback path.");
            var uncResolverCalled = false;
            var uncInput = @"\\server\share\sensor readout connection.srconnection";
            var uncPath = RemoteMonitoringDialog.ResolveConnectionFilePath(uncInput, delegate(char driveLetter)
            {
                uncResolverCalled = true;
                return mappedRoot;
            });
            Require(string.Equals(uncPath, Path.GetFullPath(uncInput), StringComparison.OrdinalIgnoreCase) && !uncResolverCalled,
                "A UNC connection path was incorrectly treated as a mapped drive.");

            var caseFile = Path.Combine(root, "connection-case.json");
            File.WriteAllText(caseFile, "{");
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError) && validationError == "unsupported",
                "A malformed connection document was accepted.");
            File.WriteAllText(caseFile, JsonConvert.SerializeObject(new RemoteConnectionDocument { Format = "WrongFormat", ServerUrl = "https://sensors.example.test/", Token = expected.Token.Trim() }));
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError) && validationError == "unsupported",
                "A connection document with the wrong format was accepted.");
            File.WriteAllText(caseFile, JsonConvert.SerializeObject(new RemoteConnectionDocument { ProtocolVersion = 2, ServerUrl = "https://sensors.example.test/", Token = expected.Token.Trim() }));
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError) && validationError == "unsupported",
                "A connection document with an unsupported protocol was accepted.");
            File.WriteAllText(caseFile, JsonConvert.SerializeObject(new RemoteConnectionDocument { ServerUrl = "https://user:password@sensors.example.test/", Token = expected.Token.Trim() }));
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError) && validationError == "address",
                "A connection document with credentials in its URL was accepted.");
            File.WriteAllText(caseFile, JsonConvert.SerializeObject(new RemoteConnectionDocument { ServerUrl = "https://sensors.example.test/", Token = "short" }));
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError) && validationError == "credentials",
                "A connection document with a short access token was accepted.");
            File.WriteAllText(caseFile, "");
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError), "An empty connection file was accepted.");
            File.WriteAllBytes(caseFile, new byte[] { 0xFF, 0xFE, 0x00, 0x7B });
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError), "A connection file with an invalid UTF-8 encoding was accepted.");
            var exactLimitJson = JsonConvert.SerializeObject(new RemoteConnectionDocument
            {
                ServerUrl = "https://sensors.example.test/",
                Token = expected.Token.Trim()
            });
            var exactLimitBytes = Encoding.UTF8.GetBytes(exactLimitJson);
            using (var exactLimit = new FileStream(caseFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                exactLimit.Write(exactLimitBytes, 0, exactLimitBytes.Length);
                var padding = new byte[(64 * 1024) - exactLimitBytes.Length];
                for (var index = 0; index < padding.Length; index++) padding[index] = (byte)' ';
                exactLimit.Write(padding, 0, padding.Length);
            }
            Require(RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError),
                "A valid connection file exactly at the safety limit was rejected.");
            File.WriteAllBytes(caseFile, new byte[(64 * 1024) + 1]);
            Require(!RemoteMonitoringDialog.TryReadConnectionDocument(caseFile, out parsed, out validationError), "An oversized connection file was accepted.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private void SelfTestRemoteRefreshExpansionPreservation()
    {
        var previousRows = latestRows.ToList();
        var previousSnapshot = activeRemoteSnapshot;
        var previousReportViewMode = reportViewMode;
        var previousRemoteViewMode = remoteViewMode;
        var previousSelectedFilterKey = selectedFilterKey;
        var previousExpansionMode = settings.ReadingTreeExpansionMode;
        try
        {
            reportViewMode = false;
            remoteViewMode = true;
            settings.ReadingTreeExpansionMode = ReadingTreeExpansionExpanded;
            selectedFilterKey = "type|Tasks";
            ResetReadingTreeExpansionForSelfTest();

            var first = CreateRemoteTaskExpansionSnapshot(1, new[] { "alpha", "beta" });
            ApplyActiveRemoteSnapshot(first);
            Require(SelectCategoryByKey("type|Tasks"), "Remote task expansion test could not select Tasks.");
            UpdateReadingList();
            var runningProcesses = FindTreeNode(readingTree.Nodes, "hardware|type|Tasks|Running processes");
            Require(runningProcesses != null && runningProcesses.IsExpanded, "Remote Running processes did not start expanded for the test.");
            runningProcesses.Collapse();

            var second = CreateRemoteTaskExpansionSnapshot(2, new[] { "alpha", "beta", "gamma" });
            ApplyActiveRemoteSnapshot(second);
            runningProcesses = FindTreeNode(readingTree.Nodes, "hardware|type|Tasks|Running processes");
            Require(runningProcesses != null, "Remote Running processes disappeared after refresh.");
            Require(!runningProcesses.IsExpanded, "A remote refresh expanded the collapsed Running processes branch.");

            runningProcesses.Expand();
            var third = CreateRemoteTaskExpansionSnapshot(3, new[] { "alpha", "gamma" });
            ApplyActiveRemoteSnapshot(third);
            runningProcesses = FindTreeNode(readingTree.Nodes, "hardware|type|Tasks|Running processes");
            Require(runningProcesses != null && runningProcesses.IsExpanded, "A remote refresh collapsed the expanded Running processes branch.");
        }
        finally
        {
            settings.ReadingTreeExpansionMode = previousExpansionMode;
            reportViewMode = previousReportViewMode;
            remoteViewMode = previousRemoteViewMode;
            activeRemoteSnapshot = previousSnapshot;
            selectedFilterKey = previousSelectedFilterKey;
            SetLatestRows(previousRows);
            ResetReadingTreeExpansionForSelfTest();
            UpdateDeviceList();
            UpdateReadingList();
        }
    }

    private static RemoteMachineSnapshot CreateRemoteTaskExpansionSnapshot(long sequence, IEnumerable<string> processNames)
    {
        var rows = processNames.Select((name, index) => new RemoteSensorRow
        {
            Key = "Tasks|Running processes|" + name + "|" + index,
            Type = "Tasks",
            Hardware = "Running processes",
            Name = name,
            Identifier = "self-test-process-" + name,
            DisplayValue = name,
            Source = "Self-test"
        }).ToList();
        return new RemoteMachineSnapshot
        {
            AppVersion = AppVersion,
            MachineId = "0123456789abcdef0123456789abcdef",
            MachineName = "REMOTE-EXPANSION-SELF-TEST",
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Sequence = sequence,
            MemoryUnitMode = ByteUnitBinary,
            StorageUnitMode = ByteUnitBinary,
            TransferUnitMode = ByteUnitBinary,
            Rows = rows
        };
    }

    private void SelfTestRemoteMonitoringServer(string outputFolder)
    {
        var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Server", "Linux", "sensor_readout_server.py");
        Require(File.Exists(script), "Bundled Sensor Readout Server script is missing.");
        var root = Path.Combine(outputFolder, "remote-server-self-test");
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(root);
        var port = FindUnusedTcpPort();
        const string token = "self-test-remote-access-token-00000001";
        const string password = "self-test-remote-password";
        var config = Path.Combine(root, "server.json");
        File.WriteAllText(config, JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            { "Host", "127.0.0.1" },
            { "Port", port },
            { "DataPath", data },
            { "AuthToken", token },
            { "LogPath", Path.Combine(root, "server.log") },
            { "MaxEnvelopeBytes", RemotePayloadCrypto.MaximumEnvelopeBytes },
            { "MaxDeltasPerMachine", 16 }
        }, Formatting.Indented));

        Process process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = QuoteArgument(script) + " --config " + QuoteArgument(config),
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            Require(process != null, "Could not start the Sensor Readout Server self-test process.");
            var serverUrl = "http://127.0.0.1:" + port + "/";
            WaitForRemoteServer(serverUrl, process);
            var connection = new RemoteConnectionSetting
            {
                Id = RemotePayloadCrypto.CreateRandomId(),
                Name = "Self-test",
                ServerUrl = serverUrl,
                ProtectedAccessToken = RemotePayloadCrypto.ProtectSecret(token),
                ProtectedPassword = RemotePayloadCrypto.ProtectSecret(password),
                Enabled = true,
                PublishThisComputer = true,
                PollIntervalSeconds = 2
            };
            var machineId = RemotePayloadCrypto.CreateRandomId();
            var machineWriteToken = RemotePayloadCrypto.CreateRandomId();
            var state = new RemotePublishState();
            RemoteMonitoringEngine.Publish(
                connection,
                state,
                new[] { new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 12.5f, DisplayValue = "12.5%", Source = "Self-test" } },
                machineId,
                "REMOTE-SELF-TEST",
                AppVersion,
                ByteUnitBinary,
                ByteUnitBinary,
                ByteUnitBinary,
                machineWriteToken);
            RemoteMonitoringEngine.Publish(
                connection,
                state,
                new[]
                {
                    new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 20, DisplayValue = "20%", Source = "Self-test" },
                    new SensorRow { Type = "Temperature", Hardware = "CPU", Name = "CPU package", Identifier = "temp", Value = 55, DisplayValue = "55 C", Source = "Self-test" }
                },
                machineId,
                "REMOTE-SELF-TEST",
                AppVersion,
                ByteUnitBinary,
                ByteUnitBinary,
                ByteUnitBinary,
                machineWriteToken);

            var machines = RemoteMonitoringEngine.ListMachines(connection);
            Require(machines.Count == 1 && string.Equals(machines[0].MachineName, "REMOTE-SELF-TEST", StringComparison.Ordinal), "Remote machine list did not decrypt the published computer.");
            var loaded = RemoteMonitoringEngine.LoadMachine(connection, machineId);
            Require(loaded.Sequence == 2 && loaded.Rows.Count == 2, "Remote client did not reload the snapshot and difference chain.");
            Require(loaded.Rows.Any(row => string.Equals(row.DisplayValue, "20%", StringComparison.Ordinal)), "Remote client did not apply the changed sensor value.");
            RemoteMonitoringEngine.Publish(
                connection,
                state,
                new[] { new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 25, DisplayValue = "25%", Source = "Self-test" } },
                machineId,
                "REMOTE-SELF-TEST",
                AppVersion,
                ByteUnitBinary,
                ByteUnitBinary,
                ByteUnitBinary,
                machineWriteToken);
            var incrementallyLoaded = RemoteMonitoringEngine.LoadMachine(connection, machineId, loaded);
            Require(incrementallyLoaded.Sequence == 3 && incrementallyLoaded.Rows.Count == 1 && string.Equals(incrementallyLoaded.Rows[0].DisplayValue, "25%", StringComparison.Ordinal), "Remote client did not apply only the revisions after its current snapshot.");

            foreach (var file in Directory.GetFiles(data, "*", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(ExtendedWindowsPath(file));
                var text = Encoding.UTF8.GetString(bytes);
                Require(text.IndexOf("REMOTE-SELF-TEST", StringComparison.Ordinal) < 0, "Remote server storage exposed the machine name in plaintext.");
                Require(text.IndexOf("\"DisplayValue\":\"12.5%\"", StringComparison.Ordinal) < 0 && text.IndexOf("\"DisplayValue\":\"20%\"", StringComparison.Ordinal) < 0 && text.IndexOf("\"DisplayValue\":\"25%\"", StringComparison.Ordinal) < 0, "Remote server storage exposed sensor values in plaintext.");
            }
        }
        finally
        {
            if (process != null && !process.HasExited)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(5000); } catch { }
            }
            if (process != null)
            {
                process.Dispose();
            }
        }
    }

    private static string ExtendedWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    private void SelfTestEmbeddedRemoteMonitoringServer(string outputFolder)
    {
        using (var timeoutListener = new HttpListener())
        {
            Require(EmbeddedRemoteServer.TryConfigureListenerTimeouts(timeoutListener), "Embedded remote server could not configure supported HTTP.sys request timeouts.");
            Require(timeoutListener.TimeoutManager.HeaderWait == EmbeddedRemoteServer.HeaderWaitTimeout,
                "Embedded remote server did not bound the HTTP header wait time.");
            Require(timeoutListener.TimeoutManager.EntityBody == EmbeddedRemoteServer.EntityBodyTimeout,
                "Embedded remote server did not bound the HTTP request body time.");
            Require(timeoutListener.TimeoutManager.IdleConnection == EmbeddedRemoteServer.IdleConnectionTimeout,
                "Embedded remote server did not bound idle HTTP connections.");
        }

        var root = Path.Combine(outputFolder, "embedded-remote-server-self-test");
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(root);
        var port = FindUnusedTcpPort();
        const string token = "self-test-embedded-access-token-00001";
        const string password = "self-test-embedded-password";
        var serverUrl = "http://127.0.0.1:" + port + "/";

        var serverMessages = new List<string>();
        using (var server = new EmbeddedRemoteServer(port, data, token, delegate(string message) { serverMessages.Add(message); }))
        {
            server.Start();
            WaitForEmbeddedRemoteServer(serverUrl);
            var connection = new RemoteConnectionSetting
            {
                Id = RemotePayloadCrypto.CreateRandomId(),
                Name = "Embedded self-test",
                ServerUrl = serverUrl,
                ProtectedAccessToken = RemotePayloadCrypto.ProtectSecret(token),
                ProtectedPassword = RemotePayloadCrypto.ProtectSecret(password),
                Enabled = true,
                PublishThisComputer = true,
                PollIntervalSeconds = 2
            };
            var machineId = RemotePayloadCrypto.CreateRandomId();
            var machineWriteToken = RemotePayloadCrypto.CreateRandomId();
            var state = new RemotePublishState();
            try
            {
                RemoteMonitoringEngine.Publish(
                    connection,
                    state,
                    new[] { new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 9, DisplayValue = "9%", Source = "Self-test" } },
                    machineId,
                    "EMBEDDED-REMOTE-SELF-TEST",
                    AppVersion,
                    ByteUnitBinary,
                    ByteUnitBinary,
                    ByteUnitBinary,
                    machineWriteToken,
                    new[] { new RemoteFanProfileDescriptor { Id = "self-test-fan-profile", Name = "Quiet" } });
                RemoteMonitoringEngine.Publish(
                    connection,
                    state,
                    new[] { new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 18, DisplayValue = "18%", Source = "Self-test" } },
                    machineId,
                    "EMBEDDED-REMOTE-SELF-TEST",
                    AppVersion,
                    ByteUnitBinary,
                    ByteUnitBinary,
                    ByteUnitBinary,
                    machineWriteToken,
                    new[] { new RemoteFanProfileDescriptor { Id = "self-test-fan-profile", Name = "Quiet" } });
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Embedded remote publish failed. Server messages: " + string.Join(" | ", serverMessages.ToArray()), error);
            }

            var machines = RemoteMonitoringEngine.ListMachines(connection);
            Require(machines.Count == 1 && string.Equals(machines[0].MachineName, "EMBEDDED-REMOTE-SELF-TEST", StringComparison.Ordinal), "Embedded remote server did not return the published computer.");
            var loaded = RemoteMonitoringEngine.LoadMachine(connection, machineId);
            Require(loaded.Sequence == 2 && loaded.Rows.Count == 1 && string.Equals(loaded.Rows[0].DisplayValue, "18%", StringComparison.Ordinal), "Embedded remote server did not reload and apply its encrypted difference.");
            Require(loaded.FanProfiles.Count == 1 && string.Equals(loaded.FanProfiles[0].Name, "Quiet", StringComparison.Ordinal), "Embedded remote server did not preserve the remotely available fan profile.");

            var fanProfile = loaded.FanProfiles[0];
            var newestFanProfile = new RemoteFanProfileDescriptor { Id = "newest-self-test-fan-profile", Name = "Newest" };
            try
            {
                RemoteMonitoringEngine.SendFanProfileCommand(connection, machineId, RemotePayloadCrypto.CreateRandomId(), fanProfile);
                RemoteMonitoringEngine.SendFanProfileCommand(connection, machineId, RemotePayloadCrypto.CreateRandomId(), newestFanProfile);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Embedded remote command delivery failed. Server messages: " + string.Join(" | ", serverMessages.ToArray()), error);
            }
            var commands = RemoteMonitoringEngine.ReadAndAcknowledgeFanProfileCommands(connection, machineId, machineWriteToken);
            Require(commands.Count == 1 && string.Equals(commands[0].FanProfileId, newestFanProfile.Id, StringComparison.Ordinal), "Embedded remote server did not retain only the newest valid fan profile request.");
            Require(RemoteMonitoringEngine.ReadAndAcknowledgeFanProfileCommands(connection, machineId, machineWriteToken).Count == 0, "Embedded remote server did not acknowledge and remove the fan profile request.");

            var replayCommandId = RemotePayloadCrypto.CreateRandomId();
            var replayCommand = new RemoteFanProfileCommand
            {
                CommandId = replayCommandId,
                TargetMachineId = machineId,
                RequestedByMachineId = RemotePayloadCrypto.CreateRandomId(),
                FanProfileId = fanProfile.Id,
                FanProfileName = fanProfile.Name,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };
            var replayPayload = RemotePayloadCrypto.Encrypt(replayCommand, password);
            var replaySpaceId = RemotePayloadCrypto.DeriveSpaceId(token, password);
            var replayClient = new RemoteServerClient(serverUrl, token);
            replayClient.PostCommand(replaySpaceId, machineId, replayCommandId, replayPayload);
            Require(RemoteMonitoringEngine.ReadAndAcknowledgeFanProfileCommands(connection, machineId, machineWriteToken).Count == 1,
                "Embedded remote server did not deliver the first valid fan command.");
            replayClient.PostCommand(replaySpaceId, machineId, replayCommandId, replayPayload);
            Require(RemoteMonitoringEngine.ReadAndAcknowledgeFanProfileCommands(connection, machineId, machineWriteToken).Count == 0,
                "Remote fan control accepted a replayed command ID within its validity window.");

            var wrongMachineTokenRejected = false;
            try
            {
                long snapshotSequence;
                long latestSequence;
                var encryptedSnapshot = new RemoteServerClient(serverUrl, token).GetSnapshot(RemotePayloadCrypto.DeriveSpaceId(token, password), machineId, out snapshotSequence, out latestSequence);
                new RemoteServerClient(serverUrl, token, RemotePayloadCrypto.CreateRandomId()).PutSnapshot(RemotePayloadCrypto.DeriveSpaceId(token, password), machineId, latestSequence + 1, encryptedSnapshot);
            }
            catch (RemoteServerException error)
            {
                wrongMachineTokenRejected = error.StatusCode == HttpStatusCode.Forbidden;
            }
            Require(wrongMachineTokenRejected, "Embedded remote server allowed another publishing credential to overwrite a registered computer.");

            var removableMachineId = RemotePayloadCrypto.CreateRandomId();
            var removableSnapshot = RemoteSnapshotCodec.CreateSnapshot(
                new[] { new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "removable-cpu", Value = 1, DisplayValue = "1%", Source = "Self-test" } },
                removableMachineId,
                "REMOVABLE-REMOTE-SELF-TEST",
                AppVersion,
                1,
                ByteUnitBinary,
                ByteUnitBinary,
                ByteUnitBinary);
            var removableWriteToken = RemotePayloadCrypto.CreateRandomId();
            new RemoteServerClient(serverUrl, token, removableWriteToken).PutSnapshot(
                RemotePayloadCrypto.DeriveSpaceId(token, password),
                removableMachineId,
                1,
                RemotePayloadCrypto.Encrypt(removableSnapshot, password));
            Require(RemoteMonitoringEngine.ListMachines(connection).Count == 2, "Embedded remote server did not register a second computer for removal testing.");
            var wrongRemovalTokenRejected = false;
            try
            {
                RemoteMonitoringEngine.RemoveMachine(connection, removableMachineId, RemotePayloadCrypto.CreateRandomId());
            }
            catch (RemoteServerException error)
            {
                wrongRemovalTokenRejected = error.StatusCode == HttpStatusCode.Forbidden;
            }
            Require(wrongRemovalTokenRejected, "Embedded remote server allowed a viewer to remove another computer.");
            Require(RemoteMonitoringEngine.ListMachines(connection).Count == 2, "Embedded remote server removed a computer after rejecting its ownership credential.");
            RemoteMonitoringEngine.RemoveMachine(connection, removableMachineId, removableWriteToken);
            Require(RemoteMonitoringEngine.ListMachines(connection).Count == 1, "Embedded remote server did not remove stored data for the selected computer.");

            RemoteMonitoringEngine.Publish(
                connection,
                state,
                new[] { new SensorRow { Type = "Performance", Hardware = "CPU", Name = "CPU usage", Identifier = "cpu", Value = 18, DisplayValue = "18%", Source = "Self-test" } },
                machineId,
                "EMBEDDED-REMOTE-SELF-TEST",
                AppVersion,
                ByteUnitBinary,
                ByteUnitBinary,
                ByteUnitBinary,
                machineWriteToken,
                new[] { new RemoteFanProfileDescriptor { Id = "replacement-fan-profile", Name = "Performance" } });
            var refreshedMachines = RemoteMonitoringEngine.ListMachines(connection);
            Require(refreshedMachines.Count == 1 && refreshedMachines[0].FanProfiles.Count == 1 && refreshedMachines[0].FanProfiles[0].Name == "Performance", "Remote computer list did not apply a fan-profile-only difference.");

            var wrongTokenRejected = false;
            try
            {
                new RemoteServerClient(serverUrl, "wrong-access-token-00000000000000000000").GetMachineIndex(RemotePayloadCrypto.DeriveSpaceId(token, password));
            }
            catch (RemoteServerException error)
            {
                wrongTokenRejected = error.StatusCode == HttpStatusCode.Unauthorized;
            }
            Require(wrongTokenRejected, "Embedded remote server accepted an incorrect access token. Server messages: " + string.Join(" | ", serverMessages.ToArray()));
        }

        var boundedSpaceId = RemotePayloadCrypto.CreateRandomId();
        var boundedMachineId = RemotePayloadCrypto.CreateRandomId();
        var boundedWriteToken = RemotePayloadCrypto.CreateRandomId();
        var deltaStore = new RemoteRelayStore(Path.Combine(root, "bounded-deltas"), 1024, 1024 * 1024, 4, 2);
        deltaStore.PutSnapshot(boundedSpaceId, boundedMachineId, 1, new byte[] { 1 }, boundedWriteToken);
        for (var sequence = 2; sequence <= 65; sequence++)
        {
            deltaStore.AppendDelta(boundedSpaceId, boundedMachineId, sequence, new byte[] { 2 }, boundedWriteToken);
        }
        Require(Directory.GetFiles(Path.Combine(root, "bounded-deltas"), "*.bin", SearchOption.AllDirectories).Count(path => path.IndexOf(Path.DirectorySeparatorChar + "Deltas" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) == 0,
            "Embedded remote storage wrote live differences to disk instead of retaining them in bounded memory.");
        RemoteRelayMetadata bufferedMetadata;
        var bufferedDeltas = deltaStore.GetDeltas(boundedSpaceId, boundedMachineId, 1, out bufferedMetadata);
        Require(bufferedDeltas.Count == 64 && bufferedMetadata.LatestSequence == 65, "Embedded remote storage did not serve its buffered differences.");
        var deltaLimitRejected = false;
        try { deltaStore.AppendDelta(boundedSpaceId, boundedMachineId, 66, new byte[] { 3 }, boundedWriteToken); }
        catch (RemoteRelaySnapshotRequired) { deltaLimitRejected = true; }
        Require(deltaLimitRejected, "Embedded remote storage did not require compaction at its bounded delta count.");
        var restartedDeltaStore = new RemoteRelayStore(Path.Combine(root, "bounded-deltas"), 1024, 1024 * 1024, 4, 2);
        Require(restartedDeltaStore.ListMachines(boundedSpaceId).Single().LatestSequence == 1, "Embedded remote storage persisted transient differences during restart testing.");
        var restartConflict = false;
        try { restartedDeltaStore.AppendDelta(boundedSpaceId, boundedMachineId, 66, new byte[] { 3 }, boundedWriteToken); }
        catch (RemoteRelayConflict) { restartConflict = true; }
        Require(restartConflict, "Embedded remote storage did not request resynchronization after transient differences were lost during restart.");
        restartedDeltaStore.PutSnapshot(boundedSpaceId, boundedMachineId, 66, new byte[] { 4 }, boundedWriteToken);
        Require(restartedDeltaStore.ListMachines(boundedSpaceId).Single().LatestSequence == 66, "Embedded remote storage did not accept a fresh snapshot after restart recovery.");

        var capacityStore = new RemoteRelayStore(Path.Combine(root, "bounded-capacity"), 1024, 1024, 1, 1);
        capacityStore.PutSnapshot(boundedSpaceId, boundedMachineId, 1, new byte[] { 1 }, boundedWriteToken);
        var machineLimitRejected = false;
        try { capacityStore.PutSnapshot(boundedSpaceId, RemotePayloadCrypto.CreateRandomId(), 1, new byte[] { 1 }, RemotePayloadCrypto.CreateRandomId()); }
        catch (RemoteRelayCapacityExceeded) { machineLimitRejected = true; }
        Require(machineLimitRejected, "Embedded remote storage accepted more computers than its configured global limit.");
        var storageLimitRejected = false;
        try { capacityStore.PutCommand(boundedSpaceId, boundedMachineId, RemotePayloadCrypto.CreateRandomId(), new byte[900]); }
        catch (RemoteRelayCapacityExceeded) { storageLimitRejected = true; }
        Require(storageLimitRejected, "Embedded remote storage exceeded its configured total byte limit.");

        var checkpointFailureRoot = Path.Combine(root, "checkpoint-failure");
        var checkpointFailureStore = new RemoteRelayStore(checkpointFailureRoot, 1024, 1024 * 1024, 2, 1);
        checkpointFailureStore.PutSnapshot(boundedSpaceId, boundedMachineId, 1, new byte[] { 11 }, boundedWriteToken);
        var checkpointStoredBytes = Directory.GetFiles(checkpointFailureRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var constrainedCheckpointStore = new RemoteRelayStore(checkpointFailureRoot, 1024, checkpointStoredBytes + 2, 2, 1);
        var checkpointCapacityRejected = false;
        try { constrainedCheckpointStore.PutSnapshot(boundedSpaceId, boundedMachineId, 2, new byte[] { 22 }, boundedWriteToken); }
        catch (RemoteRelayCapacityExceeded) { checkpointCapacityRejected = true; }
        Require(checkpointCapacityRejected, "Embedded remote storage did not reject a checkpoint that lacked metadata staging capacity.");
        RemoteRelayMetadata checkpointFailureMetadata;
        var checkpointFailureSnapshot = constrainedCheckpointStore.GetSnapshot(boundedSpaceId, boundedMachineId, out checkpointFailureMetadata);
        Require(checkpointFailureMetadata.SnapshotSequence == 1 && checkpointFailureSnapshot.SequenceEqual(new byte[] { 11 }) &&
            Directory.GetFiles(checkpointFailureRoot, "*.next", SearchOption.AllDirectories).Length == 0,
            "A failed remote checkpoint damaged the previous snapshot or left staged files behind.");

        var missingOwnershipRoot = Path.Combine(root, "missing-ownership");
        var missingOwnershipStore = new RemoteRelayStore(missingOwnershipRoot, 1024, 1024 * 1024, 2, 1);
        missingOwnershipStore.PutSnapshot(boundedSpaceId, boundedMachineId, 1, new byte[] { 1 }, boundedWriteToken);
        File.Delete(Directory.GetFiles(missingOwnershipRoot, "metadata.json", SearchOption.AllDirectories).Single());
        var missingOwnershipRejected = false;
        try { new RemoteRelayStore(missingOwnershipRoot, 1024, 1024 * 1024, 2, 1).PutSnapshot(boundedSpaceId, boundedMachineId, 2, new byte[] { 2 }, RemotePayloadCrypto.CreateRandomId()); }
        catch (InvalidDataException) { missingOwnershipRejected = true; }
        Require(missingOwnershipRejected, "Embedded remote storage allowed missing ownership metadata to be re-registered.");

        var corruptOwnershipRoot = Path.Combine(root, "corrupt-ownership");
        var corruptOwnershipStore = new RemoteRelayStore(corruptOwnershipRoot, 1024, 1024 * 1024, 2, 1);
        corruptOwnershipStore.PutSnapshot(boundedSpaceId, boundedMachineId, 1, new byte[] { 1 }, boundedWriteToken);
        File.WriteAllText(Directory.GetFiles(corruptOwnershipRoot, "metadata.json", SearchOption.AllDirectories).Single(), "{}");
        var corruptOwnershipRejected = false;
        try { new RemoteRelayStore(corruptOwnershipRoot, 1024, 1024 * 1024, 2, 1).PutSnapshot(boundedSpaceId, boundedMachineId, 2, new byte[] { 2 }, boundedWriteToken); }
        catch (InvalidDataException) { corruptOwnershipRejected = true; }
        Require(corruptOwnershipRejected, "Embedded remote storage allowed corrupt ownership metadata to be re-registered.");

        var interruptedCheckpointRoot = Path.Combine(root, "interrupted-checkpoint");
        var interruptedCheckpointStore = new RemoteRelayStore(interruptedCheckpointRoot, 1024, 1024 * 1024, 2, 1);
        interruptedCheckpointStore.PutSnapshot(boundedSpaceId, boundedMachineId, 1, new byte[] { 31 }, boundedWriteToken);
        var interruptedMetadataPath = Directory.GetFiles(interruptedCheckpointRoot, "metadata.json", SearchOption.AllDirectories).Single();
        var interruptedDirectory = Path.GetDirectoryName(interruptedMetadataPath);
        var interruptedMetadata = JsonConvert.DeserializeObject<RemoteRelayMetadata>(File.ReadAllText(interruptedMetadataPath));
        interruptedMetadata.SnapshotSequence = 2;
        interruptedMetadata.LatestSequence = 2;
        File.WriteAllBytes(Path.Combine(interruptedDirectory, "snapshot.next"), new byte[] { 32 });
        File.WriteAllText(Path.Combine(interruptedDirectory, "metadata.next"), JsonConvert.SerializeObject(interruptedMetadata, Formatting.Indented));
        File.WriteAllBytes(Path.Combine(interruptedDirectory, "checkpoint.pending"), new byte[] { 1 });
        var recoveredCheckpointStore = new RemoteRelayStore(interruptedCheckpointRoot, 1024, 1024 * 1024, 2, 1);
        RemoteRelayMetadata recoveredCheckpointMetadata;
        var recoveredCheckpointSnapshot = recoveredCheckpointStore.GetSnapshot(boundedSpaceId, boundedMachineId, out recoveredCheckpointMetadata);
        Require(recoveredCheckpointMetadata.SnapshotSequence == 2 && recoveredCheckpointSnapshot.SequenceEqual(new byte[] { 32 }) &&
            !File.Exists(Path.Combine(interruptedDirectory, "checkpoint.pending")),
            "Embedded remote storage did not complete an interrupted snapshot checkpoint during recovery.");

        foreach (var file in Directory.GetFiles(data, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Require(text.IndexOf("EMBEDDED-REMOTE-SELF-TEST", StringComparison.Ordinal) < 0, "Embedded remote server storage exposed the machine name in plaintext.");
            Require(text.IndexOf("\"DisplayValue\":\"9%\"", StringComparison.Ordinal) < 0 && text.IndexOf("\"DisplayValue\":\"18%\"", StringComparison.Ordinal) < 0, "Embedded remote server storage exposed sensor values in plaintext.");
            Require(text.IndexOf("Quiet", StringComparison.Ordinal) < 0, "Embedded remote server storage exposed a fan profile name in plaintext.");
        }
    }

    private static int FindUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void SelfTestRemoteHealthResponses()
    {
        Require(ProbeRemoteHealthResponseWithRetry(200, "{\"Name\":\"Sensor Readout Server\",\"Version\":\"6.0.0\",\"ProtocolVersion\":1}") == null,
            "Remote health validation rejected a compatible Sensor Readout Server.");
        Require(ProbeRemoteHealthResponseWithRetry(200, "{\"Name\":\"Another service\",\"Version\":\"6.0.0\",\"ProtocolVersion\":1}") is InvalidDataException,
            "Remote health validation accepted an unrelated service.");
        Require(ProbeRemoteHealthResponseWithRetry(200, "{\"Name\":\"Sensor Readout Server\",\"Version\":\"7.0.0\",\"ProtocolVersion\":2}") is InvalidDataException,
            "Remote health validation accepted an unsupported protocol.");
        Require(ProbeRemoteHealthResponseWithRetry(200, "<html>Not a Sensor Readout Server</html>") != null,
            "Remote health validation accepted an HTML page.");
        Require(ProbeRemoteHealthResponseWithRetry(302, "") != null,
            "Remote health validation accepted a redirect as a Sensor Readout Server.");
    }

    private static Exception ProbeRemoteHealthResponseWithRetry(int statusCode, string body)
    {
        InvalidOperationException lastListenerError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return ProbeRemoteHealthResponse(statusCode, body);
            }
            catch (InvalidOperationException error)
            {
                var socketError = error.InnerException as SocketException;
                if (socketError == null ||
                    (socketError.SocketErrorCode != SocketError.Interrupted &&
                     socketError.SocketErrorCode != SocketError.OperationAborted))
                {
                    throw;
                }
                lastListenerError = error;
                Thread.Sleep(50);
            }
        }
        throw lastListenerError;
    }

    private static Exception ProbeRemoteHealthResponse(int statusCode, string body)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverError = null;
        var worker = new Thread(delegate()
        {
            try
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 3000;
                    var matchedHeaderBytes = 0;
                    for (var received = 0; received < 16 * 1024 && matchedHeaderBytes < 4; received++)
                    {
                        var value = stream.ReadByte();
                        if (value < 0) throw new EndOfStreamException("The remote health test client closed before sending its request.");
                        var expected = matchedHeaderBytes == 0 || matchedHeaderBytes == 2 ? '\r' : '\n';
                        matchedHeaderBytes = value == expected ? matchedHeaderBytes + 1 : value == '\r' ? 1 : 0;
                    }
                    if (matchedHeaderBytes < 4) throw new InvalidDataException("The remote health test request headers were incomplete.");
                    var payload = Encoding.UTF8.GetBytes(body ?? "");
                    var reason = statusCode == 200 ? "OK" : statusCode == 302 ? "Found" : "Error";
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 " + statusCode + " " + reason + "\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        "Content-Length: " + payload.Length + "\r\n" +
                        (statusCode == 302 ? "Location: http://127.0.0.1/elsewhere\r\n" : "") +
                        "Connection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
            }
            catch (Exception error)
            {
                serverError = error;
            }
        });
        worker.IsBackground = true;
        worker.Start();

        Exception clientError = null;
        try
        {
            new RemoteServerClient("http://127.0.0.1:" + port + "/", "health-probe-token-00000000000000000").CheckHealth();
        }
        catch (Exception error)
        {
            clientError = error;
        }
        finally
        {
            listener.Stop();
            if (!worker.Join(3000)) throw new InvalidOperationException("Remote health response test did not finish.");
        }
        if (serverError != null) throw new InvalidOperationException("Remote health response test server failed.", serverError);
        return clientError;
    }

    private static void WaitForRemoteServer(string serverUrl, Process process)
    {
        Exception lastError = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("Sensor Readout Server stopped during startup. " + process.StandardError.ReadToEnd());
            }
            try
            {
                new RemoteServerClient(serverUrl, "health-check-token-0000000000000000").CheckHealth();
                return;
            }
            catch (Exception error)
            {
                lastError = error;
                Thread.Sleep(100);
            }
        }
        throw new InvalidOperationException("Sensor Readout Server did not become ready. " + (lastError == null ? "" : lastError.Message));
    }

    private static void WaitForEmbeddedRemoteServer(string serverUrl)
    {
        Exception lastError = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                new RemoteServerClient(serverUrl, "health-check-token-0000000000000000").CheckHealth();
                return;
            }
            catch (Exception error)
            {
                lastError = error;
                Thread.Sleep(100);
            }
        }
        throw new InvalidOperationException("Embedded Sensor Readout Server did not become ready. " + (lastError == null ? "" : lastError.Message));
    }

}
