using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DownholeStudio.Licensing;

public interface IMachineFingerprintProvider
{
    DeviceFingerprint GetFingerprint();

    /// <summary>A short human-readable device label (the computer name), shown only locally.</summary>
    string MachineName { get; }

    string OsDescription { get; }
}

/// <summary>
/// Privacy-conscious device fingerprint. Raw identifiers are normalised and hashed locally with a
/// product salt; only the hashes are transmitted. The identifiers used (Windows MachineGuid,
/// SMBIOS system UUID, baseboard serial) do not change when RAM, disks or network adapters
/// change. MAC and IP addresses are deliberately not used.
/// </summary>
public sealed class MachineFingerprintProvider(string applicationSalt) : IMachineFingerprintProvider
{
    private static readonly HashSet<string> BogusIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "00000000000000000000000000000000", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", "03000200040005000006000700080009",
        "DEFAULTSTRING", "TOBEFILLEDBYOEM", "NONE", "NOTAPPLICABLE", "0", "SYSTEMSERIALNUMBER", "123456789", "BASEBOARDSERIALNUMBER",
    };

    public string MachineName => Environment.MachineName;

    public string OsDescription => RuntimeInformation.OSDescription;

    public DeviceFingerprint GetFingerprint()
    {
        string? machineId, systemUuid, boardSerial;
        if (OperatingSystem.IsWindows())
        {
            machineId = WindowsMachineGuid();
            (systemUuid, boardSerial) = WindowsSmbios();
        }
        else
        {
            machineId = ReadFirstLine("/etc/machine-id") ?? ReadFirstLine("/var/lib/dbus/machine-id");
            systemUuid = ReadFirstLine("/sys/class/dmi/id/product_uuid");
            boardSerial = ReadFirstLine("/sys/class/dmi/id/board_serial");
        }

        machineId = Normalise(machineId);
        systemUuid = Normalise(systemUuid);
        boardSerial = Normalise(boardSerial);
        if (machineId is null && systemUuid is null)
        {
            // No stable identifier at all: fall back to the computer name so activation still works.
            machineId = Normalise(Environment.MachineName);
        }

        var device = Hash($"dhs-device-v1|{machineId}|{systemUuid}|{applicationSalt}");
        string? hardware = systemUuid is null && boardSerial is null ? null : Hash($"dhs-hw-v1|{systemUuid}|{boardSerial}|{applicationSalt}");
        return new DeviceFingerprint { DeviceHash = device, HardwareHash = hardware };
    }

    /// <summary>Upper case, braces/hyphens/whitespace removed; known placeholder values rejected.</summary>
    public static string? Normalise(string? id)
    {
        if (id is null)
        {
            return null;
        }

        var s = new string(id.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return BogusIds.Contains(s) || s.Distinct().Count() <= 1 ? null : s;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? ReadFirstLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsMachineGuid()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint signature, uint tableId, IntPtr buffer, uint size);

    /// <summary>Reads the SMBIOS system UUID (type 1) and baseboard serial number (type 2) via GetSystemFirmwareTable('RSMB').</summary>
    [SupportedOSPlatform("windows")]
    private static (string? Uuid, string? BoardSerial) WindowsSmbios()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        try
        {
            var size = GetSystemFirmwareTable(rsmb, 0, IntPtr.Zero, 0);
            if (size == 0)
            {
                return (null, null);
            }

            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetSystemFirmwareTable(rsmb, 0, ptr, size) != size)
                {
                    return (null, null);
                }

                var raw = new byte[size];
                Marshal.Copy(ptr, raw, 0, (int)size);
                return ParseSmbios(raw);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return (null, null);
        }
    }

    /// <summary>Parses a RawSMBIOSData block (8-byte header, then the structure table).</summary>
    public static (string? Uuid, string? BoardSerial) ParseSmbios(byte[] raw)
    {
        if (raw.Length < 8)
        {
            return (null, null);
        }

        var length = BitConverter.ToInt32(raw, 4);
        var end = Math.Min(raw.Length, 8 + length);
        var pos = 8;
        string? uuid = null, serial = null;
        while (pos + 4 <= end)
        {
            var type = raw[pos];
            var len = raw[pos + 1];
            if (len < 4 || pos + len > end)
            {
                break;
            }

            // Formatted area is followed by NUL-terminated strings and one extra NUL (or two NULs if none).
            var strings = new List<string>();
            var s = pos + len;
            int next;
            if (s + 1 < end && raw[s] == 0 && raw[s + 1] == 0)
            {
                next = s + 2;
            }
            else
            {
                while (s < end && raw[s] != 0)
                {
                    var e = s;
                    while (e < end && raw[e] != 0)
                    {
                        e++;
                    }

                    strings.Add(Encoding.ASCII.GetString(raw, s, e - s));
                    s = e + 1;
                }

                next = s + 1;
            }

            if (type == 1 && len >= 0x18)
            {
                uuid = Convert.ToHexString(raw, pos + 8, 16);
            }
            else if (type == 2 && len >= 0x08)
            {
                var idx = raw[pos + 7];
                if (idx > 0 && idx <= strings.Count)
                {
                    serial = strings[idx - 1];
                }
            }
            else if (type == 127)
            {
                break;
            }

            pos = next;
        }

        return (uuid, serial);
    }
}
