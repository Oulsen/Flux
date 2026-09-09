# Flux - agent notes

Flux drives lights from music, built in phases. `README.md` says what works
right now; `docs/protocol.md` is the WebSocket wire contract and wins over any
other description of a message, including code comments;
`docs/audio-constants.md` is the audit trail for every number audio beat
detection runs on, and a constant in `AudioConstants` without a derivation
there is not checkable by anyone.

## Building and testing

- The build is two steps in a fixed order: `npm run build` in
  `src/Flux.Web.Frontend` writes the SPA into `src/Flux.Web.Server/wwwroot`,
  then the .NET build copies it along. After frontend changes, rebuild both.
- Solution-level `dotnet test` currently reports zero tests (xunit v3 on
  Microsoft.Testing.Platform). Run the test projects directly instead:
  `dotnet run --project tests/Flux.Beat.Tests`,
  `dotnet run --project tests/Flux.Audio.Tests`,
  `dotnet run --project tests/Flux.Midi.Tests`,
  `dotnet run --project tests/Flux.Web.Server.Tests` and
  `dotnet run --project tests/Flux.Hue.Tests`.
- Frontend checks, from `src/Flux.Web.Frontend`: `npm run check`
  (svelte-check) and `npm test` (node --test). Both must pass; `npm run check`
  is the frontend's only type gate.
- Some audio tests skip rather than fail when they find no local recording,
  and that is the intended state: they replay a capture of real music, which
  is several megabytes of somebody's music and deliberately not committed.
  A skip there is not a broken suite. Recordings are made by running with
  `Audio:RecordAudioPath` and `Audio:RecordFramesPath` set, and a new take
  needs a new filename, since both recorders truncate.
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
  message, so write it as one.
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
