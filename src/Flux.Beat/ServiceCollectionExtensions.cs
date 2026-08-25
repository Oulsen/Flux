using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flux.Beat;

/// <summary>Wires the beat pipeline, and the sync sources feeding it, into a host.</summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers the parts that exist once regardless of how many sync sources
  /// there are: the clock, the grid and the scheduler.
  ///
  /// Split from the sources deliberately, because the counting of beats is not a
  /// source's business. There is exactly one grid and exactly one scheduler
  /// however many sources are registered, so adding a source can never change
  /// how a beat is numbered, fired or reported - it can only change which
  /// timeline the grid is being told about.
  ///
  /// <see cref="BeatScheduler"/> is registered twice on purpose - once as a
  /// singleton and once as the hosted service resolving to that same instance -
  /// so that subscribing to <see cref="BeatScheduler.Beat"/> reaches the
  /// instance the host is actually running rather than a second, idle copy.
  ///
  /// The settings are validated on start rather than on first use, because the
  /// first use of most of them is on a background thread where a bad value reads
  /// as a hardware problem.
  /// </summary>
  public static IServiceCollection AddBeatCore(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddSingleton<IValidateOptions<BeatOptions>, BeatOptionsValidator>();
    services.AddOptions<BeatOptions>()
      .Bind(configuration.GetSection(BeatOptions.SectionName))
      .ValidateOnStart();

    services.AddSingleton<IMonotonicClock, MonotonicClock>();
    services.AddSingleton<BeatGrid>(provider => {
      BeatOptions options = provider.GetRequiredService<IOptions<BeatOptions>>().Value;
      return new BeatGrid(provider.GetRequiredService<IMonotonicClock>(), options.Quantum, options.StaleAfter);
    });

    services.AddSingleton<SyncSourceManager>();

    // Manual is registered here rather than behind its own opt-in because it is
    // the source that needs no hardware, no network and no luck. It is what is
    // left when Link is absent and the audio device is the wrong one, so a build
    // that could omit it would be a build that can end the night dark.
    services.AddSingleton<ManualSource>();
    services.AddSingleton<ISyncSource>(provider => provider.GetRequiredService<ManualSource>());

    services.AddSingleton<BeatScheduler>();
    services.AddHostedService(provider => provider.GetRequiredService<BeatScheduler>());
    services.AddHostedService<SyncSourceStarter>();

    return services;
  }

  /// <summary>
  /// Adds the Ableton Link listener as a sync source.
  ///
  /// Optional in the sense that matters: a host that never calls this still has a
  /// working beat pipeline, which is the whole point of running music through
  /// audio detection with rekordbox closed.
  /// </summary>
  public static IServiceCollection AddAbletonLink(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddSingleton<IValidateOptions<LinkOptions>, LinkOptionsValidator>();
    services.AddOptions<LinkOptions>()
      .Bind(configuration.GetSection(LinkOptions.SectionName))
      .ValidateOnStart();

    // Registered three ways, all resolving to one instance: as itself, as a
    // source the manager can report on, and as the hosted service that runs its
    // socket. Letting the container build a second copy would give the selector a
    // listener that never receives anything.
    services.AddSingleton<AbletonLinkListener>();
    services.AddSingleton<ISyncSource>(provider => provider.GetRequiredService<AbletonLinkListener>());
    services.AddHostedService(provider => provider.GetRequiredService<AbletonLinkListener>());

    return services;
  }
}
