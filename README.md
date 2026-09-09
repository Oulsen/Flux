# Flux

Music in, light out.

Flux is a standalone Windows app being built, in phases, to drive Philips Hue
Entertainment lights from music with proper beat sync. Today it is the
instrument that has to come first: it finds the beat, either from an Ableton
Link session or by listening to the music itself, and shows what it found in
any browser on the LAN, phone included. Everything the lights will eventually
follow is visible on screen, which is the only way to tell that a beat grid is
right before anything is wired to it.

## What works in 0.2.0

- Finds the beat two independent ways, and either one drives the same grid.
  **Ableton Link** (rekordbox, Ableton Live, or any Link peer) is followed
  passively: Flux listens without joining, so it never appears as a peer and
  never influences the session. **Audio** analyses the sound itself, which is
  the only source that reports what is actually coming out of the speakers.
- Switches between the sources live, from the page, without a restart. Each
  one reports its own tempo, confidence and health, so the two can be compared
  rather than trusted in turn.
- Anchors bar 1 from a tap on the page or a cue press on a DDJ-FLX10. Neither
  source transmits a downbeat, so somebody has to say where the bar starts; a
  cue press is only accepted from a deck whose channel fader says it is
  audible, because previewing the next track in headphones must not move the
  grid.
- Tracks the tempo and beat grid, keeps extrapolating when the source goes
  quiet, and reports that honestly instead of freezing or guessing.
- Serves a web UI with a BPM readout, a per-beat flash, the position in the
  bar, a 64 column spectrum, and connection and source health. The page
  reconnects on its own through a Flux restart or a Wi-Fi drop, and a phone
  gets the beat display without paying for the spectrum traffic.
- Streams state, beats and spectrum over a WebSocket. The wire contract is
  [docs/protocol.md](docs/protocol.md).

Not here yet: the lights themselves. Everything above exists so that the phase
which finally talks to a Hue bridge can be about the lights rather than about
whether the beat is right.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11. That floor is
  the WinRT MIDI projection the controller observer is built on, not a
  preference.
- [.NET SDK](https://dotnet.microsoft.com/download) (version pinned in
  `global.json`)
- [Node.js](https://nodejs.org/) 22 or newer, for building the frontend
- Something to find a beat in, and either one is enough: an Ableton Link peer
  on the same network, such as rekordbox with LINK enabled, or an audio input
  carrying the music, such as a DJ controller's master return

## Build and run

The build is two steps in a fixed order, because the frontend writes its
output into the backend's `wwwroot`:

```
cd src/Flux.Web.Frontend
npm install
npm run build
cd ../..
dotnet run --project src/Flux.App
```

The console prints the addresses the page is reachable on, including the one
to type on a phone. Play a track and the page picks the beat up by itself:
enable LINK in rekordbox for the Link source, or pick an audio device on the
page and switch the source to Audio. Which source is selected at startup is
`Beat:DefaultSource` below.

For frontend work with hot reload, keep the backend running and start
`npm run dev` in `src/Flux.Web.Frontend`; Vite proxies the WebSocket to the
backend.

## Configuration

`src/Flux.App/appsettings.json`:

| Key | Default | Meaning |
|---|---|---|
| `Web:Port` | `5050` | HTTP and WebSocket port for the UI |
| `Beat:DefaultSource` | `link` | Which sync source is selected at startup: `link`, `audio` or `manual`; a name this build does not have falls back to the first source registered |
| `Link:InterfaceAddress` | `null` | IP address of the network adapter to listen for Link on; `null` picks one automatically |
| `Audio:DeviceId` | `null` | Exact endpoint id of the audio device to capture; `null` goes by name instead |
| `Audio:PreferDeviceNamed` | `DDJ-FLX10` | Part of the name of the audio device to capture, inputs before outputs; empty captures nothing until one is picked |
| `Audio:Sensitivity` | `50` | How eagerly audio beat detection triggers, 1 to 100; the only tuning knob it has |
| `Audio:RecordFramesPath` | `null` | File to write analyzed audio frames to, for turning a real track into a test fixture; off unless set |
| `Audio:RecordAudioPath` | `null` | File to write the captured audio to as a mono WAVE recording, for questions about the analyzer itself rather than the detector; about 11 MB a minute, off unless set |
| `Midi:Enabled` | `true` | Whether to watch a MIDI controller, so a cue press on an audible deck can anchor the bar; observation is read-only and does not disturb rekordbox |
| `Midi:PreferDeviceNamed` | `DDJ-FLX10` | Part of the name of the MIDI input to watch; empty watches nothing, because the notes Flux reads were measured on one controller |
| `Hue:Enabled` | `true` | Whether Flux contacts a Hue bridge at all. Off gives the beat lab back unchanged, for the sessions where the question is the grid rather than the show |
| `Hue:BridgeAddress` | `null` | The bridge's address, when discovery should not be asked; `null` discovers it. Set this only where mDNS cannot reach the bridge, never as a shortcut, since DHCP moves it |
| `Console:BeatLog` | `false` | Print one console line per beat, for debugging the beat grid without a browser |
| `Console:AudioMeter` | `false` | Print the capture level four times a second, for identifying an audio device without a browser |

An `appsettings.local.json` beside it overrides any of these and is not
committed, which is what the recording keys above are usually set from: a
recording path is a fact about one machine, and the diagnostic settings worth
turning on while investigating something are rarely worth turning on for
everyone.

## Tests

The .NET test projects are xunit v3 executables:

```
dotnet run --project tests/Flux.Beat.Tests
dotnet run --project tests/Flux.Audio.Tests
dotnet run --project tests/Flux.Midi.Tests
dotnet run --project tests/Flux.Web.Server.Tests
dotnet run --project tests/Flux.Hue.Tests
```

Frontend checks, from `src/Flux.Web.Frontend`:

```
npm run check
npm test
```

## Layout

| Path | Holds |
|---|---|
| `src/Flux.Beat` | Sync source abstraction and manager, Ableton Link listener, packet decoding, manual tap source, beat grid, bar anchor, beat scheduler |
| `src/Flux.Audio` | WASAPI capture, audio device catalog, level meter, spectrum analyzer, beat detector |
| `src/Flux.Audio.Contract` | What audio looks like to the rest of Flux: the device list, its levels, and a spectrum frame |
| `src/Flux.Midi` | Read-only FLX-10 observer: MIDI message parsing, per-deck audibility, a cue press as a downbeat |
| `src/Flux.Hue` | Bridge discovery, pairing, entertainment areas, and the encrypted store the bridge keys live in. No lights yet |
| `src/Flux.Web.Server` | WebSocket hub, beat broadcaster, web hosting |
| `src/Flux.Web.Frontend` | The Svelte single-page app |
| `src/Flux.App` | The host executable |
| `docs/protocol.md` | The WebSocket wire contract; where it and anything else disagree, it wins |
| `docs/audio-constants.md` | Every number audio beat detection runs on, where it came from, and how the frequency bands are derived |
