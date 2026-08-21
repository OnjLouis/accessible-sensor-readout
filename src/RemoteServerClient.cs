using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

internal sealed class RemoteServerClient
{
    private static readonly string UserAgent = "SensorReadout/" + SensorReadoutForm.AppVersion;
    private const int MaximumJsonResponseBytes = 24 * 1024 * 1024;
    private const int MaximumErrorResponseBytes = 64 * 1024;
    private const int MaximumMachineEntries = 256;
    private const int MaximumDeltaEnvelopes = 128;
    private const int MaximumCommandEnvelopes = 32;
    private readonly string baseUrl;
    private readonly Uri baseUri;
    private readonly string accessToken;
    private readonly string machineWriteToken;
    private readonly string connectionGroupName;

    public RemoteServerClient(string serverUrl, string accessToken, string machineWriteToken = null)
    {
        string normalizedUrl;
        if (!TryNormalizeServerUrl(serverUrl, out normalizedUrl))
        {
            throw new ArgumentException("The Sensor Readout Server address must be an HTTP or HTTPS URL.", "serverUrl");
        }
        baseUrl = normalizedUrl.TrimEnd('/');
        baseUri = new Uri(normalizedUrl, UriKind.Absolute);
        this.accessToken = (accessToken ?? "").Trim();
        if (this.accessToken.Length < 32 || this.accessToken.Length > 4096)
        {
            throw new ArgumentException("The Sensor Readout Server access token must contain between 32 and 4096 characters.", "accessToken");
        }
        this.machineWriteToken = (machineWriteToken ?? "").Trim();
        if (this.machineWriteToken.Length > 0 && (this.machineWriteToken.Length < 32 || this.machineWriteToken.Length > 4096))
        {
            throw new ArgumentException("The remote machine publishing token must contain between 32 and 4096 characters.", "machineWriteToken");
        }
        using (var sha256 = SHA256.Create())
        {
            connectionGroupName = "SensorReadout-" + Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(this.accessToken))).Substring(0, 16);
        }
    }

    public RemoteMachineIndex GetMachineIndex(string spaceId)
    {
        var value = SendJson<RemoteMachineIndex>("GET", "/api/v1/spaces/" + Escape(spaceId) + "/machines", null);
        if (value.ProtocolVersion != 1) throw new InvalidDataException("The remote server returned an unsupported computer index.");
        value.Machines = value.Machines ?? new List<RemoteMachineIndexEntry>();
        if (value.Machines.Count > MaximumMachineEntries) throw new InvalidDataException("The remote server returned too many computers.");
        return value;
    }

    public void PutSnapshot(string spaceId, string machineId, long sequence, byte[] encryptedPayload)
    {
        SendBytes("PUT", MachinePath(spaceId, machineId) + "/snapshot?sequence=" + sequence, encryptedPayload, true);
    }

    public byte[] GetSnapshot(string spaceId, string machineId, out long snapshotSequence, out long latestSequence)
    {
        var request = CreateRequest("GET", MachinePath(spaceId, machineId) + "/snapshot");
        using (var response = GetResponse(request))
        {
            snapshotSequence = HeaderLong(response, "X-SR-Snapshot-Sequence");
            latestSequence = HeaderLong(response, "X-SR-Latest-Sequence");
            return ReadBounded(response.GetResponseStream(), RemotePayloadCrypto.MaximumEnvelopeBytes);
        }
    }

    public void PostDelta(string spaceId, string machineId, long sequence, byte[] encryptedPayload)
    {
        SendBytes("POST", MachinePath(spaceId, machineId) + "/deltas?sequence=" + sequence, encryptedPayload, true);
    }

    public RemoteDeltaEnvelopeList GetDeltas(string spaceId, string machineId, long afterSequence)
    {
        var value = SendJson<RemoteDeltaEnvelopeList>("GET", MachinePath(spaceId, machineId) + "/deltas?after=" + afterSequence, null);
        if (value.ProtocolVersion != 1) throw new InvalidDataException("The remote server returned unsupported differences.");
        value.Deltas = value.Deltas ?? new List<RemoteDeltaEnvelope>();
        if (value.Deltas.Count > MaximumDeltaEnvelopes) throw new InvalidDataException("The remote server returned too many differences.");
        return value;
    }

    public void PostHeartbeat(string spaceId, string machineId)
    {
        SendJson<object>("POST", MachinePath(spaceId, machineId) + "/heartbeat", new byte[0], true);
    }

    public void PostCommand(string spaceId, string machineId, string commandId, byte[] encryptedPayload)
    {
        SendBytes("POST", MachinePath(spaceId, machineId) + "/commands/" + Escape(commandId), encryptedPayload);
    }

    public void DeleteMachine(string spaceId, string machineId)
    {
        SendJson<object>("DELETE", MachinePath(spaceId, machineId), null, true);
    }

    public RemoteCommandEnvelopeList GetCommands(string spaceId, string machineId)
    {
        var value = SendJson<RemoteCommandEnvelopeList>("GET", MachinePath(spaceId, machineId) + "/commands", null, true);
        if (value.ProtocolVersion != 1) throw new InvalidDataException("The remote server returned unsupported commands.");
        value.Commands = value.Commands ?? new List<RemoteCommandEnvelope>();
        if (value.Commands.Count > MaximumCommandEnvelopes) throw new InvalidDataException("The remote server returned too many commands.");
        return value;
    }

    public void DeleteCommand(string spaceId, string machineId, string commandId)
    {
        SendJson<object>("DELETE", MachinePath(spaceId, machineId) + "/commands/" + Escape(commandId), null, true);
    }

    public void CheckHealth()
    {
        var request = CreateRequest("GET", "/api/v1/health", false);
        using (var response = GetResponse(request))
        {
            var json = Encoding.UTF8.GetString(ReadBounded(response.GetResponseStream(), MaximumErrorResponseBytes));
            var health = JsonConvert.DeserializeObject<RemoteServerHealth>(json);
            if (health == null || !string.Equals(health.Name, "Sensor Readout Server", StringComparison.Ordinal) || health.ProtocolVersion != 1)
            {
                throw new InvalidDataException("The address did not return a compatible Sensor Readout Server health response.");
            }
        }
    }

    internal static bool TryNormalizeServerUrl(string serverUrl, out string normalizedUrl)
    {
        normalizedUrl = "";
        var candidate = (serverUrl ?? "").Trim();
        if (candidate.Length == 0)
        {
            return false;
        }
        for (var index = 0; index < candidate.Length; index++)
        {
            if (char.IsControl(candidate[index]))
            {
                return false;
            }
        }

        Uri uri;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo) ||
            !string.IsNullOrWhiteSpace(uri.Query) ||
            !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            return false;
        }

        var host = uri.DnsSafeHost.Trim('[', ']');
        IPAddress address;
        var wildcardAddress = IPAddress.TryParse(host, out address) &&
            (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
        if (wildcardAddress || uri.Port < 1 || uri.Port > 65535)
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri.TrimEnd('/') + "/";
        return true;
    }

    private T SendJson<T>(string method, string path, byte[] body, bool requireMachineWriteToken = false)
    {
        var request = CreateRequest(method, path, true, requireMachineWriteToken);
        if (body != null && body.Length > 0)
        {
            request.ContentType = "application/octet-stream";
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream())
            {
                stream.Write(body, 0, body.Length);
            }
        }
        else if (method == "POST" || method == "PUT")
        {
            request.ContentLength = 0;
        }

        using (var response = GetResponse(request))
        {
            var json = Encoding.UTF8.GetString(ReadBounded(response.GetResponseStream(), MaximumJsonResponseBytes));
            var value = JsonConvert.DeserializeObject<T>(json);
            if (object.Equals(value, default(T)))
            {
                throw new InvalidDataException("The remote server returned an empty or invalid response.");
            }
            return value;
        }
    }

    private void SendBytes(string method, string path, byte[] body, bool requireMachineWriteToken = false)
    {
        if (body == null || body.Length == 0 || body.Length > RemotePayloadCrypto.MaximumEnvelopeBytes)
        {
            throw new ArgumentException("The encrypted remote payload is empty or too large.", "body");
        }
        var request = CreateRequest(method, path, true, requireMachineWriteToken);
        request.ContentType = "application/octet-stream";
        request.ContentLength = body.Length;
        using (var stream = request.GetRequestStream())
        {
            stream.Write(body, 0, body.Length);
        }
        using (GetResponse(request))
        {
        }
    }

    private HttpWebRequest CreateRequest(string method, string path, bool authenticate = true, bool requireMachineWriteToken = false)
    {
        var requestUri = new Uri(baseUrl + path, UriKind.Absolute);
        var request = (HttpWebRequest)WebRequest.Create(requestUri);
        if (baseUri.IsLoopback)
        {
            request.Proxy = null;
        }
        request.Method = method;
        request.ConnectionGroupName = connectionGroupName;
        request.KeepAlive = false;
        request.AllowAutoRedirect = false;
        request.UserAgent = UserAgent;
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.Headers[HttpRequestHeader.CacheControl] = "no-store";
        if (authenticate)
        {
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
        }
        if (requireMachineWriteToken)
        {
            if (machineWriteToken.Length < 32)
            {
                throw new InvalidOperationException("This operation requires the local machine publishing credential.");
            }
            request.Headers["X-SR-Machine-Token"] = machineWriteToken;
        }
        return request;
    }

    private static HttpWebResponse GetResponse(HttpWebRequest request)
    {
        try
        {
            return (HttpWebResponse)request.GetResponse();
        }
        catch (WebException error)
        {
            var response = error.Response as HttpWebResponse;
            if (response == null)
            {
                throw;
            }
            using (response)
            {
                var message = Encoding.UTF8.GetString(ReadBounded(response.GetResponseStream(), MaximumErrorResponseBytes)).Trim();
                throw new RemoteServerException(response.StatusCode, message, HeaderLong(response, "X-SR-Latest-Sequence"));
            }
        }
    }

    internal static byte[] ReadBounded(Stream stream, int maximumBytes)
    {
        if (stream == null) return new byte[0];
        using (var output = new MemoryStream())
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                {
                    break;
                }
                if (output.Length + count > maximumBytes)
                {
                    throw new InvalidDataException("The remote server response exceeds the Sensor Readout safety limit.");
                }
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }

    private static long HeaderLong(HttpWebResponse response, string name)
    {
        long value;
        return response != null && long.TryParse(response.Headers[name], out value) ? value : 0;
    }

    private static string MachinePath(string spaceId, string machineId)
    {
        return "/api/v1/spaces/" + Escape(spaceId) + "/machines/" + Escape(machineId);
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString((value ?? "").Trim());
    }
}

internal sealed class RemoteServerException : Exception
{
    public RemoteServerException(HttpStatusCode statusCode, string message, long latestSequence)
        : base(string.IsNullOrWhiteSpace(message) ? "Sensor Readout Server returned " + (int)statusCode + "." : message)
    {
        StatusCode = statusCode;
        LatestSequence = latestSequence;
    }

    public HttpStatusCode StatusCode { get; private set; }
    public long LatestSequence { get; private set; }
}
