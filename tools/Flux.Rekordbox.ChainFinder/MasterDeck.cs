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
      sweep.Bytes(this.regions, (at, value) => {
        if(value == master) {
          found.Add(at);
        }
      });

      this.candidates = [.. found];
      this.started = true;
      return;
    }

    var kept = new List<long>();
    sweep.Recheck(this.regions, this.candidates, (at, value) => {
      if(value == master) {
        kept.Add(at);
      }
    });

    this.candidates = [.. kept];
  }

  /// <summary>
  /// The module chains that reach what is left. Only 4-aligned candidates are climbed:
  /// the index is a field of an object, and a field is aligned, while most of what
  /// survives elimination is a byte inside something else.
  /// </summary>
  internal List<PointerChain> Chains() {
    long[] aligned = [.. this.candidates.Where(address => address % 4 == 0)];
    Console.WriteLine($"  {aligned.Length} of {this.candidates.Length} survivors are aligned; climbing to the module.");
    return [.. sweep.Climb(sweep.Regions(), aligned, levels: 3)
      .Select(found => found.Chain)
      .DistinctBy(chain => chain.ToString())
      .OrderBy(chain => chain.Offsets.Count)];
  }

  /// <summary>The chains that read <paramref name="master"/> right now.</summary>
  internal static List<PointerChain> Reading(MemorySweep sweep, IEnumerable<PointerChain> chains, int master) =>
    [.. chains.Where(chain => chain.TryReadByte(sweep.Process, sweep.ModuleBase, out byte value) && value == master)];
}
