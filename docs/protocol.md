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

**There is no clock synchronisation and no latency compensation in version 1.**
Server and client clocks are unrelated, so `beat` messages are rendered on
arrival and never scheduled against a timestamp. The latency offset arrives in
Phase 5 as a user-set slider, which is the only honest form it can take across
devices.

## Server to client

### `hello`

Sent once, immediately after the socket opens, before anything else.

```json
{ "type": "hello", "data": {
  "protocol": 1,
  "app": "0.2.0",
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
| `beat` | string | `"none"`, `"beat"` or `"strong"` - what the detector made of this frame |

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

A `devices` snapshot is sent immediately on a subscription that turns
`deviceLevels` on, so a picker fills in at once rather than after up to a tenth
of a second of blank.

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

**`spectrum` is queued separately, and holds exactly one frame.** A newer
spectrum frame replaces the queued one instead of joining it, and the connection
sends everything else first. Both halves of that are the same rule: a
visualiser is worth exactly one frame of latency, and a beat must never be
dropped to make room for a picture of one. A spectrum feed running at 30 Hz into
the same queue as everything else would otherwise fill it in about a second and
push the beats out.

## Reconnection

The client reconnects on its own with a capped backoff and gets a fresh `hello`.
Because the server holds no position in any stream, a reconnect needs no
resumption token, no replay and no sequence numbers.

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
