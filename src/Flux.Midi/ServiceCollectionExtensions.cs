using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flux.Midi;

/// <summary>Wires the FLX-10 observer into a host.</summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Adds the read-only MIDI observer that lets a cue press anchor the bar.
  ///
  /// A hosted service and nothing else, which is the whole shape of this feature:
  /// it registers no sync source, publishes no tempo and is resolved by nobody, so
  /// there is no second interface to hand out. It needs
  /// <see cref="Flux.Beat.SyncSourceManager"/>, so it has to follow
  /// <c>AddBeatCore</c>.
  ///
  /// The settings are validated on start rather than on first use, because the first
  /// use of them is on a background thread where a bad value looks like a controller
  /// that is not plugged in.
  /// </summary>
  public static IServiceCollection AddFlxObserver(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddSingleton<IValidateOptions<MidiOptions>, MidiOptionsValidator>();
    services.AddOptions<MidiOptions>()
      .Bind(configuration.GetSection(MidiOptions.SectionName))
      .ValidateOnStart();

    services.AddHostedService<FlxObserver>();

    return services;
  }
}
