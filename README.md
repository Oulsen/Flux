# Flux

Music in, light out.

Flux is a standalone Windows app being built, in phases, to drive Philips Hue
Entertainment lights from music with proper beat sync. Today it does the
foundation of that: it follows an Ableton Link session on the local network and
shows a live beat display in any browser on the LAN, phone included.

## What works in 0.1.0

- Follows an Ableton Link session (rekordbox, Ableton Live, or any Link peer)
  passively: Flux listens without joining, so it never appears as a peer and
  never influences the session.
- Tracks the tempo and beat grid, keeps extrapolating when the source goes
  quiet, and reports that honestly instead of freezing or guessing.
- Serves a web UI with a BPM readout, a per-beat flash, the position in the
  bar, and connection and source health. The page reconnects on its own
  through a Flux restart or a Wi-Fi drop.
- Streams state and beats over a WebSocket. The wire contract is
  [docs/protocol.md](docs/protocol.md).

Not here yet: audio-analysis beat detection, bar anchoring, and the lights
themselves. The bar position counts at the right rate but does not know where
beat 1 is: Ableton Link carries no downbeat, so a later phase anchors it from a
cue press or a tap.

## Requirements

- Windows 10/11
- [.NET SDK](https://dotnet.microsoft.com/download) (version pinned in
  `global.json`)
- [Node.js](https://nodejs.org/) 22 or newer, for building the frontend
- An Ableton Link peer on the same network, such as rekordbox with LINK
  enabled

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
to type on a phone. Enable LINK in rekordbox and play a track; the page picks
the session up by itself.

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
| `Console:BeatLog` | `false` | Print one console line per beat, for debugging the beat grid without a browser |
| `Console:AudioMeter` | `false` | Print the capture level four times a second, for identifying an audio device without a browser |

## Tests

The .NET test projects are xunit v3 executables:

```
dotnet run --project tests/Flux.Beat.Tests
dotnet run --project tests/Flux.Audio.Tests
dotnet run --project tests/Flux.Web.Server.Tests
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
| `src/Flux.Web.Server` | WebSocket hub, beat broadcaster, web hosting |
| `src/Flux.Web.Frontend` | The Svelte single-page app |
| `src/Flux.App` | The host executable |
| `docs/protocol.md` | The WebSocket wire contract; where it and anything else disagree, it wins |
| `docs/audio-constants.md` | Every number audio beat detection runs on, where it came from, and how the frequency bands are derived |
