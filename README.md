# Flux

Music in, light out.

Flux is a standalone Windows app being built, in phases, to drive Philips Hue
Entertainment lights from music with proper beat sync. It finds the beat,
either from an Ableton Link session or by listening to the music itself, and
drives a Hue entertainment area from it at 50 frames a second. What it found
stays visible in any browser on the LAN, phone included, which is still the
only way to tell that a beat grid is right - and now also the only way to
tune the light against it, because the lag between a beat and a bulb is
measured by somebody standing in the room watching both.

## What works in 0.5.0

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
  grid. Under the manual source a tap is also a tempo press, so a separate
  anchor button sets the downbeat and leaves an already-right tempo alone.
- Tracks the tempo and beat grid, keeps extrapolating when the source goes
  quiet, and reports that honestly instead of freezing or guessing.
- Finds a Hue bridge and pairs with it from the page. Discovery tries mDNS and
  SSDP together, then a local network scan, then the rate-limited cloud lookup,
  and remembers where the bridge last answered. Pairing then asks the bridge
  once a second for a minute, so the "link button not pressed" refusal the
  bridge returns until somebody presses it (error 101) reads as a countdown on
  screen rather than as an error, which matters when the bridge is in a cupboard
  and the phone is in your hand.
- Lists the bridge's entertainment areas and streams to the one you pick, with
  a pre-flight for the case that decides most failed sessions: a bridge serves
  one entertainment stream at a time, so an area Hue Sync is already driving is
  named as busy before anything is opened rather than after the handshake dies.
- Holds an encrypted DTLS stream open at 50 frames a second and reports the
  rate it actually achieved, measured over the last second rather than repeated
  back from the setting. A bulb renders roughly 25 updates a second over Zigbee
  whatever the stream does, so the two numbers are different layers and both
  are worth seeing.
- Turns the music into light, per bulb. A hue is chosen every few bars and lands
  on the downbeat, sliding rather than jumping to it; brightness is knocked up on
  every beat and fades. Each light follows the part of the spectrum nearest its
  own position in the room, so a kick punches in the middle and a hi-hat lifts the
  corners, and each band is read against its own recent behaviour rather than
  against the others - otherwise the kick wins every comparison on every frame and
  the room is a fixed gradient. A beat with no kick under it does not pulse at all,
  which is what leaves a breakdown to the bands that are still playing. The room
  also breathes with the section: a passage running hotter than the room has been
  lifts what the lights fall back to between beats, so a drop and a verse no
  longer look the same.
- Eight palettes to draw from, one to three colours at once and how to spread them
  across the room, a brightness ceiling, a pulse length, how far the light drops
  between beats, and a latency offset to line the light up against the music - all
  of them owned by the page, changeable while the music plays, and saved for next
  time.
- A white strobe on the beat grid, off by default: switched on in the drawer to
  run hands-free, or held from a button on the stage for as long as a finger is
  on it. A hold ends on its own if the page goes away mid-press, so a phone that
  falls asleep cannot leave the room strobing.
- Serves a web UI built around the visualizer, which fills the window: the BPM,
  the position in the bar, the anchor and strobe buttons, the achieved frame
  rate and the connection and source health sit over it, and everything
  adjustable is behind a drawer, in three tabs: what is reached for with music
  playing, the look of the stage, and the one-time setup. Each device remembers
  the tab it left open. The page reconnects on its own through a Flux
  restart or a Wi-Fi drop, and a phone that would rather not draw a 64 column
  spectrum thirty times a second can switch to numbers only, which it then
  remembers.
- Shows the look before the room is lit: with light sync off the visualizer is
  drawn in the colours the light engine would send, laid out as the selected
  room's lights, so palettes and colour splits can be judged with the bulbs off
  or with no bridge at all. The stage says so while it is a preview.
- Streams state, beats, spectrum and bridge status over a WebSocket. The wire
  contract is [docs/protocol.md](docs/protocol.md).

Flux runs as a desktop window showing the same page, with an icon in the
notification area. Closing the window hides it and the lights keep going; Quit on
the tray icon stops Flux. Launching it again while it runs brings the window back
rather than starting a second copy, it opens where it was left, and F11 puts the
stage on the whole screen. The window shows Flux's own page and nothing else: a
link that leads anywhere else opens in the default browser. The QR code that
gets a phone onto the page without typing an address has moved to Phase 7.

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
- A Philips Hue bridge on the same network with at least one entertainment area
  set up on it, and nothing else streaming to that area. Entertainment areas are
  created in the Hue app, not in Flux, and a bridge serves one stream at a time.
  Without a bridge, `Hue:Enabled` below turns the lights off and leaves the beat
  side working on its own

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

A window opens on the page. The addresses it is reachable on, including the one
to type on a phone, are in the log: `%LOCALAPPDATA%\Flux\logs`, one file per
day, kept for two weeks. `dotnet run --project src/Flux.App -- --console` shows
the log in the terminal as well, and is what the `Console:` settings below need.
The window needs the Microsoft Edge WebView2 Runtime, which Windows 11 ships
with; without it Flux opens the page in the default browser and runs from the
tray. Play a track and the page picks the beat up by itself:
enable LINK in rekordbox for the Link source, or pick an audio device on the
page and switch the source to Audio. Whichever source you pick is remembered, so
`Beat:DefaultSource` below only decides the first run.

The lights are set up from the same page, once: find the bridge, press its link
button while the countdown runs, pick an entertainment area and start the
stream. The pairing survives a restart, so every session after the first one
starts at picking the area.

For frontend work with hot reload, keep the backend running and start
`npm run dev` in `src/Flux.Web.Frontend`; Vite proxies the WebSocket to the
backend.

## Configuration

`src/Flux.App/appsettings.json`:

| Key | Default | Meaning |
|---|---|---|
| `Web:Port` | `5050` | HTTP and WebSocket port for the UI |
| `Beat:DefaultSource` | `link` | Which sync source is selected at startup: `link`, `audio`, `manual` or `rekordbox`. Only where a source has never been chosen on the page: one chosen there is remembered and outranks this from then on. A name this build does not have falls back to the first source registered |
| `Beat:Quantum` | `4` | Beats in a bar: what the 1-2-3-4 readout counts against, and how long a bar is when the light engine changes colour every few bars. Neither source transmits a meter, so nothing confirms or contradicts it; an anchor moves where the bar starts, never how long it is |
| `Link:InterfaceAddress` | `null` | IP address of the network adapter to listen for Link on; `null` picks one automatically |
| `Audio:DeviceId` | `null` | Exact endpoint id of the audio device to capture; `null` goes by name instead. Only where a device has never been picked on the page: a device chosen there is remembered and outranks this from then on |
| `Audio:PreferDeviceNamed` | `DDJ-FLX10` | Part of the name of the audio device to capture, inputs before outputs; empty captures nothing until one is picked. Still the fallback when the remembered or configured id names a device that is not plugged in |
| `Audio:Sensitivity` | `50` | How eagerly audio beat detection triggers, 1 to 100; the only tuning knob it has |
| `Audio:RecordFramesPath` | `null` | File to write analyzed audio frames to, for turning a real track into a test fixture; off unless set |
| `Audio:RecordAudioPath` | `null` | File to write the captured audio to as a mono WAVE recording, for questions about the analyzer itself rather than the detector; about 11 MB a minute, off unless set |
| `Midi:Enabled` | `true` | Whether to watch a MIDI controller, so a cue press on an audible deck can anchor the bar; observation is read-only and does not disturb rekordbox |
| `Midi:PreferDeviceNamed` | `DDJ-FLX10` | Part of the name of the MIDI input to watch; empty watches nothing, because the notes Flux reads were measured on one controller |
| `Rekordbox:Enabled` | `true` | Whether to read rekordbox's memory and analysis files for the master deck's tempo, beat and bar. Read-only, and on whichever source is selected: with Link selected, rekordbox still sets bar 1. A rekordbox that is not running, or is a version Flux has no chains for, is a source that says why on the page |
| `Hue:Enabled` | `true` | Whether Flux contacts a Hue bridge at all. Off gives the beat lab back unchanged, for the sessions where the question is the grid rather than the show |
| `Hue:BridgeAddress` | `null` | The bridge's address, when discovery should not be asked; `null` discovers it. Discovery tries mDNS and SSDP together, then a local network scan, then the rate-limited cloud lookup, and remembers where the bridge last answered - so this is for the network none of that can cross, never a shortcut, since DHCP moves a bridge |
| `Hue:StreamHz` | `50` | Frames a second sent to the bridge while streaming, 10 to 60. 50 is the official guidance; a bulb renders roughly 25 updates a second over Zigbee regardless, so raising this cannot make the lights smoother |
| `Hue:LatencyOffsetMs` | `0` | Milliseconds to shift light against the beat as a session starts, from -500 to 500; positive sends it early. Only the starting value: the page owns it from there, because the instrument that measures it is somebody watching the room |
| `Console:BeatLog` | `false` | Print one console line per beat, for debugging the beat grid without a browser. Needs `--console` |
| `Console:AudioMeter` | `false` | Print the capture level four times a second, for identifying an audio device without a browser. Needs `--console` |

The light engine's own settings - palette, how often the colour changes, how many
colours at once and how they are spread, brightness, pulse length and pulse depth -
are deliberately not here. They are
taste rather than installation, they are adjusted while the music is playing,
and they belong to whoever is standing at the page; they persist across
restarts in `%APPDATA%\Flux\hue-settings.json`, alongside the chosen
entertainment area. The bridge key is not in there: it lives encrypted beside
it, readable only by the Windows account that paired. `Hue:LatencyOffsetMs` is
the one setting that goes both ways, and that is the point of it - the lag is a
property of a rig worth pinning, while the page still owns it during a set.

How the visualizer draws - the layout, the bar shape, the colour style, how many
bands the spectrum is divided into and how long it trails - is shared the same way, in
`%APPDATA%\Flux\stage-settings.json`. It lives on the server rather than in each
browser because the look of the stage is part of the show and should be the same on
the laptop and on the phone in somebody's hand; the band count could never have been
per-screen anyway, since it decides how many columns the analyzer produces. The one
thing each device keeps to itself is whether it draws the visualizer at all.

The audio device works the same way, in `%APPDATA%\Flux\audio-settings.json`.
Picking one on the page is remembered, so a rig whose capture device is not the
one `Audio:PreferDeviceNamed` describes is set up once rather than at the start
of every session. A remembered device that is not plugged in falls back to the
name preference rather than capturing nothing, because the file can easily
outlive the interface - which is exactly the difference between it and a device
chosen from the page while Flux is running, where capturing something other
than what was just asked for would be overruling somebody.

The sync source is remembered the same way, in `%APPDATA%\Flux\beat-settings.json`,
and the reason is a failure worth naming: Link publishes a timeline and nothing
else, so a restart that put the grid back on Link with rekordbox closed produced
no beats at all - and a room whose lights sit perfectly still looks like a broken
light engine rather than like a source nobody selected. A remembered source this
build no longer has falls back to `Beat:DefaultSource`.

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
dotnet run --project tests/Flux.Rekordbox.Tests
dotnet run --project tests/Flux.Rekordbox.ChainFinder.Tests
dotnet run --project tests/Flux.App.Tests
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
| `src/Flux.Audio.Contract` | What audio looks like to the rest of Flux: the device list, its levels, a spectrum frame, the band energies the light engine follows, and how many columns the visualizer wants it in |
| `src/Flux.Midi` | Read-only FLX-10 observer: MIDI message parsing, per-deck audibility, a cue press as a downbeat |
| `src/Flux.Rekordbox` | Read-only access to a running rekordbox: finds the process, opens it for reading only, and reads each deck's tempo, playhead, analysis file and the master deck through a table of pointer chains per rekordbox version (7.2.19 so far), and each track's beatgrid and phrases from its analysis files; and the `rekordbox` sync source built on them, which follows the master deck and sets bar 1 from its grid |
| `src/Flux.Hue` | Bridge discovery, pairing, entertainment rooms, the 50 Hz light stream, the light engine that turns beats into a colour per light, the preview that runs it while nothing streams, the room-to-frequency mapping behind it, and the encrypted store the bridge keys live in |
| `src/Flux.Web.Server` | WebSocket hub, beat, spectrum, device, bridge and stage broadcasters, the stage's own settings and their store, web hosting |
| `src/Flux.Web.Frontend` | The Svelte single-page app |
| `src/Flux.App` | The host executable and its desktop shell: the window, the tray icon, one running copy, and the log file |
| `assets/branding` | The Flux mark as SVG, one cut per size band, and the script that builds `flux.ico` from them |
| `tools/Flux.Rekordbox.ChainFinder` | A desk tool that finds the pointer chains for a new rekordbox version and appends its entry to `chains.json`; see `docs/rekordbox-chains.md` |
| `docs/protocol.md` | The WebSocket wire contract; where it and anything else disagree, it wins |
| `docs/rekordbox-chains.md` | The rekordbox chain table's rules, format and evidence per version, and the checks a new version's entry has to pass |
| `docs/rekordbox-anlz.md` | The layout of the rekordbox analysis files Flux reads, the beatgrid and the phrases, and what checking it against a real library found |
| `docs/audio-constants.md` | Every number audio beat detection runs on, where it came from, and how the frequency bands are derived |
