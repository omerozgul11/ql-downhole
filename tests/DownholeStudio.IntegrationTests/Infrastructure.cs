using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DownholeStudio.AdminPortal;
using DownholeStudio.LicenseServer;
using DownholeStudio.Licensing;
using DownholeStudio.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

[assembly: AssemblyFixture(typeof(DownholeStudio.IntegrationTests.LicensingEnvironment))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DownholeStudio.IntegrationTests;

/// <summary>Server-side clock shared by the test servers and the desktop clients under test.</summary>
public sealed class TestTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Records e-mails instead of sending them.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        lock (Sent)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Starts a disposable PostgreSQL 17 container (name prefixed "dhs-pgtest-"), the licence server and
/// the admin portal in-process against it, with ephemeral signing keys.
/// </summary>
public sealed class LicensingEnvironment : IAsyncLifetime
{
    private string? _container;

    public bool Available { get; private set; }
    public string? UnavailableReason { get; private set; }
    public string ConnectionString { get; private set; } = "";
    public TestTimeProvider Time { get; } = new();
    public CapturingEmailSender Mail { get; } = new();
    public ECDsa LicenseKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public ECDsa UpdateKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public const string WebhookSecret = "whsec_test_0123456789abcdef";
    public PublicKeyRing LicenseRing => new(new Dictionary<string, string> { ["test-lic"] = LicenseKey.ExportSubjectPublicKeyInfoPem() });
    public PublicKeyRing UpdateRing => new(new Dictionary<string, string> { ["test-upd"] = UpdateKey.ExportSubjectPublicKeyInfoPem() });
    public WebApplicationFactory<LicenseServerEntryPoint> Server { get; private set; } = null!;
    public WebApplicationFactory<AdminPortalEntryPoint> Portal { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!await DockerAvailableAsync())
        {
            UnavailableReason = "Docker is not available, so PostgreSQL integration tests cannot run.";
            return;
        }

        var port = FreePort();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        _container = $"dhs-pgtest-{Guid.NewGuid():N}"[..24];
        var (code, output) = await RunAsync("docker", $"run -d --rm --name {_container} -e POSTGRES_PASSWORD={password} -e POSTGRES_DB=dhs_licensing -p 127.0.0.1:{port}:5432 postgres:17-alpine");
        if (code != 0)
        {
            UnavailableReason = "Could not start PostgreSQL: " + output;
            _container = null;
            return;
        }

        ConnectionString = $"Host=127.0.0.1;Port={port};Database=dhs_licensing;Username=postgres;Password={password};Pooling=true";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var c = new NpgsqlConnection(ConnectionString);
                await c.OpenAsync();
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }

        // Environment variables are read by WebApplication.CreateBuilder in both applications.
        var settings = new Dictionary<string, string>
        {
            ["ConnectionStrings__Licensing"] = ConnectionString,
            ["Licensing__SigningKeyPem"] = LicenseKey.ExportPkcs8PrivateKeyPem(),
            ["Licensing__SigningKeyId"] = "test-lic",
            ["Licensing__KeyPepper"] = "integration-test-pepper",
            ["Updates__SigningKeyPem"] = UpdateKey.ExportPkcs8PrivateKeyPem(),
            ["Updates__SigningKeyId"] = "test-upd",
            ["Stripe__WebhookSecret"] = WebhookSecret,
            ["Database__MigrateOnStartup"] = "true",
            ["Workers__Enabled"] = "false",
            ["RateLimiting__PermitsPerMinute"] = "100000",
            ["RateLimiting__StrictPermitsPerMinute"] = "100000",
            ["Https__Redirect"] = "false",
        };
        foreach (var (k, v) in settings)
        {
            Environment.SetEnvironmentVariable(k, v);
        }

        Server = new WebApplicationFactory<LicenseServerEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(Time);
                s.AddSingleton<IEmailSender>(Mail);
            });
        });
        _ = Server.Server; // starts the host and applies migrations
        Portal = new WebApplicationFactory<AdminPortalEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Time));
        });
        _ = Portal.Server;
        Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Available)
        {
            await Portal.DisposeAsync();
            await Server.DisposeAsync();
        }

        if (_container is not null)
        {
            await RunAsync("docker", $"rm -f {_container}");
        }

        LicenseKey.Dispose();
        UpdateKey.Dispose();
    }

    public void RequireAvailable()
    {
        if (!Available)
        {
            Assert.Skip(UnavailableReason ?? "Integration environment unavailable.");
        }
    }

    /// <summary>Runs code with server services in a fresh scope.</summary>
    public async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Server.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<bool> DockerAvailableAsync()
    {
        try
        {
            return (await RunAsync("docker", "version --format {{.Server.Version}}")).Code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (stdout + stderr).Trim());
    }
}

/// <summary>Lets a test cut the network between a desktop client and the server.</summary>
public sealed class SwitchableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Offline ? throw new HttpRequestException("Network unreachable") : base.SendAsync(request, cancellationToken);
}

public sealed class TestFingerprint(string deviceHash, string? hardwareHash, string name = "FIELD-LAPTOP") : IMachineFingerprintProvider
{
    public DeviceFingerprint GetFingerprint() => new() { DeviceHash = deviceHash, HardwareHash = hardwareHash };

    public string MachineName => name;

    public string OsDescription => "Windows 11 Pro 24H2 (test)";
}

/// <summary>A real desktop licensing client talking to the in-process server.</summary>
public sealed class DesktopClient
{
    public DesktopClient(LicensingEnvironment env, string deviceSeed, string? hardwareSeed = null, MemoryLicenseStore? store = null, string machineName = "FIELD-LAPTOP")
    {
        DeviceHash = Hash(deviceSeed);
        Network = new SwitchableHandler(env.Server.Server.CreateHandler());
        Store = store ?? new MemoryLicenseStore();
        var options = new LicensingOptions { ServerUrl = new Uri("http://localhost/"), Keys = env.LicenseRing };
        Manager = new LicenseManager(options, Store, new TestFingerprint(DeviceHash, hardwareSeed is null ? null : Hash(hardwareSeed), machineName),
            new LicenseApiClient(new HttpClient(Network), options.ServerUrl), "1.0.0", () => env.Time.Now);
    }

    public string DeviceHash { get; }
    public SwitchableHandler Network { get; }
    public MemoryLicenseStore Store { get; }
    public LicenseManager Manager { get; }

    public static string Hash(string seed) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
