using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private const string FirmwareSecuritySource = "Windows UEFI";
    private const string EfiGlobalVariableGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

    private static readonly string[] SecureBootVariableNames = new[]
    {
        "PK", "PKDefault",
        "KEK", "KEKDefault",
        "db", "dbDefault",
        "dbx", "dbxDefault",
        "dbt", "dbtDefault",
        "dbr", "dbrDefault"
    };

    private static readonly HashSet<string> PkFailSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "55FBEF8781230084471700B3CD873AF4",
        "55FBEF878123008447170BB3CD873AF4",
        "08C2D1C36C9B514FB37C6A020812CD59",
        "15FE0D049B3B7470BC6F1AD296EDC47B",
        "3D0B418817BB6D55B0D679BA5E56D3A7",
        "1BED93E2594E2B60BE6B1F01C9AFA637",
        "584C656DD40D8DAE48E5ECBBA97F6F51",
        "0B799448A77741B11682D2BE16503B",
        "1AA9C761C86ABE884D85F5AD2B953BF1",
        "271D73F22B76F7B24D52E152E040EBDD",
        "621D1A58A834F262BF4E2BE527E9321C",
        "751548571AC5499D4BE32511E17D413A",
        "39E77F95D59D7483460CB0067554ABF5",
        "33C9DA4A889052A54DDA26FEC3C7BCBE",
        "45D3FD0033525D45B536DE474E15CC56",
        "4518B4224E57128B441825A1F45E811D",
        "3EE50134BDA5DF51BF6EA56F2A78088F",
        "08D71AD0151FF541BB20E7EE5521996D",
        "520B5C2169A25A64BBABF5D8EF5A7CCE",
        "18AB836014846E8447D4BCEA92444BD2",
        "079F37C33751BF5CB33B53739224E6C6",
        "0D4E5614A0939251B3A55899CE7C4003",
        "4F95812EA5056267B44666D1F75C229D",
        "645ECDDE8EAE668A48301EFDB88792FF",
        "53EA3387AFA20171BEFF551696910CA4",
        "59EB946E59B0F7844A0C62A94C55F51B",
        "7E2551331818C385DB21E2921A3823A88",
        "3BFF4218E02B34AF4B6D575F6ABD12BC",
        "4A2537F8DFEE4162B13A734955924B54",
        "A1BEC60DE98B0185"
    };

    private IEnumerable<SensorRow> GetFirmwareSecurityRows()
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hardware = T("type.Firmware Security", "Firmware Security");
        var firmwareMode = GetFirmwareMode();
        if (string.IsNullOrWhiteSpace(firmwareMode))
        {
            firmwareMode = TryGetFirmwareType() == 2 ? "UEFI" : "";
        }

        AddDetail(details, "Firmware mode", firmwareMode);
        AddDetail(details, "Secure Boot", GetSecureBootState());
        AddDetail(details, "Data note", "Sensor Readout reads firmware security state only. It does not modify Secure Boot variables or EFI files.");
        AddDetail(details, "Certificate expiry note", "An expired or soon-expiring certificate in a Secure Boot database is not by itself a boot failure. Windows and firmware can contain multiple trusted certificates, and current Windows boot components can continue to work while newer Secure Boot certificates are rolled out through Windows or firmware updates.");

        var rows = new List<SensorRow>();
        AddFirmwareSecurityRow(rows, hardware, "Firmware mode", string.IsNullOrWhiteSpace(firmwareMode) ? "Unknown" : firmwareMode, null, details, "firmware-security.mode");
        AddFirmwareSecurityRow(rows, hardware, "Secure Boot", string.IsNullOrWhiteSpace(GetSecureBootState()) ? "Unknown" : GetSecureBootState(), SecureBootNumericValue(GetSecureBootState()), details, "firmware-security.secure-boot");

        if (!string.Equals(firmwareMode, "UEFI", StringComparison.OrdinalIgnoreCase))
        {
            AddFirmwareSecurityRow(rows, hardware, "UEFI database access", "Not available on non-UEFI firmware", 0, details, "firmware-security.access");
            return rows;
        }

        var enabledPrivilege = EnableSystemEnvironmentPrivilege();
        AddDetail(details, "Firmware privilege enabled", FormatYesNo(enabledPrivilege));

        var summaries = new List<FirmwareVariableSummary>();
        foreach (var variable in SecureBootVariableNames)
        {
            var summary = ReadFirmwareVariableSummary(variable);
            summaries.Add(summary);
            AddFirmwareVariableDetails(details, summary);
        }

        var readable = summaries.Where(s => s.Readable).ToList();
        if (readable.Count == 0)
        {
            var error = summaries.Select(s => s.Error).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            AddDetail(details, "UEFI database access error", error);
            AddFirmwareSecurityRow(rows, hardware, "UEFI database access", string.IsNullOrWhiteSpace(error) ? "No readable Secure Boot databases" : error, 0, details, "firmware-security.access");
            return rows;
        }

        var certs = readable.SelectMany(s => s.Certificates.Select(c => new { Variable = s.Name, Certificate = c })).ToList();
        var now = DateTime.Now;
        var expired = certs.Count(c => c.Certificate.NotAfter < now);
        var notYetValid = certs.Count(c => c.Certificate.NotBefore > now);
        var possibleTestCerts = certs.Count(c => LooksLikePkFailCertificate(c.Certificate));
        var hashCount = readable.Sum(s => s.HashCount);
        var earliest = certs.Select(c => c.Certificate.NotAfter).Where(d => d != DateTime.MinValue).OrderBy(d => d).FirstOrDefault();

        AddFirmwareSecurityRow(rows, hardware, "UEFI database access", "Readable", 1, details, "firmware-security.access");
        AddFirmwareSecurityRow(rows, hardware, "UEFI certificate databases", FormatFirmwareDatabaseCount(readable.Count, certs.Count, hashCount), readable.Count, details, "firmware-security.database-count");
        if (earliest != DateTime.MinValue)
        {
            AddFirmwareSecurityRow(rows, hardware, "Earliest certificate expiry", FormatFirmwareDateWithAge(earliest), null, details, "firmware-security.earliest-expiry");
        }
        AddFirmwareSecurityRow(rows, hardware, "Expired certificates", expired.ToString(CultureInfo.InvariantCulture), expired, details, "firmware-security.expired");
        AddFirmwareSecurityRow(rows, hardware, "Not-yet-valid certificates", notYetValid.ToString(CultureInfo.InvariantCulture), notYetValid, details, "firmware-security.not-yet-valid");
        AddFirmwareSecurityRow(rows, hardware, "Possible test certificates", possibleTestCerts.ToString(CultureInfo.InvariantCulture), possibleTestCerts, details, "firmware-security.test-certs");
        AddFirmwareSecurityRow(rows, hardware, "Hash-only entries", hashCount.ToString(CultureInfo.InvariantCulture), hashCount, details, "firmware-security.hash-count");
        return rows;
    }

    private static void AddFirmwareSecurityRow(List<SensorRow> rows, string hardware, string name, string displayValue, float? value, Dictionary<string, string> details, string identifier)
    {
        rows.Add(new SensorRow
        {
            Type = "Firmware Security",
            Hardware = hardware,
            Name = name,
            DisplayValue = displayValue,
            Value = value,
            Source = FirmwareSecuritySource,
            Details = CloneDetails(details),
            Identifier = identifier
        });
    }

    private static string FormatFirmwareDatabaseCount(int readableDatabases, int certificateCount, int hashCount)
    {
        return readableDatabases.ToString(CultureInfo.InvariantCulture) + " databases; " +
            certificateCount.ToString(CultureInfo.InvariantCulture) + " certificates; " +
            hashCount.ToString(CultureInfo.InvariantCulture) + " hashes";
    }

    private static float? SecureBootNumericValue(string state)
    {
        if (string.Equals(state, "On", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(state, "Off", StringComparison.OrdinalIgnoreCase)) return 0;
        return null;
    }

    private static string FormatFirmwareDateWithAge(DateTime date)
    {
        var prefix = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = (date.Date - DateTime.Now.Date).Days;
        if (days == 0) return prefix + " (today)";
        if (days > 0) return prefix + " (" + days.ToString(CultureInfo.InvariantCulture) + " days from now)";
        return prefix + " (" + Math.Abs(days).ToString(CultureInfo.InvariantCulture) + " days ago)";
    }

    private static FirmwareVariableSummary ReadFirmwareVariableSummary(string variableName)
    {
        var summary = new FirmwareVariableSummary { Name = variableName };
        try
        {
            uint attributes;
            var bytes = ReadFirmwareVariable(variableName, out attributes);
            summary.Readable = true;
            summary.Attributes = "0x" + attributes.ToString("X8", CultureInfo.InvariantCulture);
            summary.ByteCount = bytes == null ? 0 : bytes.Length;
            if (bytes != null && bytes.Length > 0)
            {
                var entries = ParseEfiSignatureLists(bytes);
                summary.Certificates.AddRange(entries.Where(e => e.Certificate != null).Select(e => e.Certificate));
                summary.HashCount = entries.Count(e => e.Certificate == null);
            }
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode == 203)
            {
                summary.NotPresent = true;
            }
            else
            {
                summary.Error = ex.Message;
            }
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
        }

        return summary;
    }

    private static byte[] ReadFirmwareVariable(string variableName, out uint attributes)
    {
        attributes = 0;
        var buffer = new byte[256 * 1024];
        var read = GetFirmwareEnvironmentVariableEx(variableName, EfiGlobalVariableGuid, buffer, (uint)buffer.Length, out attributes);
        if (read == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error);
        }

        var result = new byte[read];
        Array.Copy(buffer, result, read);
        return result;
    }

    private static List<EfiSignatureEntry> ParseEfiSignatureLists(byte[] bytes)
    {
        var offset = FindEfiSignatureListOffset(bytes);
        if (offset < 0)
        {
            offset = 0;
        }

        var entries = new List<EfiSignatureEntry>();
        while (offset + 28 <= bytes.Length)
        {
            var typeBytes = new byte[16];
            Array.Copy(bytes, offset, typeBytes, 0, 16);
            var listSize = BitConverter.ToUInt32(bytes, offset + 16);
            var headerSize = BitConverter.ToUInt32(bytes, offset + 20);
            var signatureSize = BitConverter.ToUInt32(bytes, offset + 24);
            if (listSize < 28 || signatureSize < 16 || listSize > bytes.Length - offset)
            {
                break;
            }

            var type = new Guid(typeBytes);
            var dataOffset = offset + 28 + (int)headerSize;
            var end = offset + (int)listSize;
            while (dataOffset + signatureSize <= end)
            {
                var dataSize = (int)signatureSize - 16;
                var data = new byte[dataSize];
                Array.Copy(bytes, dataOffset + 16, data, 0, dataSize);
                X509Certificate2 cert = null;
                if (IsEfiX509Guid(type))
                {
                    try
                    {
                        cert = new X509Certificate2(data);
                    }
                    catch
                    {
                        cert = null;
                    }
                }

                entries.Add(new EfiSignatureEntry { Type = type, Certificate = cert });
                dataOffset += (int)signatureSize;
            }

            offset += (int)listSize;
        }

        return entries;
    }

    private static int FindEfiSignatureListOffset(byte[] bytes)
    {
        var known = new[]
        {
            EfiGuidBytes("a5c059a1-94e4-4aa7-87b5-ab155c2bf072"),
            EfiGuidBytes("c1c41626-504c-4092-aca9-41f936934328"),
            EfiGuidBytes("3c585400-4818-11e3-a721-000d3af82f4a"),
            EfiGuidBytes("e2b36190-879b-4a3d-ad8d-f2e7bba32784"),
            EfiGuidBytes("9254e776-a8a6-4b06-8d97-b29af6a3b551")
        };

        for (var i = 0; i <= bytes.Length - 28; i++)
        {
            foreach (var pattern in known)
            {
                var match = true;
                for (var j = 0; j < 16; j++)
                {
                    if (bytes[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                var size = BitConverter.ToUInt32(bytes, i + 16);
                if (size >= 28 && size <= bytes.Length - i)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static byte[] EfiGuidBytes(string guidText)
    {
        return new Guid(guidText).ToByteArray();
    }

    private static bool IsEfiX509Guid(Guid guid)
    {
        return guid.Equals(new Guid("a5c059a1-94e4-4aa7-87b5-ab155c2bf072"));
    }

    private static void AddFirmwareVariableDetails(Dictionary<string, string> details, FirmwareVariableSummary summary)
    {
        var prefix = "UEFI " + summary.Name + " ";
        AddDetail(details, prefix + "readable", FormatYesNo(summary.Readable));
        AddDetail(details, prefix + "present", FormatYesNo(!summary.NotPresent));
        AddDetail(details, prefix + "attributes", summary.Attributes);
        AddDetail(details, prefix + "byte count", summary.ByteCount > 0 ? summary.ByteCount.ToString(CultureInfo.InvariantCulture) : "");
        AddDetail(details, prefix + "error", summary.Error);
        AddDetail(details, prefix + "certificate count", summary.Certificates.Count.ToString(CultureInfo.InvariantCulture));
        AddDetail(details, prefix + "hash-only count", summary.HashCount.ToString(CultureInfo.InvariantCulture));

        for (var i = 0; i < summary.Certificates.Count; i++)
        {
            var cert = summary.Certificates[i];
            var certPrefix = prefix + "certificate " + (i + 1).ToString(CultureInfo.InvariantCulture) + " ";
            AddDetail(details, certPrefix + "subject", cert.Subject);
            AddDetail(details, certPrefix + "issuer", cert.Issuer);
            AddDetail(details, certPrefix + "common name", CertificateCommonName(cert.Subject));
            AddDetail(details, certPrefix + "valid from", cert.NotBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddDetail(details, certPrefix + "valid to", cert.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddDetail(details, certPrefix + "thumbprint", cert.Thumbprint);
            AddDetail(details, certPrefix + "serial number", cert.SerialNumber);
            AddDetail(details, certPrefix + "possible test certificate", FormatYesNo(LooksLikePkFailCertificate(cert)));
        }
    }

    private static string CertificateCommonName(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return "";
        }

        var parts = distinguishedName.Split(',');
        foreach (var part in parts)
        {
            var text = part.Trim();
            if (text.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return text.Substring(3).Trim();
            }
        }

        return distinguishedName;
    }

    private static bool LooksLikePkFailCertificate(X509Certificate2 cert)
    {
        if (cert == null)
        {
            return false;
        }

        var serial = NormalizeCertificateSerial(cert.SerialNumber);
        if (PkFailSerials.Contains(serial))
        {
            return true;
        }

        var combined = (cert.Subject ?? "") + " " + (cert.Issuer ?? "");
        return combined.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("not trust", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("not ship", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeCertificateSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var ch in serial)
        {
            if (Uri.IsHexDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static int TryGetFirmwareType()
    {
        uint type;
        try
        {
            if (GetFirmwareType(out type))
            {
                return (int)type;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static bool EnableSystemEnvironmentPrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token))
        {
            return false;
        }

        try
        {
            Luid luid;
            if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = 0x00000002
            };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            return Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private sealed class FirmwareVariableSummary
    {
        public string Name;
        public bool Readable;
        public string Attributes;
        public int ByteCount;
        public string Error;
        public bool NotPresent;
        public int HashCount;
        public readonly List<X509Certificate2> Certificates = new List<X509Certificate2>();
    }

    private sealed class EfiSignatureEntry
    {
        public Guid Type;
        public X509Certificate2 Certificate;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableEx(string lpName, string lpGuid, byte[] pBuffer, uint nSize, out uint pdwAttribubutes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint FirmwareType);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TokenPrivileges NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }
}
