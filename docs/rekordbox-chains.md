# rekordbox pointer chains

`src/Flux.Rekordbox/chains.json` holds, per rekordbox version, where in rekordbox's
memory Flux finds each deck's tempo, playhead and analysis file, and which deck is
master. This file is what each entry rests on. An entry without a section here is not
checkable by anyone.

## The rule: one entry per exact version

Flux reads rekordbox only when the running `rekordbox.exe` has an entry for its exact
version, taken from the executable's file version. There is no "closest known version":
a newer rekordbox with no entry is read not at all, and the source reports no signal
while Link, audio or a tap carry on.

That is deliberate. Chains move between versions, and a chain from one version walked
in another does not reliably break. It can land on a plausible value in the wrong
structure. While the 7.2.19 chains were being found, one candidate read both decks'
tempo correctly through two rekordbox restarts, then read deck 2 twice as soon as
deck 2 started playing. Silence is recoverable. A believable wrong beat grid is not.

The table starts at 7.2.19. Each rekordbox update gets its own entry once its chains
meet the checks below.

## Format

```json
{
  "version": "7.2.19",
  "decks": 4,
  "bpm": { "chain": "5E09790 0 2C0 1A0", "deckHop": 1, "deckStride": 8 },
  "samplePosition": { "chain": "5E09790 0 2C0 130", "deckHop": 1, "deckStride": 8 },
  "anlzPath": { "chain": "5F53DA8 8 3F0 0", "deckHop": 1, "deckStride": 8 },
  "masterDeck": { "chain": "61A82C0 3A0 378 254" }
}
```

- A chain is hex offsets in Cheat Engine order. The first offset is added to the base
  of `rekordbox.exe`, and each later one is added to the pointer read at the address so
  far. The last one is not dereferenced: the result is the address of the value.
- `version` has three parts. The executable's fourth part has always been 0, and a
  build where it is not counts as unknown.
- A deck chain is written for deck 1. Deck *n* (0-based) adds `n * deckStride` to the
  offset at index `deckHop`. Both fields are required, because a missing hop read as 0
  would move the module offset and every later deck would read another static.
- Values: `bpm` is a 32-bit float, `samplePosition` a 64-bit integer, `anlzPath` a
  NUL-terminated UTF-8 path to the track's `ANLZ0000.DAT`, and `masterDeck` one byte,
  0-based.
- The table is checked when it loads, and unknown fields are an error.

## Finding a new version's chains

`tools/Flux.Rekordbox.ChainFinder` does the search: `dotnet run --project
tools/Flux.Rekordbox.ChainFinder`, with rekordbox open, LINK off, every deck's pitch at
0 (so each deck's tempo is its track's own), 4-deck mode to cover four decks, and an
analysed track on every deck, with different BPMs on decks 1 and 2. It reads rekordbox
the same read-only way Flux does. Nothing is typed in: the tool says which deck to play
and which to make master, and when to restart, and asks for a yes when the tempos it
reads match the screen. Every value keeps all of its candidates until every check below
has run on them, because the candidate that looks best in one session is not reliably
the one that survives the next.

The ANLZ path is found first, because it needs nothing from the desk, and it is told
apart from rekordbox's library, which keeps hundreds of the same kind of path behind
chains of the same shape, by the file itself. The tempos in the candidate files are what
the tempo search looks for, and a tempo chain and an ANLZ chain are kept only when they
agree: the chain reads a tempo the file carries. The two are found in different ways, a
float in memory and a file on disk, so a wrong one of either needs a wrong one of the
other to get through. When the candidate files carry too many tempos to search for at
once, the tool asks for the two on screen instead.

For a new version the tool appends the entry to `chains.json`. For a version already in
the table it compares the entry with what it found on every deck, keeps each chain that
reads the same (its evidence is here), and offers to replace the ones that differ,
listing old and new, only on a yes. When only an entry's master deck has stopped
reading, `--master` skips the ANLZ, tempo and playhead searches, takes those chains from
the entry, and runs the master rounds and the restarts alone. Either way it writes only
after checking that Flux would load the result. After it writes, build, run the tests, and add the evidence here
by hand.

## What an entry has to survive

A chain goes in the table only when it has done all of these. Each check exists
because a chain that skipped it was wrong:

1. **Distinct values per deck.** Load tracks with different tempos, so a chain reading
   the same structure for two decks cannot pass.
2. **Every change that moves the value.** Tempo alone is not enough. Change which deck
   is playing and which is master, separately from each other. The candidate described
   above failed only when the playing deck changed.
3. **Three rekordbox restarts, with different tracks each time.** Most candidates that
   pass within a session break at the first: 22 of 24 tempo candidates did, and a
   restart plus one master switch left 2 of 120 master candidates. The first 7.2.19
   master chain passed two and failed on the third, so the tool asks for at least three.
4. **All four decks**, in 4-deck mode, including master values 2 and 3.
5. **Against something outside memory.** The ANLZ path has to lead to the file for the
   track on that deck, and the playhead in seconds has to match rekordbox's screen.

## Units

`samplePosition` counts in 44.1 kHz units whatever the file's own rate. Checked on
7.2.19 with a 44.1 kHz FLAC (545545 at 00:12.3 on screen, 12.37 s at 44100) and a
48 kHz MP3 (641827 at 00:14.5 on screen: 14.55 s at 44100, 13.37 s at 48000).
Seconds are `samplePosition / 44100`.

## 7.2.19

Found 2026-09-25 on Windows 11, rekordbox 7.2.19.0, in Performance mode.

| Value | Chain | Evidence |
|---|---|---|
| `bpm` | `5E09790 d*8 2C0 1A0` | Distinct tempos on four decks (87, 87, 174, 172), read correctly after a restart with new tracks |
| `samplePosition` | `5E09790 d*8 2C0 130` | Moves only on the playing deck, resets on cue, matches the screen in seconds (see Units) |
| `anlzPath` | `5F53DA8 8+d*8 3F0 0` | Three sessions with different tracks, four decks; each file's `PPTH` names the track on that deck |
| `masterDeck` | `61A82C0 3A0 378 254` | Followed four master changes with the playing deck varied separately, then two after a restart, then values 2 and 3 in 4-deck mode. Reads 255 while LINK is on, when Link is rekordbox's master and no deck is |

Compared with the 7.2.2 chains published by the rkbx_link project (used as facts, not
code): the tempo and playhead chains kept their shape with one offset moved (`2B0` to
`2C0`), the ANLZ chain kept its shape with only the base moved, and the master deck
chain changed completely. So expect a new search with every update, not a shift.

**The `masterDeck` chain is not stable.** On 2026-09-25 it failed after a third
rekordbox restart: the static and its first pointer still resolved, but the object
they lead to held data rather than a pointer at `+3A0`, so the chain landed nowhere.
The same session read `bpm`, `samplePosition` and `anlzPath` correctly. It passed two
restarts during the search, which is the check it was held to, and that was not
enough. Until a better chain is found with the chain finder, Flux treats a master that
cannot be read like one that names no deck: it follows the deck that is playing. That
is right with one deck playing and a reasoned guess with two, which is why this
entry's `masterDeck` needs a new search rather than a note. The live test in
`RekordboxProcessTests` fails while it does not resolve, and should.

Not yet checked: a reboot, which maps `rekordbox.exe` at a new base. The chains are
relative to that base, so it should not matter.

In rekordbox 7, `PPTH` holds only `?/` followed by the audio file's name, not its full
path. It identifies the track, but it cannot be used to open the audio file.
