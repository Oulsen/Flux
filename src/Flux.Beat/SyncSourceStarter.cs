using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flux.Beat;

/// <summary>
/// Brings every registered sync source into existence at startup, and says which
/// ones there are.
///
/// It exists because sources register themselves with
/// <see cref="SyncSourceManager"/> from their own constructors, and a container
/// singleton that nothing resolves is never constructed. Without this, a source
/// no other component depends on - Manual, right up until the user selects it -
/// would be absent from the selector at exactly the moment it was wanted, and the
/// fault would look like it lived in the web app.
///
/// The log line is not decoration. "Which sources does this build actually have?"
/// is the first question when the selector shows fewer than expected, and
/// answering it at startup is cheaper than answering it from a bug report.
/// </summary>
internal sealed class SyncSourceStarter: IHostedService {
  private readonly ILogger<SyncSourceStarter> log;
  private readonly string[] names;

  public SyncSourceStarter(IEnumerable<ISyncSource> sources, ILogger<SyncSourceStarter> log) {
    ArgumentNullException.ThrowIfNull(sources);

    this.log = log ?? throw new ArgumentNullException(nameof(log));

    // Enumerated here rather than held: reading the names is what forces the
    // container to construct each source, which is what makes each of them
    // register. Holding the sequence and never walking it would construct nothing.
    this.names = [.. sources.Select(source => source.Name).Order(StringComparer.Ordinal)];
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    this.log.LogInformation("Sync sources available: {Sources}.", string.Join(", ", this.names));

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    return Task.CompletedTask;
  }
}
