using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Flux.Rekordbox.ChainFinder.Tests;

/// <summary>
/// A process address space the tests fill, and the regions to match. A read succeeds
/// only when it falls inside one block, and a sweep only sees the declared regions, so
/// the search runs over exactly what a test put there.
/// </summary>
internal sealed class FakeScannableMemory: IScannableMemory {
  private readonly List<(long Start, byte[] Bytes)> blocks = [];
  private readonly List<(long Base, long Size)> regions = [];
  private readonly Dictionary<long, Stopwatch> clocks = [];

  internal FakeScannableMemory(long moduleBase) => this.ModuleBase = moduleBase;

  public long ModuleBase { get; }

  public IEnumerable<(long Base, long Size)> Regions(bool writableOnly = false) => this.regions;

  /// <summary>A readable region holding <paramref name="bytes"/>, and the bytes themselves.</summary>
  internal void Region(long start, byte[] bytes) {
    this.blocks.Add((start, bytes));
    this.regions.Add((start, bytes.Length));
  }

  /// <summary>A region large enough to hold everything written into it, filled as it is written.</summary>
  internal void Region(long start, int size) => this.Region(start, new byte[size]);

  internal void Pointer(long address, long value) => this.Write(address, 8, span => BinaryPrimitives.WriteInt64LittleEndian(span, value));

  internal void Single(long address, float value) => this.Write(address, 4, span => BinaryPrimitives.WriteSingleLittleEndian(span, value));

  internal void Int64(long address, long value) => this.Pointer(address, value);

  internal void Byte(long address, byte value) => this.Write(address, 1, span => span[0] = value);

  internal void Utf8(long address, string text) {
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    this.Write(address, bytes.Length + 1, span => bytes.CopyTo(span));
  }

  /// <summary>
  /// A 64-bit field that counts 44.1 kHz samples in real time from now, as a playing
  /// deck's playhead does. It is read live, so a wait between two reads shows it moving.
  /// </summary>
  internal void Clock(long address) => this.clocks[address] = Stopwatch.StartNew();

  public bool TryRead(long address, Span<byte> destination) {
    if(destination.Length == 8 && this.clocks.TryGetValue(address, out Stopwatch? clock)) {
      BinaryPrimitives.WriteInt64LittleEndian(destination, (long)(clock.Elapsed.TotalSeconds * 44_100));
      return true;
    }

    foreach((long start, byte[] bytes) in this.blocks) {
      if(address >= start && address + destination.Length <= start + bytes.Length) {
        bytes.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
        return true;
      }
    }

    return false;
  }

  private void Write(long address, int length, Action<Span<byte>> fill) {
    foreach((long start, byte[] bytes) in this.blocks) {
      if(address >= start && address + length <= start + bytes.Length) {
        fill(bytes.AsSpan((int)(address - start), length));
        return;
      }
    }

    throw new InvalidOperationException($"0x{address:X} is not inside any region; add one first.");
  }
}
