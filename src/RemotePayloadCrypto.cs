using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

internal static class RemotePayloadCrypto
{
    internal const int MaximumEnvelopeBytes = 8 * 1024 * 1024;
    internal const int MaximumPlaintextBytes = 32 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SRREMOTE1");
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("SensorReadout.RemoteSecret.v1");
    private static readonly byte[] MasterKeySalt = Encoding.UTF8.GetBytes("SensorReadout.RemotePayload.MasterKey.v2");
    private static readonly byte[] PayloadKeyPurpose = Encoding.UTF8.GetBytes("SensorReadout.RemotePayload.Keys.v2");
    private static readonly object MasterKeyCacheLock = new object();
    private static readonly Dictionary<string, byte[]> MasterKeyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private static readonly Queue<string> MasterKeyCacheOrder = new Queue<string>();
    private static readonly object SpaceIdCacheLock = new object();
    private static readonly Dictionary<string, string> SpaceIdCache = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly Queue<string> SpaceIdCacheOrder = new Queue<string>();
    private const string SpacePurpose = "SensorReadout.RemoteSpaceId.v2";
    private const int SpaceIdKdfIterations = 300000;

    public static byte[] Encrypt<T>(T value, string password)
    {
        RequirePassword(password);
        var plain = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value, Formatting.None));
        if (plain.LongLength > MaximumPlaintextBytes)
        {
            throw new InvalidDataException("The remote Sensor Readout payload is too large.");
        }

        var salt = RandomBytes(16);
        var iv = RandomBytes(16);
        var keys = DeriveKeysV2(password, salt);
        var compressed = Compress(plain);
        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Key = keys.EncryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using (var encryptor = aes.CreateEncryptor())
            {
                cipher = encryptor.TransformFinalBlock(compressed, 0, compressed.Length);
            }
        }

        using (var output = new MemoryStream())
        {
            output.Write(Magic, 0, Magic.Length);
            output.WriteByte(2);
            output.Write(salt, 0, salt.Length);
            output.Write(iv, 0, iv.Length);
            output.Write(cipher, 0, cipher.Length);
            using (var hmac = new HMACSHA256(keys.MacKey))
            {
                var signature = hmac.ComputeHash(output.GetBuffer(), 0, checked((int)output.Length));
                output.Write(signature, 0, signature.Length);
            }

            if (output.Length > MaximumEnvelopeBytes)
            {
                throw new InvalidDataException("The encrypted remote Sensor Readout payload exceeds the server limit.");
            }
            return output.ToArray();
        }
    }

    public static T Decrypt<T>(byte[] envelope, string password)
    {
        RequirePassword(password);
        if (envelope == null || envelope.Length < Magic.Length + 1 + 16 + 16 + 32 || envelope.Length > MaximumEnvelopeBytes)
        {
            throw new InvalidDataException("The encrypted remote Sensor Readout payload is incomplete or too large.");
        }

        for (var i = 0; i < Magic.Length; i++)
        {
            if (envelope[i] != Magic[i])
            {
                throw new InvalidDataException("This is not a supported Sensor Readout remote payload.");
            }
        }

        var offset = Magic.Length;
        var formatVersion = envelope[offset++];
        if (formatVersion != 1 && formatVersion != 2)
        {
            throw new InvalidDataException("This Sensor Readout remote payload uses an unsupported format.");
        }

        var salt = Slice(envelope, offset, 16);
        offset += 16;
        var iv = Slice(envelope, offset, 16);
        offset += 16;
        var signatureOffset = envelope.Length - 32;
        var suppliedSignature = Slice(envelope, signatureOffset, 32);
        var keys = formatVersion == 1 ? DeriveKeysV1(password, salt) : DeriveKeysV2(password, salt);
        using (var hmac = new HMACSHA256(keys.MacKey))
        {
            var expectedSignature = hmac.ComputeHash(envelope, 0, signatureOffset);
            if (!ConstantTimeEquals(expectedSignature, suppliedSignature))
            {
                throw new CryptographicException("The remote monitoring password is incorrect, or the payload was altered.");
            }
        }

        byte[] compressed;
        using (var aes = Aes.Create())
        {
            aes.Key = keys.EncryptionKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using (var decryptor = aes.CreateDecryptor())
            {
                compressed = decryptor.TransformFinalBlock(envelope, offset, signatureOffset - offset);
            }
        }

        var plain = Decompress(compressed);
        var value = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(plain));
        if (object.Equals(value, default(T)))
        {
            throw new InvalidDataException("The decrypted Sensor Readout payload is empty.");
        }
        return value;
    }

    public static string DeriveSpaceId(string accessToken, string password)
    {
        var token = (accessToken ?? "").Trim();
        RequirePassword(password);
        if (token.Length == 0)
        {
            throw new ArgumentException("The server access token is required.", "accessToken");
        }

        string cacheKey;
        byte[] kdfSalt;
        using (var sha = SHA256.Create())
        {
            cacheKey = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token + "\n" + password)));
            kdfSalt = sha.ComputeHash(Encoding.UTF8.GetBytes(SpacePurpose + "\n" + token));
        }
        lock (SpaceIdCacheLock)
        {
            string cached;
            if (SpaceIdCache.TryGetValue(cacheKey, out cached)) return cached;
        }

        string derived;
        // This is PBKDF2-HMAC-SHA1, matching Python hashlib.pbkdf2_hmac("sha1", ...).
        using (var kdf = new Rfc2898DeriveBytes(password, kdfSalt, SpaceIdKdfIterations))
        {
            derived = ToBase64Url(kdf.GetBytes(32));
        }
        lock (SpaceIdCacheLock)
        {
            if (!SpaceIdCache.ContainsKey(cacheKey))
            {
                while (SpaceIdCacheOrder.Count >= 16)
                {
                    SpaceIdCache.Remove(SpaceIdCacheOrder.Dequeue());
                }
                SpaceIdCache[cacheKey] = derived;
                SpaceIdCacheOrder.Enqueue(cacheKey);
            }
            return SpaceIdCache[cacheKey];
        }
    }

    public static string CreateRandomId()
    {
        return ToBase64Url(RandomBytes(32));
    }

    public static string CreateMonitoringPassword()
    {
        return ToBase64Url(RandomBytes(24));
    }

    public static string ProtectSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), SecretEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        var plain = ProtectedData.Unprotect(Convert.FromBase64String(value), SecretEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private static void RequirePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 1024)
        {
            throw new ArgumentException("A remote monitoring password between 8 and 1024 characters is required.", "password");
        }
    }

    private static KeyPair DeriveKeysV1(string password, byte[] salt)
    {
        using (var derive = new Rfc2898DeriveBytes(password, salt, 150000))
        {
            return new KeyPair { EncryptionKey = derive.GetBytes(32), MacKey = derive.GetBytes(32) };
        }
    }

    private static KeyPair DeriveKeysV2(string password, byte[] salt)
    {
        var masterKey = GetMasterKey(password);
        byte[] material;
        using (var hmac = new HMACSHA512(masterKey))
        {
            var input = new byte[PayloadKeyPurpose.Length + salt.Length];
            Buffer.BlockCopy(PayloadKeyPurpose, 0, input, 0, PayloadKeyPurpose.Length);
            Buffer.BlockCopy(salt, 0, input, PayloadKeyPurpose.Length, salt.Length);
            material = hmac.ComputeHash(input);
        }
        return new KeyPair
        {
            EncryptionKey = Slice(material, 0, 32),
            MacKey = Slice(material, 32, 32)
        };
    }

    private static byte[] GetMasterKey(string password)
    {
        string cacheKey;
        using (var sha = SHA256.Create())
        {
            cacheKey = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password)));
        }
        lock (MasterKeyCacheLock)
        {
            byte[] cached;
            if (MasterKeyCache.TryGetValue(cacheKey, out cached)) return (byte[])cached.Clone();
        }

        byte[] derived;
        using (var derive = new Rfc2898DeriveBytes(password, MasterKeySalt, 150000))
        {
            derived = derive.GetBytes(32);
        }
        lock (MasterKeyCacheLock)
        {
            if (!MasterKeyCache.ContainsKey(cacheKey))
            {
                while (MasterKeyCacheOrder.Count >= 16)
                {
                    var oldest = MasterKeyCacheOrder.Dequeue();
                    byte[] oldKey;
                    if (MasterKeyCache.TryGetValue(oldest, out oldKey)) Array.Clear(oldKey, 0, oldKey.Length);
                    MasterKeyCache.Remove(oldest);
                }
                MasterKeyCache[cacheKey] = (byte[])derived.Clone();
                MasterKeyCacheOrder.Enqueue(cacheKey);
            }
            return (byte[])MasterKeyCache[cacheKey].Clone();
        }
    }

    private static byte[] Compress(byte[] value)
    {
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
            {
                gzip.Write(value, 0, value.Length);
            }
            return output.ToArray();
        }
    }

    private static byte[] Decompress(byte[] value)
    {
        using (var input = new MemoryStream(value))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = gzip.Read(buffer, 0, buffer.Length);
                if (count <= 0)
                {
                    break;
                }
                if (output.Length + count > MaximumPlaintextBytes)
                {
                    throw new InvalidDataException("The remote Sensor Readout payload expands beyond its safety limit.");
                }
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }

    private static byte[] RandomBytes(int count)
    {
        var value = new byte[count];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(value);
        }
        return value;
    }

    private static byte[] Slice(byte[] source, int offset, int count)
    {
        var value = new byte[count];
        Buffer.BlockCopy(source, offset, value, 0, count);
        return value;
    }

    private static bool ConstantTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }
        var difference = 0;
        for (var i = 0; i < left.Length; i++)
        {
            difference |= left[i] ^ right[i];
        }
        return difference == 0;
    }

    private static string ToBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value ?? new byte[0]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class KeyPair
    {
        public byte[] EncryptionKey;
        public byte[] MacKey;
    }
}
