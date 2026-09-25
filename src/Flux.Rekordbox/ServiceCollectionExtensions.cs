using Flux.Beat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flux.Rekordbox;

/// <summary>Wires the rekordbox source into a host.</summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Adds rekordbox as a sync source and as the downbeat for whichever source is active.
  /// Registered three ways, one instance, as the Link listener is; it needs
  /// <see cref="SyncSourceManager"/>, so it has to follow <c>AddBeatCore</c>.
  /// </summary>
  public static IServiceCollection AddRekordboxSource(this IServiceCollection services, IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddOptions<RekordboxOptions>()
      .Bind(configuration.GetSection(RekordboxOptions.SectionName))
      .ValidateOnStart();

    services.AddSingleton<RekordboxSource>();
    services.AddSingleton<ISyncSource>(provider => provider.GetRequiredService<RekordboxSource>());
    services.AddHostedService(provider => provider.GetRequiredService<RekordboxSource>());

    return services;
  }
}
