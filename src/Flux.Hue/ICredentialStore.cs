namespace Flux.Hue;

/// <summary>
/// Where the bridge keys live between runs.
///
/// An interface for one implementation, which is deliberate: pairing writes a
/// credential at runtime from whatever device the user is holding, so the storage
/// decision is one the rest of the code must not be able to see or depend on. It is
/// also the seam that makes the pairing flow testable without a bridge, a registry
/// or a user profile.
/// </summary>
public interface ICredentialStore {
  /// <summary>
  /// The stored credentials, or null when there are none to be had.
  ///
  /// <b>Null covers unreadable as well as absent</b>, and that is a decision rather
  /// than laziness. Encrypted-at-rest data is bound to the Windows account that
  /// wrote it, so a profile copied to another machine or another user produces a
  /// blob that exists and cannot be read - and the only cure for that is pairing
  /// again, which is exactly what a caller does when there is nothing stored. An
  /// implementation says so in the log; it does not make the caller decide.
  /// </summary>
  HueCredentials? Load();

  /// <summary>Stores credentials, replacing anything already there.</summary>
  void Save(HueCredentials credentials);

  /// <summary>
  /// Forgets the credentials.
  ///
  /// Note for whoever surfaces this in the UI: the bridge keeps its whitelist entry
  /// until it is deleted bridge-side, so this is Flux forgetting the bridge rather
  /// than the bridge forgetting Flux.
  /// </summary>
  void Clear();
}
