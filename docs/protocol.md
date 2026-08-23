# Flux WebSocket protocol

**Protocol version 1.**

Flux keeps all state on the server. Browsers are dumb terminals: they render what
arrives and send control messages back. This document is the contract between
`Flux.Web` and `Flux.Web.Frontend`, which are separate projects that have to
agree byte for byte.

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

Any number of clients may connect. They are independent and hold no server-side
state, so a client that reconnects gets a complete picture immediately and never
has to replay anything.

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

## Units and conventions

| Field | Unit |
|---|---|
| `bpm` | Beats per minute, floating point |
| `beat` | Fractional beats since the Link session anchor. **May be negative** and jumps by thousands on a tempo change, so it is not a stable identity for anything |
| `beatIndex` | `floor(beat)` |
| `positionInBar` | 1-based, in the range 1..`quantum` |
| `t` | Milliseconds since the Unix epoch, as measured by whoever set the field |

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
  "app": "0.1.0",
  "quantum": 4,
  "stateHz": 20
}}
```

| Field | Type | Meaning |
|---|---|---|
| `protocol` | int | This document's version |
| `app` | string | Flux's own version, for the page footer. Answers "which build is the phone running?" |
| `quantum` | int | Beats per bar, so the client can size a beat indicator instead of assuming four |
| `stateHz` | int | Nominal `state` rate, so the client can size its own smoothing |

### `state`

The continuous picture, sent `stateHz` times a second whether or not anything
changed. It is a snapshot and is never a delta.

```json
{ "type": "state", "data": {
  "source": "link",
  "sourceState": "live",
  "grid": {
    "bpm": 128.0,
    "beat": 1024.37,
    "beatIndex": 1024,
    "positionInBar": 1,
    "barAligned": false
  },
  "diagnostics": { "packets": 4821, "reanchorMaxBeats": 0.0 }
}}
```

| Field | Type | Meaning |
|---|---|---|
| `source` | string | The sync source in use. `"link"` is the only value in version 1; Phase 2 adds `"audio"` and `"manual"` |
| `sourceState` | string | `"waiting"`, `"live"` or `"quiet"` |
| `grid` | object or **null** | Null exactly when `sourceState` is `"waiting"` |
| `diagnostics` | object | Source-specific health counters |

`sourceState` distinguishes three situations that a single boolean would blur:

- **`waiting`** - nothing has ever been received. There is no tempo to show, and
  `grid` is null rather than zero-filled, so a client cannot accidentally render
  0.00 BPM as if it were a measurement.
- **`live`** - packets are arriving.
- **`quiet`** - a timeline is known but packets have stopped. Flux keeps
  extrapolating and keeps sending a full `grid`; the tempo shown is the last one
  published and is increasingly a guess. The usual cause is LINK being switched
  off at the other end. A client should mark it, not hide it.

`grid.barAligned` is **false for the whole of Phase 1** and says so honestly.
Link transmits no downbeat, so `positionInBar` is a correct count with an
arbitrary starting point: the dot moves at the right rate but does not know
where bar 1 is. Phase 2 anchors it from a cue press or a tap. A client must not
present an unaligned position as a bar number.

`diagnostics` is scoped to the current source and will change shape when Phase 2
adds sources. For Link:

| Field | Type | Meaning |
|---|---|---|
| `packets` | int | ALIVE packets accepted so far |
| `reanchorMaxBeats` | number | Largest disagreement seen between an outgoing timeline and its replacement, ignoring tempo changes. Should sit at 0.0000 forever; a growing value means the grid is being corrected rather than confirmed |

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

This is the whole of the client-to-server direction in version 1, and it is
deliberately a real message rather than a placeholder: it exercises parsing,
dispatch and the error path from the first phase, so Phase 2's source selector
and tap button are a new `case` rather than a new mechanism.

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

## Reconnection

The client reconnects on its own with a capped backoff and gets a fresh `hello`.
Because the server holds no per-client state, a reconnect needs no resumption
token, no replay and no sequence numbers.
