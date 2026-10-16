using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownholeStudio.Licensing;

/// <summary>What the client keeps locally. The token is server-signed; the rest is integrity-protected.</summary>
public sealed record StoredLicense
{
    public string? Token { get; init; }
    /// <summary>Server time of the last successful online validation.</summary>
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    /// <summary>Highest local clock reading ever seen (clock-rollback guard).</summary>
    public DateTimeOffset? LastSeenAt { get; init; }
    /// <summary>Set when the server reported the licence revoked, suspended, expired or the device deactivated.</summary>
    public string? ServerStatus { get; init; }
    public string? ServerMessage { get; init; }
}

public interface ILicenseStore
{
    StoredLicense Load();

    void Save(StoredLicense value);
}

/// <summary>
/// Encrypted local licence state. On Windows the file is protected with DPAPI (current user);
/// elsewhere with AES-GCM using a key derived from the device hash. A file that cannot be decrypted
/// (copied from another computer, or damaged) is treated as absent — never as an accusation.
/// </summary>
public sealed class FileLicenseStore(string path, string deviceHash) : ILicenseStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuakeLogic.DownholeStudio.License.v1");

    public string Path { get; } = path;

    public StoredLicense Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new StoredLicense();
            }

            var json = Unprotect(File.ReadAllBytes(Path));
            return JsonSerializer.Deserialize<StoredLicense>(json) ?? new StoredLicense();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or FormatException or PlatformNotSupportedException)
        {
            return new StoredLicense();
        }
    }

    public void Save(StoredLicense value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllBytes(temp, Protect(JsonSerializer.SerializeToUtf8Bytes(value)));
        File.Move(temp, Path, overwrite: true);
    }

    private byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows())
        {
            return [1, .. ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)];
        }

        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Entropy);
        return [2, .. nonce, .. tag, .. cipher];
    }

    private byte[] Unprotect(byte[] data)
    {
        if (data.Length < 2)
        {
            throw new CryptographicException("Empty licence store.");
        }

        if (data[0] == 1 && OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(data[1..], Entropy, DataProtectionScope.CurrentUser);
        }

        if (data[0] == 2 && data.Length >= 29)
        {
            var nonce = data[1..13];
            var tag = data[13..29];
            var cipher = data[29..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(DeriveKey(), 16);
            aes.Decrypt(nonce, cipher, tag, plain, Entropy);
            return plain;
        }

        throw new CryptographicException("Unknown licence store format.");
    }

    private byte[] DeriveKey() => SHA256.HashData(Encoding.UTF8.GetBytes("dhs-store-key|" + deviceHash));
}

/// <summary>In-memory store for tests.</summary>
public sealed class MemoryLicenseStore : ILicenseStore
{
    public StoredLicense Value { get; set; } = new();

    public StoredLicense Load() => Value;

    public void Save(StoredLicense value) => Value = value;
}
