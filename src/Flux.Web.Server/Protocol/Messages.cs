using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flux.Web.Server.Protocol;

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
/// <param name="SpectrumHz">Nominal spectrum rate for a subscriber, so a canvas can size its own decay per frame.</param>
/// <param name="DevicesHz">Nominal device list rate for a subscriber.</param>
public sealed record HelloData(int Protocol, string App, int Quantum, int StateHz, int SpectrumHz, int DevicesHz);

/// <summary>
/// Where the beat is, as a snapshot rather than a delta.
/// </summary>
/// <param name="Bpm">Tempo in beats per minute.</param>
/// <param name="Beat">Fractional beats since the session anchor. May be negative, and is renumbered wholesale by a tempo change.</param>
/// <param name="BeatIndex">The integer beat, floored from <paramref name="Beat"/> by the one shared definition.</param>
/// <param name="PositionInBar">1-based position in the bar.</param>
/// <param name="BarAligned">Whether <paramref name="PositionInBar"/> means anything musically. False until a cue press, tap or drop anchors the bar.</param>
public sealed record GridData(double Bpm, double Beat, long BeatIndex, int PositionInBar, bool BarAligned);

/// <summary>
/// One registered source's own view of itself, active or not.
///
/// Every source reports on every snapshot because that is what the selector has
/// to show: which sources have a signal right now. The alternative is a user
/// discovering that audio was hearing nothing by switching to it mid-set.
/// </summary>
/// <param name="Name">The source's protocol name: "link", "audio" or "manual".</param>
/// <param name="State">"waiting", "live" or "quiet".</param>
/// <param name="Confidence">0 to 1. Only meaningful for a source that estimates the tempo rather than being told it.</param>
public sealed record SourceReportData(string Name, string State, double Confidence);

/// <summary>
/// Whether the bar has been anchored, and by what.
/// </summary>
/// <param name="Aligned">Whether the bar position means anything musically.</param>
/// <param name="Method">"cue", "tap" or "drop"; null exactly when <paramref name="Aligned"/> is false.</param>
public sealed record AlignmentData(bool Aligned, string? Method);

/// <summary>
/// Health counters for the active source, and the one number that belongs to the
/// grid itself.
///
/// One record with per-source nullable fields rather than a union type, because
/// the source generator cannot serialise a polymorphic payload and reflection is
/// not on the table here. A field that does not apply is omitted from the frame
/// entirely, so a client renders a blank rather than a zero that looks like a
/// measurement. The ignore condition is per property rather than global on
/// purpose: the mandatory <c>grid: null</c> in a state frame has to survive.
/// </summary>
/// <param name="ReanchorMaxBeats">Largest disagreement between an outgoing timeline and its replacement. The grid's own metric, so it is sent for every source and should stay at zero forever.</param>
/// <param name="Packets">Link: ALIVE packets accepted so far.</param>
/// <param name="Fps">Audio: measured analyzer frames a second.</param>
/// <param name="BeatsDetected">Audio: beats the detector has accepted.</param>
/// <param name="BpmLocked">Audio: whether the tempo estimate has settled.</param>
/// <param name="LastBeatAgoMs">Audio: milliseconds since the last accepted beat.</param>
/// <param name="Taps">Manual: taps in the current sequence.</param>
/// <param name="LastTapAgoMs">Manual: milliseconds since the last tap.</param>
public sealed record DiagnosticsData(
  double ReanchorMaxBeats,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Packets = null,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Fps = null,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BeatsDetected = null,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BpmLocked = null,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LastBeatAgoMs = null,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Taps = null,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? LastTapAgoMs = null);

/// <summary>
/// The continuous picture, sent at a fixed rate whether or not anything moved.
/// </summary>
/// <param name="Source">The sync source driving the grid, or "none" in a build that has no sources at all.</param>
/// <param name="SourceState">"waiting", "live" or "quiet", for <paramref name="Source"/>.</param>
/// <param name="Sources">Every registered source and its health, for the selector.</param>
/// <param name="Grid">Null exactly when nothing has ever been received, so a client cannot render an absent tempo as 0.00 BPM.</param>
/// <param name="Alignment">Whether the bar is anchored, and by what.</param>
/// <param name="Light">Null exactly when Flux is not streaming to a bridge. The single authority on whether it is.</param>
/// <param name="Diagnostics">Counters for the active source, plus the grid's own drift metric.</param>
public sealed record StateData(
  string Source,
  string SourceState,
  IReadOnlyList<SourceReportData> Sources,
  GridData? Grid,
  AlignmentData Alignment,
  LightData? Light,
  DiagnosticsData Diagnostics);

/// <summary>
/// What the light stream is achieving, and the only field that says Flux is
/// streaming at all.
///
/// It rides the 20 Hz state frame rather than the <c>hue</c> message because it is
/// a moving measurement and <c>hue</c> is pushed on change: a frame rate there would
/// re-send the entertainment area list once a second, which is the objection that
/// gave Hue its own message in the first place.
/// </summary>
/// <param name="Fps">
/// Frames a second reaching the bridge, measured over the last second and never the
/// configured rate echoed back. Not the rate a bulb renders: the bridge forwards over
/// Zigbee at roughly 25 updates a second per light and drops the excess, so the two
/// are different layers and both are correct.
/// </param>
public sealed record LightData(double Fps);

/// <summary>
/// One bridge, found or paired.
/// </summary>
/// <param name="Id">The bridge's own identifier, and the only value pairBridge accepts. Credentials belong to this rather than to an address.</param>
/// <param name="Address">Where it answered from, this time. For display only: DHCP moves it, and nothing on either side stores it.</param>
public sealed record HueBridgeData(string Id, string Address);

/// <summary>
/// One entertainment area, as the picker has to show it.
/// </summary>
/// <param name="Id">The configuration id a stream is opened against, and the only value selectArea accepts.</param>
/// <param name="Name">What it is called in the Hue app.</param>
/// <param name="ChannelCount">How many channels the area drives. Worth showing, because a user with two areas can still tell three lights from ten.</param>
/// <param name="Streaming">
/// Whether something already holds this area's streaming session. The pre-flight for a
/// failure that otherwise arrives as an opaque DTLS timeout - and deliberately not a
/// claim about <em>who</em>, because the bridge offers no name to blame.
/// </param>
public sealed record HueAreaData(Guid Id, string Name, int ChannelCount, bool Streaming);

/// <summary>
/// Everything the page knows about the bridge, as one snapshot.
///
/// Pushed on change rather than folded into <see cref="StateData"/>: state goes out
/// twenty times a second, and an area list sent at that rate is twelve hundred copies
/// a minute of something that changes when a user opens the Hue app.
///
/// Nothing here is omitted when null. Every nullable field is a documented value
/// rather than an absence - a null bridge is "none paired", a null countdown is "no
/// window is running" - and a client that had to distinguish absent from null would
/// be doing the server's job.
/// </summary>
/// <param name="State">"disabled", "unpaired", "discovering", "pairing", "connecting", "ready" or "failed".</param>
/// <param name="Bridge">The paired bridge, or null before there is one.</param>
/// <param name="Discovered">What the last discovery found. Kept while pairing, because that list is what the pairing screen is showing.</param>
/// <param name="Areas">The bridge's entertainment areas, empty until it is reachable.</param>
/// <param name="SelectedAreaId">The area lights will be streamed to, or null until one is chosen.</param>
/// <param name="PairingSecondsRemaining">Seconds left in the pairing window; non-null exactly when <paramref name="State"/> is "pairing".</param>
/// <param name="LatencyOffsetMs">The offset in force, signed, positive being early. Always a real number: there is no state in which no offset applies.</param>
/// <param name="Engine">How the light engine is set. Always present, for the reason the offset always is.</param>
/// <param name="Message">English prose for a human, or null. Never parsed, and never what a client branches on.</param>
public sealed record HueData(
  string State,
  HueBridgeData? Bridge,
  IReadOnlyList<HueBridgeData> Discovered,
  IReadOnlyList<HueAreaData> Areas,
  Guid? SelectedAreaId,
  int? PairingSecondsRemaining,
  int LatencyOffsetMs,
  HueEngineData Engine,
  string? Message);

/// <summary>
/// How the light engine turns beats into colour, as the controls for it have to show.
///
/// Named for the engine and not for the lights, because <c>state.light</c> already
/// exists and means something else entirely - what the stream is achieving. Two keys
/// spelled the same with unrelated contents is a twenty-minute detour for whoever
/// reads the document next.
///
/// Its own object inside <c>hue</c> rather than three more fields alongside the bridge,
/// because these three are set together by one message and read together by the
/// engine. A page that had to gather them from the top level would be free to send two
/// of them, and there is no such thing as two thirds of a look.
/// </summary>
/// <param name="Palette">Which colours the engine draws from: "random", "warm", "cool", "neon", "sunset", "ocean", "forest" or "party".</param>
/// <param name="ColorChangeBars">Bars between colour changes, 0 to 16, where 0 means a new colour on every beat.</param>
/// <param name="PulseMs">How long a beat's brightness pulse takes to fade, 0 to 2000, where 0 means a steady colour and no pulse.</param>
/// <param name="Brightness">How bright a beat drives the lights, 0 to 100 per cent. The ceiling for everything sent, so 0 is a blackout.</param>
/// <param name="PulseDepth">How far the light drops between beats, 0 to 100 per cent of <paramref name="Brightness"/>. 0 never dips, 100 fades to nothing.</param>
public sealed record HueEngineData(string Palette, int ColorChangeBars, int PulseMs, int Brightness, int PulseDepth);

/// <summary>
/// One analyzer frame, for the visualiser.
///
/// The bands and the flux ride together because the visualiser is a test
/// instrument rather than decoration: it exists to show a beat marker against
/// the sound that produced it, and without <paramref name="Flux"/> and
/// <paramref name="Beat"/> the picture shows what was heard but not what was
/// decided.
/// </summary>
/// <param name="Bins">64 log-spaced display columns, low frequency first, each 0 to 255.</param>
/// <param name="Kick">Bass band energy, 0 to 1.</param>
/// <param name="Mids">Mid band energy, 0 to 1.</param>
/// <param name="Highs">High band energy, 0 to 1.</param>
/// <param name="Flux">Spectral flux against the previous frame: what the detector triggers on.</param>
/// <param name="Beat">"none", "beat" or "strong".</param>
public sealed record SpectrumData(
  IReadOnlyList<int> Bins,
  double Kick,
  double Mids,
  double Highs,
  double Flux,
  string Beat);

/// <summary>
/// One audio endpoint Flux can see, and what it is currently hearing from it.
///
/// Only the selected device is measured, so <c>state</c>, <c>rms</c> and
/// <c>peak</c> are omitted for the rest rather than sent as zeroes. Measuring
/// every endpoint would mean holding twenty of them open for the length of a set,
/// which is a real cost - exclusive-mode drivers, and devices shown as in use in
/// every other application on the machine - paid to fill in meters nobody is
/// reading. An omitted field says "not measured"; a zero would say "measured, and
/// silent", which is a claim about somebody's hardware that Flux has not earned.
/// </summary>
/// <param name="Id">Opaque and stable while the device exists. The only value selectAudioDevice accepts.</param>
/// <param name="Name">What the operating system calls it, for a human to recognise.</param>
/// <param name="Kind">"capture" for an input, "loopback" for what a render device is playing.</param>
/// <param name="State">"signal", "digitalSilence", "noData" or "failed"; absent for a device Flux is not capturing.</param>
/// <param name="Selected">Whether audio analysis is listening to this one. Always present: the selection is never in doubt.</param>
/// <param name="Rms">Recent average level, 0 to 1; absent when nothing was measured.</param>
/// <param name="Peak">Recent peak level, 0 to 1. Sent beside the average because a clipping signal and a quiet one differ in exactly this pair.</param>
public sealed record DeviceData(
  string Id,
  string Name,
  string Kind,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State,
  bool Selected,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Rms,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Peak);

/// <summary>
/// The complete list of audio devices, never a delta.
///
/// Wrapped in an object rather than sent as a bare array because the envelope
/// promises <c>data</c> is an object, and because a list that later needs a
/// companion field should not have to become one.
/// </summary>
public sealed record DevicesData(IReadOnlyList<DeviceData> Devices);

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
