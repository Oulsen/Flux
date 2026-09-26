using Flux.Rekordbox;
using Flux.Rekordbox.ChainFinder;

// Guided search for the pointer chains of the running rekordbox, ending in an entry for
// src/Flux.Rekordbox/chains.json: appended for a new version, or, for one already in the
// table, compared with the entry and replaced where it differs if the user says so.
// Read-only throughout. The desk only does what the tool cannot: it is told which deck
// to play and which to make master, and says yes when a tempo read matches the screen.
// Every value keeps all of its candidates until the checks in docs/rekordbox-chains.md
// have run on them - different tempos per deck, each deck playing in turn, three
// restarts, every deck the entry covers - because the candidate that looks best in one
// session is not reliably the one that survives the next.

TimeSpan wait = TimeSpan.FromSeconds(3);

// One restart is not enough: the first 7.2.19 master chain passed two and failed on the
// third, landing on an object whose layout had changed. So every chain is held to three.
const int MinimumRestarts = 3;

// Tempos taken from the candidate ANLZ files are searched for all at once. Past this many
// the float search keeps too much to climb, and the two on screen are asked for instead.
const int MostTemposToSearch = 16;

const string Restarted = "A restart moves everything in rekordbox's memory. A route that still reads the right value afterwards "
  + "is a real one, not a coincidence of this session.";

Console.WriteLine("Flux rekordbox chain finder");
Console.WriteLine("Reads the running rekordbox to find the pointer chains for its version. It never writes to rekordbox.\n");
Console.WriteLine("Before starting: LINK off, every deck's pitch at 0, and 4-deck mode to cover four decks.");
Console.WriteLine("Load an analysed track on every deck, with different BPMs on decks 1 and 2. All decks stopped.");
Prompt.Enter();

RekordboxAttachResult attach = RekordboxProcess.Attach();
if(attach.State != RekordboxAttachState.Attached) {
  Console.WriteLine(Unattached(attach.State));
  return 1;
}

using RekordboxProcess rekordbox = attach.Process!;
var sweep = new MemorySweep(rekordbox);
bool known = RekordboxChains.TryGet(rekordbox.Version, out RekordboxChains existing);
Console.WriteLine($"Attached to rekordbox {rekordbox.Version}.");
if(args.Contains("--master")) {
  return known
    ? MasterOnly(rekordbox, sweep, existing)
    : Fail($"--master searches only the master deck of a version already in the table, and {rekordbox.Version} is not. Run without it.");
}

if(known) {
  Console.WriteLine("This version already has an entry. The run compares it with what it finds, and offers to replace what differs.");
}

// The ANLZ path comes first because it needs nothing from the desk, and the files it
// names carry the decks' tempos, which is what the tempo search looks for.
Console.WriteLine("\n== ANLZ path ==");
Prompt.Why("Finds where rekordbox keeps each deck's analysis file. Nothing to do: the files say which tempo each deck has.");
List<DeckChain> paired = DeckChains.FindAnlzPath(sweep);
List<DeckChain> anlz;
var tempos1 = new SortedSet<float>();
var tempos2 = new SortedSet<float>();
while(true) {
  tempos1.Clear();
  tempos2.Clear();
  int sameTempo = 0;
  anlz = [.. paired.Where(chain => {
    if(DeckChains.FileTempos(sweep, chain, 0) is not { } one || DeckChains.FileTempos(sweep, chain, 1) is not { } two) {
      return false;
    }

    // Checked per candidate, from its files, because the tempo search after this is
    // minutes long and a pair on one tempo cannot tell deck 1's chain from deck 2's.
    // Only the candidate goes: others, such as the one naming the decks, can still differ.
    if(one.Any(a => two.Any(b => MathF.Abs(a - b) < 0.5f))) {
      sameTempo++;
      return false;
    }

    tempos1.UnionWith(one);
    tempos2.UnionWith(two);
    return true;
  })];
  Console.WriteLine($"  {anlz.Count} candidate(s) name beatgrid files with different tempos on decks 1 and 2.");
  if(anlz.Count > 0) {
    break;
  }

  if(sameTempo == 0) {
    return Fail("No ANLZ path chain names beatgrid files on decks 1 and 2. Are both loaded with analysed tracks?");
  }

  Console.WriteLine("  Decks 1 and 2 carry the same BPM. Load a track with a different BPM on deck 2.");
  Prompt.Enter();
}

if(tempos1.Count + tempos2.Count > MostTemposToSearch) {
  Console.WriteLine($"  The candidate files carry {tempos1.Count + tempos2.Count} tempos, too many to search for at once.");
  tempos1 = [Prompt.Bpm("Deck 1 BPM on screen: ")];
  tempos2 = [Prompt.Bpm("Deck 2 BPM on screen: ")];
}

Console.WriteLine("\n== Tempo ==");
Prompt.Why("Looks for those tempos in rekordbox's memory, and keeps the routes to them that start in rekordbox's own code.");
Console.WriteLine($"  Looking for deck 1 at {string.Join(" or ", tempos1.Select(t => $"{t:0.##}"))} BPM and deck 2 at {string.Join(" or ", tempos2.Select(t => $"{t:0.##}"))}.");
List<DeckChain> tempo = DeckChains.FindTempo(sweep, tempos1, tempos2);
Console.WriteLine($"  {tempo.Count} tempo chain(s) read those tempos.");
if(tempo.Count == 0) {
  return Fail("No tempo chain reads the tempos the decks' ANLZ files carry. A pitch off 0 does this, and so does changing a track during the search.");
}

(tempo, anlz) = DeckChains.Agreeing(sweep, tempo, anlz);
Console.WriteLine($"  {tempo.Count} tempo and {anlz.Count} ANLZ candidate(s) agree with each other.");
if(tempo.Count == 0 || anlz.Count == 0) {
  return Fail("The tempo chains and the ANLZ files stopped agreeing during the search. Was a track changed? Rerun with the decks left alone.");
}

if(Confirm(sweep, tempo[0], anlz[0]) is not (float bpm1, float bpm2)) {
  return Fail("The tempos read are not what rekordbox shows.");
}

Console.WriteLine("\n== Decks ==");
Prompt.Why("Reads every deck through the chains so far, to see which decks have a track loaded.");
int decks = CountDecks(sweep, tempo[0], anlz[0]);
if(decks < 2) {
  return Fail("Fewer than two decks read a loaded track.");
}

Console.WriteLine("\n== Playhead ==");
Prompt.Why("Finds each deck's playhead: the number that counts up while that deck plays, and stands still while it does not.");
Console.WriteLine("Play deck 1, leave the others stopped.");
Prompt.Enter();
List<DeckChain> playhead = DeckChains.FindPlayhead(sweep, tempo, wait);
playhead = DeckChains.Advancing(sweep, playhead, playing: 0, still: 1, wait);
Console.WriteLine($"  {playhead.Count} playhead candidate(s) advance on deck 1 alone.");

Console.WriteLine("Now stop deck 1 and play deck 2.");
Prompt.Enter();
tempo = [.. tempo.Where(chain => DeckChains.ReadsBoth(sweep, chain, bpm1, bpm2))];
playhead = DeckChains.Advancing(sweep, playhead, playing: 1, still: 0, wait);
Console.WriteLine($"  {tempo.Count} tempo and {playhead.Count} playhead candidate(s) follow the change of playing deck.");
if(tempo.Count == 0 || playhead.Count == 0) {
  return Fail("Nothing followed the change of playing deck. Is deck 2 playing and deck 1 stopped?");
}

Console.WriteLine("\n== Master deck ==");
List<PointerChain> masterChains = MasterDeckSearch.Search(sweep, decks);
if(masterChains.Count == 0) {
  return Fail("No master candidate followed every round.");
}

// The restart moves every heap object, so it is the filter for every value at once:
// what still reads right afterwards is addressing the thing itself.
RekordboxProcess restarted = rekordbox;
MemorySweep after = sweep;
for(int restart = 1; ; restart++) {
  Console.WriteLine($"\n== Restart {restart} ==");
  Prompt.Why(Restarted);
  Console.WriteLine($"Restart rekordbox. Load a track on all {decks} decks, not the ones from before, with different BPMs on decks 1 and 2.");
  Console.WriteLine("Then: deck 1 playing alone, MASTER on deck 1.");
  Prompt.Enter();

  RekordboxAttachResult again = RekordboxProcess.Attach();
  if(again.State != RekordboxAttachState.Attached) {
    return Fail(Unattached(again.State));
  }

  if(restarted != rekordbox) {
    restarted.Dispose();
  }

  restarted = again.Process!;
  if(restarted.Version != rekordbox.Version) {
    return Fail($"Now attached to rekordbox {restarted.Version}, not {rekordbox.Version}. Start over on one version.");
  }

  after = new MemorySweep(restarted);
  (tempo, anlz) = DeckChains.Agreeing(after, tempo, anlz);
  if(tempo.Count == 0 || anlz.Count == 0) {
    return Fail($"No tempo and ANLZ chains agree after restart {restart}. Not writing an entry; rerun to try again.");
  }

  if(Confirm(after, tempo[0], anlz[0]) is null) {
    return Fail($"The tempos read after restart {restart} are not what rekordbox shows.");
  }

  playhead = DeckChains.Advancing(after, playhead, playing: 0, still: 1, wait);
  if(playhead.Count == 0) {
    return Fail($"No playhead chain advanced on deck 1 alone after restart {restart}.");
  }

  int loaded = CountDecks(after, tempo[0], anlz[0]);
  if(loaded < decks) {
    return Fail($"Only {loaded} of {decks} decks read a loaded track after restart {restart}. Load all of them and rerun.");
  }

  masterChains = CheckMaster(after, masterChains, decks);
  if(masterChains.Count == 0) {
    return Fail($"No master chain read every deck after restart {restart}. Rerun to try again.");
  }

  Console.WriteLine($"  Survived restart {restart}: {tempo.Count} tempo, {playhead.Count} playhead, {anlz.Count} ANLZ, {masterChains.Count} master.");
  if(restart >= MinimumRestarts && !Prompt.YesNo("Another restart? Each one can still catch a chain that only looked stable. (y/n): ")) {
    break;
  }
}

DeckChain bpmChain = tempo[0];
DeckChain positionChain = playhead[0];
DeckChain anlzChain = anlz[0];
PointerChain masterChain = masterChains[0];
Console.WriteLine($"\nFound, covering {decks} deck(s):");
Console.WriteLine($"  bpm            {bpmChain.Chain} (deck hop {bpmChain.DeckHop}){Twins(tempo.Count)}");
Console.WriteLine($"  samplePosition {positionChain.Chain} (deck hop {positionChain.DeckHop}){Twins(playhead.Count)}");
Console.WriteLine($"  anlzPath       {anlzChain.Chain} (deck hop {anlzChain.DeckHop}){Twins(anlz.Count)}");
Console.WriteLine($"  masterDeck     {masterChain}{Twins(masterChains.Count)}");

// The master is the value that has failed before, so its twins are listed rather than
// counted: the evidence for an entry names what else survived.
foreach(PointerChain twin in masterChains.Skip(1)) {
  Console.WriteLine($"    also         {twin}");
}

string? tablePath = LocateTable();
string newLine = tablePath is null ? Environment.NewLine : ChainTableWriter.NewLineOf(tablePath);
if(known) {
  return Repair(existing, restarted, tablePath, decks, bpmChain, positionChain, anlzChain, masterChain, newLine);
}

string entry = ChainTableWriter.Entry(restarted.Version, decks, bpmChain, positionChain, anlzChain, masterChain, newLine);
Console.WriteLine($"\nEntry for {restarted.Version}:\n{entry}\n");
if(tablePath is null) {
  Console.WriteLine("Could not find src/Flux.Rekordbox/chains.json from here. Copy the block above into it by hand.");
  return 0;
}

if(Prompt.YesNo($"Append this to {tablePath}? (y/n): ")) {
  ChainTableWriter.Insert(tablePath, restarted.Version, entry);
  Console.WriteLine("Written. Build, run the tests, and add the version's evidence to docs/rekordbox-chains.md.");
}

return 0;

// A known version's entry, read against what the run found on every deck it covered.
// What reads the same keeps the entry's own chain, whose evidence is written down; only
// what differs is replaced, and only on a yes.
static int Repair(
  RekordboxChains existing, RekordboxProcess rekordbox, string? tablePath, int decks,
  DeckChain bpm, DeckChain position, DeckChain anlz, PointerChain master, string newLine) {
  Console.WriteLine("\n== Against the entry ==");
  Prompt.Why("Compares this run with what chains.json holds. What reads the same stays as it is; only what differs is offered to replace.");
  RekordboxReading table = existing.Read(rekordbox, rekordbox.ModuleBase);
  bool bpmSame = true, positionSame = true, anlzSame = true;
  for(int deck = 0; deck < Math.Min(decks, existing.Decks); deck++) {
    DeckReading entry = table.Decks[deck];
    bool b = bpm.For(deck).TryReadSingle(rekordbox, rekordbox.ModuleBase, out float found) && entry.Bpm == found;
    // Positions may move a little between the two reads while a deck plays.
    bool p = position.For(deck).TryReadInt64(rekordbox, rekordbox.ModuleBase, out long at)
      && entry.SamplePosition is long q && Math.Abs(at - q) < 44_100;
    bool a = anlz.For(deck).TryReadUtf8(rekordbox, rekordbox.ModuleBase, 1024, out string path) && entry.AnlzPath == path;
    Console.WriteLine($"  Deck {deck + 1}: bpm {Mark(b)}, samplePosition {Mark(p)}, anlzPath {Mark(a)}");
    bpmSame &= b;
    positionSame &= p;
    anlzSame &= a;
  }

  bool masterSame = master.TryReadByte(rekordbox, rekordbox.ModuleBase, out byte m) && table.MasterDeck == m;
  Console.WriteLine($"  Master: {Mark(masterSame)}");
  if(bpmSame && positionSame && anlzSame && masterSame) {
    Console.WriteLine("The entry reads what this run found. Nothing to change.");
    return 0;
  }

  if(decks < existing.Decks) {
    return Fail($"The entry covers {existing.Decks} decks and this run {decks}. Rerun with all of them loaded before replacing anything.");
  }

  if(tablePath is null || ChainTableWriter.Existing(File.ReadAllText(tablePath), rekordbox.Version) is not { } current) {
    Console.WriteLine("Could not find the entry in src/Flux.Rekordbox/chains.json from here. Put the chains above in by hand.");
    return 1;
  }

  DeckChain newBpm = bpmSame ? Kept(current.Bpm!) : bpm;
  DeckChain newPosition = positionSame ? Kept(current.SamplePosition!) : position;
  DeckChain newAnlz = anlzSame ? Kept(current.AnlzPath!) : anlz;
  PointerChain newMaster = masterSame ? PointerChain.Parse(current.MasterDeck!.Chain!) : master;

  Console.WriteLine("\nTo replace:");
  Changed("bpm", current.Bpm!.Chain, bpmSame, bpm.Chain);
  Changed("samplePosition", current.SamplePosition!.Chain, positionSame, position.Chain);
  Changed("anlzPath", current.AnlzPath!.Chain, anlzSame, anlz.Chain);
  Changed("masterDeck", current.MasterDeck!.Chain, masterSame, master);
  if(!Prompt.YesNo($"Replace these in the {rekordbox.Version} entry of {tablePath}? (y/n): ")) {
    Console.WriteLine("Nothing was written.");
    return 1;
  }

  string replacement = ChainTableWriter.Entry(rekordbox.Version, decks, newBpm, newPosition, newAnlz, newMaster, newLine);
  ChainTableWriter.Replace(tablePath, rekordbox.Version, replacement);
  Console.WriteLine("Written. Build, run the tests, and add the new evidence to docs/rekordbox-chains.md.");
  return 0;

  static void Changed(string field, string? was, bool same, PointerChain now) {
    if(!same) {
      Console.WriteLine($"  {field,-15} {was} -> {now}");
    }
  }
}

// Only the master deck, for a version whose other chains still read: they are taken from
// the entry, which finds the loaded decks without the ANLZ and tempo searches, and the
// master rounds and the restarts run as in a full search. What reads the same keeps the
// entry's chain, so the comparison at the end offers to replace the master alone.
static int MasterOnly(RekordboxProcess rekordbox, MemorySweep sweep, RekordboxChains existing) {
  string? tablePath = LocateTable();
  if(tablePath is null || ChainTableWriter.Existing(File.ReadAllText(tablePath), rekordbox.Version) is not { } current) {
    return Fail("--master reads the entry from src/Flux.Rekordbox/chains.json, and could not find it from here.");
  }

  Console.WriteLine("Searching the master deck only; the entry's other chains are used as they are.");
  DeckChain bpm = Kept(current.Bpm!);
  DeckChain position = Kept(current.SamplePosition!);
  DeckChain anlz = Kept(current.AnlzPath!);

  Console.WriteLine("\n== Decks ==");
Prompt.Why("Reads every deck through the chains so far, to see which decks have a track loaded.");
  int decks = CountDecks(sweep, bpm, anlz);
  if(decks < 2) {
    return Fail("Fewer than two decks read a loaded track through the entry.");
  }

  Console.WriteLine("\n== Master deck ==");
  List<PointerChain> chains = MasterDeckSearch.Search(sweep, decks);
  if(chains.Count == 0) {
    return Fail("No master candidate followed every round.");
  }

  RekordboxProcess? restarted = null;
  try {
    for(int restart = 1; ; restart++) {
      Console.WriteLine($"\n== Restart {restart} ==");
      Prompt.Why(Restarted);
      Console.WriteLine($"Restart rekordbox and load a track on all {decks} decks, not the ones from before.");
      Console.WriteLine("Then: deck 1 playing alone, MASTER on deck 1.");
      Prompt.Enter();

      RekordboxAttachResult again = RekordboxProcess.Attach();
      if(again.State != RekordboxAttachState.Attached) {
        return Fail(Unattached(again.State));
      }

      restarted?.Dispose();
      restarted = again.Process!;
      if(restarted.Version != rekordbox.Version) {
        return Fail($"Now attached to rekordbox {restarted.Version}, not {rekordbox.Version}. Start over on one version.");
      }

      var after = new MemorySweep(restarted);
      if(CountDecks(after, bpm, anlz) < decks) {
        return Fail($"Not all {decks} decks read a loaded track after restart {restart}. Load all of them and rerun.");
      }

      chains = CheckMaster(after, chains, decks);
      if(chains.Count == 0) {
        return Fail($"No master chain read every deck after restart {restart}. Rerun to try again.");
      }

      Console.WriteLine($"  Survived restart {restart}: {chains.Count} master.");
      if(restart >= MinimumRestarts && !Prompt.YesNo("Another restart? Each one can still catch a chain that only looked stable. (y/n): ")) {
        break;
      }
    }

    Console.WriteLine($"\nFound masterDeck {chains[0]}{Twins(chains.Count)}");
    foreach(PointerChain twin in chains.Skip(1)) {
      Console.WriteLine($"    also         {twin}");
    }

    return Repair(existing, restarted, tablePath, decks, bpm, position, anlz, chains[0], ChainTableWriter.NewLineOf(tablePath));
  } finally {
    restarted?.Dispose();
  }
}

// After a restart every candidate has to read each deck as master in turn. A deck none
// reads is checked on the screen before the run gives up: a MASTER pressed too early is
// the desk's slip, not the chain's.
static List<PointerChain> CheckMaster(MemorySweep sweep, List<PointerChain> chains, int decks) {
  for(int deck = 0; deck < decks; deck++) {
    if(deck > 0) {
      Console.WriteLine($"Now: deck {deck} playing alone, MASTER on deck {deck + 1}.");
      Prompt.Enter();
    }

    while(true) {
      List<PointerChain> reading = MasterDeckSearch.Reading(sweep, chains, deck);
      if(reading.Count > 0) {
        chains = reading;
        break;
      }

      Console.WriteLine($"  No candidate reads deck {deck + 1} as master.");
      if(Prompt.YesNo($"Is MASTER lit on deck {deck + 1}? (y ends the run, n to set it and check again): ")) {
        return [];
      }

      Prompt.Enter();
    }
  }

  return chains;
}

static DeckChain Kept(ChainTableDeckChain chain) => new(PointerChain.Parse(chain.Chain!), chain.DeckHop!.Value, chain.DeckStride!.Value);

// Deck 1's and deck 2's tempo through a chain, once the screen agrees with them. Two decks
// on one tempo cannot tell a deck's chain from the other deck's, so that is refused too.
static (float, float)? Confirm(MemorySweep sweep, DeckChain chain, DeckChain anlz) {
  if(!chain.For(0).TryReadSingle(sweep.Process, sweep.ModuleBase, out float one)
    || !chain.For(1).TryReadSingle(sweep.Process, sweep.ModuleBase, out float two)) {
    return null;
  }

  if(MathF.Abs(one - two) < 0.5f) {
    Console.WriteLine($"  Decks 1 and 2 both read {one:0.00}: they need tracks with different BPMs, or one deck's chain could pass for the other's.");
    return null;
  }

  Console.WriteLine($"  Deck 1 reads {one:0.00} BPM{Track(sweep, anlz, 0)}, deck 2 {two:0.00}{Track(sweep, anlz, 1)}.");
  return Prompt.YesNo("  Is that what rekordbox shows? (y/n): ") ? (one, two) : null;
}

// How many decks the entry covers is not taken on trust: an empty deck reads a tempo of
// zero through a chain that works, so a deck counts only when its tempo is plausible and
// its ANLZ file carries it.
static int CountDecks(MemorySweep sweep, DeckChain tempo, DeckChain anlz) {
  int decks = 0;
  for(int deck = 0; deck < 4; deck++) {
    bool loaded = tempo.For(deck).TryReadSingle(sweep.Process, sweep.ModuleBase, out float bpm)
      && bpm is > 40 and < 300
      && DeckChains.AnlzMatches(sweep, anlz, deck, bpm);
    Console.WriteLine($"  Deck {deck + 1}: {(loaded ? $"{bpm:0.##} BPM{Track(sweep, anlz, deck)}, its ANLZ file carries it" : "no loaded track read")}");
    if(!loaded) {
      break;
    }

    decks = deck + 1;
  }

  return decks;
}

// The track on a deck, by its audio file's name, for the desk to recognise.
static string Track(MemorySweep sweep, DeckChain anlz, int deck) =>
  anlz.For(deck).TryReadUtf8(sweep.Process, sweep.ModuleBase, 1024, out string path) && AnlzTrack.Name(path) is { } name
    ? $" ({name})" : "";

static int Fail(string message) {
  Console.WriteLine($"\n{message}");
  Console.WriteLine("Nothing was written.");
  return 1;
}

static string Unattached(RekordboxAttachState state) => state switch {
  RekordboxAttachState.NotRunning => "No rekordbox is running.",
  RekordboxAttachState.Elevated => "rekordbox runs as administrator and this tool does not. Start rekordbox normally, or run this from an elevated terminal.",
  RekordboxAttachState.Unsupported => "This tool must run as a 64-bit process.",
  _ => "rekordbox is running but could not be opened for reading.",
};

static string Mark(bool ok) => ok ? "same" : "DIFFERENT";

// More than one survivor is usually a twin: another path to the same field.
static string Twins(int survivors) => survivors > 1 ? $", {survivors - 1} other survivor(s)" : "";

static string? LocateTable() {
  for(DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
    string candidate = Path.Combine(dir.FullName, "src", "Flux.Rekordbox", "chains.json");
    if(File.Exists(candidate)) {
      return candidate;
    }
  }

  return null;
}
