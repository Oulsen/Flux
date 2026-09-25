# rekordbox analysis files

rekordbox writes what it learns about a track into three analysis (ANLZ) files that
share a name and differ by extension: `ANLZ0000.DAT`, `.EXT` and `.2EX`. A deck's
`anlzPath` chain (see `docs/rekordbox-chains.md`) names the `.DAT`; the others sit
beside it. Flux reads two tags from them: the beatgrid, for where each beat falls in
its bar, and the phrases, for the light engine.

The layout comes from Deep Symmetry's published notes on the format
(djl-analysis.deepsymmetry.org, "Analysis Files"). rkbx_link and rekordcrate are not a
source for it. The notes give byte offsets in hexadecimal; this file gives them in
decimal. Where a library's worth of real files adds something the notes do not say,
that is marked below. Those points are what the reader was tuned to, and they are
the part to recheck when a rekordbox update changes the files.

## The envelope

Everything is big-endian. A file opens with `PMAI`, the header length (28 in every file
seen) and the file length, then a run of sections. Each section opens with a
four-character tag, its header length and its total length. `AnlzFile` finds a section
by walking these, and gives up rather than reads on when a section claims more bytes
than the file has.

| Tag | File | Flux reads it as |
|---|---|---|
| `PQTZ` | `.DAT` | `AnlzBeatGrid` |
| `PSSI` | `.EXT` | `AnlzPhrases` |

`PSSI` is in the `.EXT`, not the `.2EX`, which holds the 3-band waveforms (`PWV6`,
`PWV7`) that Phase 6 step 7 reads.

## Beatgrid: `PQTZ`

Header 24 bytes: tag, lengths, two unknown words, then the beat count at byte 20. Each
beat is 8 bytes:

| Bytes | Field |
|---|---|
| 0-1 | beat in bar, 1 to 4 |
| 2-3 | tempo, hundredths of a BPM |
| 4-7 | time at normal speed, milliseconds, **signed** |

Found in the library, not in the notes:

- **The time is signed.** Six tracks have a first beat of `FF FF FF FF`: -1 ms, a beat
  a millisecond before the audio starts. Read unsigned, that beat falls 49.7 days into
  the track.
- **Times can stand still at the end.** Two tracks have grids that run past the end
  of the audio, and their last few beats all sit on the final millisecond. The reader
  keeps them. It refuses only time that runs backwards.
- **Not every track starts on beat 1.** 1,039 of 5,456 grids open on beat 2, 3 or 4.
  This is the fact the rekordbox source exists for, because no other source Flux has
  says where a bar starts.
- An empty grid (73 tracks) is a track rekordbox has not analysed, and reads as no
  grid.

## Phrases: `PSSI`

Header 32 bytes. Entry size at byte 12 (always 24), entry count at 16, mood at 18, the
beat the last phrase ends on at 26. Each phrase is 24 bytes: index at 0 (counting from
1), start beat at 2, kind at 4, flag bytes at 7, 9 and 19, a fill flag at 21 and the
fill's first beat at 22.

The start beat indexes the beatgrid from 1. That is checked, not assumed: counted
from 1, 92% of 88,000 phrases start on beat 1 of a bar. Counted from 0, 2% do.

Labels depend on the mood:

| Kind | Low (3) | Mid (2) | High (1) |
|---|---|---|---|
| 1 | Intro | Intro | Intro 1 / 2 |
| 2 | Verse 1 | Verse 1 | Up 1 / 2 / 3 |
| 3 | Verse 1 | Verse 2 | Down |
| 4 | Verse 1 | Verse 3 | |
| 5 | Verse 2 | Verse 4 | Chorus 1 / 2 |
| 6 | Verse 2 | Verse 5 | Outro 1 / 2 |
| 7 | Verse 2 | Verse 6 | |
| 8 | Bridge | Bridge | |
| 9 | Chorus | Chorus | |
| 10 | Outro | Outro | |

Under the high mood, the number of an intro, chorus or outro is 1 when the flag at
byte 7 is 1 and 2 when it is 0. An up is Up 3 when the flag at byte 9 is 1, Up 2 when
the flag at byte 19 is 1, and Up 1 when both are 0. A kind or flag combination
outside this table makes the tag unreadable rather than guessed at. Across the library
none turned up, so one that does means a rekordbox that has changed what the numbers
mean. Up phrases also carry up to three extra beat numbers at bytes 12-17. Their
meaning is not established, and Flux does not read them.

**The mask.** The notes say rekordbox 6 and later XOR everything from byte 18 onwards
with a 19-byte pattern, each byte of it raised by the entry count. rekordbox's own
library copies are **not** masked: all 5,424 phrase tags in it read plain. The mask is
on what rekordbox exports to USB, which is where a track loaded from a stick has its
analysis. The reader takes both, and decides which it has from the content: a real
tag's phrases are numbered 1, 2, 3, and the mask cannot leave that standing. The
unmasking has been checked only against the notes, not against a real export. The
first USB stick at the desk is the check still owed.

Start beats are as rekordbox wrote them, and they do not always fit the grid. On ten
tracks in the library the phrases count close to twice as many beats as the grid
holds, as if the grid had been set to half tempo after the phrases were found, and
their later phrases run past the grid's end. The reader does not repair that. Whether a phrase fits its grid is for the sync
source to decide.

## How it was checked

Every analysis file in a rekordbox 7.2.19 library (5,529 tracks) was read with a
throwaway script, then with the reader itself. `AnlzLibraryTests` repeats the second
pass on any machine that has a rekordbox library: every non-empty grid and every
phrase tag has to read, and phrases have to start on beat 1 far more often than
chance. It skips in CI, because the files are somebody's music. The byte-built
fixtures in `AnlzFixture` cover each rule above on their own.
