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
  /// <see cref="AudioCaptureService"/> is registered three times, resolving to one
  /// instance: as itself for those consumers, as the <see cref="IAudioDevices"/>
  /// the web layer picks a device through, and as the hosted service that runs its
  /// loop. A second copy would enumerate its own devices and capture nothing anyone
  /// reads.
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

    services.AddSingleton<IAudioDeviceCatalog, AudioDeviceCatalog>();

    // The same per-user folder the Hue settings and the bridge key live in, resolved
    // the same way, because "where Flux remembers things" should be one place on disk
    // rather than one place per project.
    string directory = System.IO.Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "Flux");

    services.AddSingleton<IAudioSettingsStore>(provider => new JsonAudioSettingsStore(
      directory,
      provider.GetRequiredService<ILogger<JsonAudioSettingsStore>>()));

    // The recorder is registered here rather than beside the frame recorder in
    // AddAudioAnalysis, because it records what capture produces and not what the
    // analyzer makes of it. A host with a level meter and no FFT thread can still
    // record the signal, which is the configuration the calibration work needs.
    // Registered twice, resolving to one instance: as the tap the capture callback
    // hands samples to, and as the hosted service that owns the file. It does
    // nothing unless Audio:RecordAudioPath is set.
    services.AddSingleton<AudioRecorder>();
    services.AddSingleton<IMonoTap>(provider => provider.GetRequiredService<AudioRecorder>());
    services.AddHostedService(provider => provider.GetRequiredService<AudioRecorder>());

    services.AddSingleton<AudioCaptureService>(provider => new AudioCaptureService(
      provider.GetRequiredService<IAudioDeviceCatalog>(),
      provider.GetRequiredService<IMonotonicClock>(),
      provider.GetRequiredService<IOptions<AudioOptions>>(),
      provider.GetRequiredService<IMonoTap>(),
      provider.GetRequiredService<ILogger<AudioCaptureService>>(),
      provider.GetRequiredService<IAudioSettingsStore>()));
    services.AddSingleton<IAudioDevices>(provider => provider.GetRequiredService<AudioCaptureService>());
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

  /// <summary>
  /// Adds audio beat detection as a sync source, and as the feed the visualizer
  /// draws.
  ///
  /// Separate from <see cref="AddAudioAnalysis"/> and required to follow it,
  /// because the two answer different questions. Analysis produces a spectrum,
  /// which is worth having whichever source is driving the lights and is what makes
  /// recording a fixture possible before any of this exists. This turns that
  /// spectrum into a tempo, which is a claim about the music that only makes sense
  /// alongside a beat pipeline to publish it into.
  ///
  /// Registered four ways, all resolving to one instance: as itself, as a source
  /// the manager can report on, as the spectrum feed the web layer reads, and as
  /// the hosted service that subscribes to the analyzer. A second copy would run a
  /// second detector over the same frames and publish into a sink nothing is
  /// listening to.
  ///
  /// <see cref="IAudioSpectrum"/> is registered here rather than in
  /// <see cref="AddAudioAnalysis"/> deliberately: the frames a browser draws carry
  /// the detector's verdict as well as the bands, so a spectrum feed without a
  /// detector behind it could only send half a picture, and the missing half is the
  /// half the instrument exists for.
  /// </summary>
  public static IServiceCollection AddAudioSource(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    services.AddSingleton<AudioSyncSource>();
    services.AddSingleton<ISyncSource>(provider => provider.GetRequiredService<AudioSyncSource>());
    services.AddSingleton<IAudioSpectrum>(provider => provider.GetRequiredService<AudioSyncSource>());
    services.AddHostedService(provider => provider.GetRequiredService<AudioSyncSource>());

    return services;
  }
}
