using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DownholeStudio.Licensing;

namespace DownholeStudio.Licensing.Tests;

/// <summary>Controllable clock.</summary>
public sealed class TestClock(DateTimeOffset start)
{
    public DateTimeOffset Now { get; set; } = start;

    public void Advance(TimeSpan by) => Now += by;
}

public sealed class FakeFingerprint(string deviceHash, string? hardwareHash = null) : IMachineFingerprintProvider
{
    public static string HashOf(string seed) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    public DeviceFingerprint GetFingerprint() => new() { DeviceHash = deviceHash, HardwareHash = hardwareHash };

    public string MachineName => "TEST-PC";

    public string OsDescription => "Test OS";
}

/// <summary>An ephemeral signing key and matching public key ring.</summary>
public sealed class TestKeys : IDisposable
{
    public const string KeyId = "test-key";

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public PublicKeyRing Ring => new(new Dictionary<string, string> { [KeyId] = Key.ExportSubjectPublicKeyInfoPem() });

    public string Sign(LicenseClaims claims) => LicenseTokenCodec.Sign(claims, Key, KeyId);

    public void Dispose() => Key.Dispose();
}

/// <summary>Scriptable stand-in for the licensing server.</summary>
public sealed class FakeServerHandler : HttpMessageHandler
{
    public Func<string, JsonElement, LicenseResponse>? Respond { get; set; }

    public bool Unreachable { get; set; }

    public List<string> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("No route to host");
        }

        var path = request.RequestUri!.AbsolutePath;
        Calls.Add(path);
        var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        var response = Respond?.Invoke(path, body) ?? new LicenseResponse { Success = false, ErrorCode = LicenseErrorCodes.ServerError, Message = "not scripted" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
    }
}

/// <summary>A LicenseManager wired to a fake server, a memory store and a test clock.</summary>
public sealed class ClientHarness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public ClientHarness(string deviceSeed = "device-A", MemoryLicenseStore? store = null, TestKeys? keys = null, TestClock? clock = null, bool online = true)
    {
        _ownsKeys = keys is null;
        Keys = keys ?? new TestKeys();
        Clock = clock ?? new TestClock(T0);
        Store = store ?? new MemoryLicenseStore();
        DeviceHash = FakeFingerprint.HashOf(deviceSeed);
        Server = new FakeServerHandler();
        var options = new LicensingOptions { ServerUrl = online ? new Uri("https://licensing.test/") : null, Keys = Keys.Ring };
        Manager = new LicenseManager(options, Store, new FakeFingerprint(DeviceHash, FakeFingerprint.HashOf(deviceSeed + "-hw")),
            new LicenseApiClient(new HttpClient(Server), options.ServerUrl), "1.0.0-test", () => Clock.Now);
    }

    private readonly bool _ownsKeys;

    public TestKeys Keys { get; }
    public TestClock Clock { get; }
    public MemoryLicenseStore Store { get; }
    public FakeServerHandler Server { get; }
    public LicenseManager Manager { get; }
    public string DeviceHash { get; }

    public LicenseResponse TrialResponse(DateTimeOffset trialStart) => new()
    {
        Success = true, ServerTime = Clock.Now,
        Token = Keys.Sign(new LicenseClaims
        {
            Type = LicenseClaims.TrialType, Product = ProductIds.DownholeStudio, DeviceHash = DeviceHash, IssuedAt = Clock.Now,
            TrialStart = trialStart, TrialEnd = trialStart.AddDays(14), ExpiresAt = trialStart.AddDays(14),
            OfflineValidUntil = Min(Clock.Now.AddDays(3), trialStart.AddDays(14)),
        }),
    };

    public LicenseResponse LicenseResponse(DateTimeOffset? expires = null, string? device = null) => new()
    {
        Success = true, ServerTime = Clock.Now,
        Token = Keys.Sign(new LicenseClaims
        {
            Type = LicenseClaims.LicenseType, LicenseId = "6f1d1a39-9c5d-4b0e-9a1f-1b2c3d4e5f60", Product = ProductIds.DownholeStudio, DeviceHash = device ?? DeviceHash,
            LicensedTo = "Acme Geotechnical", Email = "jane@acme.example", KeyHint = "ABCD", IssuedAt = Clock.Now, ExpiresAt = expires,
            MaintenanceUntil = Clock.Now.AddYears(1), OfflineValidUntil = Clock.Now.AddDays(30), SeatCount = 1,
        }),
    };

    public static LicenseResponse Error(string code, string message) => new() { Success = false, ErrorCode = code, Message = message };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    public void Dispose()
    {
        if (_ownsKeys)
        {
            Keys.Dispose();
        }
    }
}
