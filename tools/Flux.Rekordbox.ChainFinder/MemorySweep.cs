using System.Buffers.Binary;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// Sweeps over a process's readable memory: values, pointers, and the climb from an
/// address to the module statics that lead to it.
///
/// One pass over rekordbox is several gigabytes, so everything here works in chunks
/// and takes a callback rather than returning a list. A full pass takes tens of
/// seconds, which is the unit the whole tool is paced in.
/// </summary>
internal sealed class MemorySweep(IScannableMemory process) {
  private const int ChunkBytes = 1 << 20;
  private const int Page = 0x1000;

  /// <summary>How far above a target a pointer may point and still be counted as reaching it.</summary>
  internal const long MaxFieldOffset = 0x800;

  internal IScannableMemory Process => process;

  internal long ModuleBase => process.ModuleBase;

  internal IReadOnlyList<(long Base, long Size)> Regions(bool writableOnly = false) =>
    [.. process.Regions(writableOnly)];

  /// <summary>
  /// Walks the regions a chunk at a time. A chunk that cannot be read whole is read
  /// page by page, because one unmapped page in the middle must not lose the rest.
  /// </summary>
  internal void Sweep(IReadOnlyList<(long Base, long Size)> regions, Action<long, ReadOnlySpan<byte>> chunk) {
    byte[] buffer = new byte[ChunkBytes];
    foreach((long start, long size) in regions) {
      for(long offset = 0; offset < size; offset += ChunkBytes) {
        int length = (int)Math.Min(ChunkBytes, size - offset);
        Span<byte> span = buffer.AsSpan(0, length);
        if(process.TryRead(start + offset, span)) {
          chunk(start + offset, span);
          continue;
        }

        for(int page = 0; page < length; page += Page) {
          Span<byte> one = buffer.AsSpan(page, Math.Min(Page, length - page));
          if(process.TryRead(start + offset + page, one)) {
            chunk(start + offset + page, one);
          }
        }
      }
    }
  }

  internal void Floats(IReadOnlyList<(long, long)> regions, Action<long, float> hit) =>
    this.Sweep(regions, (at, bytes) => {
      for(int i = 0; i + 4 <= bytes.Length; i += 4) {
        hit(at + i, BinaryPrimitives.ReadSingleLittleEndian(bytes[i..]));
      }
    });

  internal void Bytes(IReadOnlyList<(long, long)> regions, Action<long, byte> hit) =>
    this.Sweep(regions, (at, bytes) => {
      for(int i = 0; i < bytes.Length; i++) {
        hit(at + i, bytes[i]);
      }
    });

  /// <summary>Every 8-aligned slot holding something that could be a pointer.</summary>
  internal void Pointers(IReadOnlyList<(long, long)> regions, Action<long, long> hit) =>
    this.Sweep(regions, (at, bytes) => {
      int first = (int)((8 - (at & 7)) & 7);
      for(int i = first; i + 8 <= bytes.Length; i += 8) {
        long value = BinaryPrimitives.ReadInt64LittleEndian(bytes[i..]);
        if(Plausible(value)) {
          hit(at + i, value);
        }
      }
    });

  /// <summary>Re-reads only the addresses in <paramref name="sorted"/>, in one pass.</summary>
  internal void Recheck(IReadOnlyList<(long, long)> regions, long[] sorted, Action<long, byte> hit) =>
    this.Sweep(regions, (at, bytes) => {
      int i = Index(sorted, at);
      for(; i < sorted.Length && sorted[i] < at + bytes.Length; i++) {
        hit(sorted[i], bytes[(int)(sorted[i] - at)]);
      }
    });

  /// <summary>
  /// Slots holding a pointer that lands within <see cref="MaxFieldOffset"/> below one
  /// of <paramref name="targets"/>: the objects that own them, one level up.
  /// </summary>
  internal List<(long Slot, long Target, long Offset)> Up(
    IReadOnlyList<(long, long)> regions, IEnumerable<long> targets, long maxOffset = MaxFieldOffset) {
    long[] sorted = [.. targets.Distinct().Order()];
    var found = new List<(long, long, long)>();
    this.Pointers(regions, (slot, value) => {
      for(int i = Index(sorted, value); i < sorted.Length && sorted[i] - value <= maxOffset; i++) {
        found.Add((slot, sorted[i], sorted[i] - value));
      }
    });

    return found;
  }

  /// <summary>
  /// Climbs from <paramref name="targets"/> until a slot inside the module: the chains
  /// that reach each target, written as offsets from the module base in Cheat Engine
  /// order. Heap slots are followed up to <paramref name="levels"/> deep.
  /// </summary>
  internal List<(long Target, PointerChain Chain)> Climb(
    IReadOnlyList<(long, long)> regions, IEnumerable<long> targets, int levels, long firstOffset = MaxFieldOffset) {
    // rekordbox.exe is about 100 MB mapped; this window is comfortably past its end and
    // well short of the heap, which sits far higher. A slot beyond it is not a static,
    // and a false one here only adds a candidate the restart will drop.
    long moduleEnd = process.ModuleBase + 0x1000_0000;
    var frontier = targets.Distinct().ToDictionary(target => target, target => (Target: target, Hops: new List<long>()));
    var chains = new List<(long, PointerChain)>();
    for(int level = 1; level <= levels && frontier.Count > 0; level++) {
      long[] sorted = [.. frontier.Keys.Order()];
      var next = new Dictionary<long, (long Target, List<long> Hops)>();
      long maxOffset = level == 1 ? firstOffset : MaxFieldOffset;
      this.Pointers(regions, (slot, value) => {
        for(int i = Index(sorted, value); i < sorted.Length && sorted[i] - value <= maxOffset; i++) {
          (long target, List<long> hops) = frontier[sorted[i]];
          List<long> chain = [sorted[i] - value, .. hops];
          if(slot >= process.ModuleBase && slot < moduleEnd) {
            chains.Add((target, new PointerChain([slot - process.ModuleBase, .. chain])));
          } else if(next.Count < 400_000) {
            next.TryAdd(slot, (target, chain));
          }
        }
      });

      frontier = next;
    }

    return chains;
  }

  internal static bool Plausible(long pointer) => pointer is >= 0x10000 and <= 0x7FFF_FFFF_FFFF && (pointer & 7) == 0;

  /// <summary>The first index in a sorted array at or after <paramref name="value"/>.</summary>
  private static int Index(long[] sorted, long value) {
    int i = Array.BinarySearch(sorted, value);
    return i < 0 ? ~i : i;
  }
}
