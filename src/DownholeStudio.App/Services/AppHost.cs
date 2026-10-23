using DownholeStudio.App.ViewModels;
using DownholeStudio.Core;
using DownholeStudio.Core.Import;
using DownholeStudio.Formats;
using DownholeStudio.Infrastructure;
using DownholeStudio.Licensing;
using DownholeStudio.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownholeStudio.App.Services;

/// <summary>Composition root: builds the service container once at start-up.</summary>
public static class AppHost
{
    public static string Version { get; } =
        (typeof(AppHost).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.0").Split('+')[0];

    public static IServiceProvider Build(AppPaths? paths = null, ILicenseStore? licenseStore = null, HttpMessageHandler? handler = null, PublicKeyRing? licenseKeys = null,
        IClock? clock = null, Func<DateTimeOffset>? licenseClock = null, string? licenseServerUrl = null)
    {
        var services = new ServiceCollection();
        paths ??= new AppPaths(Environment.GetEnvironmentVariable("DOWNHOLE_STUDIO_DATA"));
        var config = AppConfiguration.Load(AppContext.BaseDirectory);
        var loggerFactory = Logging.Create(paths, Environment.GetEnvironmentVariable("DOWNHOLE_STUDIO_DEBUG") == "1");
        services.AddSingleton(paths);
        services.AddSingleton(config);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(sp => new AppCatalog(paths.CatalogFile));
        services.AddSingleton(sp => new RecoveryStore(paths.Recovery));
        services.AddSingleton(sp => new CrashReporter(paths, Version));
        services.AddSingleton(clock ?? new SystemClock());
        services.AddSingleton(sp => new AppIdentity(Version, sp.GetRequiredService<SettingsStore>().Current.EditorName ?? Environment.UserName));
        services.AddSingleton(SeismicFormatRegistry.CreateDefault());
        services.AddSingleton<ImportService>();
        services.AddSingleton<UiService>();

        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DownholeStudio/{Version}");
        services.AddSingleton(http);
        services.AddSingleton<IMachineFingerprintProvider>(new MachineFingerprintProvider(EmbeddedKeys.FingerprintSalt));
        // An empty server address means offline activation only: no licensing or update requests are made.
        Uri? ServerUrl(SettingsStore settings)
        {
            var url = licenseServerUrl ?? settings.Current.LicenseServerOverride ?? config.LicenseServerUrl;
            return string.IsNullOrWhiteSpace(url) ? null : new Uri(url.EndsWith('/') ? url : url + "/");
        }

        services.AddSingleton(sp => new LicenseApiClient(sp.GetRequiredService<HttpClient>(), ServerUrl(sp.GetRequiredService<SettingsStore>())));
        services.AddSingleton(sp =>
        {
            var fp = sp.GetRequiredService<IMachineFingerprintProvider>();
            var options = new LicensingOptions { ServerUrl = ServerUrl(sp.GetRequiredService<SettingsStore>()), Keys = licenseKeys ?? EmbeddedKeys.LicenseKeys };
            var store = licenseStore ?? new FileLicenseStore(paths.LicenseFile, fp.GetFingerprint().DeviceHash);
            return new LicenseManager(options, store, fp, sp.GetRequiredService<LicenseApiClient>(), Version, licenseClock, sp.GetRequiredService<ILogger<LicenseManager>>());
        });
        services.AddSingleton(sp => new UpdateService(sp.GetRequiredService<LicenseApiClient>(), sp.GetRequiredService<HttpClient>(), paths, Version,
            sp.GetRequiredService<ILogger<UpdateService>>(), online: ServerUrl(sp.GetRequiredService<SettingsStore>()) is not null));
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }
}
