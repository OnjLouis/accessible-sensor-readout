using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

internal sealed class RemoteMonitoringDialog : Form
{
    private const int MaximumConnectionFileBytes = 64 * 1024;
    private readonly List<RemoteConnectionSetting> connections;
    private readonly Action saveSettings;
    private readonly Action<RemoteConnectionSetting, string> viewMachine;
    private readonly Action<RemoteConnectionSetting, RemoteMachineDescriptor> runFanProfile;
    private readonly Action<IWin32Window> configureHost;
    private readonly Action disableHost;
    private readonly Action<IWin32Window> exportHost;
    private readonly Func<bool> hostEnabled;
    private readonly Func<string> localMachineId;
    private readonly Func<string> localMachineWriteToken;
    private readonly Func<string, string, string> translate;
    private readonly ListBox connectionList;
    private readonly ListBox machineList;
    private readonly CheckBox enabledCheckBox;
    private readonly CheckBox publishCheckBox;
    private readonly CheckBox allowFanProfilesCheckBox;
    private readonly CheckBox announceViewersCheckBox;
    private readonly ComboBox viewerSoundBox;
    private readonly Button previewViewerSoundButton;
    private readonly Button previewViewerSpeechButton;
    private readonly AccessibleStatusLabel statusLabel;
    private readonly Button viewButton;
    private readonly Button remoteFanButton;
    private readonly Button removeMachineButton;
    private readonly Button editButton;
    private readonly Button removeButton;
    private readonly CheckBox hostCheckBox;
    private readonly Button exportHostButton;
    private bool changingSelection;
    private bool changingHostState;
    private bool refreshingConnectionList;
    private bool settingsDirty;
    private int machineLoadGeneration;
    private static readonly object PendingImportLock = new object();
    private static readonly Queue<string> PendingImportPaths = new Queue<string>();
    private static RemoteMonitoringDialog activeDialog;

    public RemoteMonitoringDialog(
        List<RemoteConnectionSetting> connections,
        Action saveSettings,
        Action<RemoteConnectionSetting, string> viewMachine,
        Action<RemoteConnectionSetting, RemoteMachineDescriptor> runFanProfile,
        Action<IWin32Window> configureHost,
        Action disableHost,
        Action<IWin32Window> exportHost,
        Func<bool> hostEnabled,
        Func<string> localMachineId,
        Func<string> localMachineWriteToken,
        Func<string, string, string> translate)
    {
        this.connections = connections ?? new List<RemoteConnectionSetting>();
        this.saveSettings = saveSettings;
        this.viewMachine = viewMachine;
        this.runFanProfile = runFanProfile;
        this.configureHost = configureHost;
        this.disableHost = disableHost;
        this.exportHost = exportHost;
        this.hostEnabled = hostEnabled;
        this.localMachineId = localMachineId;
        this.localMachineWriteToken = localMachineWriteToken;
        this.translate = translate;
        Text = T("ui.Remote monitoring", "Remote monitoring");
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(800, 590);
        MinimumSize = new Size(680, 480);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = T("ui.remoteMonitoringIntro", "Connect copies of Sensor Readout through a password-protected server. Readings are encrypted before they leave each computer."),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 8)
        };
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(intro, 0, 0);

        connectionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = T("a11y.Remote servers", "Remote servers") };
        machineList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = T("a11y.Computers on selected server", "Computers on selected server") };
        connectionList.SelectedIndexChanged += delegate { ConnectionSelectionChanged(); };
        machineList.SelectedIndexChanged += delegate
        {
            viewButton.Enabled = machineList.SelectedItem != null;
            remoteFanButton.Enabled = SelectedMachine != null && SelectedMachine.FanProfiles != null && SelectedMachine.FanProfiles.Count > 0;
            removeMachineButton.Enabled = CanRemoveSelectedMachine;
        };
        machineList.DoubleClick += delegate { ViewSelectedMachine(); };
        machineList.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ViewSelectedMachine();
            }
            else if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RemoveSelectedMachine();
            }
        };
        var serverGroup = new GroupBox { Text = T("ui.Servers", "Servers"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        var machineGroup = new GroupBox { Text = T("ui.Computers", "Computers"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        serverGroup.Controls.Add(connectionList);
        machineGroup.Controls.Add(machineList);
        layout.Controls.Add(serverGroup, 0, 1);
        layout.Controls.Add(machineGroup, 1, 1);

        var serverButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var addButton = new Button { Text = T("ui.&Add server...", "&Add server..."), AutoSize = true };
        editButton = new Button { Text = T("ui.&Edit server...", "&Edit server..."), AutoSize = true };
        var importButton = new Button { Text = T("ui.&Import connection...", "&Import connection..."), AutoSize = true };
        removeButton = new Button { Text = T("ui.&Remove server", "&Remove server"), AutoSize = true };
        addButton.Click += delegate { AddServer(); };
        editButton.Click += delegate { EditServer(); };
        importButton.Click += delegate { ImportServer(); };
        removeButton.Click += delegate { RemoveServer(); };
        serverButtons.Controls.Add(addButton);
        serverButtons.Controls.Add(editButton);
        serverButtons.Controls.Add(importButton);
        serverButtons.Controls.Add(removeButton);
        hostCheckBox = new CheckBox { Text = T("ui.&Host this computer", "&Host this computer"), AutoSize = true };
        exportHostButton = new Button { Text = T("ui.Save host &connection...", "Save h&ost connection..."), AutoSize = true };
        UpdateHostControls();
        hostCheckBox.CheckedChanged += delegate
        {
            if (changingHostState)
            {
                return;
            }
            PersistPendingChanges();
            if (hostCheckBox.Checked)
            {
                if (configureHost != null) configureHost(this);
            }
            else if (disableHost != null)
            {
                disableHost();
            }
            UpdateHostControls();
        };
        exportHostButton.Click += delegate { if (exportHost != null) exportHost(this); };
        serverButtons.Controls.Add(hostCheckBox);
        serverButtons.Controls.Add(exportHostButton);
        layout.Controls.Add(serverButtons, 0, 2);

        var machineButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var refreshButton = new Button { Text = T("ui.Re&fresh computers", "Re&fresh computers"), AutoSize = true };
        viewButton = new Button { Text = T("ui.&View computer", "&View computer"), AutoSize = true, Enabled = false };
        remoteFanButton = new Button { Text = T("ui.Run fan &profile...", "Run fan &profile..."), AutoSize = true, Enabled = false };
        removeMachineButton = new Button { Text = T("ui.Remove this co&mputer from server", "Remove this co&mputer from server"), AutoSize = true, Enabled = false };
        refreshButton.Click += delegate { RefreshMachines(); };
        viewButton.Click += delegate { ViewSelectedMachine(); };
        remoteFanButton.Click += delegate { if (runFanProfile != null && SelectedConnection != null && SelectedMachine != null) runFanProfile(SelectedConnection, SelectedMachine); };
        removeMachineButton.Click += delegate { RemoveSelectedMachine(); };
        machineButtons.Controls.Add(refreshButton);
        machineButtons.Controls.Add(viewButton);
        machineButtons.Controls.Add(remoteFanButton);
        machineButtons.Controls.Add(removeMachineButton);
        layout.Controls.Add(machineButtons, 1, 2);

        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        enabledCheckBox = new CheckBox { Text = T("ui.E&nable this server", "E&nable this server"), AutoSize = true };
        publishCheckBox = new CheckBox { Text = T("ui.&Share this computer", "&Share this computer"), AutoSize = true };
        allowFanProfilesCheckBox = new CheckBox { Text = T("ui.A&llow remote saved fan profiles", "A&llow this server to run saved fan profiles on this computer"), AutoSize = true };
        enabledCheckBox.CheckedChanged += delegate { SaveConnectionChoices(); };
        publishCheckBox.CheckedChanged += delegate { SaveConnectionChoices(); };
        allowFanProfilesCheckBox.CheckedChanged += delegate { SaveConnectionChoices(); };
        choices.Controls.Add(enabledCheckBox);
        choices.Controls.Add(publishCheckBox);
        choices.Controls.Add(allowFanProfilesCheckBox);
        layout.SetColumnSpan(choices, 2);
        layout.Controls.Add(choices, 0, 3);

        var viewerNotifications = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2 };
        viewerNotifications.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        viewerNotifications.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewerNotifications.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        viewerNotifications.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        viewerNotifications.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        announceViewersCheckBox = new CheckBox
        {
            Text = T("ui.Announce viewer changes &with speech", "Announce viewer changes &with speech"),
            AutoSize = true,
            TabIndex = 0
        };
        viewerSoundBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            AccessibleName = T("a11y.Remote viewer notification sound", "Remote viewer notification sound"),
            TabIndex = 2
        };
        viewerSoundBox.Items.Add(T("ui.No sound", "No sound"));
        foreach (var soundFile in SensorReadoutForm.LoadSoundFileNames())
        {
            viewerSoundBox.Items.Add(soundFile);
        }
        viewerSoundBox.SelectedIndex = 0;
        previewViewerSoundButton = new Button
        {
            Text = T("ui.Preview soun&d", "Preview soun&d"),
            AutoSize = true,
            Enabled = false,
            TabIndex = 3
        };
        previewViewerSpeechButton = new Button
        {
            Text = T("ui.Preview chan&ge speech", "Preview chan&ge speech"),
            AutoSize = true,
            Enabled = false,
            TabIndex = 1
        };
        announceViewersCheckBox.CheckedChanged += delegate { SaveConnectionChoices(); };
        viewerSoundBox.SelectedIndexChanged += delegate
        {
            previewViewerSoundButton.Enabled = SelectedConnection != null && !string.IsNullOrWhiteSpace(SelectedViewerSoundFile());
            SaveConnectionChoices();
        };
        previewViewerSoundButton.Click += delegate
        {
            var soundFile = SelectedViewerSoundFile();
            if (!string.IsNullOrWhiteSpace(soundFile))
            {
                SensorReadoutForm.PreviewSoundFile(soundFile);
            }
        };
        previewViewerSpeechButton.Click += delegate
        {
            string error;
            if (!ScreenReaderOutput.TrySpeakForActiveScreenReader(
                T("status.remoteViewerPreview", "A remote computer started viewing this computer."), out error))
            {
                SetStatus(string.IsNullOrWhiteSpace(error)
                    ? T("status.screenReaderSpeechUnavailable", "Screen reader speech is not available.")
                    : error);
            }
        };
        var soundLabel = new Label
        {
            Text = T("ui.Remote viewer notification so&und", "Remote viewer notification so&und"),
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        soundLabel.TabIndex = viewerSoundBox.TabIndex - 1;
        viewerNotifications.Controls.Add(announceViewersCheckBox, 0, 0);
        viewerNotifications.SetColumnSpan(announceViewersCheckBox, 2);
        viewerNotifications.Controls.Add(previewViewerSpeechButton, 2, 0);
        viewerNotifications.Controls.Add(soundLabel, 0, 1);
        viewerNotifications.Controls.Add(viewerSoundBox, 1, 1);
        viewerNotifications.Controls.Add(previewViewerSoundButton, 2, 1);
        layout.SetColumnSpan(viewerNotifications, 2);
        layout.Controls.Add(viewerNotifications, 0, 4);

        statusLabel = new AccessibleStatusLabel { AutoSize = true, Dock = DockStyle.Fill, AccessibleName = T("a11y.Remote monitoring status", "Remote monitoring status") };
        var closeButton = new Button { Text = T("ui.Close", "Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(statusLabel, 0, 0);
        footer.Controls.Add(closeButton, 1, 0);
        layout.SetColumnSpan(footer, 2);
        layout.Controls.Add(footer, 0, 5);

        Controls.Add(layout);
        CancelButton = closeButton;
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5 && e.Modifiers == Keys.None)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RefreshMachines();
            }
        };
        Shown += delegate
        {
            lock (PendingImportLock)
            {
                activeDialog = this;
            }
            ImportPendingConnectionFiles();
        };
        FormClosing += delegate { machineLoadGeneration++; };
        FormClosed += delegate
        {
            lock (PendingImportLock)
            {
                if (ReferenceEquals(activeDialog, this))
                {
                    activeDialog = null;
                }
            }
            PersistPendingChanges();
        };
        RefreshConnectionList();
    }

    internal static bool QueueConnectionFileImport(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A connection file path is required.", "path");
        }
        var normalizedPath = Path.GetFullPath(path ?? "");
        lock (PendingImportLock)
        {
            if (activeDialog != null && !activeDialog.IsDisposed && activeDialog.IsHandleCreated)
            {
                var dialog = activeDialog;
                dialog.BeginInvoke((MethodInvoker)delegate
                {
                    if (!dialog.IsDisposed)
                    {
                        dialog.ImportServerFromPath(normalizedPath);
                        dialog.Activate();
                    }
                });
                return true;
            }
            PendingImportPaths.Enqueue(normalizedPath);
            return false;
        }
    }

    internal static bool ActivateOpenDialog()
    {
        lock (PendingImportLock)
        {
            if (activeDialog == null || activeDialog.IsDisposed || !activeDialog.IsHandleCreated)
            {
                return false;
            }
            var dialog = activeDialog;
            dialog.BeginInvoke((MethodInvoker)delegate
            {
                if (!dialog.IsDisposed)
                {
                    dialog.Activate();
                }
            });
            return true;
        }
    }

    private void UpdateHostControls()
    {
        var enabled = hostEnabled != null && hostEnabled();
        changingHostState = true;
        hostCheckBox.Checked = enabled;
        changingHostState = false;
        exportHostButton.Enabled = enabled;
    }

    private RemoteConnectionSetting SelectedConnection
    {
        get { return connectionList.SelectedItem as RemoteConnectionSetting; }
    }

    private RemoteMachineDescriptor SelectedMachine
    {
        get { return machineList.SelectedItem as RemoteMachineDescriptor; }
    }

    private bool CanRemoveSelectedMachine
    {
        get
        {
            var machine = SelectedMachine;
            var id = localMachineId == null ? "" : localMachineId();
            return machine != null && !string.IsNullOrWhiteSpace(id) && string.Equals(machine.MachineId, id, StringComparison.Ordinal);
        }
    }

    private void RefreshConnectionList()
    {
        var selectedId = SelectedConnection == null ? "" : SelectedConnection.Id;
        refreshingConnectionList = true;
        try
        {
            connectionList.BeginUpdate();
            connectionList.Items.Clear();
            foreach (var connection in connections.OrderBy(item => item.Name))
            {
                connectionList.Items.Add(connection);
            }
            connectionList.EndUpdate();
            if (connectionList.Items.Count > 0)
            {
                connectionList.SelectedIndex = 0;
                SelectConnection(selectedId);
            }
        }
        finally
        {
            refreshingConnectionList = false;
        }
        ConnectionSelectionChanged();
    }

    private void ConnectionSelectionChanged()
    {
        if (refreshingConnectionList)
        {
            return;
        }
        machineLoadGeneration++;
        changingSelection = true;
        var connection = SelectedConnection;
        enabledCheckBox.Enabled = connection != null;
        publishCheckBox.Enabled = connection != null;
        allowFanProfilesCheckBox.Enabled = connection != null;
        announceViewersCheckBox.Enabled = connection != null;
        viewerSoundBox.Enabled = connection != null;
        previewViewerSpeechButton.Enabled = connection != null;
        editButton.Enabled = connection != null && !connection.IsEmbeddedHostConnection;
        removeButton.Enabled = connection != null && (!connection.IsEmbeddedHostConnection || hostEnabled == null || !hostEnabled());
        enabledCheckBox.Checked = connection != null && connection.Enabled;
        publishCheckBox.Checked = connection != null && connection.PublishThisComputer;
        allowFanProfilesCheckBox.Checked = connection != null && connection.AllowRemoteFanProfiles;
        announceViewersCheckBox.Checked = connection != null && connection.AnnounceRemoteViewers;
        SelectViewerSound(connection == null ? "" : connection.RemoteViewerSoundFile);
        previewViewerSoundButton.Enabled = connection != null && !string.IsNullOrWhiteSpace(SelectedViewerSoundFile());
        changingSelection = false;
        machineList.Items.Clear();
        viewButton.Enabled = false;
        remoteFanButton.Enabled = false;
        removeMachineButton.Enabled = false;
        if (connection != null && connection.Enabled)
        {
            RefreshMachines();
        }
        else if (connection != null)
        {
            SetStatus(T("status.remoteServerDisabled", "This server is disabled."));
        }
    }

    private void SaveConnectionChoices()
    {
        if (changingSelection || SelectedConnection == null)
        {
            return;
        }
        var wasEnabled = SelectedConnection.Enabled;
        SelectedConnection.Enabled = enabledCheckBox.Checked;
        SelectedConnection.PublishThisComputer = publishCheckBox.Checked;
        SelectedConnection.AllowRemoteFanProfiles = allowFanProfilesCheckBox.Checked;
        SelectedConnection.AnnounceRemoteViewers = announceViewersCheckBox.Checked;
        SelectedConnection.RemoteViewerSoundFile = SelectedViewerSoundFile();
        if (SelectedConnection.AllowRemoteFanProfiles)
        {
            SelectedConnection.PublishThisComputer = true;
            changingSelection = true;
            publishCheckBox.Checked = true;
            changingSelection = false;
        }
        settingsDirty = true;
        if (wasEnabled != SelectedConnection.Enabled)
        {
            machineLoadGeneration++;
            if (SelectedConnection.Enabled)
            {
                RefreshMachines();
            }
            else
            {
                machineList.Items.Clear();
                viewButton.Enabled = false;
                remoteFanButton.Enabled = false;
                removeMachineButton.Enabled = false;
                SetStatus(T("status.remoteServerDisabled", "This server is disabled."));
            }
        }
    }

    private void AddServer()
    {
        RemoteConnectionSetting connection;
        if (!TryEditConnection(this, null, out connection))
        {
            return;
        }
        connections.Add(connection);
        settingsDirty = true;
        RefreshConnectionList();
        SelectConnection(connection.Id);
    }

    private void ImportServer()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = T("ui.Import Sensor Readout Server connection", "Import Sensor Readout Server connection");
            dialog.Filter = T("ui.remoteConnectionFilter", "Sensor Readout Server connections (*.srconnection)|*.srconnection|JSON files (*.json)|*.json|All files (*.*)|*.*");
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
            ImportServerFromPath(dialog.FileName);
        }
    }

    private void ImportServerFromPath(string path)
    {
        try
        {
            RemoteConnectionDocument document;
            string validationError;
            if (!TryReadConnectionDocument(path, out document, out validationError))
            {
                var message = validationError == "address"
                    ? T("message.validRemoteServerAddress", "Enter a valid HTTP or HTTPS server address.")
                    : validationError == "credentials"
                        ? T("message.remoteConnectionFileCredentials", "The server connection file does not contain a valid access token.")
                        : T("message.unsupportedRemoteConnectionFile", "This is not a supported Sensor Readout Server connection file.");
                throw new InvalidDataException(message);
            }
            RemoteConnectionSetting connection;
            if (!TryEditConnection(this, new RemoteConnectionSetting
            {
                ServerUrl = document.ServerUrl,
                ProtectedAccessToken = RemotePayloadCrypto.ProtectSecret(document.Token)
            }, out connection, true))
            {
                return;
            }
            connections.Add(connection);
            settingsDirty = true;
            RefreshConnectionList();
            SelectConnection(connection.Id);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, T("message.couldNotImportRemoteConnection", "Could not import the server connection:") + " " + error.Message, T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static bool TryReadConnectionDocument(string path, out RemoteConnectionDocument document, out string validationError)
    {
        return TryReadConnectionDocument(path, null, out document, out validationError);
    }

    internal static bool TryReadConnectionDocument(string path, Func<char, string> mappedDriveRoot, out RemoteConnectionDocument document, out string validationError)
    {
        document = null;
        validationError = "unsupported";
        var resolvedPath = mappedDriveRoot == null
            ? ResolveConnectionFilePath(path)
            : ResolveConnectionFilePath(path, mappedDriveRoot);
        var file = new FileInfo(resolvedPath);
        if (!file.Exists || file.Length == 0 || file.Length > MaximumConnectionFileBytes)
        {
            return false;
        }

        try
        {
            byte[] content;
            using (var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                content = RemoteServerClient.ReadBounded(stream, MaximumConnectionFileBytes);
            }
            var json = new UTF8Encoding(false, true).GetString(content);
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }
            document = JsonConvert.DeserializeObject<RemoteConnectionDocument>(json);
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
        catch (InvalidDataException)
        {
            document = null;
            return false;
        }
        catch (DecoderFallbackException)
        {
            document = null;
            return false;
        }
        if (document == null ||
            !string.Equals(document.Format, "SensorReadoutRemoteConnection", StringComparison.Ordinal) ||
            document.ProtocolVersion != 1)
        {
            document = null;
            return false;
        }

        string normalizedUrl;
        if (!RemoteServerClient.TryNormalizeServerUrl(document.ServerUrl, out normalizedUrl))
        {
            document = null;
            validationError = "address";
            return false;
        }
        var token = (document.Token ?? "").Trim();
        if (token.Length < 32 || token.Length > 4096)
        {
            document = null;
            validationError = "credentials";
            return false;
        }

        document.ServerUrl = normalizedUrl;
        document.Token = token;
        validationError = "";
        return true;
    }

    internal static string ResolveConnectionFilePath(string path)
    {
        return ResolveConnectionFilePath(path, delegate(char driveLetter)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\" + char.ToUpperInvariant(driveLetter)))
                {
                    return key == null ? "" : Convert.ToString(key.GetValue("RemotePath")) ?? "";
                }
            }
            catch
            {
                return "";
            }
        });
    }

    internal static string ResolveConnectionFilePath(string path, Func<char, string> mappedDriveRoot)
    {
        var normalizedPath = Path.GetFullPath(path ?? "");
        if (File.Exists(normalizedPath) || mappedDriveRoot == null)
        {
            return normalizedPath;
        }

        var root = Path.GetPathRoot(normalizedPath) ?? "";
        if (root.Length != 3 || root[1] != ':' || (root[2] != '\\' && root[2] != '/'))
        {
            return normalizedPath;
        }

        var mappedRoot = mappedDriveRoot(root[0]);
        if (string.IsNullOrWhiteSpace(mappedRoot))
        {
            return normalizedPath;
        }

        var relativePath = normalizedPath.Substring(root.Length);
        var mappedPath = Path.GetFullPath(Path.Combine(mappedRoot.TrimEnd('\\', '/'), relativePath));
        return File.Exists(mappedPath) ? mappedPath : normalizedPath;
    }

    private void EditServer()
    {
        var existing = SelectedConnection;
        if (existing == null) return;
        RemoteConnectionSetting replacement;
        if (!TryEditConnection(this, existing, out replacement)) return;
        var index = connections.IndexOf(existing);
        if (index >= 0) connections[index] = replacement;
        settingsDirty = true;
        RefreshConnectionList();
        SelectConnection(replacement.Id);
    }

    private void RemoveServer()
    {
        var connection = SelectedConnection;
        if (connection == null || MessageBox.Show(this, string.Format(T("message.removeRemoteServer", "Remove the server '{0}'?"), connection.Name), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        connections.Remove(connection);
        settingsDirty = true;
        RefreshConnectionList();
    }

    private void RefreshMachines()
    {
        var selectedConnection = SelectedConnection;
        if (selectedConnection == null)
        {
            return;
        }
        var connection = CloneConnection(selectedConnection);
        var generation = ++machineLoadGeneration;
        var selectedMachineId = SelectedMachine == null ? "" : SelectedMachine.MachineId;
        SetStatus(T("status.loadingRemoteComputers", "Loading computers..."));
        machineList.Items.Clear();
        viewButton.Enabled = false;
        removeMachineButton.Enabled = false;
        Task.Factory.StartNew(delegate { return RemoteMonitoringEngine.ListMachines(connection); })
            .ContinueWith(delegate(Task<List<RemoteMachineDescriptor>> task)
            {
                if (IsDisposed || generation != machineLoadGeneration || SelectedConnection == null || !string.Equals(SelectedConnection.Id, connection.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (task.IsFaulted)
                {
                    var error = task.Exception == null ? null : task.Exception.GetBaseException();
                    SetStatus(T("status.couldNotLoadRemoteComputers", "Could not load computers:") + " " + (error == null ? T("ui.Unknown error", "Unknown error") : error.Message));
                    return;
                }
                foreach (var machine in task.Result)
                {
                    machineList.Items.Add(machine);
                }
                SetStatus(task.Result.Count == 1
                    ? T("status.oneRemoteComputerAvailable", "1 computer available.")
                    : string.Format(T("status.remoteComputersAvailable", "{0} computers available."), task.Result.Count));
                if (machineList.Items.Count > 0)
                {
                    machineList.SelectedIndex = 0;
                    for (var index = 0; index < machineList.Items.Count; index++)
                    {
                        var candidate = machineList.Items[index] as RemoteMachineDescriptor;
                        if (candidate != null && string.Equals(candidate.MachineId, selectedMachineId, StringComparison.Ordinal))
                        {
                            machineList.SelectedIndex = index;
                            break;
                        }
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ViewSelectedMachine()
    {
        var connection = SelectedConnection;
        var machine = SelectedMachine;
        if (connection == null || machine == null)
        {
            return;
        }
        if (viewMachine != null)
        {
            viewMachine(connection, machine.MachineId);
        }
        Close();
    }

    private void RemoveSelectedMachine()
    {
        var connection = SelectedConnection;
        var machine = SelectedMachine;
        if (connection == null || machine == null) return;
        if (!CanRemoveSelectedMachine)
        {
            SetStatus(T("status.onlyPublishingComputerCanRemove", "Only the publishing computer can remove its own stored entry from the server."));
            return;
        }
        if (MessageBox.Show(this,
            string.Format(T("message.removeThisRemoteComputer", "Stop sharing {0} and remove its stored encrypted readings from this server?"), machine.MachineName),
            T("ui.Remote monitoring", "Remote monitoring"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        connection.PublishThisComputer = false;
        connection.AllowRemoteFanProfiles = false;
        changingSelection = true;
        publishCheckBox.Checked = false;
        allowFanProfilesCheckBox.Checked = false;
        changingSelection = false;
        settingsDirty = true;
        PersistPendingChanges();
        var generation = ++machineLoadGeneration;
        SetStatus(T("status.removingRemoteComputer", "Removing remote computer..."));
        var writeToken = localMachineWriteToken == null ? "" : localMachineWriteToken();
        Task.Factory.StartNew(delegate { RemoteMonitoringEngine.RemoveMachine(connection, machine.MachineId, writeToken); })
            .ContinueWith(delegate(Task task)
            {
                if (IsDisposed || generation != machineLoadGeneration) return;
                if (task.IsFaulted)
                {
                    var error = task.Exception == null ? null : task.Exception.GetBaseException();
                    SetStatus(T("status.couldNotRemoveRemoteComputer", "Could not remove remote computer:") + " " + (error == null ? T("ui.Unknown error", "Unknown error") : error.Message));
                    return;
                }
                SetStatus(T("status.remoteComputerRemoved", "Remote computer removed from the server."));
                RefreshMachines();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SelectConnection(string id)
    {
        for (var i = 0; i < connectionList.Items.Count; i++)
        {
            var item = connectionList.Items[i] as RemoteConnectionSetting;
            if (item != null && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                connectionList.SelectedIndex = i;
                return;
            }
        }
    }

    private void ImportPendingConnectionFiles()
    {
        while (true)
        {
            string path;
            lock (PendingImportLock)
            {
                if (PendingImportPaths.Count == 0)
                {
                    return;
                }
                path = PendingImportPaths.Dequeue();
            }
            ImportServerFromPath(path);
        }
    }

    private string SelectedViewerSoundFile()
    {
        return viewerSoundBox.SelectedIndex <= 0 ? "" : Convert.ToString(viewerSoundBox.SelectedItem) ?? "";
    }

    private void SelectViewerSound(string fileName)
    {
        viewerSoundBox.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }
        for (var index = 1; index < viewerSoundBox.Items.Count; index++)
        {
            if (string.Equals(Convert.ToString(viewerSoundBox.Items[index]), fileName, StringComparison.OrdinalIgnoreCase))
            {
                viewerSoundBox.SelectedIndex = index;
                return;
            }
        }
    }

    private void PersistPendingChanges()
    {
        if (!settingsDirty || saveSettings == null)
        {
            return;
        }
        saveSettings();
        settingsDirty = false;
    }

    private static RemoteConnectionSetting CloneConnection(RemoteConnectionSetting source)
    {
        return new RemoteConnectionSetting
        {
            Id = source.Id,
            Name = source.Name,
            ServerUrl = source.ServerUrl,
            ProtectedAccessToken = source.ProtectedAccessToken,
            ProtectedPassword = source.ProtectedPassword,
            PublishThisComputer = source.PublishThisComputer,
            AllowRemoteFanProfiles = source.AllowRemoteFanProfiles,
            AnnounceRemoteViewers = source.AnnounceRemoteViewers,
            RemoteViewerSoundFile = source.RemoteViewerSoundFile,
            Enabled = source.Enabled,
            PollIntervalSeconds = source.PollIntervalSeconds,
            IsEmbeddedHostConnection = source.IsEmbeddedHostConnection
        };
    }

    private bool TryEditConnection(IWin32Window owner, RemoteConnectionSetting existing, out RemoteConnectionSetting result, bool importedConnection = false)
    {
        result = null;
        using (var dialog = new Form())
        {
            dialog.Text = existing == null ? T("ui.Add Sensor Readout Server", "Add Sensor Readout Server") : T("ui.Edit Sensor Readout Server", "Edit Sensor Readout Server");
            dialog.Font = SystemFonts.MessageBoxFont;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(580, 330);
            dialog.MinimumSize = new Size(520, 300);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var nameBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 200, Text = existing == null ? "" : existing.Name };
            var urlBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 2048, Text = existing == null ? "" : existing.ServerUrl };
            var tokenBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 4096, UseSystemPasswordChar = true };
            if (existing != null && !string.IsNullOrWhiteSpace(existing.ProtectedAccessToken))
            {
                tokenBox.Text = RemotePayloadCrypto.UnprotectSecret(existing.ProtectedAccessToken);
            }
            var passwordBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 1024, UseSystemPasswordChar = true };
            if (existing != null && !string.IsNullOrWhiteSpace(existing.ProtectedPassword))
            {
                passwordBox.Text = RemotePayloadCrypto.UnprotectSecret(existing.ProtectedPassword);
            }
            if (importedConnection)
            {
                urlBox.ReadOnly = true;
                tokenBox.ReadOnly = true;
            }
            var intro = new Label
            {
                Text = importedConnection
                    ? T("ui.remoteImportPasswordExplanation", "The connection file supplied the server address and access token. Enter a name and the same monitoring password used by the other Sensor Readout computers.")
                    : T("ui.remotePasswordExplanation", "Use the same monitoring password on every Sensor Readout client. The password is never sent to the server."),
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.SetColumnSpan(intro, 2);
            layout.Controls.Add(intro, 0, 0);
            AddField(layout, 1, T("ui.&Name", "&Name"), nameBox);
            AddField(layout, 2, T("ui.Server &address", "Server &address"), urlBox);
            AddField(layout, 3, T("ui.Access &token", "Access &token"), tokenBox);
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
            AddField(layout, 4, T("ui.Monitoring &password", "Monitoring &password"), passwordPanel);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = T("ui.Cancel", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.SetColumnSpan(buttons, 2);
            layout.Controls.Add(buttons, 0, 5);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            while (true)
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                string normalizedServerUrl;
                if (!RemoteServerClient.TryNormalizeServerUrl(urlBox.Text, out normalizedServerUrl))
                {
                    MessageBox.Show(owner, T("message.validRemoteServerAddress", "Enter a valid HTTP or HTTPS server address."), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    dialog.DialogResult = DialogResult.None;
                    continue;
                }
                var serverUri = new Uri(normalizedServerUrl, UriKind.Absolute);
                if (tokenBox.Text.Trim().Length < 32 || passwordBox.Text.Length < 8)
                {
                    MessageBox.Show(owner, T("message.remoteCredentialsRequired", "Enter an access token of at least 32 characters and a monitoring password of at least 8 characters."), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    dialog.DialogResult = DialogResult.None;
                    continue;
                }
                if (tokenBox.Text.Trim().Length > 4096 || passwordBox.Text.Length > 1024)
                {
                    MessageBox.Show(owner, T("message.remoteCredentialsTooLong", "The access token or monitoring password is too long."), T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    dialog.DialogResult = DialogResult.None;
                    continue;
                }
                if (serverUri.Scheme == Uri.UriSchemeHttp && !serverUri.IsLoopback &&
                    MessageBox.Show(owner,
                        T("message.remotePlainHttpWarning", "This server uses unencrypted HTTP. Although readings are encrypted, the server access token is not protected in transit. Use this only on a trusted local network or private VPN. Continue?"),
                        T("ui.Remote monitoring", "Remote monitoring"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    dialog.DialogResult = DialogResult.None;
                    continue;
                }
                result = new RemoteConnectionSetting
                {
                    Id = existing == null || string.IsNullOrWhiteSpace(existing.Id) ? RemotePayloadCrypto.CreateRandomId() : existing.Id,
                    Name = string.IsNullOrWhiteSpace(nameBox.Text) ? serverUri.Host : nameBox.Text.Trim(),
                    ServerUrl = normalizedServerUrl,
                    ProtectedAccessToken = RemotePayloadCrypto.ProtectSecret(tokenBox.Text.Trim()),
                    ProtectedPassword = RemotePayloadCrypto.ProtectSecret(passwordBox.Text),
                    PublishThisComputer = existing != null && existing.PublishThisComputer,
                    AllowRemoteFanProfiles = existing != null && existing.AllowRemoteFanProfiles,
                    AnnounceRemoteViewers = existing == null || existing.AnnounceRemoteViewers,
                    RemoteViewerSoundFile = existing == null ? "" : existing.RemoteViewerSoundFile,
                    Enabled = existing == null || existing.Enabled,
                    PollIntervalSeconds = existing == null ? 5 : existing.PollIntervalSeconds,
                    IsEmbeddedHostConnection = existing != null && existing.IsEmbeddedHostConnection
                };
                return true;
            }
        }
    }

    private static void AddField(TableLayoutPanel layout, int row, string labelText, Control control)
    {
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left };
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
        label.TabIndex = control.TabIndex - 1;
    }

    private void SetStatus(string message)
    {
        statusLabel.SetStatus(message);
    }

    private string T(string key, string fallback)
    {
        return translate == null ? fallback : translate(key, fallback);
    }
}
