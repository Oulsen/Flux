namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// Finds the master deck byte by elimination, which is the only way available: it is
/// one small number with no distinctive value, so there is nothing to search for.
/// Instead every byte in writable memory that holds the current master index is kept,
/// and each time the master changes the ones that did not follow are dropped.
///
/// It does not converge on its own. rekordbox has UI state that follows the MASTER
/// buttons exactly and survives any number of switches, so the run ends by climbing
/// what is left to module chains and letting a rekordbox restart decide - a restart
/// moves every heap object, and a chain that still reads the master afterwards is
/// addressing the thing itself.
/// </summary>
internal sealed class MasterDeckSearch(MemorySweep sweep) {
  private readonly IReadOnlyList<(long, long)> regions = sweep.Regions(writableOnly: true);
  private long[] candidates = [];
  private bool started;

  internal int Count => this.candidates.Length;

  /// <summary>
  /// The master changes the search asks for on <paramref name="decks"/> decks, as 1-based
  /// deck numbers. The tool names each one rather than asking which deck was chosen, so
  /// the order is its own: it starts off deck 1, which <see cref="Narrow"/> cannot start
  /// on, and makes every deck master. The rounds alternate between the master playing
  /// itself and the master stopped while the previous one plays on, from the start. In
  /// 7.2.19 a field meaning "the master is stopped while another deck plays" followed five
  /// rounds that were all of the second kind, and a byte that follows whichever deck
  /// plays would pass rounds all of the first; mixing them drops both in two rounds.
  /// </summary>
  internal static IReadOnlyList<(int Master, int Playing)> Rounds(int decks) {
    if(decks < 2) {
      throw new ArgumentOutOfRangeException(nameof(decks), decks, "The master can only be changed with two decks or more.");
    }

    int[] masters = [.. Enumerable.Range(2, decks - 1), 1, .. Enumerable.Range(2, decks - 1).Reverse()];
    return [.. masters.Select((master, round) => (master, round % 2 == 0 ? master : masters[round - 1]))];
  }

  /// <summary>
  /// Walks the rounds at the desk, then climbs what followed all of them to module chains.
  /// </summary>
  internal static List<PointerChain> Search(MemorySweep sweep, int decks) {
    var master = new MasterDeckSearch(sweep);
    IReadOnlyList<(int Master, int Playing)> rounds = Rounds(decks);
    Prompt.Why("Finds where rekordbox keeps which deck is master. Each round keeps only what names the deck you set, and drops the rest.");
    Console.WriteLine("Each round names a state to set up: one deck playing, the others stopped, and MASTER lit on a deck,");
    Console.WriteLine("often a different one. Start the playing deck first and press MASTER last. Press Enter once the screen shows it.");
    for(int round = 0; round < rounds.Count; round++) {
      (int deck, int playing) = rounds[round];
      Console.WriteLine($"\nRound {round + 1} of {rounds.Count}: deck {playing} playing alone, MASTER on deck {deck}.");
      Prompt.Enter();
      long[] before = master.candidates;
      master.Narrow(deck - 1);
      Console.WriteLine($"  {master.Count} bytes follow the master so far.");

      // Nothing left means the real byte went too, so this round did not happen as asked:
      // a master pressed before the playing deck was started, say. The round is redone
      // from where it began rather than ending a run that is otherwise going fine.
      if(master.Count == 0 && round > 0) {
        master.candidates = before;
        Console.WriteLine($"  No byte says deck {deck}. Check that MASTER is lit on deck {deck}, then redo the round.");
        round--;
      }
    }

    List<PointerChain> chains = master.Chains();
    Console.WriteLine($"  {chains.Count} master candidate(s) reach the module.");
    return chains;
  }

  /// <summary>
  /// Narrows to the bytes holding <paramref name="master"/>. The first call takes
  /// every byte in writable memory that does.
  /// </summary>
  internal void Narrow(int master) {
    if(!this.started) {
      // The first round keeps every byte that holds the index, and several gigabytes of
      // memory hold far too many zeros to keep. Deck 2 or higher makes it a byte value
      // that is rare enough to list.
      if(master == 0) {
        throw new ArgumentException("The first round needs a master other than deck 1.", nameof(master));
      }

      var found = new List<long>();
      sweep.Find(this.regions, (byte)master, found.Add, $"finding every byte that says deck {master + 1}");
      this.candidates = [.. found];
      this.started = true;
      return;
    }

    var kept = new List<long>();
    sweep.Recheck(this.regions, this.candidates, (at, value) => {
      if(value == master) {
        kept.Add(at);
      }
    }, $"checking which of them now say deck {master + 1}");

    this.candidates = [.. kept];
  }

  /// <summary>
  /// The module chains that reach what is left, up to four heap hops: every chain of three
  /// or fewer to a master byte in 7.2.19 broke on a restart. Only 4-aligned candidates are climbed:
  /// the index is a field of an object, and a field is aligned, while most of what
  /// survives elimination is a byte inside something else.
  /// </summary>
  internal List<PointerChain> Chains() {
    long[] aligned = [.. this.candidates.Where(address => address % 4 == 0)];
    Console.WriteLine($"  {aligned.Length} of {this.candidates.Length} survivors are aligned; climbing to the module.");
    Prompt.Why("  These addresses change every time rekordbox starts. Climbing finds routes to them from rekordbox's own code,");
    Prompt.Why("  which stays put, one pointer at a time; each level reads all of rekordbox's memory once.");
    return [.. sweep.Climb(sweep.Regions(), aligned, levels: 4, what: "climbing from the master candidates to rekordbox's own code")
      .Select(found => found.Chain)
      .DistinctBy(chain => chain.ToString())
      .OrderBy(chain => chain.Offsets.Count)];
  }

  /// <summary>The chains that read <paramref name="master"/> right now.</summary>
  internal static List<PointerChain> Reading(MemorySweep sweep, IEnumerable<PointerChain> chains, int master) =>
    [.. chains.Where(chain => chain.TryReadByte(sweep.Process, sweep.ModuleBase, out byte value) && value == master)];
}
