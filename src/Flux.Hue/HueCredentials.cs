namespace Flux.Hue;

/// <summary>
/// What the bridge hands back when pairing succeeds, and everything Flux needs to
/// stream to it again without the link button being pressed a second time.
/// </summary>
/// <param name="BridgeId">
/// The bridge's own identifier, which is what these keys belong to. Deliberately not
/// an address: DHCP moves a bridge and the keys stay valid, so storing where it was
/// last seen would create a second source of truth that goes stale first.
/// </param>
/// <param name="ApplicationKey">
/// The key every CLIP v2 request is made with. A credential: it grants control of
/// the lights to anyone on the LAN who holds it.
/// </param>
/// <param name="ClientKey">
/// The DTLS pre-shared key for the entertainment stream. A credential in the
/// strictest sense - it is a symmetric secret, and it is why this record is
/// encrypted at rest rather than written as plain JSON beside the other settings.
/// </param>
public sealed record HueCredentials(string BridgeId, string ApplicationKey, string ClientKey);
