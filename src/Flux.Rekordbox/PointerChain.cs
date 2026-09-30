using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Flux.Rekordbox;

/// <summary>
/// A path from rekordbox's module base to one value: the first offset is added to the
/// base, and each later offset is added to the pointer read at the address so far.
/// The result is the address of the value itself, not a pointer to it.
///
/// That is the order Cheat Engine writes chains in, and the order the chains for each
/// rekordbox version are found and written down in, so the table can be copied from a
/// probe session without translating it. A chain one hop short or one hop long reads
/// a plausible number from the wrong place, which is the failure this project has to
/// guard against: a broken chain is easy to see, a wrong one is not.
///
/// Every pointer read on the way is checked before it is followed. It has to lie in
/// the user-mode half of a 64-bit address space and be 8-byte aligned, because what
/// rekordbox stores there is a pointer to an object, and heap objects on x64 are at
/// least that aligned. A value failing either test was not a pointer, and following
/// it would at best fail to read and at worst read something else.
/// </summary>
internal sealed class PointerChain {
  /// <summary>Below this is the null page and the range Windows never maps.</summary>
  private const long LowestPointer = 0x10000;

  /// <summary>The top of user-mode address space on x64 Windows.</summary>
  private const long HighestPointer = 0x7FFF_FFFF_FFFF;

  /// <summary>Throws on invalid bytes instead of substituting, see <see cref="TryReadUtf8"/>.</summary>
  private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

  private readonly long[] offsets;

  internal PointerChain(IReadOnlyList<long> offsets) {
    ArgumentNullException.ThrowIfNull(offsets);
    if(offsets.Count == 0) {
      throw new ArgumentException("A pointer chain needs at least the offset from the module base.", nameof(offsets));
    }

    this.offsets = [.. offsets];
  }

  internal IReadOnlyList<long> Offsets => this.offsets;

  /// <summary>
  /// Reads a chain written as hex offsets separated by spaces, <c>5E09790 8 2C0 1A0</c>,
  /// with or without a <c>0x</c> on each. The form the probe prints and the version
  /// table stores.
  /// </summary>
  internal static PointerChain Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    long[] offsets = new long[parts.Length];
    for(int i = 0; i < parts.Length; i++) {
      string part = parts[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[i][2..] : parts[i];
      if(!long.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offsets[i]) || offsets[i] < 0) {
        throw new FormatException($"'{parts[i]}' in pointer chain '{text}' is not a hex offset.");
      }
    }

    return new PointerChain(offsets);
  }

  /// <summary>
  /// The same chain with <paramref name="delta"/> added to one offset. Per-deck chains
  /// differ only by a fixed stride on one hop, so a deck's chain is the first deck's
  /// with that hop moved.
  /// </summary>
  internal PointerChain WithOffsetAdded(int index, long delta) {
    ArgumentOutOfRangeException.ThrowIfNegative(index);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, this.offsets.Length);
    long[] moved = [.. this.offsets];
    moved[index] += delta;
    return new PointerChain(moved);
  }

  /// <summary>
  /// Walks the chain from <paramref name="moduleBase"/>. False when a pointer on the
  /// way cannot be read or is not a pointer, which is also what a chain from the
  /// wrong rekordbox version usually looks like.
  /// </summary>
  internal bool TryResolve(IProcessMemory memory, long moduleBase, out long address) {
    ArgumentNullException.ThrowIfNull(memory);
    address = moduleBase + this.offsets[0];
    Span<byte> buffer = stackalloc byte[8];
    for(int i = 1; i < this.offsets.Length; i++) {
      if(!memory.TryRead(address, buffer)) {
        address = 0;
        return false;
      }

      long pointer = BinaryPrimitives.ReadInt64LittleEndian(buffer);
      if(!IsPointer(pointer)) {
        address = 0;
        return false;
      }

      address = pointer + this.offsets[i];
    }

    return true;
  }

  internal bool TryReadSingle(IProcessMemory memory, long moduleBase, out float value) {
    Span<byte> buffer = stackalloc byte[4];
    if(this.TryReadValue(memory, moduleBase, buffer)) {
      value = BinaryPrimitives.ReadSingleLittleEndian(buffer);
      return true;
    }

    value = 0;
    return false;
  }

  internal bool TryReadInt64(IProcessMemory memory, long moduleBase, out long value) {
    Span<byte> buffer = stackalloc byte[8];
    if(this.TryReadValue(memory, moduleBase, buffer)) {
      value = BinaryPrimitives.ReadInt64LittleEndian(buffer);
      return true;
    }

    value = 0;
    return false;
  }

  internal bool TryReadByte(IProcessMemory memory, long moduleBase, out byte value) {
    Span<byte> buffer = stackalloc byte[1];
    if(this.TryReadValue(memory, moduleBase, buffer)) {
      value = buffer[0];
      return true;
    }

    value = 0;
    return false;
  }

  /// <summary>
  /// A NUL-terminated UTF-8 string at the end of the chain, of at most
  /// <paramref name="maxBytes"/> bytes before the NUL. False when there is no NUL in
  /// that span or the bytes are not UTF-8: either means the chain landed somewhere that
  /// is not the string, and a truncated or repaired guess would read as a real value.
  /// </summary>
  internal bool TryReadUtf8(IProcessMemory memory, long moduleBase, int maxBytes, out string value) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
    value = "";
    if(!this.TryResolve(memory, moduleBase, out long address)) {
      return false;
    }

    // Read page by page rather than all at once: the string may end just before an
    // unmapped page, where reading the full span would fail on bytes past the NUL.
    // Memory is mapped in whole pages, so a read that stays inside one cannot fail
    // halfway.
    // On the stack for a path's length, since the poll loop reads one per deck on every
    // poll, a few hundred a second. The string decoded from it is still allocated each
    // time; the buffer is kept off the heap so it does not add its own garbage to that.
    const int Page = 0x1000;
    const int OnStack = 2048;
    Span<byte> text = maxBytes < OnStack ? stackalloc byte[maxBytes + 1] : new byte[maxBytes + 1];
    int filled = 0;
    while(filled < text.Length) {
      int toPageEnd = Page - (int)((address + filled) & (Page - 1));
      int length = Math.Min(toPageEnd, text.Length - filled);
      if(!memory.TryRead(address + filled, text.Slice(filled, length))) {
        return false;
      }

      int end = text.Slice(filled, length).IndexOf((byte)0);
      if(end >= 0) {
        return TryDecode(text[..(filled + end)], out value);
      }

      filled += length;
    }

    return false;
  }

  public override string ToString() =>
    string.Join(' ', this.offsets.Select(o => o.ToString("X", CultureInfo.InvariantCulture)));

  private static bool IsPointer(long value) =>
    value is >= LowestPointer and <= HighestPointer && (value & 7) == 0;

  private static bool TryDecode(ReadOnlySpan<byte> bytes, out string value) {
    try {
      value = Utf8Strict.GetString(bytes);
      return true;
    } catch(DecoderFallbackException) {
      value = "";
      return false;
    }
  }

  private bool TryReadValue(IProcessMemory memory, long moduleBase, Span<byte> destination) =>
    this.TryResolve(memory, moduleBase, out long address) && memory.TryRead(address, destination);
}
