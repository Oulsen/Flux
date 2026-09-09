namespace Flux.Hue;

/// <summary>
/// Where Flux has got to with a bridge.
///
/// Seven states rather than a paired boolean, for the same reason the sync sources
/// report three: the useful question is never "connected or not", it is which of
/// several honest situations the user is looking at, and each of these calls for a
/// different screen.
/// </summary>
public enum HueConnectionState {
  /// <summary>Lights are switched off in the settings. Nothing will be attempted.</summary>
  Disabled,

  /// <summary>No credentials. The pairing screen is the whole of the UI here.</summary>
  Unpaired,

  /// <summary>Listening for bridges.</summary>
  Discovering,

  /// <summary>
  /// Asking the bridge to register, once a second, waiting for the link button.
  /// The countdown belongs to this state and to no other.
  /// </summary>
  Pairing,

  /// <summary>Paired, and asking the bridge what it has.</summary>
  Connecting,

  /// <summary>Paired, reachable, and its entertainment areas are known.</summary>
  Ready,

  /// <summary>
  /// Something failed that the user has to know about. Distinct from
  /// <see cref="Unpaired"/> deliberately: "press the button" and "the bridge did not
  /// answer" lead to different actions, and collapsing them sends people to the
  /// hall cupboard for nothing.
  /// </summary>
  Failed,
}

/// <summary>
/// The whole of what the page shows about the bridge, as one snapshot.
///
/// A record rather than a bag of properties on the service, because it is broadcast
/// and compared: a client is told when this changes, and "changed" has to mean
/// something exact. Sent on change rather than at the state rate - a list of
/// entertainment areas has no business going out twenty times a second.
/// </summary>
/// <param name="State">Which situation the user is in.</param>
/// <param name="Bridge">The paired bridge, or null before there is one.</param>
/// <param name="Discovered">
/// What the last discovery found. Kept while pairing, because the pairing screen is
/// showing this list and a user who cannot see their bridge needs to know whether
/// none were found or theirs was not among them.
/// </param>
/// <param name="Areas">The bridge's entertainment areas, empty until it is reachable.</param>
/// <param name="SelectedAreaId">The area lights will be streamed to, once one is chosen.</param>
/// <param name="PairingSecondsRemaining">
/// Seconds left in the pairing window, and null in every state but
/// <see cref="HueConnectionState.Pairing"/>. Null rather than zero for the reason the
/// beat display never invents a tempo: a countdown of zero is a claim about time
/// running out, and there is no window to run out.
/// </param>
/// <param name="LatencyOffsetMs">
/// Milliseconds light is shifted against the beat, positive being early.
///
/// Part of this snapshot rather than read separately, because the slider is on the
/// same screen as everything else here and two phones must not be able to show
/// different values for it. Always a real number: unlike the countdown, there is no
/// situation in which no offset is in force.
/// </param>
/// <param name="Light">
/// How the light engine is set: which colours, how often they change, how hard a beat
/// hits.
///
/// Here beside the offset and for the same reason - these are all controls on one
/// screen, and two phones showing different values for the same knob is the failure
/// this snapshot exists to prevent. Always a real value: there is no situation in
/// which the engine is running on no settings at all.
/// </param>
/// <param name="Message">
/// English prose for a human, set when <see cref="State"/> is
/// <see cref="HueConnectionState.Failed"/>. Never parsed, and never the thing a
/// client branches on.
/// </param>
public sealed record HueConnection(
  HueConnectionState State,
  HueBridge? Bridge,
  IReadOnlyList<HueBridge> Discovered,
  IReadOnlyList<HueArea> Areas,
  Guid? SelectedAreaId,
  int? PairingSecondsRemaining,
  int LatencyOffsetMs,
  LightSettings Light,
  string? Message) {
  /// <summary>The state before anything has been attempted.</summary>
  public static HueConnection Initial(HueConnectionState state, int latencyOffsetMs, LightSettings light) {
    return new HueConnection(
      state,
      Bridge: null,
      [],
      [],
      SelectedAreaId: null,
      PairingSecondsRemaining: null,
      latencyOffsetMs,
      light,
      Message: null);
  }
}
