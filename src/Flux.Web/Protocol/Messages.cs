using System.Text.Json;

namespace Flux.Web.Protocol;

/// <summary>
/// The wrapper every message in both directions wears: a type tag and its
/// payload.
///
/// Nesting the payload rather than flattening it is what lets a client switch on
/// <c>type</c> before it has to know anything about the fields, and what keeps a
/// future payload field from ever colliding with the tag itself.
/// </summary>
public sealed record Envelope<TData>(string Type, TData Data);

/// <summary>
/// Sent once per connection, before anything else.
///
/// The protocol version rides here rather than on every frame on purpose: at 20
/// frames a second a per-message version tag costs more bandwidth than it ever
/// tells anyone, and it can only ever change between connections anyway.
/// </summary>
/// <param name="Protocol">The version of <c>docs/protocol.md</c> this server speaks.</param>
/// <param name="App">Flux's own version, for the page footer.</param>
/// <param name="Quantum">Beats per bar, so the client sizes its indicator instead of assuming four.</param>
/// <param name="StateHz">Nominal state rate, so the client can size its own smoothing.</param>
public sealed record HelloData(int Protocol, string App, int Quantum, int StateHz);

/// <summary>
/// Where the beat is, as a snapshot rather than a delta.
/// </summary>
/// <param name="Bpm">Tempo in beats per minute.</param>
/// <param name="Beat">Fractional beats since the session anchor. May be negative, and is renumbered wholesale by a tempo change.</param>
/// <param name="BeatIndex">The integer beat, floored from <paramref name="Beat"/> by the one shared definition.</param>
/// <param name="PositionInBar">1-based position in the bar.</param>
/// <param name="BarAligned">Whether <paramref name="PositionInBar"/> means anything musically. False for all of Phase 1.</param>
public sealed record GridData(double Bpm, double Beat, long BeatIndex, int PositionInBar, bool BarAligned);

/// <summary>
/// Health counters for the current sync source.
/// </summary>
/// <param name="Packets">ALIVE packets accepted so far.</param>
/// <param name="ReanchorMaxBeats">Largest disagreement between an outgoing timeline and its replacement. Should stay at zero forever.</param>
public sealed record DiagnosticsData(long Packets, double ReanchorMaxBeats);

/// <summary>
/// The continuous picture, sent at a fixed rate whether or not anything moved.
/// </summary>
/// <param name="Source">The sync source in use. Only "link" exists in version 1.</param>
/// <param name="SourceState">"waiting", "live" or "quiet".</param>
/// <param name="Grid">Null exactly when nothing has ever been received, so a client cannot render an absent tempo as 0.00 BPM.</param>
/// <param name="Diagnostics">Source-specific counters for the diagnostics row.</param>
public sealed record StateData(string Source, string SourceState, GridData? Grid, DiagnosticsData Diagnostics);

/// <summary>
/// One beat, sent at the instant it lands. What a flash renders from.
/// </summary>
/// <param name="Index">The Link session beat number. Not a countable sequence - see "Backpressure" in the protocol document.</param>
/// <param name="PositionInBar">1-based position in the bar.</param>
/// <param name="BarAligned">Whether the bar position has been anchored yet.</param>
/// <param name="Bpm">Tempo at the moment of the beat.</param>
public sealed record BeatData(long Index, int PositionInBar, bool BarAligned, double Bpm);

/// <summary>
/// The payload of both <c>ping</c> and <c>pong</c>: a client timestamp the
/// server never interprets and returns untouched.
///
/// Not interpreting it is what makes the measurement work. The two clocks are
/// unrelated, so the only meaningful number is the round trip measured entirely
/// against the client's own.
/// </summary>
/// <param name="T">Milliseconds since the Unix epoch, by the client's clock.</param>
public sealed record EchoData(long T);

/// <summary>
/// A message the server could not act on. Never closes the connection.
/// </summary>
/// <param name="Code">A stable machine-readable code; branch on this.</param>
/// <param name="Message">English prose for a developer console. Not stable, never parse it.</param>
public sealed record ErrorData(string Code, string Message);

/// <summary>
/// An inbound message, decoded only as far as its type tag.
///
/// The payload stays a <see cref="JsonElement"/> until the tag says what it
/// should be, so one client message with the wrong shape is answered with an
/// error rather than failing the parse of the envelope around it.
/// </summary>
public sealed record ClientEnvelope(string? Type, JsonElement Data);
