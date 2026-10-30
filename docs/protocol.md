# Flux WebSocket protocol

**Protocol version 2.**

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

Version 1 grew once more in Phase 4 and again did not break, in three additions.
`state.light` gained a `color`, which is one added field on an existing payload; an
older page ignores it and keeps reading the frame rate it came for. A `stage` message
and a `setStageSettings` message arrived together, and both are new types, which the
rule above already covers.

The third is the one worth arguing rather than asserting: **`spectrum.bins` went from
64 columns to a count between 16 and 128, and the version did not move.** Narrowing a
promise is normally exactly the change that increments it. It does not here because the
promise was never made: this document has always said to read the array's length rather
than to assume it, and it said so in the same paragraph that shows a shortened example.
A client that followed that draws a finer or coarser spectrum and notices nothing else.
One that hardcoded 64 was outside the contract before this change, which is a different
thing from being broken by it.

**Version 2 is the first break, and it is one changed meaning rather than a
rewrite.** The light engine now sends a colour per light instead of one for the
room, so `state.light` gained a `channels` array - an addition, which on its own
would not have moved anything. What moved it is `state.light.color`, which meant
"the colour every light was sent" and now means "the colour the engine chose
before each light's own band was applied". Those differ by however much the
spectrum is favouring one part of the room, and a version 1 page reading the new
field would go on looking correct while drawing a room slightly brighter than the
real one. That is precisely the quiet misreading the version exists to convert
into a refusal, so it was converted into one.

Everything else in version 2 is unchanged, and a client updating to it has one
thing to do beyond the number: decide whether to draw `channels` or keep drawing
`color`. Both remain honest answers to different questions - one is what each
bulb received, the other is what the engine decided the beat was worth.

Version 2 has since grown without breaking, and one of those additions is worth
arguing. `anchor` and `strobe` are new types and `state.preview` is a new field, which
the rule above covers. But
`setLightSettings` gained `strobe` and `strobeCycle` as **required** fields, the way
`colorCount` and `colorLayout` arrived before them, and `setStageSettings` gained `trailMs`
the same way, so a page built before them has those settings refused as
`invalid_data`. That is loud rather than quiet - the page is
told, and nothing is applied that it did not mean - which is the difference between a
refusal and the misreading the version exists to prevent. Making the fields optional
instead would have broken the rule that message stands on: that it is one complete
statement of the look, never a patch.

Version 2 grew again in Phase 6 without breaking. rekordbox arrived as a fourth
source, `"rekordbox"`, and as a fourth way of anchoring the bar, also `"rekordbox"`;
`diagnostics` gained its `deck` and `detail`; and `shiftBar` is a new inbound type.
The two new values in existing fields are the part to argue. This document has always
told a client to expect a source it does not know and to degrade rather than fail,
and an older page does exactly that: it names the source as the server does, shows
no diagnostics rows under it, and reads the alignment as a bar anchored by something
it cannot name. The bar position it draws is still right, which is the difference
between a degradation and a misreading.

Phase 6 then gave the light engine rekordbox's phrases: `setLightSettings` gained
`followPhrases` as a **required** field, the way `strobe` arrived, and `diagnostics`
gained `phrase`. The required field is loud for the reason given above, and `phrase`
is one more rekordbox row that an older page does not draw.

Phase 2's lesson about `devices` is worth repeating rather than filing, because
this document is once again describing messages before anything produces them.
Where that left a field's honesty uncertain the field is nullable rather than
promised: `light` is null unless a stream is genuinely running, and `fps` is a
measurement or it is absent along with the object holding it. If a promise made
here turns out to be one Flux cannot keep, correcting it increments the version
unless no build ever sent the field.

Updating itself added `update`, `checkUpdate` and `applyUpdate`, all new types. An
older page ignores `update` and never sends the other two, so the version stays 2.

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
  "protocol": 2,
  "app": "0.6.0",
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
  "light": {
    "fps": 49.7,
    "color": { "r": 0.8, "g": 0.2, "b": 0.55 },
    "channels": [
      { "at": 0, "r": 0.8, "g": 0.2, "b": 0.55 },
      { "at": 0.5, "r": 0.46, "g": 0.12, "b": 0.32 },
      { "at": 1, "r": 0.12, "g": 0.03, "b": 0.08 }
    ]
  },
  "diagnostics": { "reanchorMaxBeats": 0.0, "packets": 4821 },
  "preview": null
}}
```

| Field | Type | Meaning |
|---|---|---|
| `source` | string | The sync source driving the grid: `"link"`, `"audio"`, `"manual"` or `"rekordbox"`. `"none"` when a build has no sources at all, which is also the only case where `sources` is empty |
| `sourceState` | string | `"waiting"`, `"live"` or `"quiet"`, for `source` |
| `sources` | array | Every source this build has, whether or not it is the active one |
| `grid` | object or **null** | Null exactly when `sourceState` is `"waiting"` |
| `alignment` | object | Whether the bar has been anchored, and by what |
| `light` | object or **null** | Null exactly when Flux is not streaming to a bridge |
| `diagnostics` | object | Health counters for the active source |
| `preview` | object or **null** | What the light engine would be sending while nothing streams. **Null whenever `light` is present** |

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
| `name` | string | `"link"`, `"audio"`, `"manual"` or `"rekordbox"` |
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
| `method` | string or **null** | `"cue"`, `"tap"`, `"drop"` or `"rekordbox"`; null exactly when `aligned` is false |

Only one source knows where a bar starts: rekordbox, which reads it from the track's
own beatgrid. It sets the bar whichever source is keeping time, so with Link selected
the tempo is Link's and bar 1 is rekordbox's. Otherwise bar 1 comes from a cue press
on an audible deck, a tap in the web UI, or the detector deciding a drop landed.
These are not equally trustworthy and only the user can judge which applies, so the
method travels with the alignment: showing what aligned the bar is what lets a wrong
one be recognised as wrong.

When rekordbox names no master deck, or the master cannot be read, the deck that is
playing stands in for the master deck: the one on air while the next is started, and the next once the first
stops.

rekordbox sets the bar when there is a new reason to read it - a new track on the
master deck, a different master, playback starting, a jump in the track - and not in
between, so a tap holds until the next of those. A grid whose downbeat is a beat off
is corrected with `shiftBar`, which rekordbox keeps for the rest of the track when
rekordbox set the bar.

An alignment is a beat index, and the beat number is not a stable identity: a
tempo change, a session change or a source switch renumbers it wholesale. The
anchor survives that. The grid knows how far the numbering moved and moves the
anchor with it, rounded to the nearest beat, so bar 1 stays where it was in the
music rather than at the number it had. What does clear it is the grid having no
timeline at all - switching to a source with nothing to show yet, or the active
source withdrawing its tempo - since then there is no beat for an anchor to be.

### `state.light`

What the light stream is actually achieving, and the single authority on whether
Flux is streaming at all.

| Field | Type | Meaning |
|---|---|---|
| `fps` | number | Frames a second the stream is achieving, measured over the last second. Not the configured rate |
| `color` | object or **absent** | The colour the room is on: `{ "r": 0.8, "g": 0.2, "b": 0.55 }`, linear 0 to 1 per channel. Absent until a frame has gone out |
| `channels` | array | One entry per light, in the order the bridge streams them. Empty until a frame has gone out |

Each entry of `channels` is `{ "at": 0.25, "r": 0.8, "g": 0.2, "b": 0.55 }`:

| Field | Type | Meaning |
|---|---|---|
| `at` | number | Where this light listens on the frequency axis: 0 follows the kick, 1 follows the top |
| `r`, `g`, `b` | number | What this light was sent, linear 0 to 1, on the same scale as `color` and, like it, with no gamma correction |

**Where a light is decides how hard the beat hits it, and above one colour which
colour it is in.** A light near the centre of the room follows the low end and one
out at the edge follows the high end, so a kick punches in the middle and a cymbal
wash lifts the corners. Which band a light listens to is decided from the position
the bridge reports for its channel, ranked by distance from the centre of the room
and then spread across the spectrum - ranked rather than scaled, because a real room
clusters: four of the seven lights in one area measured here sit within 0.07 of
each other, and scaling raw distance would have given those four almost the same
band while three lights covered everything else.

**Each band is measured against its own recent loudest and never against the other
two.** Music carries far more energy in the kick band than in the top on essentially
every frame of every track, so comparing the raw levels answers "which lights lead"
the same way every time and the room becomes a fixed bright-in-the-middle gradient
that pulses in unison. Measured against itself, a hi-hat at the top of what hats do
in this track counts as fully lit exactly as a kick at the top of what kicks do - so
the outer lights lead through a break and the centre takes it back on the drop. **The
ordering has to be able to invert or there is no information in it.**

**A light whose band is busy stays lifted between beats**, up to seven tenths of what
a beat would give it. The beat is divided between the lights, while what happens in the gaps
is a fact about each band on its own: a hat pattern under a held chord lifts the outer
lights whether or not anything is louder elsewhere. That is the difference between a
room the music moves through and a set of simultaneous flashes at different heights.

**The pulse answers the music and not only the grid**, and it does so in two ways at
once because they are two different questions.

*Is a kick playing on this beat* is the kick band against the loudest this track's
kick has been over about the last minute, ramped so that under two fifths of it is no
kick and four fifths or more is a whole one, read as the beat lands and allowed to rise - never fall - while the pulse is alive.
It may rise because the light path runs deliberately ahead of the audio, so the kick a
beat announces has usually not reached the analyzer at the moment the beat is consumed;
it never falls, so the fade is not cut off by the gap after the kick that caused it.
**A beat with no kick under it does not pulse the room at all.** A grid keeps publishing beats through a
breakdown - a clock has no opinion about whether anything is playing - so without
this the lights fire a synchronous pulse right through a passage with no kick in it,
and it is the unison rather than the brightness that gives it away. What carries a
break instead is the per-band response above: with the pulse out of the way the lights
follow their own bands, so hats lift the outer lights and a stab lifts the middle.
There is no break mode; this is what the room already does once the pulse stops
sitting on top of it. The measure is against the track's own kick and not against
the band's last second, which is what it was until 2026-09-30: a drop's bassline fills
the band, so its kicks barely stand out from their own second and the drop pulsed at a
fifth, while the bass left under a break or a build of strings sits well under what
the track's kick does and reads as little or nothing.

*Is anything playing at all* is how loud the room is now against the loudest it has
been over about the last minute, applied continuously rather than captured, so a
track that stops mid-pulse kills the tail instead of finishing at the volume of a
track that is no longer playing. Against the room's own recent loudest rather than
against full scale, because absolute level says more about where the gain is set than
about the music. A minute is deliberately far longer than the second a band's own
average looks back over, and longer than the two bars its level is measured against: a
breakdown runs sixteen to thirty-two bars, and a reference that forgot inside one
would decide halfway through the break that the break is now normal.

**Neither reaches the floor itself.** Both spend the range above it,
and the floor is never zero while a stream is open - a light that goes out is
indistinguishable from one that has stopped receiving anything. A `brightness` of 0
is still black, because that is a blackout and somebody asking for no light should
get none.

**`at` is the server's answer and a page must not recompute it.** A browser could
derive the same ordering from the array and it would be a second implementation of
one tuned rule, which is the arrangement this document already rejects for colour.
It is fixed for the life of a stream, since the geometry belongs to the area the
session opened against, and it is still sent on every frame: a page that connected
mid-show cannot be told to wait for a message it already missed.

**A light whose band is silent falls back to the room's resting level and never to
black.** The spectrum scales the pulse above the floor and never the floor itself,
for the reason the floor exists at all - a light that goes out is indistinguishable
from a light that has stopped receiving anything.

**Where there is nothing to listen to, every light gets the room's own envelope.**
A build with no audio, a closed capture device, silence, and a frame with no energy
in any band all arrive at the same answer, which is the behaviour Flux had before
per-light colour existed. Link supplying a tempo with no microphone anywhere near it
is an ordinary configuration and not a degraded one.

**`color` is not the average of `channels`**, and the distinction is the same one that
keeps this whole object honest: an average of nine bulbs is a colour nothing in the room
is showing, while this is a value the engine genuinely computes on its way to deciding
what each light gets. With `colorCount` above one it is the first of the hues the engine
chose - again a colour some of the room is genuinely on, rather than a blend of two that
none of it is. A page wanting one number for the room should use it; a page drawing the
room itself should use `channels`.

**Both are what the bridge was sent, read back**, and they are here so that a page can
draw the room rather than compute its own opinion of it. The alternative - a second
beat-to-colour engine in the browser - needs no protocol at all and was rejected: two
implementations of one tuned behaviour drift apart the first time either is retuned, and
what a viewer then has is a screen and a room that disagree with no way to tell which is
right. **Anything a bulb also does is decided on the server and read from here.**
Embellishments a bulb cannot do, such as a hue gradient across the frequency axis, remain
the page's own business.

It rides the 20 Hz state feed rather than getting a feed of its own, and the rate is
defensible rather than merely convenient: a bulb renders roughly 25 updates a second over
Zigbee whatever the stream sends, so a 20 Hz preview is within a hair of the fidelity the
room itself has. A client should still interpolate between frames rather than stepping,
because a brightness pulse of 240 ms is only five frames long here.

The values are linear, on the same 0 to 1 scale the spectrum bands use, and **no gamma
correction is applied to them, by anything, and that is a decision rather than a gap.**
The bridge reads the RGB it is sent as light output, not as screen values: a still
ladder of whites at 100, 75, 50 and 25 per cent was hard to tell apart on the bulbs,
while an orange of full red and half green came out orange. A brightness curve on the
way to the bridge was then built and tried with music, and it flattened the lights that
spend their time just above the lit floor - the ones on the quieter bands - until they
stopped visibly moving. Every brightness setting was tuned by eye against the uncurved
bridge, so it already accounts for it. One consequence for a page: a monitor does curve,
so the dim end of the stage looks darker than the same value on a bulb. That is the
honest cost of drawing what was sent, and correcting it in the page would be a second
engine's opinion about the room. Absent rather than black before the first
frame, because black is a colour the engine can legitimately send and "not yet" is not
it.

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

### `state.preview`

The light engine's output while no stream is open, so a page can show the look
before the room is lit - or with no bridge at all, since the look is stored on this
machine rather than on the bridge.

```json
"preview": {
  "color": { "r": 0.8, "g": 0.2, "b": 0.55 },
  "channels": [
    { "at": 0.0, "r": 0.8, "g": 0.2, "b": 0.55 },
    { "at": 1.0, "r": 0.4, "g": 0.1, "b": 0.27 }
  ]
}
```

`color` and `channels` mean what they mean on `state.light`, with no frame rate
because nothing is being sent. **It is never present alongside `light`**, and a page
must not read it as a sign that the room is lit: `light` stays the single authority
on whether Flux is streaming, which is why this is a field of its own rather than
`light` with the rate left out. A client drawing the stage draws `light` when there is
one and this when there is not, and says which it is showing - the picture looks the
same either way.

It is the same engine the stream runs, not an imitation of it: with no session open
the server runs the engine itself at the stream's rate, and stands down the moment a
session starts. So the colours on screen are the colours the bulbs would get, beat
for beat, and the handover to a real stream continues them rather than starting over.

**`channels` is the selected area as the bridge laid it out**, read from the same
answer that lists the areas, so each light listens where the stream would place it and
falls in the colour group it would fall in - a `colorCount` of two or three splits the
preview exactly as it would split the room. With no area to read - no bridge, or none
chosen - it is six stand-in lights spread evenly across the spectrum instead of none,
because a colour split needs lights to divide between and a preview without them could
only ever show the first colour.

Null in a build with no Hue support, which has no engine to preview.

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
| `deck` | int | rekordbox | The deck being followed, counted from 1 as rekordbox shows it: the master deck, or when rekordbox names none or the master cannot be read, the deck that is playing. Absent until rekordbox has been read |
| `detail` | string | rekordbox | Why it is not following a playing deck, in words: rekordbox not running, running as administrator, a version Flux has no chains for, a track with no beatgrid, a deck whose tempo or playhead cannot be read for a moment, the deck stopped, no deck playing while rekordbox names no master. **Absent while it is following one**, so its absence is the good state |
| `phrase` | string | rekordbox | The phrase playing on the deck followed as rekordbox labels it, such as `"Chorus 2"` or `"Down"`, or why there is none, in words beginning `"none: "`: no phrase analysis, phrases that do not fit the beatgrid, before the first phrase or past the last. Display text, not an enumeration. Absent while no deck is followed |

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
| `bins` | array of int | 128 log-spaced display columns, low frequency first, each 0 to 255 |
| `kick` | number | Bass band energy, 0 to 1 |
| `mids` | number | Mid band energy, 0 to 1 |
| `highs` | number | High band energy, 0 to 1 |
| `flux` | number | Spectral flux against the previous frame: the number the beat detector actually triggers on |
| `beat` | string | `"none"`, `"beat"` or `"strong"` - the strongest verdict the detector reached since the previous frame sent to this client |

The example shows eight bins for readability. There are always exactly 128, and a
client should still check rather than index blindly.

**The count changed from 64 to 128 and the protocol version did not move**, which is
the rule this document sets for itself applied rather than bent: a version bump is for
a change an existing client could misread. A client that derives its column count from
the array it was sent, as this rule has always told it to, draws a finer spectrum and
notices nothing else. One that hardcoded 64 was already outside the contract, and the
sentence above is why. The derivation of the number itself, and what the transform
underneath it can actually resolve, is in
[docs/audio-constants.md](audio-constants.md).

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
  "engine": { "palette": "random", "colorChangeBars": 4, "pulseMs": 240, "brightness": 100, "pulseDepth": 85, "colorCount": 1, "colorLayout": "radial", "strobe": false, "strobeCycle": 50, "followPhrases": true },
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
| `colorCount` | int | How many colours the room is in at once, 1 to 3 |
| `colorLayout` | string | How the room is divided between them: `radial`, `alternating` or `random` |
| `strobe` | bool | Whether the strobe is switched on. **A held strobe button never shows here** |
| `strobeCycle` | int | How long one strobe cycle is, 10 to 100 per cent of a beat |
| `followPhrases` | bool | Whether a phrase starting changes the colour and restarts the bar count, when the track has phrases |

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

### `stage`

How the visualizer draws, pushed on change and once to every client the moment it
connects.

```json
{ "type": "stage", "data": {
  "layout": "upward",
  "shape": "bars",
  "color": "randomBeat",
  "bands": 64,
  "trailMs": 0
}}
```

| Field | Type | Meaning |
|---|---|---|
| `layout` | string | `"upward"` (bars from the floor, full width) or `"mirrored"` (outward from the centre, each bar centred on the axis) |
| `shape` | string | `"bars"`, `"thinBars"`, `"chonkerBars"`, `"pixel"`, `"smooth"` or `"smoothSimple"` |
| `color` | string | `"randomBeat"`, `"hueCycle"`, `"hueWave"`, `"hueWaveVert"` or `"static"` |
| `bands` | int | How many display columns `spectrum.bins` carries, 16 to 128 |
| `trailMs` | int | How long the picture's afterimage lasts, 0 to 1000 ms. **0 is off**, and the default |

Its own message rather than fields on `state`, on the argument that gave `hue` one:
state goes out twenty times a second, and a look that changes when somebody taps a
chip would be twelve hundred identical copies a minute of something nobody moved.
Like `hue`, that means it does not repeat, which is why a client is sent one on
connecting.

**Server-side rather than per browser, and that is a reversal worth stating.** The
first version of these settings lived in each browser's own storage, on the argument
that whether a phone draws a canvas is a fact about the phone. That argument survives
for exactly one switch - whether the stage is drawn at all, which is still local and
is not in this protocol - and fails for the rest: the look of the stage is part of the
show, it is set by whoever is standing at the page, and it should be the same look on
the laptop and on the phone in somebody's hand.

`bands` settles it on its own, because it is not a drawing preference at all. It
decides how many columns the **analyzer** produces, which is what makes every count
exact: the display columns are log-spaced between fixed endpoints, so column *c* of 64
spans precisely columns 2c and 2c+1 of 128. Folding a finer feed down in the browser
is exact only where the counts nest like that, and a count such as 100 would leave
some bars covering two source columns and their neighbours one - a faint comb across a
smooth spectrum. Computing them at the source makes any count evenly spaced and sends
less over the wire at a low one.

**Size a drawing from the length of `spectrum.bins`, not from this field.** The two
agree, but a spectrum frame already in flight when the count changed carries the old
one, and the array is the thing being drawn.

Nothing the beat detector decides is affected by `bands`. It reads band energies over
the whole transform and never these columns, which is why this is a resolution knob
rather than a re-calibration. The derivation of the bounds, and what the transform
underneath can actually resolve, is in
[docs/audio-constants.md](audio-constants.md).

**`trailMs` is how long the picture leaves an afterimage**: the last few frames of the
spectrum drawn again under the live one, each fainter, falling to a tenth over
`trailMs` - the threshold `pulseMs` names, so the number is how long the trail
visibly lasts.

It stands in for the reference scene's fade and departs from it on two counts, both
settled by looking at the stage. The scene smeared pixels, repainting the background at
partial opacity, and an 8-bit canvas cannot quite fade a faint smear to the background,
so its long end left a haze of old bars; an afterimage is drawn from the spectrum and
is simply not drawn once it has run out. And the scene counted its fade per frame, so
one setting trailed twice as long on a 60 Hz display as on a 120 Hz one, with a top end
of nearly ten seconds; this is counted in time, on any display, and stops at a second.

It is on the screen only. In the scene the canvas was the device and the fade smeared
the LEDs along with the picture; Flux's lights have an envelope of their own, and
`setLightSettings.pulseMs` is what decides how long a light trails a beat.

### `update`

Whether a newer Flux is waiting. Sent once on connect, right after `stage`, and again to
every client whenever it changes. The desktop app always sends it, and a host with no
updater at all sends none. A page that has received none since the last `hello`, or
has received `unavailable`, shows no update control.

```json
{ "type": "update", "data": { "status": "ready", "version": "0.7.0" } }
```

| Field | Type | Meaning |
|---|---|---|
| `status` | string | `unavailable`, `checking`, `current`, `ready` or `failed` |
| `version` | string or null | The downloaded version when `status` is `ready`, otherwise null |

- `unavailable`: this copy cannot update itself. Either it is a development build
  that Setup.exe never installed, or updates are switched off with `Updates:Enabled`.
  Nothing changes it while the process runs.
- `checking`: asking GitHub for a newer release, or downloading one it found.
- `current`: no newer release is known. The last check found none, or none has run
  yet: the first runs a minute after start, and then one every two hours.
- `ready`: a newer version is downloaded and is applied by `applyUpdate`, or else
  when Flux next quits.
- `failed`: the last check got no answer, which is ordinary when offline. The next
  check runs on schedule.

Pushed on change rather than repeated, like `stage`: it moves a few times a day at
most.

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
rather than a detail of it: everything expensive on this socket is opt-in, so a
client that draws nothing pays for nothing, and the server never has to guess
which kind it is talking to.

What any given client then asks for is its own business. Flux's own page asks
for the spectrum from the first frame, because the visualizer is the page rather
than a panel in it, and offers a numbers-only switch for the phone in the booth
that would rather not spend its battery and its Wi-Fi drawing a canvas thirty
times a second.

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
and the bar alignment is carried across the rebase, so the downbeat stays where it
was in the music. Switching to a source that has not published yet leaves the grid
with nothing, and the alignment goes with it.

### `selectAudioDevice`

```json
{ "type": "selectAudioDevice", "data": { "deviceId": "{0.0.1.00000000}.{a1b2c3d4}" } }
```

Points audio analysis at one of the devices from `devices`. Answered with
`rejected` when no device by that id exists, which includes the ordinary race of
a device unplugged while its button was on screen, and the case of a build with
no audio support at all.

**A device accepted here is remembered across restarts**, so this message is a
lasting choice rather than a setting for the session. Flux comes back to it next
launch in preference to `Audio:DeviceId`, and falls back to
`Audio:PreferDeviceNamed` if it is not plugged in - which is deliberately *not*
what happens to a selection made while Flux is running, since capturing
something other than what was just asked for would be overruling the person who
asked.

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

### `anchor`

```json
{ "type": "anchor", "data": { } }
```

One press on the downbeat. `data` is empty and is ignored, and the press is
timestamped on arrival for the reason `tap` is.

The downbeat without the tempo, which is what `tap` cannot say under the manual
source: a tap there is also a tempo press, so two taps in quick succession retune
the grid to however fast somebody happened to press. This message never touches a
tempo - not the manual estimate, not its tap sequence - so it is safe to press
repeatedly on any source, and it is the way to correct the bar after typing a
tempo. It reads back as `alignment.method` of `"tap"`, because from the grid's
side that is what it is: a human press saying where bar one starts.

Like `tap`, it works whatever the active source, on the same argument. With no
timeline on the grid there is no beat to anchor, and the press does nothing.

### `shiftBar`

```json
{ "type": "shiftBar", "data": { "beats": 1 } }
```

| Field | Type | Meaning |
|---|---|---|
| `beats` | int | Whole beats to move bar 1 by, from -3 to 3 and not 0. Positive is later |

Moves the downbeat and keeps what set it, so a bar anchored by rekordbox and moved a
beat still reads back as `"rekordbox"`. It exists for a track whose rekordbox
beatgrid has the downbeat in the wrong place: a tap would fix that only until the next
seek, when rekordbox reads the bar from the grid again, while a shift is kept as a
correction and applied every time rekordbox reads the bar for the rest of the track.
A new track on the master starts from its own grid again.

Works on a bar anchored any way, not only by rekordbox, but only a shift of a bar
rekordbox set is kept as its correction. A bar a cue press or a tap set is moved and
nothing more: the press replaced rekordbox's downbeat until the next seek, and a shift
correcting it says nothing about the track's grid. Past 3 is refused rather than
wrapped: at rekordbox's four beats to the bar, four is a whole bar and moves nothing a
position can show. The range is sized for that bar; at another quantum a shift moves
by the beats given all the same, so a bar of eight takes two presses to reach its
furthest downbeat. With no
anchored bar there is nothing to move, and that is `rejected` rather than ignored,
because a button that does nothing and says nothing looks broken.

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

**Choosing a different area while the stream is open closes it**, and `state.light`
becomes null shortly after the `hue` frame carrying the new selection. Leaving it open
would keep the lights running in the room nobody has selected any more, under a page
showing the new one and a stop button. Streaming to the new area is a fresh
`startStream`, because the bridge grants its one session against a particular area.

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

**It ends a pairing countdown and closes an open stream**, so a client should expect
`state` to leave `pairing` without the window running out, and `state.light` to become
null. Both are the same point: a countdown left running would register the link press
somebody was already walking towards and write the credentials straight back, and a
session left open would hold the bridge's one entertainment slot with nothing on screen
accounting for it.

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
  "pulseDepth": 85,
  "colorCount": 1,
  "colorLayout": "radial",
  "strobe": false,
  "strobeCycle": 50,
  "followPhrases": true
}}
```

| Field | Type | Meaning |
|---|---|---|
| `palette` | string | `random`, `warm`, `cool`, `neon`, `sunset`, `ocean`, `forest` or `party` |
| `colorChangeBars` | int | Bars between colour changes, 0 to 16. **0 means a new colour on every beat** |
| `pulseMs` | int | How long a beat's brightness pulse takes to fade, 0 to 2000. **0 means no pulse** |
| `brightness` | int | How bright a beat drives the lights, 0 to 100 per cent. **0 is a blackout** |
| `pulseDepth` | int | How far the light drops between beats, 0 to 100 per cent of `brightness`. **0 never dips, 100 fades to nothing** |
| `colorCount` | int | How many colours the room is in at once, 1 to 3 |
| `colorLayout` | string | How the room is divided between them: `radial`, `alternating` or `random`. A no-op at one colour |
| `strobe` | bool | Whether the room strobes white on the beat grid until this is switched off. **Off by default** |
| `strobeCycle` | int | How long one strobe cycle is, 10 to 100 per cent of a beat. **Smaller is faster**: 50 flashes twice a beat, 100 once |
| `followPhrases` | bool | Whether a phrase starting changes the colour and restarts the `colorChangeBars` count, when the track playing has phrases. **On by default** |

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

**Colour changes land on the downbeat**, not merely every fourth beat, and they are
**counted from the anchor**: the bar a `tap`, a cue press or a drop marked is bar
one, and it takes a colour. Anchoring means "this is bar one", and it has to mean
that for everything the bar count drives or it only half means it - before this the
count ran from wherever the source's numbering happened to start, so anchoring
moved the beat indicator and left the colours changing on the old phase. A press
restarts the cycle even on a beat that was already a downbeat, and a press a hair
after the kick, which names a beat that has already been lit, is answered on the next
frame rather than a bar later. A drop only ever moves bar one by whole bars, so it
restarts the cycle when it lands a bar or more from the anchor and changes nothing
when it only confirms it. A source going quiet does not touch the anchor, so it does
not restart the cycle either. How long a bar is comes from the same `quantum` the `hello`
frame carries - four by default, and the engine has no separate opinion about it.
Where no downbeat is known - `alignment.method` of `none` - it falls back to every
`quantum x n`-th beat from wherever the numbering starts, because changing on a
guessed phase is worth more than not changing at all.

**With `followPhrases` on, a phrase restarts the count.** rekordbox reads the phrases
of the track on its master deck - intro, verse, up, down, chorus, bridge, outro - and
a phrase starting always takes a colour, with the count running on from there: every
`colorChangeBars` bars from the phrase start rather than from the anchor. The anchor
still puts the changes on downbeats, but it does not say which downbeat the count
should run from. rekordbox sets it at the downbeat nearest the playhead on every load,
master change and jump, which is rarely where a phrase begins, and then every change
lands a bar or three off the track's structure. The phrases hold whichever source keeps
time, as rekordbox's downbeat does. A track whose phrases do not fit its beatgrid - a
few whose grid was set to half or double tempo after rekordbox found the phrases -
offers none, and neither do Link, audio or a typed tempo on their own. Without phrases,
or with the setting off, the count runs from the anchor exactly as above. The
`diagnostics` field `phrase` shows which phrase is playing, or why there is none.

**`colorCount` splits the room along the frequency axis**, which is the same axis
that already decides how hard each light is hit: at two, the lights listening to the
lower half of the spectrum take one hue and the upper half another; at three, inner,
middle and outer. Grouped by frequency rather than by channel order, so a light's
colour says the same thing about where it is as its brightness does. The hues are
drawn from the same palette and held the same distance apart from each other that a
new colour is held from the one it replaces, so two colours can never be two shades
of one. A palette too tight to supply that many distinct hues gets the farthest
apart it can manage rather than a failure.

**`colorLayout` decides how those groups are handed out**, and only `radial` has an
argument behind it - the other two exist to be tried against a real room, which is
the only way a look gets settled. `radial` gives contiguous groups along the axis, so
the lights nearest the centre share a hue. `alternating` hands the hues to every
other light in order of distance, which is bolder and discards the spatial mapping
entirely. `random` draws each light's group from its own place on the axis mixed with
a number re-drawn at each colour change, so the shape holds between changes rather
than flickering. All three are a no-op at one colour, and all three are functions of
where a light sits and never of its channel id or its position in the array.

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

**The strobe is the reference scene's, carried across**: a cycle of `strobeCycle`
per cent of a beat, counted from the last beat, white for the first quarter of each
cycle and the ordinary room for the rest. Off by default, because a strobe is a
photosensitivity hazard and nobody should meet one from a setting they did not
choose. The same strobe runs while anybody holds the button - see `strobe` below -
and `strobeCycle` sets its speed in both cases.

Three things it does that the scene did not, each with its reason. **The flash is
white at `brightness`, not at full**, so the ceiling somebody set still holds and a
blackout stays black rather than becoming a strobe. **It needs a beat to flash on**:
before the first beat there is no grid to strobe to, and once no beat has arrived for
two beat lengths it stops rather than extrapolating a grid a source has stopped
publishing. **The page does not draw a strobe of its own**: the stage shows what
each light was sent through `state.light.channels`, which at 20 frames a second
catches some flashes and misses others. Timing one in the browser would be a second
strobe engine that drifts from this one, which is the argument that put the colour
engine on the server in the first place.

**Short cycles are honoured and will not look like a club strobe.** Each flash is a
quarter of a cycle, so at 128 BPM and the default of 50 it lasts about 59 ms - one
or two of the updates the bridge forwards to a bulb each second at roughly 25 Hz.
On Hue that reads as energetic pulsing. The value is legal and the screen tracks it;
the bulbs simply stop resolving it.

Values outside the ranges are `rejected` rather than clamped, on the same argument
as `setLatencyOffset`. A palette name that is not on the list is also `rejected`,
with the name quoted back; a missing field, a non-integer, a palette that is not
a string, or a `strobe` that is not a boolean is `invalid_data`.

Like the offset, these are server-side and echoed in every `hue` frame as
`hue.engine`, so a second phone opening the page sees the settings actually in
force rather than its own controls' idea of them. They survive a restart, and they
survive `forgetBridge`: a palette is a choice about how a room should look, not a
fact about the bridge.

### `strobe`

```json
{ "type": "strobe", "data": { "held": true } }
```

| Field | Type | Meaning |
|---|---|---|
| `held` | bool | Whether this page's finger is on the strobe button |

Strobes the room for as long as the button is held, whether or not `strobe` is
switched on in `setLightSettings`, at that message's `strobeCycle`. The switch is for
a strobe that runs hands-free; this is the one for a build-up, pressed through it and
let go on the drop, and it cannot be left on by accident. Never saved, never echoed
in `hue`, and not acknowledged: the room is the confirmation.

**A hold is a lease, not a switch.** A page sends `held: true` when the button goes
down and **repeats it every 500 ms while it stays down**; the server drops any hold
that goes 1.5 seconds without one. `held: false` ends it at once, and so does the
connection closing. The lease is for the case neither arrives - a phone whose screen
sleeps mid-press, Wi-Fi dropping - where a socket can stay open with nobody behind it
for far longer than a room should strobe unattended.

Each connection is one holder, so the room strobes while any page is holding and
stops when the last lets go; pressing twice from one page is still one hold. A hold
with no stream open lights nothing, and is a strobe the moment one opens.

A `held` that is not a boolean is `invalid_data`. A build with no Hue support answers
`rejected`.

### `setStageSettings`

One complete statement of how the visualizer should draw.

```json
{ "type": "setStageSettings", "data": {
  "layout": "upward",
  "shape": "bars",
  "color": "randomBeat",
  "bands": 64,
  "trailMs": 0
}}
```

Every field is required, for the reason `setLightSettings` requires all of its own: a
page that could send a shape alone would be relying on the server to remember the
rest, and two pages doing that at once produce a look neither of them chose.

Confirmed by the `stage` frame that follows rather than by a reply, and that frame goes
to **every** client rather than to the one that asked. That is the whole point of the
setting having moved to the server: a shape chosen on a phone has to move the laptop's
controls too, and a reply addressed to the phone would leave the laptop showing a look
nobody is running.

A name that is not on one of the three lists is `rejected` with the name quoted back.
A `bands` outside 16 to 128 or a `trailMs` outside 0 to 1000 is `rejected` rather than
clamped, on the same argument as `setLatencyOffset`. A missing field, a non-integer
`bands` or `trailMs`, or a name that is not a string is `invalid_data`.

These survive a restart, in `%APPDATA%\Flux\stage-settings.json`, or under
`Flux-dev` for a build run from source.

### `checkUpdate`

```json
{ "type": "checkUpdate", "data": { } }
```

Asks for a newer release now, rather than at the next scheduled check. `data` is
empty and is ignored. Confirmed by the `update` frames that follow, `checking` and
then the outcome, which go to every client. Joins a check already running, and does
nothing while `status` is `ready` or `unavailable`.

### `applyUpdate`

```json
{ "type": "applyUpdate", "data": { } }
```

Stops Flux and starts the downloaded version in its place. `data` is empty and is
ignored. It does nothing unless `status` is `ready`.

**There is no reply, because nothing is left to send it.** Flux stops the ordinary
way: the Hue stream is ended and every socket closes. The new version starts a few
seconds later, and a page reconnects to it as it would after any restart (see
"Reconnection"). Its `hello` carries the new `app` version, which is the confirmation.

A host that cannot update itself ignores both messages, rather than answering with
`unknown_type`. A page that is reconnecting may not yet know whether the host has an
updater, and the messages are well formed either way.

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
