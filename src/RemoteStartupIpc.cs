using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal enum RemoteStartupCommandKind
{
    ImportConnection,
    ShowRemoteComputers,
    DisconnectRemote,
    ConnectRemote
}

internal sealed class RemoteStartupCommand
{
    public RemoteStartupCommandKind Kind;
    public string Path = "";
    public string Target = "";

    public static bool TryParse(string[] args, out RemoteStartupCommand command, out string error)
    {
        command = null;
        error = "";
        if (args == null || args.Length == 0)
        {
            return false;
        }

        string importPath = null;
        var showRemoteComputers = false;
        var disconnectRemote = false;
        string connectServer = null;
        string connectComputer = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index] ?? "";
            if (string.Equals(argument, "--import-remote-connection", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "--import-remote-connection requires a .srconnection file path.";
                    return true;
                }
                importPath = args[++index];
            }
            else if (argument.StartsWith("--import-remote-connection=", StringComparison.OrdinalIgnoreCase))
            {
                importPath = argument.Substring(argument.IndexOf('=') + 1);
            }
            else if (string.Equals(argument, "--remote-computers", StringComparison.OrdinalIgnoreCase))
            {
                showRemoteComputers = true;
            }
            else if (string.Equals(argument, "--return-to-this-computer", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(argument, "--disconnect-remote", StringComparison.OrdinalIgnoreCase))
            {
                disconnectRemote = true;
            }
            else if (string.Equals(argument, "--connect-remote", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 2 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 2]) ||
                    args[index + 1].StartsWith("--", StringComparison.Ordinal) || args[index + 2].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "--connect-remote requires a saved server name or ID followed by a computer name or ID.";
                    return true;
                }
                connectServer = args[++index];
                connectComputer = args[++index];
            }
        }

        if (importPath == null && args.Length == 1 &&
            string.Equals(System.IO.Path.GetExtension(args[0]), ".srconnection", StringComparison.OrdinalIgnoreCase))
        {
            importPath = args[0];
        }

        var commandCount = (importPath == null ? 0 : 1) + (showRemoteComputers ? 1 : 0) + (disconnectRemote ? 1 : 0) + (connectServer == null ? 0 : 1);
        if (commandCount == 0)
        {
            return false;
        }
        if (commandCount != 1)
        {
            error = "Specify only one remote monitoring startup command at a time.";
            return true;
        }

        if (importPath != null)
        {
            if (string.IsNullOrWhiteSpace(importPath) ||
                !string.Equals(System.IO.Path.GetExtension(importPath), ".srconnection", StringComparison.OrdinalIgnoreCase))
            {
                error = "The remote connection file must use the .srconnection extension.";
                return true;
            }
            try
            {
                importPath = System.IO.Path.GetFullPath(importPath);
            }
            catch (Exception pathError)
            {
                error = "The remote connection file path is invalid: " + pathError.Message;
                return true;
            }
            command = new RemoteStartupCommand { Kind = RemoteStartupCommandKind.ImportConnection, Path = importPath };
            return true;
        }

        if (connectServer != null)
        {
            command = new RemoteStartupCommand
            {
                Kind = RemoteStartupCommandKind.ConnectRemote,
                Path = connectServer.Trim(),
                Target = connectComputer.Trim()
            };
            return true;
        }

        command = new RemoteStartupCommand
        {
            Kind = showRemoteComputers ? RemoteStartupCommandKind.ShowRemoteComputers : RemoteStartupCommandKind.DisconnectRemote
        };
        return true;
    }
}

internal sealed class RemoteStartupIpcServer : IDisposable
{
    private const int MaximumMessageBytes = 64 * 1024;
    private static readonly string CurrentPipeName = BuildPipeName();
    private readonly Action<RemoteStartupCommand> dispatch;
    private readonly Thread serverThread;
    private volatile bool stopping;

    public RemoteStartupIpcServer(Action<RemoteStartupCommand> dispatch)
    {
        this.dispatch = dispatch;
        serverThread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "Sensor Readout remote startup IPC"
        };
        serverThread.Start();
    }

    public static bool TrySend(RemoteStartupCommand command, int timeoutMilliseconds)
    {
        if (command == null)
        {
            return false;
        }
        try
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(Math.Max(1, timeoutMilliseconds));
                WriteMessage(pipe, Serialize(command));
                var response = ReadMessage(pipe);
                return string.Equals(response, "OK", StringComparison.Ordinal);
            }
        }
        catch
        {
            return false;
        }
    }

    private void ServerLoop()
    {
        while (!stopping)
        {
            try
            {
                using (var pipe = CreateServerPipe())
                {
                    pipe.WaitForConnection();
                    var message = ReadMessage(pipe);
                    if (stopping)
                    {
                        return;
                    }
                    RemoteStartupCommand command;
                    if (!TryDeserialize(message, out command))
                    {
                        WriteMessage(pipe, "ERROR");
                        continue;
                    }
                    if (dispatch != null)
                    {
                        dispatch(command);
                    }
                    WriteMessage(pipe, "OK");
                }
            }
            catch
            {
                if (!stopping)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private static string Serialize(RemoteStartupCommand command)
    {
        return ((int)command.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
               Convert.ToBase64String(Encoding.UTF8.GetBytes(command.Path ?? "")) + "\n" +
               Convert.ToBase64String(Encoding.UTF8.GetBytes(command.Target ?? ""));
    }

    private static NamedPipeServerStream CreateServerPipe()
    {
        var security = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User == null)
            {
                throw new InvalidOperationException("The current Windows user identity is unavailable.");
            }
            security.SetOwner(identity.User);
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        return new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            4096,
            4096,
            security);
    }

    private static bool TryDeserialize(string value, out RemoteStartupCommand command)
    {
        command = null;
        try
        {
            var parts = (value ?? "").Split(new[] { '\n' }, 3);
            int kindValue;
            if (parts.Length < 2 || !int.TryParse(parts[0], out kindValue) ||
                !Enum.IsDefined(typeof(RemoteStartupCommandKind), kindValue))
            {
                return false;
            }
            command = new RemoteStartupCommand
            {
                Kind = (RemoteStartupCommandKind)kindValue,
                Path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])),
                Target = parts.Length >= 3 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])) : ""
            };
            if (command.Kind == RemoteStartupCommandKind.ImportConnection)
            {
                return !string.IsNullOrWhiteSpace(command.Path) &&
                       string.Equals(System.IO.Path.GetExtension(command.Path), ".srconnection", StringComparison.OrdinalIgnoreCase);
            }
            return command.Kind != RemoteStartupCommandKind.ConnectRemote ||
                   (!string.IsNullOrWhiteSpace(command.Path) && !string.IsNullOrWhiteSpace(command.Target));
        }
        catch
        {
            return false;
        }
    }

    private static void WriteMessage(Stream stream, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message ?? "");
        if (bytes.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The startup IPC message is too large.");
        }
        var length = BitConverter.GetBytes(bytes.Length);
        stream.Write(length, 0, length.Length);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static string ReadMessage(Stream stream)
    {
        var lengthBytes = ReadExactly(stream, sizeof(int));
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length < 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The startup IPC message length is invalid.");
        }
        return Encoding.UTF8.GetString(ReadExactly(stream, length));
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(result, offset, count - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return result;
    }

    private static string PipeName
    {
        get { return CurrentPipeName; }
    }

    private static string BuildPipeName()
    {
        var identity = (Environment.UserDomainName ?? "") + "." + (Environment.UserName ?? "");
        var safeIdentity = new StringBuilder(identity.Length);
        foreach (var character in identity)
        {
            safeIdentity.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        using (var process = Process.GetCurrentProcess())
        {
            return "SensorReadout.RemoteStartup." + safeIdentity + "." + process.SessionId;
        }
    }

    public void Dispose()
    {
        stopping = true;
        try
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                pipe.Connect(250);
                WriteMessage(pipe, "stop");
            }
        }
        catch
        {
        }
        if (serverThread.IsAlive)
        {
            serverThread.Join(1000);
        }
    }
}

public sealed partial class SensorReadoutForm
{
    internal void OpenRemoteMonitoringFromStartup(string connectionFilePath)
    {
        if (!string.IsNullOrWhiteSpace(connectionFilePath))
        {
            if (RemoteMonitoringDialog.QueueConnectionFileImport(connectionFilePath))
            {
                return;
            }
        }
        else if (RemoteMonitoringDialog.ActivateOpenDialog())
        {
            return;
        }

        BringToFrontForUserPrompt();
        ShowRemoteMonitoringDialog();
    }

    internal void DisconnectRemoteFromStartup()
    {
        ReturnToLiveReadings();
    }

    internal async void ConnectRemoteFromStartup(string serverNameOrId, string computerNameOrId)
    {
        var serverMatches = settings.RemoteConnections
            .Where(connection => connection != null && connection.Enabled &&
                (string.Equals(connection.Id, serverNameOrId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(connection.Name, serverNameOrId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (serverMatches.Count != 1)
        {
            MessageBox.Show(this,
                serverMatches.Count == 0
                    ? "No enabled saved remote server matched \"" + serverNameOrId + "\"."
                    : "More than one enabled saved remote server matched \"" + serverNameOrId + "\". Use its unique ID instead.",
                "Sensor Readout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedConnection = serverMatches[0];
        try
        {
            BringToFrontForUserPrompt();
            statusLabel.Text = T("status.loadingRemoteComputers", "Loading computers...");
            var machines = await Task.Factory.StartNew(delegate { return RemoteMonitoringEngine.ListMachines(selectedConnection); });
            var machineMatches = machines
                .Where(machine => machine != null &&
                    (string.Equals(machine.MachineId, computerNameOrId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(machine.MachineName, computerNameOrId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (machineMatches.Count != 1)
            {
                throw new InvalidOperationException(machineMatches.Count == 0
                    ? "No remote computer matched \"" + computerNameOrId + "\"."
                    : "More than one remote computer matched \"" + computerNameOrId + "\". Use its unique ID instead.");
            }
            BeginRemoteView(selectedConnection, machineMatches[0].MachineId);
        }
        catch (Exception error)
        {
            statusLabel.Text = T("status.couldNotLoadRemoteComputers", "Could not load computers:") + " " + error.Message;
            MessageBox.Show(this, statusLabel.Text, T("ui.Remote monitoring", "Remote monitoring"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
