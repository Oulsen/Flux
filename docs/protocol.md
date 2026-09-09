# Flux WebSocket protocol

**Protocol version 1.**

Flux keeps all state on the server. Browsers are dumb terminals: they render what
arrives and send control messages back. This document is the contract between
`Flux.Web.Server` and `Flux.Web.Frontend`, which are separate projects that have
to agree byte for byte.

Where this document and any other description of a message disagree, this
document wins.

## Transport

| | |
|---|---|
| Endpoint | `ws://<host>:<port>/ws`, same origin as the page |
| Frames | UTF-8 text, one JSON object per frame. Never batched, never fragmented by Flux |
| Binary frames | Rejected: the server closes with status 1003 |
| Inbound size limit | 4096 bytes per message; larger closes the connection with 1009 |
| `Origin` | Rejected with 403 when present and neither loopback nor the address the page was served from |
| Authentication | None. LAN only, by design: see "Remote control web app" in the findings |

The `Origin` check exists because the same-origin policy does not cover
WebSockets: any page open in the DJ's browser can connect to a known local port.
A missing `Origin` is allowed, since that is a native client rather than a
browser, and one could forge the header anyway. Loopback origins are allowed
outright so the Vite dev server, which is a second origin by definition, works
without special-casing a port that changes.

Any number of clients may connect, and they are independent. The only
server-side state a connection has is what it asked for with `subscribe`, which
is a preference rather than a position in a stream: a client that reconnects
gets a complete picture immediately and never has to replay anything.

## Envelope

Every message in both directions is:

```json
{ "type": "state", "data": { } }
```

- `type` (string) selects the payload. Both fields are always present.
- `data` (object) is the payload for that type, described below.
- Field names are camelCase. Numbers are JSON numbers, never strings.

**Unknown types are handled asymmetrically, on purpose.** A client ignores a
`type` it does not know, so a newer server can add messages without breaking an
older page. A server answers an unknown `type` with an `error`, so a typo in a
client is visible immediately rather than silently doing nothing.

## Versioning

The protocol version is sent once per connection in `hello`, rather than tagged
onto every frame, because at 20 frames a second the tag would cost more than it
tells anyone.

`protocol` increments on any change that an existing client could misread:
a removed or renamed field, a changed unit, a changed meaning. Adding a message
type, or adding a field to an existing payload, does not increment it.

A client that receives a `protocol` it was not built for should say so on screen
and stop trusting the feed, rather than rendering fields that may have moved.
Being pre-1.0, breaking this protocol is cheap and should be done deliberately
rather than worked around: see "Versioning" in the plan.

Version 1 grew in Phase 2 and did not break: several new message types, several
new fields on `state`, and the `source` values this document had already
reserved. The one change that looks like a break and is not is `diagnostics`,
which is now per-source - this document always said it was scoped to the current
source and would change shape, and the Link fields kept their names. An older
page therefore reads a blank diagnostics row while a non-Link source is active,
which is a degradation and not a misreading.

`devices` also became looser than first written: `state`, `rms` and `peak` are
optional now rather than mandatory. Narrowing a promise like that would normally
be exactly the change that increments the version, and it does not here for one
specific reason: no build ever sent a `devices` frame before this one, so there is
no client anywhere that could have depended on those fields being present. The
message was specified before it had a producer, and it was the specification that
turned out to be wrong about what could honestly be measured.

Version 1 grew again in Phase 3 and again did not break: a `hue` message, eight
inbound messages to drive the bridge and the lights, and a `light` object on
`state`. Every one of those is an addition, and an older page ignores a `type` it
does not know by the rule above, so the worst it suffers is a build whose lights
it cannot see - a degradation and not a misreading. Nothing existing changed name,
unit or meaning.

Phase 2's lesson about `devices` is worth repeating rather than filing, because
this document is once again describing messages before anything produces them.
Where that left a field's honesty uncertain the field is nullable rather than
promised: `light` is null unless a stream is genuinely running, and `fps` is a
measurement or it is absent along with the object holding it. If a promise made
here turns out to be one Flux cannot keep, correcting it increments the version
unless no build ever sent the field.

## Units and conventions

| Field | Unit |
|---|---|
| `bpm` | Beats per minute, floating point |
| `beat` | Fractional beats since the Link session anchor. **May be negative** and jumps by thousands on a tempo change, so it is not a stable identity for anything |
| `beatIndex` | `floor(beat)` |
| `positionInBar` | 1-based, in the range 1..`quantum` |
| `t` | Milliseconds since the Unix epoch, as measured by whoever set the field |
| `confidence` | 0 to 1, how much a source trusts its own tempo |
| `rms`, `peak`, band levels | 0 to 1, linear amplitude, not decibels |
| Spectrum `bins` | 0 to 255 integers, already scaled for display |
| `offsetMs` | Milliseconds, and **signed**: positive sends light early |

**There is no clock synchronisation, and nothing a browser renders is
compensated for latency.** Server and client clocks are unrelated, so `beat`
messages are rendered on arrival and never scheduled against a timestamp.

The latency offset is a different quantity that is easy to mistake for that one.
It shifts the *lights* against the beat, it is applied on the server where the
lights are driven from, and it changes nothing a page draws. A page sets it and
reads it back because a human watching the room is the only instrument that can
measure it - not because the page is corrected by it. See `setLatencyOffset`.

## Server to client

### `hello`

Sent once, immediately after the socket opens, before anything else.

```json
{ "type": "hello", "data": {
  "protocol": 1,
  "app": "0.3.0",
  "quantum": 4,
  "stateHz": 20,
  "spectrumHz": 30,
  "devicesHz": 10
}}
```

| Field | Type | Meaning |
|---|---|---|
| `protocol` | int | This document's version |
| `app` | string | Flux's own version, for the page footer. Answers "which build is the phone running?" |
| `quantum` | int | Beats per bar, so the client can size a beat indicator instead of assuming four |
| `stateHz` | int | Nominal `state` rate, so the client can size its own smoothing |
| `spectrumHz` | int | Nominal `spectrum` rate for a subscriber |
| `devicesHz` | int | Nominal `devices` rate for a subscriber |

The three rates are nominal and are what a client should size its own decay and
smoothing against. They are not promises: every feed drops frames for a client
that has fallen behind, so a page that counts frames to measure time will be
wrong. Measure time with the clock.

### `state`

The continuous picture, sent `stateHz` times a second whether or not anything
changed. It is a snapshot and is never a delta.

```json
{ "type": "state", "data": {
  "source": "link",
  "sourceState": "live",
  "sources": [
    { "name": "link", "state": "live", "confidence": 1.0 },
    { "name": "audio", "state": "quiet", "confidence": 0.0 },
    { "name": "manual", "state": "waiting", "confidence": 0.0 }
  ],
  "grid": {
    "bpm": 128.0,
    "beat": 1024.37,
    "beatIndex": 1024,
    "positionInBar": 1,
    "barAligned": false
  },
  "alignment": { "aligned": false, "method": null },
  "light": { "fps": 49.7 },
  "diagnostics": { "reanchorMaxBeats": 0.0, "packets": 4821 }
}}
```

| Field | Type | Meaning |
|---|---|---|
| `source` | string | The sync source driving the grid: `"link"`, `"audio"` or `"manual"`. `"none"` when a build has no sources at all, which is also the only case where `sources` is empty |
| `sourceState` | string | `"waiting"`, `"live"` or `"quiet"`, for `source` |
| `sources` | array | Every source this build has, whether or not it is the active one |
| `grid` | object or **null** | Null exactly when `sourceState` is `"waiting"` |
| `alignment` | object | Whether the bar has been anchored, and by what |
| `light` | object or **null** | Null exactly when Flux is not streaming to a bridge |
| `diagnostics` | object | Health counters for the active source |

`sourceState` distinguishes three situations that a single boolean would blur:

- **`waiting`** - nothing has ever been received. There is no tempo to show, and
  `grid` is null rather than zero-filled, so a client cannot accidentally render
  0.00 BPM as if it were a measurement.
- **`live`** - the source is publishing.
- **`quiet`** - a timeline is known but the source has stopped publishing. Flux
  keeps extrapolating and keeps sending a full `grid`; the tempo shown is the
  last one published and is increasingly a guess. The usual cause is LINK being
  switched off at the other end. A client should mark it, not hide it.

Switching source is not a fourth state. The new source's last known timeline is
adopted at once, and a source that has never published leaves the grid with
nothing to report, so `sourceState` goes back to `"waiting"` and `grid` to null
rather than leaving the outgoing source's tempo on screen under the new name.

`grid.barAligned` is false until something anchors the bar, and says so
honestly: `positionInBar` is a correct count with an arbitrary starting point,
so the dot moves at the right rate but does not know where bar 1 is until a cue
press or a tap arrives. A client must not present an unaligned position as a bar
number.

### `state.sources`

Every registered source reports itself on every snapshot, not just the active
one, because that is the question the source selector has to answer: choosing
between Link and audio means seeing which of them currently has a signal.
Finding out by switching and watching the room go dark is not an acceptable way
to ask.

| Field | Type | Meaning |
|---|---|---|
| `name` | string | `"link"`, `"audio"` or `"manual"` |
| `state` | string | `"waiting"`, `"live"` or `"quiet"`, as above |
| `confidence` | number | 0 to 1, how much the source trusts its own tempo |

`confidence` is only meaningful for a source that *estimates* the tempo. A
source that is told the tempo - Link on the wire, a human typing a number -
reports 1 whenever it knows one at all, because it has no uncertainty to
describe and inventing a number would only make the honest ones look worse.

The array order is whatever order the sources registered in. It is not a
ranking, and a client should present them in its own fixed order rather than
the server's.

`"manual"` is never `"quiet"`, and the difference is real rather than an
oversight. Silence from Link means the session has gone and the extrapolated
tempo is a guess that worsens by the second. Silence from a typed tempo means
nobody has changed their mind.

### `state.alignment`

| Field | Type | Meaning |
|---|---|---|
| `aligned` | bool | Whether `positionInBar` means anything musically. Same value as `grid.barAligned` |
| `method` | string or **null** | `"cue"`, `"tap"` or `"drop"`; null exactly when `aligned` is false |

No source transmits a downbeat, so bar 1 comes from a cue press on an audible
deck, a tap in the web UI, or the detector deciding a drop landed. The three are
not equally trustworthy and only the user can judge which applies, so the method
travels with the alignment: showing what aligned the bar is what lets a wrong
one be recognised as wrong.

An alignment is a beat index, so anything that renumbers the beat - a tempo
change, a session change, a source switch, a tap - clears it and it has to be
made again. That is a property of the beat numbering rather than a shortcoming
of the anchor: see `beat` below.

### `state.light`

What the light stream is actually achieving, and the single authority on whether
Flux is streaming at all.

| Field | Type | Meaning |
|---|---|---|
| `fps` | number | Frames a second the stream is achieving, measured over the last second. Not the configured rate |

It rides the 20 Hz feed rather than the `hue` message because it is a moving
measurement, and `hue` is pushed on change: a frame rate belongs with the other
numbers that are different every time they are read, not with a list of
entertainment areas that would then be re-sent once a second for nothing.

**`fps` is measured, never the configured rate echoed back**, which is the whole
point of the field. Flux aims at `Hue:StreamHz`, and a stream that is quietly
achieving half of it looks identical from the outside to one that is not - the
lights are simply less smooth than somebody expected, on a night when everything
else is also a suspect.

Rates that are all correct and all different get confused here, so this one names
its layer: `light.fps` counts frames Flux sent to the *bridge*. A bulb renders
roughly 25 updates a second over Zigbee whatever this says, because the bridge
forwards at its own rate and drops the rest, and a gap between those two is the
design rather than a fault. It is also **not** `diagnostics.fps`, which is the
audio analyzer at the far end of the chain: one is how fast Flux is listening,
the other is how fast it is speaking, and they have no reason to match.

Null exactly when Flux is not streaming, on the same principle that makes `grid`
null while waiting: there is no honest frame rate for a stream that is not
running, and a zero would read as a stream that is running and failing. Why it
is not running is `hue`'s question, not this one's - no bridge, no area chosen,
somebody pressed stop, or the bridge's one session belongs to another
application.

### `state.diagnostics`

Health counters for the active source, and one field that belongs to the grid.
**A field that does not apply to the current source is omitted entirely**,
rather than sent as null or as a zero, so a client renders a blank where it has
nothing rather than a number that means nothing.

| Field | Type | Source | Meaning |
|---|---|---|---|
| `reanchorMaxBeats` | number | always | Largest disagreement seen between an outgoing timeline and its replacement, ignoring tempo changes and source switches. Should sit at 0.0000 forever; a growing value means the grid is being corrected rather than confirmed |
| `packets` | int | link | ALIVE packets accepted so far. Counted by the listener, so it keeps counting while another source is driving the grid |
| `fps` | number | audio | Analyzer frames a second, measured. Well under 60 means the machine is not keeping up and every other audio number is suspect |
| `beatsDetected` | int | audio | Beats the detector has accepted |
| `bpmLocked` | bool | audio | Whether the tempo estimate has settled. False means the BPM shown is still moving |
| `lastBeatAgoMs` | number | audio | Milliseconds since the last accepted beat |
| `taps` | int | manual | Taps in the current sequence, up to the eight the estimate uses |
| `lastTapAgoMs` | number | manual | Milliseconds since the last tap |

`reanchorMaxBeats` is the grid's own metric rather than any source's, which is
why it is always present: it measures whether extrapolating the current timeline
still agrees with what arrives next, and that question is worth asking of every
source. It resets with the grid when the active source changes, because carrying
it across would blame the arriving source for the departing one's drift.

### `beat`

One message per beat, sent at the instant the beat lands. This is what a flash
renders from: `state` at 20 Hz cannot place a beat closer than 50 ms.

```json
{ "type": "beat", "data": {
  "index": 1024,
  "positionInBar": 1,
  "barAligned": false,
  "bpm": 128.0
}}
```

**Beats may be dropped and are not a countable sequence.** A client that has
fallen behind loses the oldest queued messages (see Backpressure), so `index`
can skip. Anything derived by counting beats client-side will be wrong; derive
it from `index` or from `state` instead.

### `spectrum`

What the audio analyzer is hearing, sent only to clients that asked for it with
`subscribe`, at the rate named in `hello` as `spectrumHz`.

```json
{ "type": "spectrum", "data": {
  "bins": [214, 198, 120, 96, 61, 44, 30, 22],
  "kick": 0.62,
  "mids": 0.24,
  "highs": 0.11,
  "flux": 0.031,
  "beat": "strong"
}}
```

| Field | Type | Meaning |
|---|---|---|
| `bins` | array of int | 64 log-spaced display columns, low frequency first, each 0 to 255 |
| `kick` | number | Bass band energy, 0 to 1 |
| `mids` | number | Mid band energy, 0 to 1 |
| `highs` | number | High band energy, 0 to 1 |
| `flux` | number | Spectral flux against the previous frame: the number the beat detector actually triggers on |
| `beat` | string | `"none"`, `"beat"` or `"strong"` - the strongest verdict the detector reached since the previous frame sent to this client |

The example shows eight bins for readability. There are always exactly 64, and a
client should still check rather than index blindly.

This is a **test instrument, not a decoration.** It exists so that a beat marker
can be watched against the music and against the spectrum that produced it,
which is the only way to tell a detector that is right from one that is merely
firing at a plausible rate. That is also why `flux` and `beat` ride along with
the bands: without them the picture shows what was heard but not what was
decided.

The frames are sampled from the analyzer's latest frame rather than queued from
it, so a subscriber that cannot keep up sees fewer frames and never an older
one. The analyzer runs faster than this feed on purpose: downsampling a frame
that already exists is free, and slowing the analyzer to match a browser would
put a phone's refresh rate in charge of beat detection.

The one exception to sampling is `beat`: a verdict lives on exactly one analyzer
frame, so sampling it would drop about half the accepted beats from the picture.
It is latched instead - the value carried is the strongest verdict since the
previous frame this client was sent, so every accepted beat is drawn exactly
once.

Bins arrive already scaled to 0..255 rather than as magnitudes, so the page
never has to know the FFT size, the sample rate, or where the band edges are.
Those live in `docs/audio-constants.md` and are the server's business.

### `devices`

The audio inputs Flux can see, sent only to clients that asked with `subscribe`,
once immediately and then at the rate named in `hello` as `devicesHz`.

```json
{ "type": "devices", "data": { "devices": [
  {
    "id": "{0.0.1.00000000}.{a1b2c3d4}",
    "name": "Microfoon (2- DDJ-FLX10)",
    "kind": "capture",
    "selected": true,
    "state": "signal",
    "rms": 0.12,
    "peak": 0.44
  },
  {
    "id": "{0.0.0.00000000}.{e5f6a7b8}",
    "name": "Speakers (DDJ-FLX10)",
    "kind": "loopback",
    "selected": false
  }
]}}
```

| Field | Type | Meaning |
|---|---|---|
| `id` | string | Opaque, stable for as long as the device exists. The only value `selectAudioDevice` accepts |
| `name` | string | What the operating system calls it, for a human to recognise |
| `kind` | string | `"capture"` for an input, `"loopback"` for what a render device is playing |
| `selected` | bool | Whether audio analysis is listening to this one. Always present |
| `state` | string, optional | `"signal"`, `"digitalSilence"`, `"noData"` or `"failed"`. Absent for a device Flux is not capturing |
| `rms` | number, optional | Recent average level, 0 to 1. Absent when nothing was measured |
| `peak` | number, optional | Recent peak level, 0 to 1. Absent when nothing was measured |

**Only the selected device is measured, and the other rows say so by leaving
those three fields out.** Measuring every endpoint means holding all of them open
for the length of a set, which costs real things: drivers that only open
exclusively, and a device that then shows as in use in every other application on
the machine. The alternative of sending zeroes was rejected for the same reason
`state.diagnostics` omits a counter rather than sending one: an absent field says
"not measured", while a zero says "measured, and silent", which is a claim about
somebody's hardware that Flux has not earned. So a client renders a device with no
`state` as one it knows nothing about beyond its name, never as a dead one.

A device that was selected and could not be opened keeps its `state` - `"failed"`
is the answer to why there is no audio, and it belongs on the row of the device
somebody picked. Its `rms` and `peak` are still absent, because a device that
never opened delivered no samples to measure. `"digitalSilence"`, by contrast,
arrives *with* levels: bit-exact zeros are a real reading of a stopped deck, and a
meter pinned at the bottom is exactly what distinguishes that from Flux not
listening.

`state` separates three ways of hearing nothing, because they call for three
different responses and a single "no signal" would blur them:

- **`signal`** - audio is arriving and is not silent.
- **`digitalSilence`** - audio is arriving and every sample is zero. The cable
  is fine and the deck is stopped, or the wrong output is selected.
- **`noData`** - the device delivered no callbacks at all. For a loopback
  device this is ordinary: Windows delivers nothing while nothing is playing,
  which is silence rather than a fault.
- **`failed`** - the device could not be opened or stopped delivering after an
  error. This is the only one worth colouring as a problem.

`rms` and `peak` are both sent because they answer different questions: a signal
that is present but far too quiet has a low peak, while a signal that is
clipping has a peak pinned at 1 with an rms well below it. A meter drawn from
either one alone hides one of those.

### `hue`

Everything the page knows about the bridge, as one snapshot. Sent once
immediately after `hello`, and then **only when something changes**.

```json
{ "type": "hue", "data": {
  "state": "ready",
  "bridge": { "id": "001788fffe1a2b3c", "address": "192.168.1.42" },
  "discovered": [
    { "id": "001788fffe1a2b3c", "address": "192.168.1.42" }
  ],
  "areas": [
    { "id": "6f3a1c58-0c1e-4f1a-9b7d-2f8e5a0d4c11", "name": "Booth", "channelCount": 7, "streaming": false }
  ],
  "selectedAreaId": "6f3a1c58-0c1e-4f1a-9b7d-2f8e5a0d4c11",
  "pairingSecondsRemaining": null,
  "latencyOffsetMs": 90,
  "engine": { "palette": "random", "colorChangeBars": 4, "pulseMs": 240, "brightness": 100, "pulseDepth": 85 },
  "message": null
}}
```

| Field | Type | Meaning |
|---|---|---|
| `state` | string | Which of the seven situations below the user is in |
| `bridge` | object or **null** | The paired bridge. Null before there is one |
| `discovered` | array | What the last discovery found. May be empty, and may contain the paired bridge |
| `areas` | array | The bridge's entertainment areas. Empty until it is reachable |
| `selectedAreaId` | string (uuid) or **null** | The area lights will be streamed to. Null until one is chosen |
| `pairingSecondsRemaining` | int or **null** | Seconds left in the pairing window. Non-null **exactly** when `state` is `"pairing"` |
| `latencyOffsetMs` | int | The current light latency offset, signed. See `setLatencyOffset` |
| `engine` | object | How the light engine turns beats into colour. See `hue.engine` |
| `message` | string or **null** | English prose for a human, when there is something to say |

**This is a separate message rather than a field on `state`, and the area list is
the reason.** `state` goes out twenty times a second; an entertainment area list
sent at that rate is twelve hundred copies a minute of something that changes
when a user opens the Hue app, which is roughly never. Pushing on change instead
also means a page can render the pairing screen from the message it was sent
rather than from a diff it computed.

A frame is sent immediately on connect so a page that joins mid-session is not
blank until the next thing happens. That matters more here than anywhere else on
this socket: pairing is driven from a phone that may have connected thirty
seconds ago, and "nothing has changed yet" and "the bridge is unreachable" must
not look the same.

#### `hue.state`

Seven values rather than a paired boolean, because each one is a different
screen and collapsing any two of them sends somebody to a hall cupboard for
nothing.

| Value | Meaning |
|---|---|
| `disabled` | Lights are switched off in the settings (`Hue:Enabled`). Nothing will be attempted, and no button on the page will change that |
| `unpaired` | No credentials. The pairing screen is the whole of the UI here |
| `discovering` | Listening for bridges |
| `pairing` | Asking the bridge to register, once a second, waiting for the link button |
| `connecting` | Paired, and asking the bridge what it has |
| `ready` | Paired, reachable, and the areas are known |
| `failed` | Something failed that the user has to know about, and `message` says what |

**`unpaired` with a `message` is the normal end of a pairing attempt nobody
answered, and it is deliberately not `failed`.** Nothing broke: the button is on
a physical object in another room and it was not pressed in time. A client that
paints that red teaches its user to distrust red.

`disabled` is not an error either, and a client should say what it is rather than
showing a pairing screen that cannot work. It exists so that a build with the
lights switched off and a build that cannot find a bridge stop looking identical
from the outside.

#### `hue.engine`

How the light engine turns beats into colour. Set with `setLightSettings`, which
documents the ranges and what each value does; this is what is in force now.

| Field | Type | Meaning |
|---|---|---|
| `palette` | string | Which colours the engine draws from |
| `colorChangeBars` | int | Bars between colour changes, 0 to 16, where **0 means every beat** |
| `pulseMs` | int | How long a beat's brightness pulse takes to fade, 0 to 2000, where **0 means no pulse** |
| `brightness` | int | How bright a beat drives the lights, 0 to 100 per cent. The ceiling for everything sent |
| `pulseDepth` | int | How far the light drops between beats, 0 to 100 per cent of `brightness` |

**Named `engine` and not `light`, because `state.light` already exists and means
something else** - what the stream is achieving, as a measurement. These are
settings. Two keys spelled the same with unrelated contents would cost whoever
reads this next twenty minutes for nothing.

It is an object rather than three more fields on `hue` because it is set as one
message and read as one record. A page that could send a palette on its own would
be relying on the server to supply the rest, and two pages doing that at once
produce a combination neither of them chose.

#### `hue.bridge` and `hue.discovered`

| Field | Type | Meaning |
|---|---|---|
| `id` | string | The bridge's own identifier, and the only value `pairBridge` accepts |
| `address` | string | Where it answered from, this time |

**`address` is for display and for nothing else.** It is expected to change
between sessions, because DHCP moves a bridge and Flux stores only the id: every
reconnect re-discovers where that id currently answers. A client that remembers
an address has remembered the one part of this that is not durable.

The discovered list is kept while pairing rather than cleared, because that list
is what the pairing screen is showing. A user who cannot see their bridge needs
to know whether none were found or whether theirs was not among them, and an
empty list during pairing answers a question nobody asked.

#### `hue.areas`

| Field | Type | Meaning |
|---|---|---|
| `id` | string (uuid) | The configuration id, which is what a stream is opened against, and the only value `selectArea` accepts |
| `name` | string | What it is called in the Hue app |
| `channelCount` | int | How many channels the area drives |
| `streaming` | bool | Whether something is already streaming to this area |

`channelCount` is worth showing beside the name because a user with two areas has
usually named them badly and can still tell three lights from ten.

**`streaming` is the pre-flight, and it is the most valuable field in this
message.** A bridge allows exactly one streaming session, and a second one does
not fail with a message saying so - it fails as a DTLS handshake that times out,
which is indistinguishable from a wrong key, a moved bridge or a network
problem. Asking the bridge cheaply, before anything opens a socket, is what turns
that into a sentence a user can act on. The usual cause is Hue Sync still running
in the tray.

It does not say *who* is streaming, because the bridge does not offer a name to
blame. It also does not distinguish Flux's own stream from another
application's: when Flux is streaming to the selected area, that area reports
`streaming` too. `state.light` is the field that answers "is this us".

### `pong`

The reply to `ping`, echoing `t` unchanged, so the client can measure a
round trip against its own clock without either side knowing the other's.

```json
{ "type": "pong", "data": { "t": 1755950400123 } }
```

### `error`

A message the server could not act on. It never closes the connection by itself.

```json
{ "type": "error", "data": { "code": "unknown_type", "message": "Unknown message type 'pnig'." } }
```

| `code` | Meaning |
|---|---|
| `malformed` | Not JSON, or not an object with a string `type` |
| `unknown_type` | A well-formed message of a type this server does not handle |
| `invalid_data` | The type is known but its `data` did not fit |
| `rejected` | The message was well formed and the server declined it: an unknown device, a source this build does not have, a tempo outside the accepted range |

`invalid_data` and `rejected` are separated because they mean different things to
whoever is looking. `invalid_data` is a bug in the client and will happen every
time. `rejected` is a request the client was right to make and the server was
right to refuse, usually because the world changed underneath it - the device
was unplugged between the list arriving and the button being pressed.

`message` is English prose for a developer console. It is not stable and must
never be parsed; branch on `code`.

## Client to server

### `ping`

```json
{ "type": "ping", "data": { "t": 1755950400123 } }
```

`t` is the client's own clock and is echoed back untouched. The server never
interprets it, which is what makes the measurement meaningful despite the two
clocks being unrelated.

### `subscribe`

```json
{ "type": "subscribe", "data": { "spectrum": true, "deviceLevels": false } }
```

| Field | Type | Meaning |
|---|---|---|
| `spectrum` | bool | Send this client `spectrum` frames |
| `deviceLevels` | bool | Send this client `devices` frames |

**A complete statement of what the client wants, not a delta.** Both fields are
required, and each message replaces the previous answer outright: there is no
`unsubscribe`, because a client that says `{"spectrum": false}` has already said
it. The alternative - subscribe and unsubscribe as separate messages - has a
reconnecting client and a server disagreeing about what is on, and the
disagreement is invisible until someone wonders why the visualiser is dark.

Both default to false until a client asks. That is the point of the message
rather than a detail of it: the phone in the booth wants a BPM readout and a
flash, and shipping it a spectrum frame thirty times a second would spend its
battery and its Wi-Fi on a canvas nobody is looking at. Everything expensive on
this socket is opt-in.

A `devices` snapshot is sent immediately on any subscribe that asks for
`deviceLevels`, whether or not it was already on, so a picker fills in at once
rather than after up to a tenth of a second of blank.

### `selectSource`

```json
{ "type": "selectSource", "data": { "source": "audio" } }
```

Makes that source the one driving the grid. Answered with `rejected` when this
build has no such source, which is the honest answer for a build with no audio
support rather than a silent no-op.

The switch is confirmed by the next `state` frame and not by a reply. A client
should show the button as pending until `state.source` agrees, because the
server is the only thing that knows whether the switch happened - and a page
that lights its own button on click will keep lying about it after a rejection.

Switching adopts that source's last known timeline immediately, so a manual
tempo set two minutes ago is still there when Manual is selected. The beat
numbering is rebased by the switch, exactly as a Link session change rebases it,
so the bar alignment clears and has to be made again.

### `selectAudioDevice`

```json
{ "type": "selectAudioDevice", "data": { "deviceId": "{0.0.1.00000000}.{a1b2c3d4}" } }
```

Points audio analysis at one of the devices from `devices`. Answered with
`rejected` when no device by that id exists, which includes the ordinary race of
a device unplugged while its button was on screen, and the case of a build with
no audio support at all.

### `tap`

```json
{ "type": "tap", "data": { } }
```

One tap in time with the music. `data` is empty and is ignored.

**The server timestamps the tap on arrival, and this is deliberate**: there is
no clock synchronisation in this protocol, so a timestamp the client sent would
be a number from an unrelated clock and would place the beat somewhere
arbitrary. The cost is the network delay between the finger and the socket,
which on a LAN is far inside the tolerance of a tap made by a human hand.

A tap says *when* as well as *how fast*, so it does two things: it feeds the
manual tempo estimate, and it sets the downbeat. The second happens whether or
not Manual is the active source, because a downbeat is a statement about the
music in the room rather than one source's opinion about tempo. Tapping the one
while Link supplies the tempo is the primary way a bar ever gets aligned.

The first tap of a sequence carries no interval and therefore no tempo, and it
still sets the downbeat. Waiting for a second tap before honouring it would feel
broken in exactly the case that matters most.

### `setBpm`

```json
{ "type": "setBpm", "data": { "bpm": 128.0 } }
```

| Field | Type | Meaning |
|---|---|---|
| `bpm` | number or **null** | The manual tempo, from 20 to 200. Null clears it |

A typed tempo deliberately leaves the bar alone. A number says nothing about
where the downbeat falls, and moving the bar to wherever the user happened to
press Enter would be inventing a phase out of a keystroke.

A tempo outside 20..200 is answered with `rejected` rather than clamped: the
values just outside that range are typos, and a clamp turns a typo into a
plausible wrong answer. Anything that is not a number and not null is
`invalid_data`.

`null` clears the manual tempo, which is not the same as setting it to zero. The
source goes back to `"waiting"`, and if it was the active source the grid stops
reporting a tempo at all - a cleared tempo that kept extrapolating would be the
one reading a beat display must never invent.

### The Hue messages

Eight messages drive the bridge and the lights, and **none of them is answered
with a reply.** Each is confirmed by the broadcast that follows - a `hue` frame
for the six that change the bridge picture, `state.light` for `startStream` and
`stopStream` - or by `error` when the server declines. That is not a shortcut: pairing is normally
watched on two screens at once, a phone in a hallway and a laptop in the booth,
and a reply addressed to one of them would leave the other showing a state that
had already moved. The broadcast is the answer, and everybody gets it.

So a client shows a pressed button as pending until the feed agrees with it,
exactly as `selectSource` requires. A page that lights its own button on click
will keep lying about it after a rejection.

`rejected` is the answer to a request that was well formed and could not be
honoured, and it needs no new error code: an unknown bridge id and an area the
bridge never reported are both the ordinary race of a screen that went stale
while somebody was walking, which is precisely what `rejected` already means.

### `discoverBridges`

```json
{ "type": "discoverBridges", "data": { } }
```

Looks for bridges and puts the result in `discovered`. **Once a bridge is paired
it also re-reads that bridge's areas**, so the same message refreshes
`areas[].streaming`. `data` is empty and is ignored.

The two halves are one message because they answer one question - what is out
there now - and because the half that matters changes fastest. An area's
`streaming` flag has a lifetime of seconds, and a page that could re-run
discovery but never re-read the areas would show a stale flag indefinitely with a
button beside it that looked like it should have fixed exactly that.

**Discovery does not disturb a working pairing.** It is how an unpaired user
finds a bridge, and it is also how a paired one checks that theirs is still on
the network, so it never clears credentials or the chosen area. Anyone may ask
at any time.

It takes seconds rather than milliseconds, because mDNS answers arrive over time
and Flux waits for them: `state` goes to `discovering` for the duration, which is
what a client should render rather than a spinner of its own invention. A bridge
that answers after the window is missed, not broken, and asking again is the fix.

### `pairBridge`

```json
{ "type": "pairBridge", "data": { "bridgeId": "001788fffe1a2b3c" } }
```

| Field | Type | Meaning |
|---|---|---|
| `bridgeId` | string | The `id` of a bridge from `discovered` |

Asks that bridge to register Flux, once a second for about a minute, until
somebody presses its link button. `state` is `"pairing"` throughout and
`pairingSecondsRemaining` counts down; when the window closes unanswered the
state returns to `unpaired` with a `message`, which is not a failure.

**The countdown is redrawn from a real attempt rather than from a timer running
beside one**, which is what makes it honest. Anything that makes it lie costs
somebody a walk down a hall.

Answered with `rejected` for a bridge id that is not in the discovered list. The
usual cause is a pairing screen left open while the network changed underneath
it, so a client should offer discovery again rather than repeat the request.

A second `pairBridge` arriving mid-countdown does not start a competing loop:
commands are serialised server-side, and two phones on the same page is the
normal case rather than an exotic one.

**Flux never reuses another application's credentials**, Hue Sync's included.
Pairing creates Flux's own whitelist entry on the bridge, which is the only
form that can be revoked without taking something else down with it.

### `selectArea`

```json
{ "type": "selectArea", "data": { "areaId": "6f3a1c58-0c1e-4f1a-9b7d-2f8e5a0d4c11" } }
```

| Field | Type | Meaning |
|---|---|---|
| `areaId` | string (uuid) | The `id` of an area from `areas` |

Chooses the entertainment area lights are streamed to, and remembers it across
restarts. Answered with `rejected` for an area the bridge did not report.

A remembered area the bridge no longer has is dropped when Flux connects rather
than offered, because it was deleted in the Hue app between sessions and
streaming to it would fail at the one moment nobody is watching a screen.

Selecting an area that another application is already streaming to is allowed and
is not rejected: the bridge's one session may well be free again by the time
anybody presses start, and refusing the choice now would be answering a question
about the future.

### `startStream`

```json
{ "type": "startStream", "data": { } }
```

Opens the light stream to the selected area. `data` is empty and is ignored.
Confirmed by `state.light` becoming non-null, not by a `hue` frame alone.

Answered with `rejected` when there is nothing to stream to - no bridge, no area
chosen, or a bridge that is not currently reachable. A busy area is refused the
same way and is the one case worth a specific sentence on screen, because the
fix is in another application rather than in this one.

**The area's `streaming` flag is re-read from the bridge before anything opens a
socket, and the refusal happens on that reading rather than on the handshake.**
This is not belt-and-braces. A bridge whose one session is taken does not refuse
a second one: it never answers, and the handshake underneath is not cancellable,
so a stream that waits for it waits for as long as the process lives. The
pre-flight is the only thing standing between a busy bridge and a lock-up, which
is why it is asked again here rather than trusted from a list a page may have
been holding for an hour.

Success is therefore not instant: the reply is an area listing plus a DTLS
handshake, and a page should treat the button as pending until `state.light`
appears. Failure is bounded rather than open-ended - a handshake that has not
completed within a few seconds is abandoned and answered as a refusal.

### `stopStream`

```json
{ "type": "stopStream", "data": { } }
```

Closes the stream and releases the bridge's session. `data` is empty and is
ignored.

**Stopping is idempotent and is never rejected.** A stop that arrives when
nothing is running has got exactly what it asked for, and answering it with an
error would put a red message on screen for a user whose lights are already off.

### `forgetBridge`

```json
{ "type": "forgetBridge", "data": { } }
```

Discards the stored credentials and the chosen area, returning `state` to
`unpaired`. `data` is empty and is ignored.

**This is Flux forgetting the bridge and not the other way round.** The bridge
keeps its whitelist entry until somebody deletes it in the Hue app, and pairing
again creates a second entry rather than reusing the first. Worth saying on
screen, because "forget" reads as undoing the pairing and it is only half of
that.

Like stopping, it is never rejected: forgetting a bridge that was never paired
leaves the world exactly as the caller wanted it.

**`latencyOffsetMs` and `engine` survive it**, and they are the only things that
do. One says how long light takes to reach the room and the other says how the
room should look; neither is a fact about the bridge, so re-pairing after a network
change must not discard a number somebody tuned by eye or a palette they picked for
the night.

### `setLatencyOffset`

```json
{ "type": "setLatencyOffset", "data": { "offsetMs": 90 } }
```

| Field | Type | Meaning |
|---|---|---|
| `offsetMs` | int | Milliseconds to shift light against the beat, from -500 to 500. Positive sends light early |

The light path runs roughly 60 to 140 ms behind the music - the bridge, the
Zigbee hop and the bulb itself all take their share - so a change sent the
instant a beat lands is seen late, and a positive offset sends it early to
compensate. Negative values delay the lights instead, which is what a rig with a
long audio path rather than a long light one needs.

**A slider exists for this because a rebuild is the wrong unit of adjustment.**
It is the one number that decides whether the light looks like it lands on the
music, the right value depends on the room and the fixtures, and the only
instrument that can measure it is somebody standing in front of the lights while
the music plays. `Hue:LatencyOffsetMs` sets the value the session starts at and
the page owns it from there.

The value is server-side and is echoed in every `hue` frame, so a second phone
opening the page sees the offset that is actually in force rather than its own
slider's idea of one.

Outside -500..500 is answered with `rejected` rather than clamped, on the same
argument as `setBpm`: at 120 BPM half a second is a whole beat, so an offset past
that is not a late light but a light on the wrong beat, and clamping a typo into a
plausible wrong answer is worse than refusing it. Anything that is not an integer
is `invalid_data`.

### `setLightSettings`

```json
{ "type": "setLightSettings", "data": {
  "palette": "neon",
  "colorChangeBars": 4,
  "pulseMs": 240,
  "brightness": 100,
  "pulseDepth": 85
}}
```

| Field | Type | Meaning |
|---|---|---|
| `palette` | string | `random`, `warm`, `cool`, `neon`, `sunset`, `ocean`, `forest` or `party` |
| `colorChangeBars` | int | Bars between colour changes, 0 to 16. **0 means a new colour on every beat** |
| `pulseMs` | int | How long a beat's brightness pulse takes to fade, 0 to 2000. **0 means no pulse** |
| `brightness` | int | How bright a beat drives the lights, 0 to 100 per cent. **0 is a blackout** |
| `pulseDepth` | int | How far the light drops between beats, 0 to 100 per cent of `brightness`. **0 never dips, 100 fades to nothing** |

Sets how beats become colour. **Every field is required**: this is one complete
statement of how the lights should look, not a patch, for the reason `subscribe`
requires both of its flags. A message that could carry a palette alone would rely
on the server to supply the rest, and two phones doing that at once produce a look
neither of them asked for.

The engine works in two layers at two rates, and the settings are how a person
sets each one. A hue is chosen every `colorChangeBars` bars and slides to its new
value; brightness is knocked to full on every beat and fades over `pulseMs`. That
separation is the point - one setting doing both jobs gives either lights that sit
still for eight seconds between colours or lights that strobe through the palette
four times a bar.

**Colour changes land on the downbeat**, not merely every fourth beat. Flux counts
bars from the beat grid's own bar position, so the changes stay on the bar across a
re-anchor rather than drifting off it for the rest of the night. Where no downbeat
is known - `alignment.method` of `none` - it falls back to every `4 x n`-th beat
from wherever the numbering starts, because changing on a guessed phase is worth
more than not changing at all.

**Both zeroes read backwards and both are deliberate.** `colorChangeBars` of 0
does not stop the colours changing, it removes the interval, so they change as
often as they can - every beat. `pulseMs` of 0 does not make the pulse
instantaneous, it removes it, leaving a steady colour: a one-frame flash at 50 Hz
is a 20 ms strobe nobody would ask for, while holding the colour is what the change
interval looks like on its own.

**`brightness` is a ceiling and `pulseDepth` is a fraction of it, rather than a
floor and a peak.** Two reasons, and the second is the useful one. A floor given
absolutely can be set above the ceiling, and there is nothing sensible to do with
that. And depth is the number somebody actually means - how much should a beat
stand out - which is what makes a short pulse usable: a `pulseMs` of 40 at a depth
of 85 leaves the room at 15 per cent almost all of the time, and the same 40 ms at
a depth of 20 puts the identical flash on top of a bright room.

So a spent pulse fades to `brightness x (100 - pulseDepth)` per cent, and at any
depth below 100 that is a dim room rather than darkness - which is the argument
that makes the holding colour a dim white. A light that goes out between beats is
indistinguishable from one that has stopped receiving anything, and between beats
is most of the time. A depth of exactly 100 does fade to nothing, because somebody
asking for that has said so explicitly.

**A `pulseMs` between 1 and about 80 is honoured and should not be sent.** The
bridge forwards to a bulb at roughly 25 updates a second, so a bulb samples the
envelope about every 40 ms and a pulse that short is one or two frames. The result
is not a dimmer flash but an inconsistent one: some beats are sampled near the peak
and some near the tail, so the room flickers unevenly at no setting anybody chose.
It is the same two-rates confusion `state.light` warns about, met from the other
end.

The server accepts it, because it is a legal instruction and narrowing a published
range is a different kind of change. **Flux's own page does not offer it** - its
pulse control snaps to 0 or 80 - and any other client should do the same. Zero
stays reachable there because no pulse is a setting rather than a very short one.

The palettes are eight curated sets of hues plus `random`, which is any hue on the
wheel. Two of them - `ocean` and `warm` - are tight on purpose, and a curated
palette that cannot produce a large enough change gives the largest one it has
rather than repeating itself.

Values outside the ranges are `rejected` rather than clamped, on the same argument
as `setLatencyOffset`. A palette name that is not on the list is also `rejected`,
with the name quoted back; a missing field, a non-integer, or a palette that is not
a string is `invalid_data`.

Like the offset, these are server-side and echoed in every `hue` frame as
`hue.engine`, so a second phone opening the page sees the settings actually in
force rather than its own controls' idea of them. They survive a restart, and they
survive `forgetBridge`: a palette is a choice about how a room should look, not a
fact about the bridge.

## Backpressure

Each client has a bounded outbound queue. When it is full the **oldest** queued
message is dropped to make room, and the connection stays open.

Dropping the oldest is right for this feed rather than merely convenient: every
`state` is a complete snapshot, so a stale one has no value once a newer one
exists, and a beat that arrives late is worse than one that never arrives. The
alternative - blocking the writer - would let one phone on bad Wi-Fi stall the
beat scheduler thread, and the light show must never wait for a browser.

`hello`, `pong` and `error` are queued the same way. A client slow enough to
lose its `hello` is slow enough to be losing everything else too.

**`hue` is queued the same way too, and it is the one message where that costs
something**, because it is the only one pushed on change: a dropped `state` is
replaced fifty milliseconds later, while a dropped `hue` leaves a stale bridge
picture on screen until the bridge next changes, which may be never. It is
queued normally anyway rather than given a slot of its own, for two reasons. It
is a handful of frames an hour outside pairing, so a queue full at that exact
moment means the client is already losing beats and has a worse problem. And
during pairing - the one time `hue` frames come in a run - the countdown re-sends
the complete picture every second, so the case with the most frames to lose is
also the case that heals itself.

**`spectrum` is queued separately, and holds exactly one frame.** A newer
spectrum frame replaces the queued one instead of joining it, and the connection
sends everything else first. Both halves of that are the same rule: a
visualiser is worth exactly one frame of latency, and a beat must never be
dropped to make room for a picture of one. A spectrum feed running at 30 Hz into
the same queue as everything else would otherwise fill it in about a second and
push the beats out.

## Reconnection

The client reconnects on its own with a capped backoff and gets a fresh `hello`,
followed by a fresh `hue`. Because the server holds no position in any stream, a
reconnect needs no resumption token, no replay and no sequence numbers.

The two unprompted frames are unprompted for the same reason: everything else on
this socket repeats, and those two do not. `hello` is sent once per connection by
definition, and `hue` is pushed on change, so a page that had to wait for the
next change would sit on an empty pairing screen through a Wi-Fi hiccup that
happened to land between two of them. Neither is subscription-gated, because a
phone that has switched off the spectrum to save its battery still has to be able
to pair a bridge.

**Subscriptions do not survive a reconnect** and are not meant to. A new
connection starts with everything expensive switched off, so a client that wants
the spectrum back has to say so again. The alternative would have the server
remembering a page that may since have been closed, backgrounded or reloaded
into a different layout, and the client is the only thing that knows which.

The feed is its own heartbeat: `state` arrives `stateHz` times a second, so a
client treats several seconds without any frame as a dead connection and
reconnects, without waiting for a close event. The close event cannot be relied
on - a phone with its radio in power save routinely never receives the server's
closing packet - and no separate keepalive message is needed.
