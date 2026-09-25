using Xunit;

namespace Flux.Rekordbox.Tests;

public class PointerChainTests {
  private const long ModuleBase = 0x7FF7_B00B_0000;
  private const long Deck = 0x1998_DE6A_0000;
  private const long Sub = 0x1998_DE70_0000;

  [Fact]
  public void FollowsAChainInCheatEngineOrder() {
    // base + first offset holds a pointer; each later offset is added to the pointer
    // read so far; the last one is not dereferenced. This is the deck BPM chain's
    // shape, with the numbers found at the desk for rekordbox 7.2.19.
    FakeProcessMemory memory = Rekordbox(bpm: 174.0f);

    Assert.True(PointerChain.Parse("5E09790 0 2C0 1A0").TryReadSingle(memory, ModuleBase, out float bpm));
    Assert.Equal(174.0f, bpm);
  }

  [Fact]
  public void EndsAtTheValueItselfRatherThanAPointerToIt() {
    FakeProcessMemory memory = Rekordbox(bpm: 174.0f);

    Assert.True(PointerChain.Parse("5E09790 0 2C0 1A0").TryResolve(memory, ModuleBase, out long address));
    Assert.Equal(Sub + 0x1A0, address);
  }

  [Fact]
  public void StopsAtANullPointerInsteadOfReadingNearZero() {
    // An empty deck slot holds null. Following it would read the null page plus an
    // offset, which fails in the real process and must fail the same way here.
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x5E09790, 0x1998_0000_0000);
    memory.PutPointer(0x1998_0000_0000, 0);

    Assert.False(PointerChain.Parse("5E09790 0 2C0 1A0").TryResolve(memory, ModuleBase, out _));
  }

  [Fact]
  public void RefusesAPointerThatIsNotAligned() {
    // Heap objects are 8-byte aligned, so a value that is not was never a pointer:
    // most likely a chain from another version reading the middle of some field.
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, Deck + 3);
    memory.Put(Deck, new byte[0x1000]);

    Assert.False(PointerChain.Parse("100 0").TryResolve(memory, ModuleBase, out _));
  }

  [Fact]
  public void RefusesAPointerAboveUserSpace() {
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, unchecked((long)0xFFFF_8000_0000_0000));

    Assert.False(PointerChain.Parse("100 0").TryResolve(memory, ModuleBase, out _));
  }

  [Fact]
  public void FailsWhenAHopCannotBeRead() {
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, Deck);

    Assert.False(PointerChain.Parse("100 8 10").TryResolve(memory, ModuleBase, out _));
  }

  [Fact]
  public void AChainOfOneOffsetIsAStaticInTheModule() {
    var memory = new FakeProcessMemory();
    memory.Put(ModuleBase + 0x40, [3]);

    Assert.True(PointerChain.Parse("40").TryReadByte(memory, ModuleBase, out byte value));
    Assert.Equal(3, value);
  }

  [Fact]
  public void ReadsTheSamplePositionAsALittleEndianInteger() {
    FakeProcessMemory memory = Rekordbox(bpm: 175.0f);
    memory.PutInt64(Sub + 0x130, 641_827);

    Assert.True(PointerChain.Parse("5E09790 0 2C0 130").TryReadInt64(memory, ModuleBase, out long position));
    Assert.Equal(641_827, position);
  }

  [Fact]
  public void MovesOneHopForTheNextDeck() {
    // Deck chains differ only in the hop into the deck array. The first chain must be
    // left as it was, since the table holds it once and derives the others.
    PointerChain first = PointerChain.Parse("5E09790 0 2C0 1A0");
    PointerChain third = first.WithOffsetAdded(1, 2 * 8);

    Assert.Equal(new long[] { 0x5E09790, 0x10, 0x2C0, 0x1A0 }, third.Offsets);
    Assert.Equal(new long[] { 0x5E09790, 0, 0x2C0, 0x1A0 }, first.Offsets);
  }

  [Fact]
  public void ParsesHexWithOrWithoutAPrefix() {
    Assert.Equal(new long[] { 0x5F53DA8, 0x8, 0x3F0, 0 }, PointerChain.Parse("0x5F53DA8 8  0x3f0 0").Offsets);
  }

  [Theory]
  [InlineData("")]
  [InlineData("5E09790 zz")]
  [InlineData("5E09790 -8")]
  public void RefusesAChainThatIsNotHexOffsets(string text) {
    Assert.ThrowsAny<Exception>(() => PointerChain.Parse(text));
  }

  [Fact]
  public void WritesItselfBackInTheFormItWasParsedFrom() {
    Assert.Equal("5E09790 0 2C0 1A0", PointerChain.Parse("0x5e09790 0 2c0 1a0").ToString());
  }

  [Fact]
  public void ReadsAStringUpToItsNul() {
    // One mapped page holding the text and whatever follows its NUL, as a heap page
    // would: the read stops at the NUL, not at the end of the allowance.
    const long Text = 0x2000_0000_1000;
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, Deck);
    memory.PutPointer(Deck + 0x3F0, Text);
    byte[] page = new byte[0x1000];
    "C:/Users/someone/USBANLZ/1a2/b3c/ANLZ0000.DAT\0trailing"u8.CopyTo(page);
    memory.Put(Text, page);

    Assert.True(PointerChain.Parse("100 3F0 0").TryReadUtf8(memory, ModuleBase, 1024, out string path));
    Assert.Equal("C:/Users/someone/USBANLZ/1a2/b3c/ANLZ0000.DAT", path);
  }

  [Fact]
  public void ReadsAStringThatEndsJustBeforeAnUnmappedPage() {
    // The string's NUL is the last mapped byte. Reading the whole allowance in one go
    // would ask for bytes past it and fail; page by page, the first read is enough.
    const long Text = 0x2000_0000_0FF0;
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, Text);
    memory.Put(Text, [.. "ANLZ0000.DAT"u8, 0, 0, 0, 0]);

    Assert.True(PointerChain.Parse("100 0").TryReadUtf8(memory, ModuleBase, 1024, out string text));
    Assert.Equal("ANLZ0000.DAT", text);
  }

  [Fact]
  public void RefusesAStringWithoutANulInItsAllowance() {
    // Landing in the middle of a long run of text is a wrong chain, and a truncated
    // path would look like a real one.
    const long Text = 0x2000_0000_0000;
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, Text);
    memory.Put(Text, [.. Enumerable.Repeat((byte)'a', 0x1000)]);

    Assert.False(PointerChain.Parse("100 0").TryReadUtf8(memory, ModuleBase, 64, out _));
  }

  [Fact]
  public void RefusesBytesThatAreNotUtf8() {
    const long Text = 0x2000_0000_0000;
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x100, Text);
    memory.Put(Text, [0xC3, 0x28, 0x41, 0, .. new byte[0xFFC]]);

    Assert.False(PointerChain.Parse("100 0").TryReadUtf8(memory, ModuleBase, 64, out _));
  }

  /// <summary>The deck array for deck 1 as rekordbox 7.2.19 lays it out.</summary>
  private static FakeProcessMemory Rekordbox(float bpm) {
    const long Array = 0x1998_DE6A_A810;
    var memory = new FakeProcessMemory();
    memory.PutPointer(ModuleBase + 0x5E09790, Array);
    memory.PutPointer(Array, Deck);
    memory.PutPointer(Deck + 0x2C0, Sub);
    memory.PutSingle(Sub + 0x1A0, bpm);
    return memory;
  }
}
