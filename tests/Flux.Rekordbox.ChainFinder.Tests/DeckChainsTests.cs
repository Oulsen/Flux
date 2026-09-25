using Xunit;

namespace Flux.Rekordbox.ChainFinder.Tests;

public class DeckChainsTests {
  private const long ModuleBase = 0x1_4000_0000;

  [Fact]
  public void FindsTheTempoChainThe7219LayoutWouldGive() {
    // Two decks with different tempos, arranged as rekordbox arranges them: a module
    // static -> deck array -> deck object -> sub object -> BPM. The search should land
    // on the deck-1 chain with the deck stride on the array hop, and read both decks.
    var memory = new FakeScannableMemory(ModuleBase);
    Layout.DeckArray(memory);
    Layout.Deck(memory, 0, bpm: 174.0f);
    Layout.Deck(memory, 1, bpm: 175.0f);

    List<DeckChain> found = DeckChains.FindTempo(new MemorySweep(memory), 174.0f, 175.0f);

    Assert.NotEmpty(found);
    Assert.Equal("5E09790 0 2C0 1A0", found[0].Chain.ToString());
    Assert.Equal(1, found[0].DeckHop);
    Assert.Equal(8, found[0].Stride);
    Assert.True(found[0].For(1).TryReadSingle(memory, ModuleBase, out float deck2));
    Assert.Equal(175.0f, deck2);
  }

  [Fact]
  public void FindsNoTempoChainWhenTheTemposAreNotThere() {
    var memory = new FakeScannableMemory(ModuleBase);
    Layout.DeckArray(memory);
    Layout.Deck(memory, 0, bpm: 174.0f);
    Layout.Deck(memory, 1, bpm: 175.0f);

    Assert.Empty(DeckChains.FindTempo(new MemorySweep(memory), 128.0f, 130.0f));
  }

  [Fact]
  public void FindsTheAnlzChainAndPicksDeck1() {
    // Two decks' ANLZ paths, plus a third path that belongs to neither deck (the
    // library is full of them). Only the two reached by a deck-stride pair count, and
    // the result is the lowest deck's chain.
    var memory = new FakeScannableMemory(ModuleBase);
    Layout.AnlzArray(memory);
    Layout.Anlz(memory, 0, "C:/USBANLZ/aaa/ANLZ0000.DAT");
    Layout.Anlz(memory, 1, "C:/USBANLZ/bbb/ANLZ0000.DAT");
    Layout.LoosePath(memory, "C:/USBANLZ/ccc/ANLZ0000.DAT");

    List<DeckChain> found = DeckChains.FindAnlzPath(new MemorySweep(memory));

    Assert.NotEmpty(found);
    Assert.Equal("5F53DA8 8 3F0 0", found[0].Chain.ToString());
    Assert.True(found[0].For(0).TryReadUtf8(memory, ModuleBase, 1024, out string deck1));
    Assert.Equal("C:/USBANLZ/aaa/ANLZ0000.DAT", deck1);
  }

  [Fact]
  public void TellsTheDecksFromTheLibraryByTheTempoTheirFilesCarry() {
    // The hazard found at the desk: rekordbox keeps the library as a list of ANLZ paths
    // eight bytes apart behind a static of its own, with chains the same length as the
    // decks'. Pairing alone cannot tell them apart and the library's files exist too.
    // What the deck's file has and the library's does not is the deck's tempo.
    using var files = new AnlzFiles();
    var memory = new FakeScannableMemory(ModuleBase);
    Layout.AnlzArray(memory);
    Layout.Anlz(memory, 0, files.Write("deck1", 174.0f));
    Layout.Anlz(memory, 1, files.Write("deck2", 175.0f));
    Layout.Library(memory, [files.Write("lib1", 128.0f), files.Write("lib2", 130.0f), files.Write("lib3", 132.0f)]);
    var sweep = new MemorySweep(memory);

    List<DeckChain> paired = DeckChains.FindAnlzPath(sweep);
    List<DeckChain> decks = [.. paired.Where(c => DeckChains.AnlzMatches(sweep, c, 0, 174.0f) && DeckChains.AnlzMatches(sweep, c, 1, 175.0f))];

    Assert.Contains(paired, c => c.Chain.ToString() == "61AA8B8 4F8 3F0 0");
    Assert.Equal(["5F53DA8 8 3F0 0"], decks.Select(c => c.Chain.ToString()));
  }

  [Fact]
  public void RefusesAPlayheadThatFollowsWhicheverDeckIsPlaying() {
    // The chain that fooled step 0: both of its "decks" were the playing deck. With deck
    // 1 playing, its deck 2 moves as well, which a deck's own playhead never does.
    var memory = new FakeScannableMemory(ModuleBase);
    Layout.DeckArray(memory);
    Layout.Deck(memory, 0, bpm: 174.0f);
    Layout.Deck(memory, 1, bpm: 175.0f);
    Layout.Playing(memory, deck: 0);
    Layout.FollowsPlaying(memory, deck: 0);
    var own = new DeckChain(PointerChain.Parse("5E09790 0 2C0 130"), 1, 8);
    var follows = new DeckChain(PointerChain.Parse("5E0A000 0 2C0 130"), 1, 8);

    List<DeckChain> advancing = DeckChains.Advancing(new MemorySweep(memory), [own, follows], playing: 0, still: 1, TimeSpan.FromMilliseconds(300));

    Assert.Equal([own], advancing);
  }

  [Fact]
  public void RefusesToStartTheMasterSearchOnDeck1() {
    // Round one lists every byte that holds the index, and a real process holds
    // billions of zeros: starting on deck 1 would run out of memory.
    var memory = new FakeScannableMemory(ModuleBase);
    memory.Region(0x300_0000_0000, 0x100);

    Assert.Throws<ArgumentException>(() => new MasterDeckSearch(new MemorySweep(memory)).Narrow(0));
  }

  /// <summary>The 7.2.19 shapes, spaced far apart so no search matches across structures.</summary>
  private static class Layout {
    private const long DeckArray0 = 0x200_0000_0000;
    private const long AnlzArray0 = 0x201_0000_0000;

    internal static void DeckArray(FakeScannableMemory memory) {
      memory.Region(ModuleBase + 0x5E09790, 8);
      memory.Pointer(ModuleBase + 0x5E09790, DeckArray0);
      memory.Region(DeckArray0, 0x100);
    }

    internal static void Deck(FakeScannableMemory memory, int deck, float bpm) {
      long deckObject = 0x200_1000_0000 + (deck * 0x1000_0000);
      long sub = 0x200_5000_0000 + (deck * 0x1000_0000);
      memory.Pointer(DeckArray0 + (deck * 8), deckObject);
      memory.Region(deckObject, 0x400);
      memory.Pointer(deckObject + 0x2C0, sub);
      memory.Region(sub, 0x400);
      memory.Single(sub + 0x1A0, bpm);
    }

    internal static void AnlzArray(FakeScannableMemory memory) {
      memory.Region(ModuleBase + 0x5F53DA8, 8);
      memory.Pointer(ModuleBase + 0x5F53DA8, AnlzArray0);
      memory.Region(AnlzArray0, 0x100);
    }

    internal static void Anlz(FakeScannableMemory memory, int deck, string path) {
      long anlzObject = 0x201_1000_0000 + (deck * 0x1000_0000);
      long textBlock = 0x201_5000_0000 + (deck * 0x1000_0000);
      memory.Pointer(AnlzArray0 + 8 + (deck * 8), anlzObject);
      memory.Region(anlzObject, 0x400);
      // The string sits 8-aligned, as a real one does; the zero-filled byte before it
      // is the NUL that marks its start.
      memory.Pointer(anlzObject + 0x3F0, textBlock + 8);
      memory.Region(textBlock, 0x2000);
      memory.Utf8(textBlock + 8, path);
    }

    /// <summary>A library list: a static of its own, entries eight apart from 0x4F8, each an ANLZ path.</summary>
    internal static void Library(FakeScannableMemory memory, string[] paths) {
      const long list = 0x203_0000_0000;
      memory.Region(ModuleBase + 0x61AA8B8, 8);
      memory.Pointer(ModuleBase + 0x61AA8B8, list);
      memory.Region(list, 0x800);
      for(int i = 0; i < paths.Length; i++) {
        long entry = 0x203_1000_0000 + (i * 0x1000_0000);
        long text = 0x204_1000_0000 + (i * 0x1000_0000);
        memory.Pointer(list + 0x4F8 + (i * 8), entry);
        memory.Region(entry, 0x400);
        memory.Pointer(entry + 0x3F0, text + 8);
        memory.Region(text, 0x2000);
        memory.Utf8(text + 8, paths[i]);
      }
    }

    /// <summary>Deck <paramref name="deck"/>'s own playhead, advancing as if it plays.</summary>
    internal static void Playing(FakeScannableMemory memory, int deck) =>
      memory.Clock(0x200_5000_0000 + (deck * 0x1000_0000) + 0x130);

    /// <summary>
    /// A second static whose array holds the playing deck's object in both slots, the
    /// way the list that fooled step 0 did: its deck 1 and deck 2 are the same deck.
    /// </summary>
    internal static void FollowsPlaying(FakeScannableMemory memory, int deck) {
      const long array = 0x205_0000_0000;
      long deckObject = 0x200_1000_0000 + (deck * 0x1000_0000);
      memory.Region(ModuleBase + 0x5E0A000, 8);
      memory.Pointer(ModuleBase + 0x5E0A000, array);
      memory.Region(array, 0x100);
      memory.Pointer(array, deckObject);
      memory.Pointer(array + 8, deckObject);
    }

    internal static void LoosePath(FakeScannableMemory memory, string path) {
      const long block = 0x202_0000_0000;
      memory.Region(block, 0x400);
      memory.Utf8(block + 8, path);
    }
  }
}
