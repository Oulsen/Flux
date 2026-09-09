using Microsoft.Extensions.Hosting;

namespace Flux.Web.Server.Tests;

/// <summary>
/// A host lifetime that never stops.
///
/// <see cref="SocketHub"/> takes one only to bound the bridge commands it runs off the
/// receive loop, and a test that wants a never-cancelled token wants exactly this. The
/// real implementation lives behind the generic host, which none of these tests build.
/// </summary>
internal sealed class FakeHostLifetime: IHostApplicationLifetime {
  private readonly CancellationTokenSource started = new CancellationTokenSource();

  public FakeHostLifetime() {
    this.started.Cancel();
  }

  /// <summary>Already signalled, as it is for anything the host has finished starting.</summary>
  public CancellationToken ApplicationStarted => this.started.Token;

  public CancellationToken ApplicationStopping => CancellationToken.None;

  public CancellationToken ApplicationStopped => CancellationToken.None;

  /// <summary>
  /// Does nothing, deliberately. Nothing under test asks the host to stop, and a fake
  /// that quietly cancelled would make a bridge command look cancelled for a reason no
  /// test had asked for.
  /// </summary>
  public void StopApplication() {
  }
}
