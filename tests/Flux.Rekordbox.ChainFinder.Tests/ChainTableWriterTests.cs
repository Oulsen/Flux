using Xunit;

namespace Flux.Rekordbox.ChainFinder.Tests;

public class ChainTableWriterTests {
  private const string Seed = """
    {
      "versions": [
        {
          "version": "7.2.19",
          "decks": 4,
          "bpm": { "chain": "5E09790 0 2C0 1A0", "deckHop": 1, "deckStride": 8 },
          "samplePosition": { "chain": "5E09790 0 2C0 130", "deckHop": 1, "deckStride": 8 },
          "anlzPath": { "chain": "5F53DA8 8 3F0 0", "deckHop": 1, "deckStride": 8 },
          "masterDeck": { "chain": "61A82C0 3A0 378 254" }
        }
      ]
    }
    """;

  [Fact]
  public void WritesAnEntryTheChainTableCanLoad() {
    // The whole point: what the tool appends has to be readable by the table it feeds.
    string path = Path.Combine(Path.GetTempPath(), $"chains-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, Seed);
    try {
      string entry = ChainTableWriter.Entry(
        new Version(7, 3, 0),
        4,
        Deck("5E09790 0 2D0 1A0"),
        Deck("5E09790 0 2D0 130"),
        Deck("5F60000 8 3F0 0"),
        PointerChain.Parse("61B0000 3A0 378 254"),
        ChainTableWriter.NewLineOf(path));
      ChainTableWriter.Insert(path, new Version(7, 3, 0), entry);

      IReadOnlyDictionary<Version, RekordboxChains> table;
      using(FileStream json = File.OpenRead(path)) {
        table = RekordboxChains.Load(json);
      }

      Assert.True(RekordboxChains.TryGet(table, new Version(7, 2, 19, 0), out _));
      Assert.True(RekordboxChains.TryGet(table, new Version(7, 3, 0, 0), out RekordboxChains added));
      Assert.Equal(new Version(7, 3, 0), added.Version);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void RefusesToWriteAVersionAlreadyInTheTable() {
    // An existing entry has passed a restart. A rerun landing on other numbers is a
    // reason to look, not to overwrite silently.
    string path = Path.Combine(Path.GetTempPath(), $"chains-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, Seed);
    try {
      string entry = ChainTableWriter.Entry(
        new Version(7, 2, 19), 4, Deck("1 2 3"), Deck("1 2 3"), Deck("1 2 3"), PointerChain.Parse("1 2 3"), "\n");

      Assert.Throws<InvalidOperationException>(() => ChainTableWriter.Insert(path, new Version(7, 2, 19), entry));
      Assert.Equal(Seed, File.ReadAllText(path));
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void RefusesToWriteATableFluxWouldNotLoad() {
    // A deck hop past the end of its chain is an entry RekordboxChains rejects at load,
    // which would stop Flux at start. The file has to stay as it was.
    string path = Path.Combine(Path.GetTempPath(), $"chains-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, Seed);
    try {
      var broken = new DeckChain(PointerChain.Parse("5E09790 0"), 5, 8);
      string entry = ChainTableWriter.Entry(
        new Version(7, 3, 0), 4, broken, broken, broken, PointerChain.Parse("61B0000 3A0"), ChainTableWriter.NewLineOf(path));

      Assert.Throws<InvalidDataException>(() => ChainTableWriter.Insert(path, new Version(7, 3, 0), entry));
      Assert.Equal(Seed, File.ReadAllText(path));
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void ReplacesOneVersionsEntryAndLeavesTheRestAsItWas() {
    // Repairing an entry whose master chain moved: the new block goes where the old one
    // was, the other version is untouched byte for byte, and Flux loads the result.
    const string seed = """
      {
        "versions": [
          {
            "version": "7.2.19",
            "decks": 4,
            "bpm": { "chain": "5E09790 0 2C0 1A0", "deckHop": 1, "deckStride": 8 },
            "samplePosition": { "chain": "5E09790 0 2C0 130", "deckHop": 1, "deckStride": 8 },
            "anlzPath": { "chain": "5F53DA8 8 3F0 0", "deckHop": 1, "deckStride": 8 },
            "masterDeck": { "chain": "61A82C0 3A0 378 254" }
          },
          {
            "version": "7.3.0",
            "decks": 2,
            "bpm": { "chain": "1 2 3", "deckHop": 1, "deckStride": 8 },
            "samplePosition": { "chain": "1 2 4", "deckHop": 1, "deckStride": 8 },
            "anlzPath": { "chain": "1 2 5", "deckHop": 1, "deckStride": 8 },
            "masterDeck": { "chain": "1 2 6" }
          }
        ]
      }
      """;
    string path = Path.Combine(Path.GetTempPath(), $"chains-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, seed);
    try {
      string entry = ChainTableWriter.Entry(
        new Version(7, 2, 19), 4, Deck("5E09790 0 2C0 1A0"), Deck("5E09790 0 2C0 130"), Deck("5F53DA8 8 3F0 0"),
        PointerChain.Parse("61B0000 10 20"), ChainTableWriter.NewLineOf(path));

      ChainTableWriter.Replace(path, new Version(7, 2, 19), entry);

      string written = File.ReadAllText(path);
      Assert.Contains("\"masterDeck\": { \"chain\": \"61B0000 10 20\" }", written, StringComparison.Ordinal);
      Assert.DoesNotContain("61A82C0", written, StringComparison.Ordinal);
      Assert.Contains("\"masterDeck\": { \"chain\": \"1 2 6\" }", written, StringComparison.Ordinal);
      Assert.Equal("61B0000 10 20", ChainTableWriter.Existing(written, new Version(7, 2, 19))!.MasterDeck!.Chain);
      using FileStream json = File.OpenRead(path);
      Assert.Equal(2, RekordboxChains.Load(json).Count);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void RefusesToReplaceAVersionNotInTheTable() {
    string path = Path.Combine(Path.GetTempPath(), $"chains-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, Seed);
    try {
      string entry = ChainTableWriter.Entry(
        new Version(7, 3, 0), 4, Deck("1 2 3"), Deck("1 2 3"), Deck("1 2 3"), PointerChain.Parse("1 2 3"), "\n");

      Assert.Throws<InvalidOperationException>(() => ChainTableWriter.Replace(path, new Version(7, 3, 0), entry));
      Assert.Equal(Seed, File.ReadAllText(path));
    } finally {
      File.Delete(path);
    }
  }

  private static DeckChain Deck(string chain) => new(PointerChain.Parse(chain), 1, 8);
}
