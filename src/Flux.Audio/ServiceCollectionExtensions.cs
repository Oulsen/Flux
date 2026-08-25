using Flux.Beat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Audio;

/// <summary>Wires audio capture into a host.</summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Adds the capture service that feeds both the analyzer and the visualizer.
  ///
  /// Capture is registered on its own, ahead of anything that consumes it, because
  /// the samples have two independent customers: the beat detector, which is one
  /// sync source among three and may not be the selected one, and the spectrum the
  /// page draws, which is worth having whichever source is driving the lights. A
  /// spectrum that went dark because the DJ selected Link would be a test
  /// instrument that switches itself off exactly when it is being used to check
  /// the thing under test.
  ///
  /// <see cref="AudioCaptureService"/> is registered twice, resolving to one
  /// instance: as itself for those consumers, and as the hosted service that runs
  /// its loop. A second copy would enumerate its own devices and capture nothing
  /// anyone reads.
  ///
  /// The settings are validated on start rather than on first use, because the
  /// first use of them is on a background thread where a bad value reads as a
  /// hardware problem.
  /// </summary>
  public static IServiceCollection AddAudioCapture(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddSingleton<IValidateOptions<AudioOptions>, AudioOptionsValidator>();
    services.AddOptions<AudioOptions>()
      .Bind(configuration.GetSection(AudioOptions.SectionName))
      .ValidateOnStart();

    services.AddSingleton<AudioDeviceCatalog>();
    services.AddSingleton<AudioCaptureService>(provider => new AudioCaptureService(
      provider.GetRequiredService<AudioDeviceCatalog>(),
      provider.GetRequiredService<IMonotonicClock>(),
      provider.GetRequiredService<IOptions<AudioOptions>>(),
      provider.GetRequiredService<ILogger<AudioCaptureService>>()));
    services.AddHostedService(provider => provider.GetRequiredService<AudioCaptureService>());

    return services;
  }
}
