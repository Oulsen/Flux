namespace Flux.Web.Server;

/// <summary>
/// A startup failure whose message is already the sentence to show somebody.
///
/// Its own type so the host can tell a failure it has explained from one it has not:
/// this message goes on screen as it is, while anything else is shown with a pointer
/// to the log that holds the stack trace.
/// </summary>
public sealed class FluxStartupException: Exception {
  public FluxStartupException() {
  }

  public FluxStartupException(string message)
    : base(message) {
  }

  public FluxStartupException(string message, Exception innerException)
    : base(message, innerException) {
  }
}
