namespace Flux.Hue;

/// <summary>
/// A bridge Flux has found, or the one it is paired with.
/// </summary>
/// <param name="Id">
/// The bridge's own identifier. This is the identity that survives the network:
/// credentials belong to it, not to wherever DHCP last put it.
/// </param>
/// <param name="Address">
/// Where it answered from, this time. Expected to change between sessions, which is
/// why nothing stores it.
/// </param>
public sealed record HueBridge(string Id, string Address);

/// <summary>
/// An entertainment area on the bridge, as the picker needs to show it.
/// </summary>
/// <param name="Id">The configuration id, which is what a stream is opened against.</param>
/// <param name="Name">What it is called in the Hue app, for a human to recognise.</param>
/// <param name="ChannelCount">
/// How many channels the area drives. Worth showing next to the name, because a user
/// with two areas has usually named them badly and can still tell three lights from
/// ten.
/// </param>
/// <param name="Streaming">
/// Whether something is already streaming to this area. A bridge allows exactly one
/// session, so this is the pre-flight answer to a failure that otherwise arrives as
/// an opaque DTLS timeout - and it does not say <i>who</i>, because the bridge does
/// not offer a name to blame.
/// </param>
public sealed record HueArea(Guid Id, string Name, int ChannelCount, bool Streaming);
