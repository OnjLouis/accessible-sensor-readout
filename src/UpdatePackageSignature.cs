using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

internal sealed class SignedUpdatePackageManifest
{
    public string Format;
    public string Component;
    public string Version;
    public string Algorithm;
    public Dictionary<string, string> Files;
    public string Signature;
}

internal static class UpdatePackageSignature
{
    internal const string ManifestFileName = "sensor-readout-update-manifest.json";
    private const string ExpectedFormat = "SensorReadoutUpdatePackage";
    private const string ExpectedComponent = "WindowsClient";
    private const string ExpectedAlgorithm = "RSA-SHA256";
    private const string PublicModulusBase64 = "vWihMIt1Sm7uWv9QQD+3Svk5fzthiiILv/zbJVWlljA8Z07WpNBuIMAE2fDG19Loi9fZVrmIYV+DN1jPsLSAgoz0jn2rd/qgUz5IU1NdTikCW/QRxPw6omWwPr7Kx/xS6BabGC8vntZt+U4E1kvUzaFp+1N5f/43jKy4A7Q9dXrhvDp1jZd+xlDfNEgagWS19EtDw2CarQ5mubD4XdRplUW2bQ4QNA8Emp36MZrQy2GMer0TGWKngINdKlVUnrnW/oabopK8EQLHvu/6iS80LNzyJ88FkH9eE+aTl5ZO/SnnnTqCkLSs1VMuoQ2rhXUzgGPcs9PFZLiXFOV4x/U9a7Epo6hiigopV+Q4jop36KPYnXyUpNb7M6qeOioZr9WuTAqTwYbAxkQnzWY4iKEkHkd5JRiPf1s08PeKg5mlQObL8PLrXGyCKkN57o7ysz3V96t5GXtDxWkdmvAhVb/KlDYUz/xGzh+KHBEzcEbt3CirjoOqoUEmG0vcODSVDsP5";
    private const string PublicExponentBase64 = "AQAB";

    internal static void VerifyAndRemove(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The update package does not contain a Sensor Readout signature.");
        }

        var info = new FileInfo(manifestPath);
        if (info.Length <= 0 || info.Length > 1024 * 1024)
        {
            throw new InvalidDataException("The update package signature manifest has an invalid size.");
        }

        SignedUpdatePackageManifest manifest;
        try
        {
            manifest = JsonConvert.DeserializeObject<SignedUpdatePackageManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
        }
        catch (Exception error)
        {
            throw new InvalidDataException("The update package signature manifest is invalid.", error);
        }

        ValidateManifestHeader(manifest, packageRoot);
        var expectedFiles = NormalizeManifestFiles(manifest.Files);
        var actualFiles = Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => RelativePackagePath(packageRoot, path), path => path, StringComparer.OrdinalIgnoreCase);

        if (expectedFiles.Count != actualFiles.Count ||
            expectedFiles.Keys.Any(path => !actualFiles.ContainsKey(path)) ||
            actualFiles.Keys.Any(path => !expectedFiles.ContainsKey(path)))
        {
            throw new InvalidDataException("The update package files do not match its signed manifest.");
        }

        using (var sha256 = SHA256.Create())
        {
            foreach (var entry in expectedFiles)
            {
                string actualHash;
                using (var stream = File.OpenRead(actualFiles[entry.Key]))
                {
                    actualHash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "");
                }

                if (!string.Equals(actualHash, entry.Value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The signed update file failed verification: " + entry.Key);
                }
            }
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (Exception error)
        {
            throw new InvalidDataException("The update package signature is invalid.", error);
        }

        var payload = BuildCanonicalPayload(manifest, expectedFiles);
        var parameters = new RSAParameters
        {
            Modulus = Convert.FromBase64String(PublicModulusBase64),
            Exponent = Convert.FromBase64String(PublicExponentBase64)
        };
        var csp = new CspParameters { ProviderType = 24 };
        using (var rsa = new RSACryptoServiceProvider(csp))
        {
            rsa.ImportParameters(parameters);
            if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
            {
                throw new InvalidDataException("The update package was not signed by Sensor Readout.");
            }
        }

        File.Delete(manifestPath);
        if (File.Exists(manifestPath))
        {
            throw new IOException("The verified update signature manifest could not be removed from staging.");
        }
    }

    internal static byte[] BuildCanonicalPayload(SignedUpdatePackageManifest manifest, IDictionary<string, string> files)
    {
        var builder = new StringBuilder();
        builder.Append(manifest.Format).Append('\n');
        builder.Append(manifest.Component).Append('\n');
        builder.Append(manifest.Version).Append('\n');
        builder.Append(manifest.Algorithm).Append('\n');
        foreach (var entry in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('\t').Append(entry.Value).Append('\n');
        }
        return new UTF8Encoding(false).GetBytes(builder.ToString());
    }

    private static void ValidateManifestHeader(SignedUpdatePackageManifest manifest, string packageRoot)
    {
        if (manifest == null ||
            !string.Equals(manifest.Format, ExpectedFormat, StringComparison.Ordinal) ||
            !string.Equals(manifest.Component, ExpectedComponent, StringComparison.Ordinal) ||
            !string.Equals(manifest.Algorithm, ExpectedAlgorithm, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Files == null || manifest.Files.Count == 0 ||
            string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidDataException("The update package signature manifest is incomplete or unsupported.");
        }

        Version signedVersion;
        Version executableVersion;
        var executablePath = Path.Combine(packageRoot, "Sensor Readout.exe");
        var executableVersionText = File.Exists(executablePath)
            ? FileVersionInfo.GetVersionInfo(executablePath).FileVersion
            : "";
        if (!Version.TryParse(manifest.Version, out signedVersion) ||
            !Version.TryParse(executableVersionText, out executableVersion) ||
            signedVersion.Major != executableVersion.Major ||
            signedVersion.Minor != executableVersion.Minor ||
            signedVersion.Build != executableVersion.Build)
        {
            throw new InvalidDataException("The signed update version does not match Sensor Readout.exe.");
        }
    }

    private static Dictionary<string, string> NormalizeManifestFiles(IDictionary<string, string> source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source)
        {
            var path = (entry.Key ?? "").Replace('\\', '/');
            var hash = (entry.Value ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) ||
                path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..") ||
                !Regex.IsMatch(hash, "\\A[0-9A-F]{64}\\z") || result.ContainsKey(path))
            {
                throw new InvalidDataException("The update package signature manifest contains an invalid file entry.");
            }
            result.Add(path, hash);
        }
        return result;
    }

    private static string RelativePackagePath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update package contains a file outside its staging folder.");
        }
        return fullPath.Substring(fullRoot.Length).Replace('\\', '/');
    }
}
