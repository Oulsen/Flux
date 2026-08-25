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

  /// <summary>
  /// Adds the analyzer that turns captured samples into a spectrum, and the
  /// recorder that can write those frames to a file.
  ///
  /// Separate from <see cref="AddAudioCapture"/> and required to follow it. Capture
  /// has a customer with no interest in analysis - the device picker and its level
  /// meters, which have to work before anyone has chosen a device worth analyzing -
  /// so a host that wants a level meter and no FFT thread is a real configuration
  /// rather than a hypothetical one.
  ///
  /// The analyzer is registered twice, resolving to one instance: as itself for the
  /// things that read its frames, and as the hosted service that runs its thread. A
  /// second copy would transform the same audio again and publish to a slot nobody
  /// reads.
  ///
  /// <see cref="FrameRecorder"/> is registered unconditionally and does nothing
  /// unless <see cref="AudioOptions.RecordFramesPath"/> is set. Deciding here
  /// instead would mean reading configuration twice, in two places that can
  /// disagree about what "set" means.
  /// </summary>
  public static IServiceCollection AddAudioAnalysis(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.AddSingleton<SpectrumAnalyzer>(provider => new SpectrumAnalyzer(
      provider.GetRequiredService<AudioCaptureService>(),
      provider.GetRequiredService<IMonotonicClock>(),
      provider.GetRequiredService<ILogger<SpectrumAnalyzer>>()));
    services.AddHostedService(provider => provider.GetRequiredService<SpectrumAnalyzer>());
    services.AddHostedService<FrameRecorder>();

    return services;
  }
}
