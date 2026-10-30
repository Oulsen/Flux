# Flux - agent notes

Flux drives lights from music, built in phases. `README.md` says what works
right now; `docs/protocol.md` is the WebSocket wire contract and wins over any
other description of a message, including code comments;
`docs/audio-constants.md` is the audit trail for every number audio beat
detection runs on, and a constant in `AudioConstants` without a derivation
there is not checkable by anyone. `docs/rekordbox-chains.md` is the same for
`src/Flux.Rekordbox/chains.json`: an entry is keyed to one exact rekordbox
version, is never reused for a nearby one, and goes in only after the checks
listed there. `docs/rekordbox-anlz.md` is where the analysis-file layout was held
against a real library; what it found there beyond the published notes (signed
beat times, phrases unmasked on disk) is what the readers rely on.

That audit trail is scoped to audio beat detection and does not cover the light
engine, which is deliberate rather than an omission: the light engine's tuned
numbers were not derived, they were carried across. Eight palettes as hue
degrees, the 0.12 hue separation, 50 attempts at meeting it, the
5.0-per-second transition rate, the 240 ms default pulse and the strobe's
quarter-cycle flash all come from the
scene this project reimplements, and each is documented where it is declared -
`src/Flux.Hue/LightPalette.cs`, `LightSettings.cs` and `LightEngine.cs`. How
long a bar is is not among them: it is `Beat:Quantum`, which predates the light
engine and is Flux's own, and the engine takes its bar count from the beat grid,
which reads it, rather than restating four.
The standing rule for the carried-across material is that structure gets
rewritten while values somebody arrived at by listening get copied unchanged,
so do not re-derive one from colour theory or from a fresh reading of the
hardware: a plausible replacement would look entirely reasonable and would
quietly regress something already tuned by ear. Where the code departs from the
scene it says so and says why, and those departures are the parts open to
argument.

**Not every number in the light engine is carried across, and the difference
matters more than the numbers do.** The scene drove colour sections by index and
never compared one frequency band against another, so everything in
`src/Flux.Hue/BandDynamics.cs` is Flux's own and has no ancestor to be faithful
to - the per-band and per-room reference windows, the transient ratio, the kick
presence ramp that sizes the pulse - as are `ShimmerShare`,
`MinimumLitFraction` and `StrobeStaleBeats` in `LightEngine.cs`, and the six
stand-in lights in `LightPreview.cs`. The stage's afterimage trail in
`src/Flux.Web.Frontend/src/lib/scene.ts` is Flux's own too: the scene's trail
ran to nearly ten seconds and was cut to one by looking, and the copy count and
opacity beside it were reasoned about. These are the
opposite case to the palette: they were reasoned about rather than listened to,
they are the first things to move when the room looks wrong, and each says so
where it is declared. Treating them as carried-across material would protect
numbers nobody has ever heard.

## Building and testing

- The build is two steps in a fixed order: `npm run build` in
  `src/Flux.Web.Frontend` writes the SPA into `src/Flux.Web.Server/wwwroot`,
  then the .NET build copies it along. After frontend changes, rebuild both.
- Solution-level `dotnet test` currently reports zero tests (xunit v3 on
  Microsoft.Testing.Platform). Run the test projects directly instead:
  `dotnet run --project tests/Flux.Beat.Tests`,
  `dotnet run --project tests/Flux.Audio.Tests`,
  `dotnet run --project tests/Flux.Midi.Tests`,
  `dotnet run --project tests/Flux.Web.Server.Tests`,
  `dotnet run --project tests/Flux.Hue.Tests`,
  `dotnet run --project tests/Flux.Rekordbox.Tests`,
  `dotnet run --project tests/Flux.Rekordbox.ChainFinder.Tests` and
  `dotnet run --project tests/Flux.App.Tests`.
- Frontend checks, from `src/Flux.Web.Frontend`: `npm run check`
  (svelte-check) and `npm test` (node --test). Both must pass; `npm run check`
  is the frontend's only type gate.
- Some audio tests skip rather than fail when they find no local recording,
  and that is the intended state: they replay a capture of real music, which
  is several megabytes of somebody's music and deliberately not committed.
  A skip there is not a broken suite. Recordings are made by running with
  `Audio:RecordAudioPath` and `Audio:RecordFramesPath` set, and a new take
  needs a new filename, since both recorders truncate.
- `Flux.App` is a window application. Its log is in
  `%LOCALAPPDATA%\Flux\logs`, and `--console` (after `--` under `dotnet run`)
  prints it to the terminal too. Only one copy runs per Windows session: a
  second launch shows the first one's window and exits with code 0 at once, which
  looks like a start that quietly died. Check `tasklist` for a running `Flux.exe`
  before reading anything into that. The window's DevTools (F12) exist only in a
  Debug build; for inspecting the page in a Release build, open it in a browser.
- After C# edits are complete, run
  `dotnet format style --severity warn --verbosity quiet` (this repo opts in;
  it strips unused usings), then build, then run the tests.
- `.github/workflows/ci.yml` runs all of the above on every pull request, and
  adds three rules the local commands do not enforce: the restore is
  `--locked-mode`, so a changed package reference needs its `packages.lock.json`
  committed in the same change; the build is `-warnaserror`; and the run fails
  if the frontend build did not leave an `index.html` in
  `src/Flux.Web.Server/wwwroot`.

## Conventions

- Everything committed is written as if the repo were already public: commit
  messages, branch names, PR bodies, comments, docs. No internal chatter, no
  jokes on disk.
- Commit messages: imperative mood, specific about what changed.
- Branches: `type/short-description`, lowercase. Every change reaches
  `develop` through a PR, squash-merged; the PR body becomes the commit
  message, so write it as one. `develop` is not the default branch, so open
  each PR with `--base develop` and write `Refs #N`, not `Fixes #N`: a
  closing keyword does nothing outside the default branch.
- `main` holds releases only and is the default branch. It is never merged
  into: a release moves `main` forward to `develop`'s tip with
  `git push origin <commit>:main`, which only a fast-forward passes, so
  `main` and `develop` are the same commit after a release.
- Releasing, in order:
  1. Try the installer from `develop`'s tip. Either run
     `pwsh tools/publish-release.ps1` and then `releases\Flux-win-Setup.exe`, or push
     a test tag `vX.Y.Z-test1`, which CI turns into a prerelease draft. Delete a test
     draft and its tag afterwards.
  2. Open a release PR from `develop` to `main`, then move `main` with
     `git fetch origin && git push origin <develop tip>:main`.
  3. Tag that commit `git tag -a vX.Y.Z -m vX.Y.Z` and push the tag.
     `.github/workflows/release.yml` refuses a tag that is lightweight, not on
     `main`, or different from `<Version>`. It drafts the release with
     `Setup.exe` and the update feed attached.
  4. Install the draft's `Setup.exe`, then publish it. Installed copies only see
     published releases.
  A published version is used up: Velopack offers updates by comparing versions,
  so a number is never reused or lowered.
- Comments explain why, not what. Match the existing register: file-level
  summaries argue the design decision the code cannot show.
- Prose on disk uses " - " or a colon, never an em dash.
- The version lives only in `Directory.Build.props`, bumped by hand, one minor
  per completed phase. The frontend `package.json` stays at `0.0.0` on
  purpose.
- When the version bumps, review `README.md` and this file in the same change:
  each bump is a phase completing, which changes what a user can do and what
  an agent needs to know here.
- Any protocol change updates `docs/protocol.md` in the same change, and bumps
  the protocol version when an existing client could misread the change.

## Working notes

If a `.plan/` folder exists locally, it holds gitignored working notes: read
`.plan/PROGRESS.md` first and update it last (dated log entry, absolute
dates). It is intentionally never committed; do not reference its contents in
committed text.
