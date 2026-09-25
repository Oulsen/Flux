using System.Buffers.Binary;
using System.Text;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>A chain for deck 1, and the hop that moves by <see cref="Stride"/> per deck.</summary>
internal sealed record DeckChain(PointerChain Chain, int DeckHop, long Stride) {
  internal PointerChain For(int deck) => this.Chain.WithOffsetAdded(this.DeckHop, deck * this.Stride);
}

/// <summary>
/// Finds the per-deck chains: tempo, playhead and ANLZ path.
///
/// Each search is in the shape the chains have had in every rekordbox version looked at
/// so far - a module static, an array of deck pointers eight apart, then fields - and
/// is driven by two decks at once. Two decks with different tempos is what makes the
/// search precise: a candidate has to reach both at the same offsets, with the deck
/// pointers side by side, which is what an array is and what a coincidence is not.
/// </summary>
internal static class DeckChains {
  private const long DeckStride = 8;
  private const int SamplesPerSecond = 44_100;

  /// <summary>
  /// Tempo chains for two loaded decks showing <paramref name="bpm1"/> and
  /// <paramref name="bpm2"/>, strongest first: shortest chain, then fewest hops above
  /// the array.
  /// </summary>
  internal static List<DeckChain> FindTempo(MemorySweep sweep, float bpm1, float bpm2) {
    IReadOnlyList<(long, long)> regions = sweep.Regions();
    var floats1 = new List<long>();
    var floats2 = new List<long>();
    sweep.Floats(regions, (at, value) => {
      if(MathF.Abs(value - bpm1) < 0.006f) {
        floats1.Add(at);
      }

      if(MathF.Abs(value - bpm2) < 0.006f) {
        floats2.Add(at);
      }
    });

    Console.WriteLine($"  {floats1.Count} places hold {bpm1}, {floats2.Count} hold {bpm2}.");

    // Level 1: a field pointing at an object whose H2 is the tempo. Level 2: a field
    // pointing at an object whose H1 is that field. Both decks must share H1 and H2.
    var subs = Shared(sweep.Up(regions, floats1), sweep.Up(regions, floats2));
    var decks = Shared(sweep.Up(regions, subs.First.Select(s => s.Slot)), sweep.Up(regions, subs.Second.Select(s => s.Slot)));
    Console.WriteLine($"  {subs.First.Count} candidate tempo fields, {decks.First.Count} candidate deck objects.");

    var h2ByField = subs.First.ToLookup(s => s.Slot, s => s.Offset);
    var deck2Slots = decks.Second.ToLookup(d => d.Slot, d => d.Offset);
    var arrays = new List<(long Slot, long H1, long H2)>();
    foreach((long slot, long target, long h1) in decks.First) {
      if(!deck2Slots[slot + DeckStride].Contains(h1)) {
        continue;
      }

      foreach(long h2 in h2ByField[target]) {
        arrays.Add((slot, h1, h2));
      }
    }

    Console.WriteLine($"  {arrays.Count} deck arrays; climbing to the module.");
    var found = new List<DeckChain>();
    var byArray = arrays.ToLookup(a => a.Slot);
    foreach((long target, PointerChain toArray) in sweep.Climb(regions, byArray.Select(g => g.Key), levels: 2)) {
      foreach((long _, long h1, long h2) in byArray[target]) {
        var chain = new DeckChain(new PointerChain([.. toArray.Offsets, h1, h2]), toArray.Offsets.Count - 1, DeckStride);
        if(ReadsBoth(sweep, chain, bpm1, bpm2)) {
          found.Add(chain);
        }
      }
    }

    return [.. found.DistinctBy(c => c.Chain.ToString()).OrderBy(c => c.Chain.Offsets.Count).ThenBy(c => c.DeckHop)];
  }

  /// <summary>
  /// The playhead: a 64-bit field in the same object as the tempo that advances at 44.1
  /// kHz on the playing deck. Two reads a known time apart, with deck 1 playing, leave
  /// only the fields that moved at that rate, within a fifth either way. Every tempo
  /// candidate's object is read across the same wait, so one sleep covers them all.
  /// </summary>
  internal static List<DeckChain> FindPlayhead(MemorySweep sweep, IReadOnlyList<DeckChain> tempos, TimeSpan interval) {
    var objects = new List<(DeckChain Tempo, long Sub, byte[] Before)>();
    foreach(DeckChain tempo in tempos) {
      PointerChain objectChain = new([.. tempo.Chain.Offsets.Take(tempo.Chain.Offsets.Count - 1), 0]);
      byte[] before = new byte[0x800];
      if(objectChain.TryResolve(sweep.Process, sweep.ModuleBase, out long sub) && sweep.Process.TryRead(sub, before)) {
        objects.Add((tempo, sub, before));
      }
    }

    DateTime start = DateTime.UtcNow;
    Thread.Sleep(interval);
    double expected = (DateTime.UtcNow - start).TotalSeconds * SamplesPerSecond;

    var found = new List<DeckChain>();
    byte[] after = new byte[0x800];
    foreach((DeckChain tempo, long sub, byte[] before) in objects) {
      if(!sweep.Process.TryRead(sub, after)) {
        continue;
      }

      for(int offset = 0; offset + 8 <= before.Length; offset += 8) {
        long moved = BinaryPrimitives.ReadInt64LittleEndian(after.AsSpan(offset)) - BinaryPrimitives.ReadInt64LittleEndian(before.AsSpan(offset));
        if(moved > expected * 0.8 && moved < expected * 1.2) {
          long[] offsets = [.. tempo.Chain.Offsets];
          offsets[^1] = offset;
          found.Add(tempo with { Chain = new PointerChain(offsets) });
        }
      }
    }

    return [.. found.DistinctBy(c => c.Chain.ToString())];
  }

  /// <summary>
  /// The playhead candidates that advance at 44.1 kHz on <paramref name="playing"/> while
  /// <paramref name="still"/> stays put. This is the check that separates a deck's own
  /// field from one that happens to track whichever deck is playing: at the desk, a chain
  /// that read both decks correctly through two restarts failed only this.
  /// </summary>
  internal static List<DeckChain> Advancing(MemorySweep sweep, IReadOnlyList<DeckChain> playheads, int playing, int still, TimeSpan interval) {
    var before = new List<(DeckChain Chain, long Playing, long Still)>();
    foreach(DeckChain chain in playheads) {
      if(chain.For(playing).TryReadInt64(sweep.Process, sweep.ModuleBase, out long moving)
        && chain.For(still).TryReadInt64(sweep.Process, sweep.ModuleBase, out long resting)) {
        before.Add((chain, moving, resting));
      }
    }

    DateTime start = DateTime.UtcNow;
    Thread.Sleep(interval);
    double expected = (DateTime.UtcNow - start).TotalSeconds * SamplesPerSecond;

    return [.. before.Where(b =>
      b.Chain.For(playing).TryReadInt64(sweep.Process, sweep.ModuleBase, out long moving)
        && b.Chain.For(still).TryReadInt64(sweep.Process, sweep.ModuleBase, out long resting)
        && moving - b.Playing > expected * 0.8 && moving - b.Playing < expected * 1.2
        && resting == b.Still)
      .Select(b => b.Chain)];
  }

  /// <summary>
  /// Whether the ANLZ file a chain names for <paramref name="deck"/> exists and carries
  /// <paramref name="bpm"/> in its beatgrid. The library's paths name real files too, so
  /// existing is not enough; carrying the deck's tempo is what makes it that deck's file.
  /// </summary>
  internal static bool AnlzMatches(MemorySweep sweep, DeckChain chain, int deck, float bpm) =>
    chain.For(deck).TryReadUtf8(sweep.Process, sweep.ModuleBase, 1024, out string path)
      && path.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)
      && AnlzTempo.Carries(path, bpm);

  /// <summary>
  /// The ANLZ path per deck, with no help from the desk: every full path to an
  /// <c>ANLZ0000.DAT</c> in memory, climbed to the module with the last hop pointing
  /// exactly at the text, then pairs of chains alike but for one hop eight apart that
  /// end on different files. rekordbox keeps hundreds of these paths for the library,
  /// so the pairing is what picks out the decks.
  /// </summary>
  internal static List<DeckChain> FindAnlzPath(MemorySweep sweep) {
    IReadOnlyList<(long, long)> regions = sweep.Regions();
    byte[] needle = "ANLZ0000.DAT"u8.ToArray();
    var paths = new Dictionary<long, string>();
    sweep.Sweep(regions, (at, bytes) => {
      for(int i = bytes.IndexOf(needle); i >= 0;) {
        int start = i;
        while(start > 0 && bytes[start - 1] >= 0x20 && i - start < 1024) {
          start--;
        }

        bool clean = start + 3 <= bytes.Length && bytes[start + 1] == (byte)':' && bytes[start + 2] == (byte)'/'
          && (start == 0 || bytes[start - 1] == 0);
        if(clean) {
          paths.TryAdd(at + start, Encoding.UTF8.GetString(bytes[start..(i + needle.Length)]));
        }

        int next = bytes[(i + 1)..].IndexOf(needle);
        i = next < 0 ? -1 : i + 1 + next;
      }
    });

    Console.WriteLine($"  {paths.Count} ANLZ paths in memory; climbing to the module.");
    List<(long Target, PointerChain Chain)> chains = sweep.Climb(regions, paths.Keys, levels: 3, firstOffset: 0);
    var found = new List<DeckChain>();
    foreach(var group in chains.GroupBy(c => (c.Chain.Offsets[0], c.Chain.Offsets.Count))) {
      var list = group.ToList();
      foreach((long targetA, PointerChain a) in list) {
        foreach((long targetB, PointerChain b) in list) {
          if(paths[targetA] == paths[targetB]) {
            continue;
          }

          int[] differ = [.. Enumerable.Range(0, a.Offsets.Count).Where(k => a.Offsets[k] != b.Offsets[k])];
          if(differ.Length == 1 && b.Offsets[differ[0]] - a.Offsets[differ[0]] == DeckStride) {
            found.Add(new DeckChain(a, differ[0], DeckStride));
          }
        }
      }
    }

    // A pair is decks n and n+1; the chain for deck 1 is the lowest of a run, which is
    // the one whose hop minus a stride has no pair of its own.
    var keys = found.Select(f => f.Chain.ToString()).ToHashSet();
    return [.. found
      .Where(f => !keys.Contains(f.Chain.WithOffsetAdded(f.DeckHop, -DeckStride).ToString()))
      .DistinctBy(f => f.Chain.ToString())
      .OrderBy(f => f.Chain.Offsets.Count)];
  }

  /// <summary>Whether a chain reads both tempos, deck 1 then deck 2.</summary>
  internal static bool ReadsBoth(MemorySweep sweep, DeckChain chain, float bpm1, float bpm2) =>
    chain.For(0).TryReadSingle(sweep.Process, sweep.ModuleBase, out float one) && MathF.Abs(one - bpm1) < 0.006f
      && chain.For(1).TryReadSingle(sweep.Process, sweep.ModuleBase, out float two) && MathF.Abs(two - bpm2) < 0.006f;

  /// <summary>Candidates for deck 1 and deck 2, reduced to the field offsets both decks have.</summary>
  private static (List<(long Slot, long Target, long Offset)> First, List<(long Slot, long Target, long Offset)> Second) Shared(
    List<(long Slot, long Target, long Offset)> first, List<(long Slot, long Target, long Offset)> second) {
    HashSet<long> offsets = [.. first.Select(f => f.Offset)];
    offsets.IntersectWith(second.Select(s => s.Offset));
    return ([.. first.Where(f => offsets.Contains(f.Offset))], [.. second.Where(s => offsets.Contains(s.Offset))]);
  }
}
