using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flux.Beat;

/// <summary>Wires the Ableton Link sync source into a host.</summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers the listener, the grid and the scheduler as one unit.
  ///
  /// <see cref="BeatScheduler"/> is registered twice on purpose - once as a
  /// singleton and once as the hosted service resolving to that same instance -
  /// so that subscribing to <see cref="BeatScheduler.Beat"/> reaches the
  /// instance the host is actually running rather than a second, idle copy.
  /// </summary>
  public static IServiceCollection AddAbletonLink(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.Configure<LinkOptions>(configuration.GetSection(LinkOptions.SectionName));

    services.AddSingleton<IMonotonicClock, MonotonicClock>();
    services.AddSingleton<BeatGrid>(provider => {
      LinkOptions options = provider.GetRequiredService<IOptions<LinkOptions>>().Value;
      return new BeatGrid(provider.GetRequiredService<IMonotonicClock>(), options.Quantum, options.StaleAfter);
    });

    services.AddSingleton<BeatScheduler>();
    services.AddHostedService(provider => provider.GetRequiredService<BeatScheduler>());
    services.AddHostedService<AbletonLinkListener>();

    return services;
  }
}
