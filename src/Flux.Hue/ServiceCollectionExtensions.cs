using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace Flux.Hue;

/// <summary>Wires the Hue bridge into a host.</summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Adds the settings and the credential store the bridge work is built on.
  ///
  /// Windows-only, and annotated rather than guarded, because the only Windows-bound
  /// piece is DPAPI and the alternative would be a runtime failure on a platform this
  /// application does not ship to. Everything above the store - discovery, pairing,
  /// the stream, the colour engine - is ordinary HTTP and UDP and stays portable, so
  /// the day a second credential store exists this annotation is the only thing in
  /// the way.
  ///
  /// The settings are validated on start rather than on first use: first use is a
  /// background loop, where a rejected value looks exactly like a bridge nobody
  /// switched on.
  /// </summary>
  [SupportedOSPlatform("windows")]
  public static IServiceCollection AddHue(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddSingleton<IValidateOptions<HueOptions>, HueOptionsValidator>();
    services.AddOptions<HueOptions>()
      .Bind(configuration.GetSection(HueOptions.SectionName))
      .ValidateOnStart();

    // %APPDATA%\Flux - per-user, user-writable and surviving an update, which is the
    // combination an installed app needs and the install directory has none of. The
    // path is resolved here rather than inside the stores so that a test can point the
    // same classes at a temporary folder without a setting existing for it.
    string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flux");

    services.AddSingleton<ICredentialStore>(provider => new DpapiCredentialStore(
      directory,
      provider.GetRequiredService<ILogger<DpapiCredentialStore>>()));
    services.AddSingleton<IHueSettingsStore>(provider => new JsonHueSettingsStore(
      directory,
      provider.GetRequiredService<ILogger<JsonHueSettingsStore>>()));

    services.AddSingleton<IHueBridgeClient, HueApiBridgeClient>();

    // Registered as itself and as the hosted service, because the web layer calls it
    // by name: a second instance would hold a second, disagreeing picture of the same
    // bridge, and the page would show whichever one answered.
    services.AddSingleton<HueBridgeService>();
    services.AddHostedService(provider => provider.GetRequiredService<HueBridgeService>());

    return services;
  }
}
