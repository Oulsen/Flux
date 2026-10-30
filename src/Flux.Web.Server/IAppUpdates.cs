namespace Flux.Web.Server;

/// <summary>Where an update stands, as far as the page needs to know.</summary>
public enum UpdateStatus {
  /// <summary>
  /// This copy cannot update itself: a development build that was never installed, or
  /// updates switched off. Nothing will change it while the process runs.
  /// </summary>
  Unavailable,

  /// <summary>Asking for a newer release, or downloading one that was found.</summary>
  Checking,

  /// <summary>No newer release is known: the last check found none, or none has run yet.</summary>
  Current,

  /// <summary>A newer release is downloaded and waits for a restart.</summary>
  Ready,

  /// <summary>The last check did not get an answer: offline, or GitHub did not respond.</summary>
  Failed,
}

/// <summary>An update's status, and the version it concerns when there is one.</summary>
/// <param name="Status">Where the update stands.</param>
/// <param name="Version">The downloaded version when <see cref="UpdateStatus.Ready"/>, otherwise null.</param>
public sealed record UpdateState(UpdateStatus Status, string? Version);

/// <summary>
/// The app updating itself, as the page sees it.
///
/// Here rather than beside its implementation because the dependency runs the other way:
/// the desktop app references this project and not the reverse, and the installer that
/// does the work is the desktop app's business. A host with no installer registers none,
/// which is how <c>IAudioDevices</c> is optional too.
/// </summary>
public interface IAppUpdates {
  /// <summary>Where the update stands now.</summary>
  UpdateState State { get; }

  /// <summary>Raised on whichever thread moved the state on.</summary>
  event Action<UpdateState>? Changed;

  /// <summary>
  /// Asks for a newer release now. Returns at once; the answer arrives through
  /// <see cref="Changed"/>. Joins a check already running rather than starting another.
  /// </summary>
  void Check();

  /// <summary>
  /// Stops Flux and starts the downloaded version in its place. Nothing happens unless
  /// the state is <see cref="UpdateStatus.Ready"/>.
  /// </summary>
  void RestartToUpdate();
}
