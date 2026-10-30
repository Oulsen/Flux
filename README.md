# Flux

Music in, light out.

Flux is a Windows app that drives Philips Hue Entertainment lights from music,
in time with the beat. It finds the beat from rekordbox, from an Ableton Link
session, or by listening to the music, and streams light to a Hue entertainment
area at 50 frames a second.

Everything is controlled from a web page, in the Flux window or in any browser
on the network, phone included. That page is also where the light is tuned
against the music, because the lag between a beat and a bulb can only be judged
by somebody standing in the room.

Flux is being built in phases; this README describes what works now.

## Install

Download `Flux-win-Setup.exe` from the latest
[release](https://github.com/Oulsen/Flux/releases) and run it. It installs for the
current user, into `%LOCALAPPDATA%\Flux`, without asking for administrator rights,
and adds Flux to the Start menu.

- **SmartScreen.** The installer is not code-signed, so Windows warns about it the
  first time: choose "More info", then "Run anyway".
- **Firewall.** On the first start Windows asks whether Flux may use the network.
  Allow it on private networks: that is how a phone reaches the page and how Flux
  hears an Ableton Link session.
- **WebView2.** The window needs the Microsoft Edge WebView2 Runtime, which Windows
  11 ships with. Without it Flux opens the page in the default browser and runs
  from the tray.

You also need:

- Windows 10 version 2004 (build 19041) or newer, or Windows 11. That floor comes
  from the WinRT MIDI API the controller observer uses.
- A beat to follow, any one of: rekordbox 7.2.19 on the same machine, an Ableton
  Link peer on the network (such as rekordbox with LINK on), or an audio input
  carrying the music (such as a DJ controller's master return).
- A Philips Hue bridge on the network with at least one entertainment area,
  created in the Hue app. Without one, set `Hue:Enabled` to `false` (see
  [Configuration](#configuration)) and the beat side runs on its own.

## First run

1. Pick a beat source on the page: enable LINK in rekordbox, or pick an audio
   device and switch to Audio. The choice is remembered.
2. Find the bridge, press its link button while the countdown runs, and pick an
   entertainment area. Pairing survives restarts.
3. Start light sync.

To control Flux from a phone, open `http://<this PC's address>:5050` on the same
network. The log lists the addresses to try.

## Updates

Flux looks for a newer release a minute after start and every two hours after
that, and downloads it in the background. The drawer then offers to restart into
it, which stops the lights for a few seconds. If nobody does, it is applied the
next time Flux quits. `Updates:Enabled` turns this off.

To uninstall, use Windows Settings, Apps. Settings in `%APPDATA%\Flux` stay
behind; delete that folder as well for a clean slate.

**Logs** are in `%LOCALAPPDATA%\Flux\logs`, one file a day, kept two weeks.

## What works in 0.6.0

### Finding the beat

Three sources, switchable live from the page. Each reports its own tempo,
confidence and health, so they can be compared rather than trusted blindly.

- **rekordbox** - follows the master deck's tempo and playhead, read-only, and
  takes bar 1 from the track's own beatgrid, so the bar is right without a tap
  and follows a deck switch. Tied to one rekordbox version at a time (7.2.19 so
  far, see [docs/rekordbox-chains.md](docs/rekordbox-chains.md)); on any other
  version the source says so and stays silent.
- **Ableton Link** - rekordbox, Ableton Live or any Link peer. Flux listens
  without joining, so it never shows up as a peer or influences the session.
- **Audio** - analyses the sound itself: the only source that reports what is
  actually coming out of the speakers.

Around them:

- **Phrases.** With rekordbox, a new phrase (Intro, Chorus, Down) changes the
  colour on the track's structure rather than a bar or three off it, and the
  phrase playing is named on the stage. Works under Link too; a switch turns it
  off.
- **Setting bar 1.** Link and audio carry no downbeat, so it comes from a tap on
  the page or a cue press on a DDJ-FLX10. A cue press only counts from a deck
  whose fader is up, so previewing in headphones does not move the grid. Under
  rekordbox the page offers to shift a misplaced grid by a beat instead.
- **Dropouts.** When a source goes quiet the grid keeps running and says so,
  rather than freezing or guessing.

### Driving the lights

- **Setup from the page.** Flux finds the bridge (mDNS, SSDP, a network scan,
  then the Hue cloud lookup) and remembers where it was. Pairing shows a
  countdown for the link button. An area that Hue Sync is already streaming to
  is flagged as busy before anything connects, since a bridge serves one stream
  at a time.
- **A steady stream.** An encrypted 50 Hz stream, with the rate actually
  achieved shown on screen. Bulbs render about 25 updates a second over Zigbee
  whatever the stream does.
- **Per-bulb light.** A new hue every few bars, landing on the downbeat.
  Brightness pulses on each beat and fades. Each bulb follows the part of the
  spectrum nearest its place in the room, so a kick punches in the middle and a
  hi-hat lifts the corners. A beat without a kick under it does not pulse, which
  leaves breakdowns to whatever is still playing.
- **Tuning while it plays.** Eight palettes, one to three colours and how they
  spread, brightness, pulse length and depth, and a latency offset. All of it is
  saved for next time.
- **Strobe.** White, on the beat grid, off by default. Switch it on to run hands
  free, or hold the button on the stage. A hold ends by itself if the page goes
  away, so a phone falling asleep cannot leave the room strobing.

### The page

- The visualizer fills the window, with tempo, bar position, the anchor and
  strobe buttons and the connection health over it.
- The beat source and the palette are one tap away from the corner reading.
  Everything else is in a drawer with three tabs: **lights**, **screen** and
  **setup**.
- With light sync off, the stage previews the look in the colours the lights
  would get, laid out like the room, so palettes can be judged with no bulbs on
  or no bridge at all.
- A phone can switch the visualizer off and keep the numbers.
- The page reconnects by itself through a Flux restart or a Wi-Fi drop.
- State, beats, spectrum and bridge status travel over a WebSocket, specified in
  [docs/protocol.md](docs/protocol.md).

### The window

Flux runs as a desktop window with a tray icon.

- Closing the window hides it; the lights keep going. Quit from the tray icon.
- Launching Flux again brings the running window back instead of starting a
  second copy.
- F11 puts the stage on the whole screen.
- Links to anything other than Flux's own page open in the default browser.

## Configuration

Installation settings come from `appsettings.json` beside the executable, and
from an `appsettings.local.json` that overrides it. Put that file in
`%APPDATA%\Flux`, beside the settings the page saves: an update replaces the
install folder, and a file kept there would go with it. The command line
overrides both for one run, for example `--Hue:Enabled=false`.

```json
{
  "Hue": { "Enabled": false }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Web:Port` | `5050` | HTTP and WebSocket port for the page. A build run from source uses 5051 unless this is set in `appsettings.local.json` or on the command line |
| `Beat:DefaultSource` | `link` | Source on first run: `link`, `audio`, `manual` or `rekordbox`. Once one is picked on the page, that choice wins |
| `Beat:Quantum` | `4` | Beats in a bar. rekordbox counts in fours, so any other value logs a warning at startup |
| `Link:InterfaceAddress` | `null` | Network adapter to listen for Link on; `null` picks one |
| `Audio:DeviceId` | `null` | Exact endpoint id of the device to capture; `null` goes by name. A device picked on the page wins |
| `Audio:PreferDeviceNamed` | `DDJ-FLX10` | Part of the capture device's name, inputs before outputs; empty captures nothing until one is picked |
| `Audio:Sensitivity` | `50` | How eagerly audio beat detection triggers, 1 to 100 |
| `Audio:RecordFramesPath` | `null` | Write analysed audio frames to this file, to turn a track into a test fixture |
| `Audio:RecordAudioPath` | `null` | Write the captured audio to this mono WAVE file; about 11 MB a minute |
| `Midi:Enabled` | `true` | Watch a MIDI controller for cue presses, read-only |
| `Midi:PreferDeviceNamed` | `DDJ-FLX10` | Part of the MIDI input's name; empty watches nothing, since the notes were measured on one controller |
| `Rekordbox:Enabled` | `true` | Read rekordbox for the master deck's tempo, beat and bar. With Link selected, rekordbox still sets bar 1 |
| `Hue:Enabled` | `true` | Contact a Hue bridge at all. Off leaves only the beat side |
| `Hue:BridgeAddress` | `null` | Skip discovery and use this address. Only for networks discovery cannot cross, since DHCP can move a bridge |
| `Hue:StreamHz` | `50` | Frames a second to the bridge, 10 to 60. Higher does not look smoother: bulbs render about 25 a second |
| `Hue:LatencyOffsetMs` | `0` | Starting light offset, -500 to 500 ms; positive sends light early. The page owns it from there |
| `Updates:Enabled` | `true` | Look for newer releases. Only an installed copy can update; a build run from source never checks |
| `Console:BeatLog` | `false` | One console line per beat. Needs `--console` |
| `Console:AudioMeter` | `false` | Capture level four times a second. Needs `--console` |

### Saved from the page

What is tuned while the music plays belongs to the page rather than to a settings
file, and is saved in the same folder, `%APPDATA%\Flux\`:

| File | Holds |
|---|---|
| `hue-settings.json` | Palette, colour changes, brightness, pulse, latency offset and the chosen entertainment area. The bridge key sits beside it, encrypted for the Windows account that paired |
| `stage-settings.json` | How the visualizer draws. Shared by every screen, since the look of the stage is part of the show; only whether a device draws it at all stays per device |
| `audio-settings.json` | The capture device. If it is unplugged, Flux falls back to `Audio:PreferDeviceNamed` |
| `beat-settings.json` | The sync source, so a restart does not silently put the grid back on a source with nothing playing |

## rekordbox

Flux is an independent project, not affiliated with or endorsed by AlphaTheta.
rekordbox is a trademark of AlphaTheta Corporation.

The rekordbox source reads, and never writes:

- the running rekordbox's memory, on the same machine, for the master deck's
  tempo, playhead and analysis file;
- the analysis files rekordbox writes for each track, for its beatgrid and
  phrases.

It does not modify rekordbox, does not open its encrypted library database, and
gets around no protection. It needs nothing from the network. `Rekordbox:Enabled`
turns it off.

## Development

Building needs the [.NET SDK](https://dotnet.microsoft.com/download), version
pinned in `global.json`, and [Node.js](https://nodejs.org/) 22 or newer for the
frontend. The frontend builds into the backend's `wwwroot`, so build it first:

```
cd src/Flux.Web.Frontend
npm install
npm run build
cd ../..
dotnet run --project src/Flux.App
```

**A build run from source is kept apart from an installed Flux.** It is called
"Flux (dev)", keeps its settings, bridge key and `appsettings.local.json` in
`%APPDATA%\Flux-dev` and its log in `%LOCALAPPDATA%\Flux-dev\logs`, and serves
the page on port 5051. Both can run at once, but only one can stream to the
bridge, and a development build has to be paired once on its own. An
`appsettings.local.json` beside `src/Flux.App/appsettings.json` works too.

`dotnet run --project src/Flux.App -- --console` prints the log to the terminal
as well.

**Frontend work with hot reload:** keep the backend running and run `npm run
dev` in `src/Flux.Web.Frontend`; Vite proxies the WebSocket through.

**Releases** are drafted by `.github/workflows/release.yml` from a version tag,
using `tools/publish-release.ps1`, which also builds the installer locally.

### Tests

```
dotnet run --project tests/Flux.Beat.Tests
dotnet run --project tests/Flux.Audio.Tests
dotnet run --project tests/Flux.Midi.Tests
dotnet run --project tests/Flux.Web.Server.Tests
dotnet run --project tests/Flux.Hue.Tests
dotnet run --project tests/Flux.Rekordbox.Tests
dotnet run --project tests/Flux.Rekordbox.ChainFinder.Tests
dotnet run --project tests/Flux.App.Tests
```

The .NET test projects are xunit v3 executables, run directly. Frontend checks,
from `src/Flux.Web.Frontend`:

```
npm run check
npm test
```

### Layout

| Path | Holds |
|---|---|
| `src/Flux.Beat` | Sync sources and their manager, the Link listener, the manual tap source, beat grid, bar anchor and scheduler |
| `src/Flux.Audio` | WASAPI capture, device catalog, level meter, spectrum analyser, beat detector |
| `src/Flux.Audio.Contract` | What audio looks like to the rest of Flux: devices, levels, spectrum frames and band energies |
| `src/Flux.Midi` | Read-only FLX-10 observer: message parsing, deck audibility, cue press as downbeat |
| `src/Flux.Rekordbox` | Read-only rekordbox access: the process, per-version pointer chains, analysis files, and the `rekordbox` sync source |
| `src/Flux.Hue` | Bridge discovery and pairing, entertainment areas, the 50 Hz stream, the light engine and its preview, and the encrypted key store |
| `src/Flux.Web.Server` | WebSocket hub, broadcasters, stage settings, web hosting |
| `src/Flux.Web.Frontend` | The Svelte single-page app |
| `src/Flux.App` | The host executable: window, tray icon, single instance, log file |
| `assets/branding` | The Flux mark as SVG and the script that builds `flux.ico` |
| `tools/publish-release.ps1` | Builds the installer and update feed; the release workflow runs the same script |
| `tools/Flux.Rekordbox.ChainFinder` | Desk tool that finds the pointer chains for a rekordbox version; see `docs/rekordbox-chains.md` |
| `docs/protocol.md` | The WebSocket wire contract; where it and anything else disagree, it wins |
| `docs/rekordbox-chains.md` | The chain table's rules, format and per-version evidence |
| `docs/rekordbox-anlz.md` | The analysis-file layout Flux reads, checked against a real library |
| `docs/audio-constants.md` | Every number audio beat detection runs on, and where it came from |

## License

MIT, see `LICENSE`.
