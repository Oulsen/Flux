using Flux.Rekordbox;
using Flux.Rekordbox.ChainFinder;

// Guided search for the pointer chains of the running rekordbox, ending in an entry for
// src/Flux.Rekordbox/chains.json. Read-only throughout; it prompts for the deck states it
// cannot set itself. Every value keeps all of its candidates until the checks in
// docs/rekordbox-chains.md have run on them - different tempos per deck, each deck
// playing in turn, a restart, every deck the entry covers - because the candidate that
// looks best in one session is not reliably the one that survives the next.

TimeSpan wait = TimeSpan.FromSeconds(3);

Console.WriteLine("Flux rekordbox chain finder");
Console.WriteLine("Reads the running rekordbox to find the pointer chains for its version. It never writes to rekordbox.");
Console.WriteLine("Keep every deck's pitch at 0 so the BPM on screen is the track's own, and use 4-deck mode to cover four decks.\n");

RekordboxAttachResult attach = RekordboxProcess.Attach();
if(attach.State != RekordboxAttachState.Attached) {
  Console.WriteLine(Unattached(attach.State));
  return 1;
}

using RekordboxProcess rekordbox = attach.Process!;
var sweep = new MemorySweep(rekordbox);
bool known = RekordboxChains.TryGet(rekordbox.Version, out RekordboxChains existing);
Console.WriteLine($"Attached to rekordbox {rekordbox.Version}.");
if(known) {
  Console.WriteLine("This version already has an entry. This run checks it: the result is compared, not written.");
}

Console.WriteLine("\nLoad two tracks with different BPMs on decks 1 and 2, both stopped.");
float bpm1 = Prompt.Bpm("Deck 1 BPM: ");
float bpm2 = Prompt.Bpm("Deck 2 BPM: ");
while(MathF.Abs(bpm1 - bpm2) < 0.5f) {
  Console.WriteLine("  The two decks need different tempos, or one deck's chain could pass for the other's.");
  bpm2 = Prompt.Bpm("Deck 2 BPM: ");
}

Console.WriteLine("\n== Tempo ==");
List<DeckChain> tempo = DeckChains.FindTempo(sweep, bpm1, bpm2);
Console.WriteLine($"  {tempo.Count} tempo candidate(s).");
if(tempo.Count == 0) {
  return Fail("No tempo chain found. Check the BPMs, the pitch at 0, and that both decks are loaded.");
}

Console.WriteLine("\n== Playhead ==");
Console.WriteLine("Play deck 1, leave deck 2 stopped.");
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

Console.WriteLine("\n== ANLZ path ==");
List<DeckChain> anlz = [.. DeckChains.FindAnlzPath(sweep)
  .Where(chain => DeckChains.AnlzMatches(sweep, chain, 0, bpm1) && DeckChains.AnlzMatches(sweep, chain, 1, bpm2))];
Console.WriteLine($"  {anlz.Count} candidate(s) name files carrying each deck's tempo.");
if(anlz.Count == 0) {
  return Fail("No ANLZ path chain names both decks' files.");
}

Console.WriteLine("\n== Master deck ==");
Console.WriteLine("A few master changes. Vary which deck is playing as well as which is master.");
var master = new MasterDeckSearch(sweep);
for(int round = 1; ; round++) {
  int current = Prompt.Master($"Round {round}: set the master deck, then enter which deck it is (1-4): ");
  if(round == 1 && current == 1) {
    Console.WriteLine("  Start with any deck but deck 1: the first round lists every byte holding the index, and there are too many zeros.");
    round--;
    continue;
  }

  master.Narrow(current - 1);
  Console.WriteLine($"  {master.Count} bytes still follow the master.");
  if(round >= 3 && !Prompt.YesNo("Another round? More rounds help, but the restart decides. (y/n): ")) {
    break;
  }
}

List<PointerChain> masterChains = master.Chains();
Console.WriteLine($"  {masterChains.Count} master candidate(s) reach the module.");

// The restart moves every heap object, so it is the filter for every value at once:
// what still reads right afterwards is addressing the thing itself.
Console.WriteLine("\n== Restart ==");
Console.WriteLine("Restart rekordbox. Load two tracks with different BPMs on decks 1 and 2, make deck 1 master, and play deck 1 with deck 2 stopped.");
Prompt.Enter();

RekordboxAttachResult again = RekordboxProcess.Attach();
if(again.State != RekordboxAttachState.Attached) {
  return Fail(Unattached(again.State));
}

using RekordboxProcess restarted = again.Process!;
if(restarted.Version != rekordbox.Version) {
  return Fail($"Now attached to rekordbox {restarted.Version}, not {rekordbox.Version}. Start over on one version.");
}

var after = new MemorySweep(restarted);
float again1 = Prompt.Bpm("Deck 1 BPM now: ");
float again2 = Prompt.Bpm("Deck 2 BPM now: ");
tempo = [.. tempo.Where(chain => DeckChains.ReadsBoth(after, chain, again1, again2))];
playhead = DeckChains.Advancing(after, playhead, playing: 0, still: 1, wait);
anlz = [.. anlz.Where(chain => DeckChains.AnlzMatches(after, chain, 0, again1) && DeckChains.AnlzMatches(after, chain, 1, again2))];
masterChains = MasterDeckSearch.Reading(after, masterChains, 0);
Console.WriteLine($"  Survived: {tempo.Count} tempo, {playhead.Count} playhead, {anlz.Count} ANLZ, {masterChains.Count} master reading deck 1.");
if(tempo.Count == 0 || playhead.Count == 0 || anlz.Count == 0 || masterChains.Count == 0) {
  return Fail("Something did not survive the restart. Not writing an entry; rerun to try again.");
}

Console.WriteLine("Make deck 2 master.");
Prompt.Enter();
masterChains = MasterDeckSearch.Reading(after, masterChains, 1);
if(masterChains.Count == 0) {
  return Fail("No master chain read deck 2.");
}

// How many decks the entry covers is not taken on trust: an empty deck reads a tempo of
// zero through a chain that works, so a deck counts only when its track comes back, its
// ANLZ file carries its tempo, and the master byte names it.
DeckChain bpmChain = tempo[0];
DeckChain anlzChain = anlz[0];
Console.WriteLine("\n== Decks ==");
Console.WriteLine("Load a track on every deck this entry should cover, up to four.");
Prompt.Enter();
int decks = 0;
for(int deck = 0; deck < 4; deck++) {
  bool loaded = bpmChain.For(deck).TryReadSingle(restarted, restarted.ModuleBase, out float deckBpm)
    && deckBpm is > 40 and < 300
    && DeckChains.AnlzMatches(after, anlzChain, deck, deckBpm);
  Console.WriteLine($"  Deck {deck + 1}: {(loaded ? $"{deckBpm:0.##} BPM, its ANLZ file carries it" : "no loaded track read")}");
  if(!loaded) {
    break;
  }

  decks = deck + 1;
}

if(decks < 2) {
  return Fail("Fewer than two decks read a loaded track.");
}

for(int deck = 2; deck < decks; deck++) {
  Console.WriteLine($"Make deck {deck + 1} master.");
  Prompt.Enter();
  masterChains = MasterDeckSearch.Reading(after, masterChains, deck);
  if(masterChains.Count == 0) {
    return Fail($"No master chain read deck {deck + 1}.");
  }
}

DeckChain positionChain = playhead[0];
PointerChain masterChain = masterChains[0];
Console.WriteLine($"\nFound, covering {decks} deck(s):");
Console.WriteLine($"  bpm            {bpmChain.Chain} (deck hop {bpmChain.DeckHop}){Twins(tempo.Count)}");
Console.WriteLine($"  samplePosition {positionChain.Chain} (deck hop {positionChain.DeckHop}){Twins(playhead.Count)}");
Console.WriteLine($"  anlzPath       {anlzChain.Chain} (deck hop {anlzChain.DeckHop}){Twins(anlz.Count)}");
Console.WriteLine($"  masterDeck     {masterChain}{Twins(masterChains.Count)}");

if(known) {
  return Compare(existing, restarted, decks, bpmChain, positionChain, anlzChain, masterChain) ? 0 : 1;
}

string? tablePath = LocateTable();
string entry = ChainTableWriter.Entry(
  restarted.Version, decks, bpmChain, positionChain, anlzChain, masterChain,
  tablePath is null ? Environment.NewLine : ChainTableWriter.NewLineOf(tablePath));
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

// A known version's entry, read against what the run found: every deck the run covered,
// and the master. Positions may move a little between the two reads while a deck plays.
static bool Compare(
  RekordboxChains existing, RekordboxProcess rekordbox, int decks,
  DeckChain bpm, DeckChain position, DeckChain anlz, PointerChain master) {
  RekordboxReading table = existing.Read(rekordbox, rekordbox.ModuleBase);
  bool same = true;
  for(int deck = 0; deck < Math.Min(decks, existing.Decks); deck++) {
    DeckReading entry = table.Decks[deck];
    bool bpmSame = bpm.For(deck).TryReadSingle(rekordbox, rekordbox.ModuleBase, out float b) && entry.Bpm == b;
    bool positionSame = position.For(deck).TryReadInt64(rekordbox, rekordbox.ModuleBase, out long p)
      && entry.SamplePosition is long q && Math.Abs(p - q) < 44_100;
    bool anlzSame = anlz.For(deck).TryReadUtf8(rekordbox, rekordbox.ModuleBase, 1024, out string a) && entry.AnlzPath == a;
    Console.WriteLine($"  Deck {deck + 1}: bpm {Mark(bpmSame)}, samplePosition {Mark(positionSame)}, anlzPath {Mark(anlzSame)}");
    same &= bpmSame && positionSame && anlzSame;
  }

  bool masterSame = master.TryReadByte(rekordbox, rekordbox.ModuleBase, out byte m) && table.MasterDeck == m;
  Console.WriteLine($"  Master: {Mark(masterSame)}");
  Console.WriteLine(same && masterSame
    ? "The existing entry reads what this run found."
    : "The existing entry does not read what this run found. Look before trusting either.");
  return same && masterSame;
}

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
