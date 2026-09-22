using Flux.Audio;
using Flux.Beat;
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
    services.AddSingleton<IHueStreamFactory, HueApiStreamFactory>();

    // One instance behind three registrations, resolved rather than constructed each
    // time, for the reason the bridge service is: the engine carries the transition and
    // the pulse across frames, so a second one would render a second envelope and the
    // page would see whichever answered. It is a hosted service only to subscribe to
    // the beat scheduler at start and come off it at stop - it opens nothing.
    //
    // Constructed by hand rather than by the container so the audio dependency is
    // visibly optional: GetService and not GetRequiredService, because a build with no
    // audio project registers no IAudioSpectrum and the engine is expected to light
    // every channel alike in that case. The constructor takes it as nullable but not
    // optional, which is what makes this line fail to compile rather than silently skip
    // it - the trap step 3 hit with the analyzer's band count.
    services.AddSingleton<LightEngine>(provider => new LightEngine(
      provider.GetRequiredService<BeatScheduler>(),
      provider.GetRequiredService<HueBridgeService>(),
      provider.GetRequiredService<IMonotonicClock>(),
      provider.GetRequiredService<IOptions<BeatOptions>>(),
      provider.GetService<IAudioSpectrum>(),
      provider.GetRequiredService<ILogger<LightEngine>>()));
    services.AddSingleton<ILightSource>(provider => provider.GetRequiredService<LightEngine>());
    services.AddHostedService(provider => provider.GetRequiredService<LightEngine>());

    // Not a hosted service, unlike the bridge conversation. Nothing should open a
    // DTLS session because Flux started: the bridge allows exactly one, and taking it
    // at launch would mean a beat-lab session nobody asked for was holding the show's
    // only slot.
    services.AddSingleton<HueStreamService>();

    // The engine for the page while nothing is streaming. Registered as itself so the web
    // layer can read its frames, and as a hosted service so its loop runs from startup:
    // unlike the stream it opens nothing, so there is no reason to wait to be asked.
    services.AddSingleton(provider => new LightPreview(
      provider.GetRequiredService<ILightSource>(),
      provider.GetRequiredService<HueStreamService>(),
      provider.GetRequiredService<HueBridgeService>(),
      provider.GetRequiredService<IOptions<HueOptions>>(),
      provider.GetRequiredService<ILogger<LightPreview>>()));
    services.AddHostedService(provider => provider.GetRequiredService<LightPreview>());

    // Registered as itself and as the hosted service, because the web layer calls it
    // by name: a second instance would hold a second, disagreeing picture of the same
    // bridge, and the page would show whichever one answered.
    services.AddSingleton<HueBridgeService>();
    services.AddHostedService(provider => provider.GetRequiredService<HueBridgeService>());

    return services;
  }
}
