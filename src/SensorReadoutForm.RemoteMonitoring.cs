using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private string embeddedRemoteFirewallWarning = "";

    private sealed class RemoteCommandDispatch
    {
        public string ConnectionId;
        public RemoteReceivedCommands Commands;
    }

    private sealed class RemoteViewerPresenceState
    {
        public string ConnectionId;
        public string ViewerMachineId;
        public string ViewerMachineName;
        public string SessionId;
        public DateTime LastEventUtc;
        public DateTime ExpiresUtc;
        public bool Connected;
    }

    private void ShowRemoteMonitoringDialog()
    {
        using (var dialog = new RemoteMonitoringDialog(
            settings.RemoteConnections,
            delegate
            {
                SaveSettings(settings);
                ResetRemotePublishStates();
            },
            delegate(RemoteConnectionSetting connection, string machineId)
            {
                BeginRemoteView(connection, machineId);
            },
            delegate(RemoteConnectionSetting connection, RemoteMachineDescriptor machine)
            {
                ShowRemoteFanProfileDialog(connection, machine);
            },
            delegate(IWin32Window owner) { ConfigureEmbeddedRemoteServer(owner); },
            delegate { DisableEmbeddedRemoteServer(); },
            delegate(IWin32Window owner) { ExportEmbeddedRemoteConnection(owner); },
            delegate { return settings.RemoteHostEnabled; },
            delegate { return settings.RemoteMachineId; },
            delegate { return RemotePayloadCrypto.UnprotectSecret(settings.ProtectedRemoteMachineWriteToken); },
            T))
        {
            dialog.ShowDialog(this);
        }
    }

    private void StartEmbeddedRemoteServerIfEnabled()
    {
        StopEmbeddedRemoteServer();
        if (!settings.RemoteHostEnabled)
        {
            string unusedFirewallError;
            RemoteFirewallManager.TryRemoveInboundRule(out unusedFirewallError);
            return;
        }
        try
        {
            var token = RemotePayloadCrypto.UnprotectSecret(settings.ProtectedRemoteHostAccessToken);
            embeddedRemoteServer = new EmbeddedRemoteServer(
                settings.RemoteHostPort,
                System.IO.Path.Combine(GetConfigFolderPath(), "Remote Server"),
                token,
                message => LogMessage("Debug", message));
            embeddedRemoteServer.Start();
            LogMessage("Normal", "Embedded Sensor Readout Server started on port " + settings.RemoteHostPort + ".");
            string firewallError;
            if (RemoteFirewallManager.TryEnsureInboundRule(settings.RemoteHostPort, out firewallError))
            {
                embeddedRemoteFirewallWarning = "";
            }
            else
            {
                embeddedRemoteFirewallWarning = firewallError;
                LogMessage("Normal", "Windows Firewall access for the embedded remote server could not be configured: " + firewallError);
                statusLabel.Text = T("status.remoteFirewallRuleFailed", "Remote server started, but Windows Firewall access could not be configured:") + " " + firewallError;
            }
        }
        catch (Exception error)
        {
            embeddedRemoteServer = null;
            embeddedRemoteFirewallWarning = "";
            string firewallError;
            if (!RemoteFirewallManager.TryRemoveInboundRule(out firewallError))
            {
                LogMessage("Normal", "Stale Windows Firewall access for the failed embedded remote server could not be removed: " + firewallError);
            }
            LogMessage("Normal", "Embedded Sensor Readout Server could not start: " + error.GetType().Name + ": " + error.Message);
            statusLabel.Text = T("status.remoteServerCouldNotStart", "Remote server could not start:") + " " + error.Message;
        }
    }

    private void StopEmbeddedRemoteServer()
    {
        if (embeddedRemoteServer == null)
        {
            return;
        }
        try { embeddedRemoteServer.Dispose(); } catch { }
        embeddedRemoteServer = null;
    }

    private void ConfigureEmbeddedRemoteServer(IWin32Window owner)
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Host this computer", "Host this computer");
            dialog.Font = System.Drawing.SystemFonts.MessageBoxFont;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new System.Drawing.Size(540, 270);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var intro = new Label
            {
                Text = T("ui.remoteHostExplanation", "This copy of Sensor Readout will relay encrypted readings for your computers. Choose the password that every client will use. The server never receives that password or readable sensor data."),
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.SetColumnSpan(intro, 2);
            layout.Controls.Add(intro, 0, 0);
            var portBox = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = settings.RemoteHostPort, Dock = DockStyle.Left, Width = 120 };
            var passwordBox = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
            var passwordPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            passwordPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            passwordPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var generatePassword = new Button { Text = T("ui.&Generate password...", "&Generate password..."), AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            generatePassword.Click += delegate
            {
                passwordBox.Text = RemotePayloadCrypto.CreateMonitoringPassword();
                var copied = true;
                try { Clipboard.SetText(passwordBox.Text); }
                catch { copied = false; }
                MessageBox.Show(dialog,
                    copied
                        ? T("message.remotePasswordGenerated", "A secure monitoring password was generated and copied to the clipboard. Save it somewhere safe and use it on every Sensor Readout computer you want to connect.")
                        : T("message.remotePasswordGeneratedNotCopied", "A secure monitoring password was generated, but Windows could not copy it to the clipboard. The password remains selected in the password field."),
                    T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                passwordBox.Focus();
                passwordBox.SelectAll();
            };
            passwordPanel.Controls.Add(passwordBox, 0, 0);
            passwordPanel.Controls.Add(generatePassword, 1, 0);
            layout.Controls.Add(new Label { Text = T("ui.Listening p&ort", "Listening p&ort"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(portBox, 1, 1);
            layout.Controls.Add(new Label { Text = T("ui.Monitoring &password", "Monitoring &password"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            layout.Controls.Add(passwordPanel, 1, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = T("ui.Cancel", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.SetColumnSpan(buttons, 2);
            layout.Controls.Add(buttons, 0, 3);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            while (true)
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    return;
                }
                if (passwordBox.Text.Length >= 8)
                {
                    break;
                }
                MessageBox.Show(owner, T("message.remotePasswordLength", "Use a monitoring password of at least 8 characters."), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                dialog.DialogResult = DialogResult.None;
                passwordBox.Focus();
            }

            var port = Decimal.ToInt32(portBox.Value);
            var token = string.IsNullOrWhiteSpace(settings.ProtectedRemoteHostAccessToken)
                ? RemotePayloadCrypto.CreateRandomId()
                : RemotePayloadCrypto.UnprotectSecret(settings.ProtectedRemoteHostAccessToken);
            settings.RemoteHostEnabled = true;
            settings.RemoteHostPort = port;
            settings.ProtectedRemoteHostAccessToken = RemotePayloadCrypto.ProtectSecret(token);
            var localUrl = "http://127.0.0.1:" + port + "/";
            var localConnection = settings.RemoteConnections.FirstOrDefault(connection =>
                connection.IsEmbeddedHostConnection ||
                string.Equals(connection.ServerUrl, localUrl, StringComparison.OrdinalIgnoreCase));
            if (localConnection == null)
            {
                localConnection = new RemoteConnectionSetting { Id = RemotePayloadCrypto.CreateRandomId() };
                settings.RemoteConnections.Add(localConnection);
            }
            localConnection.Name = T("ui.This computer", "This computer");
            localConnection.ServerUrl = localUrl;
            localConnection.ProtectedAccessToken = RemotePayloadCrypto.ProtectSecret(token);
            localConnection.ProtectedPassword = RemotePayloadCrypto.ProtectSecret(passwordBox.Text);
            localConnection.Enabled = true;
            localConnection.PublishThisComputer = true;
            localConnection.PollIntervalSeconds = Math.Max(2, localConnection.PollIntervalSeconds);
            localConnection.IsEmbeddedHostConnection = true;
            SaveSettings(settings);
            ResetRemotePublishStates();
            StartEmbeddedRemoteServerIfEnabled();
            if (string.IsNullOrWhiteSpace(embeddedRemoteFirewallWarning))
            {
                statusLabel.Text = T("status.remoteServerStarted", "Remote server started.");
            }
            else
            {
                MessageBox.Show(owner,
                    T("message.remoteFirewallRuleFailed", "The remote server started, but Sensor Readout could not allow it through Windows Firewall. Other computers may be unable to connect:") + " " + embeddedRemoteFirewallWarning,
                    T("ui.Remote monitoring", "Remote monitoring"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private void DisableEmbeddedRemoteServer()
    {
        settings.RemoteHostEnabled = false;
        foreach (var connection in settings.RemoteConnections.Where(item => item != null && item.IsEmbeddedHostConnection))
        {
            connection.Enabled = false;
            connection.PublishThisComputer = false;
            connection.AllowRemoteFanProfiles = false;
        }
        SaveSettings(settings);
        StopEmbeddedRemoteServer();
        string firewallError;
        if (RemoteFirewallManager.TryRemoveInboundRule(out firewallError))
        {
            statusLabel.Text = T("status.remoteServerStopped", "Remote server stopped.");
        }
        else
        {
            LogMessage("Normal", "Windows Firewall access for the embedded remote server could not be removed: " + firewallError);
            statusLabel.Text = T("status.remoteFirewallRuleCouldNotBeRemoved", "Remote server stopped, but its Windows Firewall rule could not be removed:") + " " + firewallError;
        }
    }

    private void ExportEmbeddedRemoteConnection(IWin32Window owner)
    {
        if (!settings.RemoteHostEnabled || string.IsNullOrWhiteSpace(settings.ProtectedRemoteHostAccessToken))
        {
            MessageBox.Show(owner, T("message.startRemoteHostFirst", "Start hosting this computer before saving its connection file."), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string connectionUrl;
        if (!RemoteHostConnectionExportDialog.TryChoose(owner, settings.RemoteHostPort, settings.RemoteHostConnectionUrl, T, out connectionUrl))
        {
            return;
        }
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = T("ui.Save server connection", "Save server connection");
            dialog.Filter = "Sensor Readout Server connections (*.srconnection)|*.srconnection|All files (*.*)|*.*";
            dialog.FileName = "SensorReadout-" + Environment.MachineName + ".srconnection";
            if (dialog.ShowDialog(owner) != DialogResult.OK)
            {
                return;
            }
            var document = new RemoteConnectionDocument
            {
                ServerUrl = connectionUrl,
                Token = RemotePayloadCrypto.UnprotectSecret(settings.ProtectedRemoteHostAccessToken)
            };
            System.IO.File.WriteAllText(dialog.FileName, Newtonsoft.Json.JsonConvert.SerializeObject(document, Newtonsoft.Json.Formatting.Indented));
            settings.RemoteHostConnectionUrl = connectionUrl;
            SaveSettings(settings);
            statusLabel.Text = T("status.remoteConnectionSaved", "Server connection saved.");
        }
    }

    private void ResetRemotePublishStates()
    {
        lock (remotePublishStatesLock)
        {
            remotePublishStates.Clear();
        }
    }

    private void PublishRemoteRowsAsync(IList<SensorRow> rows)
    {
        var connections = CloneRemoteConnections(settings.RemoteConnections)
            .Where(connection => connection.Enabled && connection.PublishThisComputer)
            .ToList();
        if (connections.Count == 0 || rows == null || remotePublishInProgress)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var dueConnections = new List<KeyValuePair<RemoteConnectionSetting, RemotePublishState>>();
        lock (remotePublishStatesLock)
        {
            foreach (var connection in connections)
            {
                RemotePublishState state;
                if (!remotePublishStates.TryGetValue(connection.Id, out state))
                {
                    state = new RemotePublishState();
                    remotePublishStates[connection.Id] = state;
                }
                if (RemoteMonitoringEngine.TryBeginPublish(connection, state, nowUtc))
                {
                    dueConnections.Add(new KeyValuePair<RemoteConnectionSetting, RemotePublishState>(connection, state));
                }
            }
        }
        if (dueConnections.Count == 0)
        {
            return;
        }

        remotePublishInProgress = true;
        // Collection returns a fresh row list which is not mutated after this point. A shallow
        // list snapshot keeps publication off the UI thread without cloning every Details map.
        var publishRows = rows.Where(row => row != null).ToList();
        var machineId = settings.RemoteMachineId;
        var protectedMachineWriteToken = settings.ProtectedRemoteMachineWriteToken;
        var machineName = Environment.MachineName ?? "";
        var memoryMode = activeMemoryUnitMode;
        var storageMode = activeStorageUnitMode;
        var transferMode = activeTransferUnitMode;
        var availableRemoteFanProfiles = RemoteMonitoringEngine.CreateFanProfileDescriptors(settings.FanProfiles);
        Task.Factory.StartNew(delegate
        {
            var commands = new List<RemoteCommandDispatch>();
            var machineWriteToken = RemotePayloadCrypto.UnprotectSecret(protectedMachineWriteToken);
            foreach (var dueConnection in dueConnections)
            {
                var connection = dueConnection.Key;
                try
                {
                    var state = dueConnection.Value;
                    var fanProfiles = connection.AllowRemoteFanProfiles
                        ? availableRemoteFanProfiles
                        : new List<RemoteFanProfileDescriptor>();
                    RemoteMonitoringEngine.Publish(connection, state, publishRows, machineId, machineName, AppVersion, memoryMode, storageMode, transferMode, machineWriteToken, fanProfiles);
                    commands.Add(new RemoteCommandDispatch
                    {
                        ConnectionId = connection.Id,
                        Commands = RemoteMonitoringEngine.ReadAndAcknowledgeCommands(connection, machineId, machineWriteToken, connection.AllowRemoteFanProfiles)
                    });
                }
                catch (Exception error)
                {
                    LogMessage("Debug", "Remote monitoring publish failed for " + connection.Name + ": " + error.GetType().Name + ": " + error.Message);
                }
            }
            return commands;
        }).ContinueWith(delegate(Task<List<RemoteCommandDispatch>> task)
        {
            remotePublishInProgress = false;
            if (IsDisposed)
            {
                return;
            }
            if (task.IsFaulted)
            {
                var error = task.Exception == null ? null : task.Exception.GetBaseException();
                LogMessage("Debug", "Remote monitoring publish cycle failed before completion: " +
                    (error == null ? "Unknown error" : error.GetType().Name + ": " + error.Message));
                return;
            }
            if (!task.Status.Equals(TaskStatus.RanToCompletion))
            {
                return;
            }
            foreach (var command in task.Result)
            {
                foreach (var fanCommand in command.Commands.FanProfileCommands)
                {
                    ApplyRemoteFanProfileCommand(command.ConnectionId, fanCommand);
                }
                ApplyRemoteViewerPresenceCommands(command.ConnectionId, command.Commands.ViewerPresenceCommands);
            }
            ExpireRemoteViewerPresenceSessions();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool IsRemotePublicationDue()
    {
        var connections = CloneRemoteConnections(settings.RemoteConnections)
            .Where(connection => connection.Enabled && connection.PublishThisComputer)
            .ToList();
        if (connections.Count == 0)
        {
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        lock (remotePublishStatesLock)
        {
            foreach (var connection in connections)
            {
                RemotePublishState state;
                if (!remotePublishStates.TryGetValue(connection.Id, out state) || RemoteMonitoringEngine.IsPublishDue(connection, state, nowUtc))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void BeginRemoteView(RemoteConnectionSetting connection, string machineId)
    {
        if (connection == null || string.IsNullOrWhiteSpace(machineId))
        {
            return;
        }
        EndActiveRemoteViewerPresence();
        activeRemoteConnection = CloneRemoteConnections(new[] { connection }).FirstOrDefault();
        if (activeRemoteConnection == null)
        {
            return;
        }
        remoteViewGeneration++;
        activeRemoteMachineId = machineId;
        activeRemoteViewerSessionId = RemotePayloadCrypto.CreateRandomId();
        activeRemotePresenceLastSentUtc = DateTime.MinValue;
        activeRemoteSnapshot = null;
        activeRemoteLastPollUtc = DateTime.MinValue;
        remoteViewMode = true;
        reportViewMode = false;
        SetLatestRows(Enumerable.Empty<SensorRow>());
        readingTreeExpansionInitialized = false;
        lastReadingTreeSignature = "";
        lastReadingTreeShapeSignature = "";
        lastReadingTreeFilterKey = "";
        UpdateReportViewMenuState();
        UpdateWindowTitle();
        UpdateDeviceList();
        UpdateReadingList();
        statusLabel.Text = T("status.loadingRemoteComputer", "Loading remote computer...");
        SendActiveRemoteViewerPresenceAsync("connected", true);
        PollActiveRemoteMachineAsync(true);
    }

    private void PollActiveRemoteMachineIfDue()
    {
        if (!remoteViewMode || activeRemoteConnection == null || remotePollGenerations.Contains(remoteViewGeneration))
        {
            return;
        }
        var seconds = Math.Max(2, activeRemoteConnection.PollIntervalSeconds);
        if (DateTime.UtcNow - activeRemoteLastPollUtc >= TimeSpan.FromSeconds(seconds))
        {
            PollActiveRemoteMachineAsync(false);
        }
    }

    private void PollActiveRemoteMachineAsync(bool userRequested)
    {
        if (!remoteViewMode || activeRemoteConnection == null || string.IsNullOrWhiteSpace(activeRemoteMachineId))
        {
            return;
        }
        var connection = activeRemoteConnection;
        var machineId = activeRemoteMachineId;
        var generation = remoteViewGeneration;
        if (!remotePollGenerations.Add(generation))
        {
            return;
        }
        if (userRequested)
        {
            statusLabel.Text = T("status.refreshingRemoteComputer", "Refreshing remote computer...");
        }
        var previousSnapshot = activeRemoteSnapshot;
        Task.Factory.StartNew(delegate { return RemoteMonitoringEngine.LoadMachine(connection, machineId, previousSnapshot); })
            .ContinueWith(delegate(Task<RemoteMachineSnapshot> task)
            {
                remotePollGenerations.Remove(generation);
                if (IsDisposed || !remoteViewMode || generation != remoteViewGeneration || activeRemoteConnection == null ||
                    !string.Equals(activeRemoteConnection.Id, connection.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(activeRemoteMachineId, machineId, StringComparison.Ordinal))
                {
                    return;
                }
                activeRemoteLastPollUtc = DateTime.UtcNow;
                if (task.IsFaulted)
                {
                    var error = task.Exception == null ? null : task.Exception.GetBaseException();
                    statusLabel.Text = T("status.remoteComputerRefreshFailed", "Remote computer refresh failed:") + " " + (error == null ? T("ui.Unknown error", "Unknown error") : error.Message);
                    return;
                }

                ApplyActiveRemoteSnapshot(task.Result);
                SendActiveRemoteViewerPresenceAsync("heartbeat", false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SendActiveRemoteViewerPresenceAsync(string action, bool force)
    {
        if (!remoteViewMode || activeRemoteConnection == null || string.IsNullOrWhiteSpace(activeRemoteMachineId) ||
            string.IsNullOrWhiteSpace(activeRemoteViewerSessionId))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var heartbeatSeconds = Math.Max(5, activeRemoteConnection.PollIntervalSeconds);
        if (!force && now - activeRemotePresenceLastSentUtc < TimeSpan.FromSeconds(heartbeatSeconds))
        {
            return;
        }

        activeRemotePresenceLastSentUtc = now;
        var connection = activeRemoteConnection;
        var targetMachineId = activeRemoteMachineId;
        var sessionId = activeRemoteViewerSessionId;
        var lifetimeSeconds = Math.Max(20, Math.Min(120, heartbeatSeconds * 4));
        Task.Factory.StartNew(delegate
        {
            try
            {
                RemoteMonitoringEngine.SendViewerPresence(
                    connection,
                    targetMachineId,
                    settings.RemoteMachineId,
                    Environment.MachineName ?? "",
                    sessionId,
                    action,
                    lifetimeSeconds);
            }
            catch (Exception error)
            {
                LogMessage("Debug", "Remote viewer presence update failed: " + error.GetType().Name + ": " + error.Message);
            }
        });
    }

    private void EndActiveRemoteViewerPresence()
    {
        if (!remoteViewMode || activeRemoteConnection == null || string.IsNullOrWhiteSpace(activeRemoteMachineId) ||
            string.IsNullOrWhiteSpace(activeRemoteViewerSessionId))
        {
            activeRemoteViewerSessionId = "";
            activeRemotePresenceLastSentUtc = DateTime.MinValue;
            return;
        }

        SendActiveRemoteViewerPresenceAsync("disconnected", true);
        activeRemoteViewerSessionId = "";
        activeRemotePresenceLastSentUtc = DateTime.MinValue;
    }

    private void ApplyRemoteViewerPresenceCommands(string connectionId, IEnumerable<RemoteViewerPresenceCommand> commands)
    {
        foreach (var command in (commands ?? Enumerable.Empty<RemoteViewerPresenceCommand>())
            .Where(item => item != null)
            .OrderBy(item => PresenceCommandCreatedUtc(item)))
        {
            if (command == null || string.IsNullOrWhiteSpace(command.SessionId))
            {
                continue;
            }

            var key = (connectionId ?? "") + "|" + command.SessionId;
            RemoteViewerPresenceState existing;
            var action = (command.Action ?? "").Trim().ToLowerInvariant();
            var createdUtc = PresenceCommandCreatedUtc(command);
            if (createdUtc == DateTime.MinValue)
            {
                continue;
            }
            if (action == "disconnected")
            {
                if (remoteViewerPresenceStates.TryGetValue(key, out existing))
                {
                    if (createdUtc <= existing.LastEventUtc)
                    {
                        continue;
                    }
                    if (existing.Connected)
                    {
                        AnnounceRemoteViewerChange(connectionId, existing.ViewerMachineName, false);
                    }
                    existing.Connected = false;
                    existing.LastEventUtc = createdUtc;
                    existing.ExpiresUtc = DateTime.UtcNow.AddMinutes(3);
                }
                else
                {
                    remoteViewerPresenceStates[key] = new RemoteViewerPresenceState
                    {
                        ConnectionId = connectionId ?? "",
                        ViewerMachineId = command.ViewerMachineId ?? "",
                        ViewerMachineName = command.ViewerMachineName ?? "",
                        SessionId = command.SessionId,
                        LastEventUtc = createdUtc,
                        ExpiresUtc = DateTime.UtcNow.AddMinutes(3),
                        Connected = false
                    };
                }
                continue;
            }

            DateTime expiresUtc;
            if (!DateTime.TryParse(command.ExpiresUtc, out expiresUtc))
            {
                continue;
            }
            expiresUtc = expiresUtc.ToUniversalTime();
            if (remoteViewerPresenceStates.TryGetValue(key, out existing))
            {
                if (createdUtc <= existing.LastEventUtc)
                {
                    continue;
                }
                var wasConnected = existing.Connected;
                existing.Connected = true;
                existing.LastEventUtc = createdUtc;
                existing.ExpiresUtc = expiresUtc;
                existing.ViewerMachineName = command.ViewerMachineName ?? existing.ViewerMachineName;
                if (!wasConnected)
                {
                    AnnounceRemoteViewerChange(connectionId, existing.ViewerMachineName, true);
                }
                continue;
            }

            var state = new RemoteViewerPresenceState
            {
                ConnectionId = connectionId ?? "",
                ViewerMachineId = command.ViewerMachineId ?? "",
                ViewerMachineName = command.ViewerMachineName ?? "",
                SessionId = command.SessionId,
                LastEventUtc = createdUtc,
                ExpiresUtc = expiresUtc,
                Connected = true
            };
            remoteViewerPresenceStates[key] = state;
            AnnounceRemoteViewerChange(connectionId, state.ViewerMachineName, true);
        }
    }

    private void ExpireRemoteViewerPresenceSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in remoteViewerPresenceStates.Where(item => item.Value == null || item.Value.ExpiresUtc < now).ToList())
        {
            remoteViewerPresenceStates.Remove(pair.Key);
            if (pair.Value != null && pair.Value.Connected)
            {
                AnnounceRemoteViewerChange(pair.Value.ConnectionId, pair.Value.ViewerMachineName, false);
            }
        }
    }

    private static DateTime PresenceCommandCreatedUtc(RemoteViewerPresenceCommand command)
    {
        DateTime createdUtc;
        return command != null && DateTime.TryParse(command.CreatedUtc, out createdUtc)
            ? createdUtc.ToUniversalTime()
            : DateTime.MinValue;
    }

    private void AnnounceRemoteViewerChange(string connectionId, string viewerMachineName, bool connected)
    {
        var connection = settings.RemoteConnections.FirstOrDefault(item => item != null &&
            string.Equals(item.Id, connectionId, StringComparison.OrdinalIgnoreCase));
        if (connection == null)
        {
            return;
        }

        var viewerName = string.IsNullOrWhiteSpace(viewerMachineName)
            ? T("ui.A remote computer", "A remote computer")
            : viewerMachineName.Trim();
        var message = connected
            ? string.Format(T("message.remoteViewerConnected", "{0} started viewing this computer remotely."), viewerName)
            : string.Format(T("message.remoteViewerDisconnected", "{0} stopped viewing this computer remotely."), viewerName);
        if (!string.IsNullOrWhiteSpace(connection.RemoteViewerSoundFile))
        {
            PlaySoundFile(connection.RemoteViewerSoundFile);
        }
        if (connection.AnnounceRemoteViewers)
        {
            SpeakTextWithScreenReader(message, "remote viewer notification");
        }
        statusLabel.Text = message;
    }

    private void ApplyActiveRemoteSnapshot(RemoteMachineSnapshot snapshot)
    {
        activeRemoteSnapshot = snapshot;
        SetLatestRows(RemoteSnapshotCodec.ToSensorRows(activeRemoteSnapshot));
        UpdateWindowTitle();
        UpdateDeviceList();
        UpdateReadingList();
        UpdateTrayStatus();
        UpdateRemoteStatusText();
    }

    private void UpdateRemoteStatusText()
    {
        if (!remoteViewMode || activeRemoteSnapshot == null)
        {
            return;
        }
        DateTime generatedUtc;
        var age = DateTime.TryParse(activeRemoteSnapshot.GeneratedUtc, out generatedUtc)
            ? FormatRecentElapsedAge(generatedUtc.ToLocalTime(), DateTime.Now)
            : T("ui.Unknown", "Unknown");
        statusLabel.Text = string.Format(
            T("status.viewingRemoteComputer", "Viewing {0} remotely. Last update: {1}. {2} readings."),
            activeRemoteSnapshot.MachineName,
            age,
            latestRows.Count);
    }

    private void ShowRemoteFanProfileDialog(RemoteConnectionSetting connection, RemoteMachineDescriptor machine)
    {
        if (connection == null || machine == null || machine.FanProfiles == null || machine.FanProfiles.Count == 0)
        {
            MessageBox.Show(this, T("message.remoteComputerHasNoFanProfiles", "This computer has not made any fan profiles available for remote control."), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Run remote fan profile", "Run remote fan profile");
            dialog.Font = System.Drawing.SystemFonts.MessageBoxFont;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new System.Drawing.Size(520, 380);
            dialog.MinimumSize = new System.Drawing.Size(430, 300);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = new Label
            {
                Text = string.Format(T("message.remoteFanProfileExplanation", "Choose a saved fan profile to request on {0}. The other computer must be online and have explicitly allowed remote fan profiles."), machine.MachineName),
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = T("a11y.Remote fan profiles", "Remote fan profiles") };
            foreach (var profile in machine.FanProfiles) list.Items.Add(profile);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var run = new Button { Text = T("ui.&Run", "&Run"), AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = T("ui.Cancel", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(run);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(list, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = run;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedItem == null) return;
            var selected = (RemoteFanProfileDescriptor)list.SelectedItem;
            statusLabel.Text = T("status.sendingRemoteFanProfile", "Sending remote fan profile request...");
            Task.Factory.StartNew(delegate
            {
                RemoteMonitoringEngine.SendFanProfileCommand(connection, machine.MachineId, settings.RemoteMachineId, selected);
            }).ContinueWith(delegate(Task task)
            {
                if (IsDisposed) return;
                if (task.IsFaulted)
                {
                    var error = task.Exception == null ? null : task.Exception.GetBaseException();
                    statusLabel.Text = T("status.remoteFanProfileFailed", "Could not send the remote fan profile request:") + " " + (error == null ? T("ui.Unknown error", "Unknown error") : error.Message);
                    return;
                }
                statusLabel.Text = string.Format(T("status.remoteFanProfileSent", "Requested fan profile {0} on {1}."), selected.Name, machine.MachineName);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private void ApplyRemoteFanProfileCommand(string connectionId, RemoteFanProfileCommand command)
    {
        if (command == null || settings.FanProfiles == null) return;
        var liveConnection = settings.RemoteConnections == null ? null : settings.RemoteConnections.FirstOrDefault(connection =>
            connection != null && string.Equals(connection.Id, connectionId, StringComparison.OrdinalIgnoreCase));
        if (liveConnection == null || !liveConnection.Enabled || !liveConnection.PublishThisComputer || !liveConnection.AllowRemoteFanProfiles)
        {
            LogMessage("Normal", "Ignored remote fan profile request because remote fan profiles are no longer allowed for this server.");
            return;
        }
        var profile = settings.FanProfiles.FirstOrDefault(candidate =>
            candidate != null && string.Equals(RemoteMonitoringEngine.FanProfileId(candidate), command.FanProfileId, StringComparison.Ordinal));
        if (profile == null)
        {
            LogMessage("Normal", "Ignored remote fan profile request because the saved profile no longer matches: " + (command.FanProfileName ?? ""));
            return;
        }
        var safeProfile = new FanProfileSetting
        {
            Name = profile.Name,
            SoundFile = profile.SoundFile,
            Speak = profile.Speak,
            SpeechMessage = profile.SpeechMessage,
            ToggleAutomatic = false,
            Actions = profile.Actions == null
                ? new List<FanProfileActionSetting>()
                : profile.Actions.Select(action => new FanProfileActionSetting
                {
                    FanControlKey = action.FanControlKey,
                    Manual = action.Manual,
                    Percent = action.Percent
                }).ToList()
        };
        LogMessage("Normal", "Applying authorized remote fan profile request: " + safeProfile.Name + ".");
        ApplyFanProfile(safeProfile, true, true);
    }
}
