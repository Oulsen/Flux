using System.Text;
using Xunit;

namespace Flux.Rekordbox.Tests;

public class RekordboxChainsTests {
  private const long ModuleBase = 0x7FF7_B00B_0000;

  [Fact]
  public void KnowsRekordbox7219AndNothingOlder() {
    // 7.2.19 is where the table starts: the first version whose chains were found and
    // survived a restart. Everything else has to be added, one version at a time.
    Assert.Equal(new Version(7, 2, 19), RekordboxChains.KnownVersions[0]);
    Assert.True(RekordboxChains.TryGet(new Version(7, 2, 19, 0), out RekordboxChains chains));
    Assert.Equal(4, chains.Decks);
  }

  [Theory]
  [InlineData(7, 2, 20, 0)]
  [InlineData(7, 3, 0, 0)]
  [InlineData(7, 2, 18, 0)]
  [InlineData(7, 2, 2, 0)]
  [InlineData(7, 2, 19, 1)]
  public void ReadsNothingFromAVersionItHasNoEntryFor(int major, int minor, int build, int revision) {
    // Newer included. A chain from 7.2.19 walked in 7.2.20 need not break; it can
    // read a believable number from the wrong structure, which is worse than silence.
    Assert.False(RekordboxChains.TryGet(new Version(major, minor, build, revision), out _));
  }

  [Fact]
  public void ReadsEveryDeckAndTheMasterFromTheLayoutFoundIn7219() {
    RekordboxChains chains = Known();
    var memory = new FakeProcessMemory();
    Rekordbox7219.Deck(memory, 0, bpm: 87f, position: 7_276, path: "C:/USBANLZ/a/ANLZ0000.DAT");
    Rekordbox7219.Deck(memory, 1, bpm: 87f, position: 1_146, path: "C:/USBANLZ/b/ANLZ0000.DAT");
    Rekordbox7219.Deck(memory, 2, bpm: 174f, position: 6_791, path: "C:/USBANLZ/c/ANLZ0000.DAT");
    Rekordbox7219.Deck(memory, 3, bpm: 172f, position: 2_249, path: "C:/USBANLZ/d/ANLZ0000.DAT");
    Rekordbox7219.Master(memory, 3);

    RekordboxReading reading = chains.Read(memory, ModuleBase);

    Assert.Equal(new float?[] { 87f, 87f, 174f, 172f }, reading.Decks.Select(d => d.Bpm));
    Assert.Equal(new long?[] { 7_276, 1_146, 6_791, 2_249 }, reading.Decks.Select(d => d.SamplePosition));
    Assert.Equal("C:/USBANLZ/d/ANLZ0000.DAT", reading.Decks[3].AnlzPath);
    Assert.Equal(3, reading.MasterDeck);
  }

  [Fact]
  public void LeavesADeckItCannotReadEmptyRatherThanFailingTheRest() {
    RekordboxChains chains = Known();
    var memory = new FakeProcessMemory();
    Rekordbox7219.Deck(memory, 0, bpm: 174f, position: 1, path: "C:/USBANLZ/a/ANLZ0000.DAT");
    Rekordbox7219.Master(memory, 0);

    RekordboxReading reading = chains.Read(memory, ModuleBase);

    Assert.Equal(174f, reading.Decks[0].Bpm);
    Assert.Null(reading.Decks[1].Bpm);
    Assert.Null(reading.Decks[1].AnlzPath);
    Assert.Equal(0, reading.MasterDeck);
  }

  [Fact]
  public void RefusesAMasterDeckTheVersionDoesNotHave() {
    RekordboxChains chains = Known();
    var memory = new FakeProcessMemory();
    Rekordbox7219.Master(memory, 7);

    Assert.Null(chains.Read(memory, ModuleBase).MasterDeck);
  }

  [Theory]
  [InlineData("""{"versions":[]}""", "no versions")]
  [InlineData("""{"versions":[{"version":"7.2","decks":4}]}""", "three-part")]
  [InlineData("""{"versions":[{"version":"7.2.19.0","decks":4}]}""", "three-part")]
  [InlineData("""{"versions":[{"version":"7.2.19","decks":5}]}""", "decks")]
  [InlineData("""{"versions":[{"version":"7.2.19"}]}""", "decks")]
  public void RefusesATableEntryThatIsNotComplete(string json, string named) {
    InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
    Assert.Contains(named, error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusesADeckChainWithoutItsHop() {
    // Defaulting the hop to 0 would move the module offset for every deck after the
    // first: each of them would read from another static and look fine.
    string json = Entry("7.2.19").Replace(""","deckHop": 1""", "", StringComparison.Ordinal);

    InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
    Assert.Contains("deckHop", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusesAMisspeltField() {
    string json = Entry("7.2.19").Replace("deckStride", "deckStrde", StringComparison.Ordinal);

    Assert.Throws<InvalidDataException>(() => Load(json));
  }

  [Fact]
  public void RefusesTheSameVersionTwice() {
    string twice = Entry("7.2.19").Replace("]}", "," + Entry("7.2.19")[13..], StringComparison.Ordinal);

    InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(twice));
    Assert.Contains("twice", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusesAChainThatIsNotHex() {
    string json = Entry("7.2.19").Replace("61A82C0", "61A82CZ", StringComparison.Ordinal);

    InvalidDataException error = Assert.Throws<InvalidDataException>(() => Load(json));
    Assert.Contains("masterDeck", error.Message, StringComparison.Ordinal);
  }

  private static RekordboxChains Known() {
    Assert.True(RekordboxChains.TryGet(new Version(7, 2, 19, 0), out RekordboxChains chains));
    return chains;
  }

  private static IReadOnlyDictionary<Version, RekordboxChains> Load(string json) =>
    RekordboxChains.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

  private static string Entry(string version) =>
    $$$"""{"versions":[{"version":"{{{version}}}","decks":4,"bpm":{"chain":"5E09790 0 2C0 1A0","deckHop": 1,"deckStride":8},"samplePosition":{"chain":"5E09790 0 2C0 130","deckHop": 1,"deckStride":8},"anlzPath":{"chain":"5F53DA8 8 3F0 0","deckHop": 1,"deckStride":8},"masterDeck":{"chain":"61A82C0 3A0 378 254"}}]}""";

  /// <summary>Memory laid out the way rekordbox 7.2.19 lays it out, per <c>chains.json</c>.</summary>
  private static class Rekordbox7219 {
    private const long DeckArray = 0x1998_DE6A_A810;
    private const long AnlzArray = 0x1998_0010_0000;

    internal static void Deck(FakeProcessMemory memory, int deck, float bpm, long position, string path) {
      long deckObject = 0x1998_2000_0000 + (deck * 0x10000);
      long sub = deckObject + 0x8000;
      long anlzObject = 0x1998_3000_0000 + (deck * 0x10000);
      long text = anlzObject + 0x8000;

      memory.PutPointer(ModuleBase + 0x5E09790, DeckArray);
      memory.PutPointer(DeckArray + (deck * 8), deckObject);
      memory.PutPointer(deckObject + 0x2C0, sub);
      memory.PutSingle(sub + 0x1A0, bpm);
      memory.PutInt64(sub + 0x130, position);

      memory.PutPointer(ModuleBase + 0x5F53DA8, AnlzArray);
      memory.PutPointer(AnlzArray + 8 + (deck * 8), anlzObject);
      memory.PutPointer(anlzObject + 0x3F0, text);
      byte[] page = new byte[0x1000];
      Encoding.UTF8.GetBytes(path).CopyTo(page, 0);
      memory.Put(text, page);
    }

    internal static void Master(FakeProcessMemory memory, byte master) {
      memory.PutPointer(ModuleBase + 0x61A82C0, 0x1998_4000_0000);
      memory.PutPointer(0x1998_4000_0000 + 0x3A0, 0x1998_5000_0000);
      memory.PutPointer(0x1998_5000_0000 + 0x378, 0x1998_6000_0000);
      memory.Put(0x1998_6000_0000 + 0x254, [master]);
    }
  }
}
