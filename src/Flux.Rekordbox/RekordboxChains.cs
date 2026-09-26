using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flux.Rekordbox;

/// <summary>What one read of rekordbox found for one deck. A null is a value that could not be read.</summary>
internal readonly record struct DeckReading(float? Bpm, long? SamplePosition, string? AnlzPath);

/// <summary>
/// What one read of rekordbox found. <see cref="MasterDeck"/> is 0-based, and null when
/// it could not be read or named a deck the version does not have.
/// </summary>
internal sealed record RekordboxReading(IReadOnlyList<DeckReading> Decks, int? MasterDeck);

/// <summary>
/// The pointer chains for one rekordbox version, and the table of every version Flux
/// knows.
///
/// The table is a data file, <c>chains.json</c>, compiled into the assembly, and it is
/// keyed by the exact version: a rekordbox Flux has no entry for is read not at all,
/// newer ones included. That is the whole safety model. Chains move between versions,
/// and a chain from one version walked in the next does not reliably break - at the
/// desk one read a plausible BPM from the wrong structure until the decks changed -
/// so "closest known version" would be the one policy guaranteed to fail quietly. A
/// new version gets its own entry once its chains are found and have survived a
/// restart; <c>docs/rekordbox-chains.md</c> says how, and what each entry rests on.
///
/// The table is checked when it loads, so a malformed entry stops Flux at start with
/// the version and field named, rather than reading from the wrong place later.
/// </summary>
internal sealed class RekordboxChains {
  private const string ResourceName = "Flux.Rekordbox.chains.json";

  /// <summary>An ANLZ path is a Windows path; this is generous for one and short for anything else.</summary>
  private const int MaxPathBytes = 1024;

  private static readonly Lazy<IReadOnlyDictionary<Version, RekordboxChains>> BuiltIn = new(LoadBuiltIn);

  private readonly DeckChain bpm;
  private readonly DeckChain samplePosition;
  private readonly DeckChain anlzPath;
  private readonly PointerChain masterDeck;

  private RekordboxChains(Version version, int decks, DeckChain bpm, DeckChain samplePosition, DeckChain anlzPath, PointerChain masterDeck) {
    this.Version = version;
    this.Decks = decks;
    this.bpm = bpm;
    this.samplePosition = samplePosition;
    this.anlzPath = anlzPath;
    this.masterDeck = masterDeck;
  }

  /// <summary>The rekordbox version these chains were found in, three parts.</summary>
  internal Version Version { get; }

  internal int Decks { get; }

  /// <summary>The versions the built-in table knows, oldest first.</summary>
  internal static IReadOnlyList<Version> KnownVersions => [.. BuiltIn.Value.Keys.Order()];

  /// <summary>
  /// The chains for <paramref name="version"/>, as read from the executable. rekordbox
  /// versions have three parts and the file carries a fourth that has always been
  /// zero; one that is not is a build Flux has not seen, and counts as unknown.
  /// </summary>
  internal static bool TryGet(Version version, out RekordboxChains chains) =>
    TryGet(BuiltIn.Value, version, out chains);

  internal static bool TryGet(IReadOnlyDictionary<Version, RekordboxChains> table, Version version, out RekordboxChains chains) {
    ArgumentNullException.ThrowIfNull(version);
    chains = null!;
    if(version.Revision > 0) {
      return false;
    }

    return table.TryGetValue(new Version(version.Major, version.Minor, Math.Max(version.Build, 0)), out chains!);
  }

  /// <summary>Reads and checks a table in the <c>chains.json</c> format.</summary>
  internal static IReadOnlyDictionary<Version, RekordboxChains> Load(Stream json) {
    ArgumentNullException.ThrowIfNull(json);
    ChainTableFile? file;
    try {
      file = JsonSerializer.Deserialize(json, ChainTableJson.Default.ChainTableFile);
    } catch(JsonException error) {
      throw new InvalidDataException($"The rekordbox chain table is not valid JSON: {error.Message}", error);
    }

    if(file?.Versions is not { Count: > 0 } entries) {
      throw new InvalidDataException("The rekordbox chain table has no versions.");
    }

    var table = new Dictionary<Version, RekordboxChains>();
    foreach(ChainTableEntry entry in entries) {
      RekordboxChains chains = FromEntry(entry);
      if(!table.TryAdd(chains.Version, chains)) {
        throw new InvalidDataException($"The rekordbox chain table lists {chains.Version} twice.");
      }
    }

    return table;
  }

  /// <summary>
  /// Every deck and the master deck, in one pass. Nothing here decides what a value
  /// means - a BPM of 0 is an empty deck, and that is for the sync source to say - it
  /// only refuses what cannot be a value at all.
  /// </summary>
  internal RekordboxReading Read(IProcessMemory memory, long moduleBase) {
    ArgumentNullException.ThrowIfNull(memory);
    var decks = new DeckReading[this.Decks];
    for(int deck = 0; deck < this.Decks; deck++) {
      float? bpm = this.bpm.For(deck).TryReadSingle(memory, moduleBase, out float b) && float.IsFinite(b) ? b : null;
      long? position = this.samplePosition.For(deck).TryReadInt64(memory, moduleBase, out long p) ? p : null;
      string? path = this.anlzPath.For(deck).TryReadUtf8(memory, moduleBase, MaxPathBytes, out string a) ? a : null;
      decks[deck] = new DeckReading(bpm, position, path);
    }

    int? master = this.masterDeck.TryReadByte(memory, moduleBase, out byte m) && m < this.Decks ? m : null;
    return new RekordboxReading(decks, master);
  }

  /// <summary>
  /// The master byte as rekordbox keeps it, before <see cref="Read"/> turns anything
  /// that is not a deck into null. The first 7.2.19 chain read 255 there while LINK was
  /// on. For checking the chain rather than for reading.
  /// </summary>
  internal bool TryReadMasterByte(IProcessMemory memory, long moduleBase, out byte value) {
    ArgumentNullException.ThrowIfNull(memory);
    return this.masterDeck.TryReadByte(memory, moduleBase, out value);
  }

  private static IReadOnlyDictionary<Version, RekordboxChains> LoadBuiltIn() {
    using Stream json = typeof(RekordboxChains).Assembly.GetManifestResourceStream(ResourceName)
      ?? throw new InvalidOperationException($"The rekordbox chain table '{ResourceName}' is not in the assembly.");
    return Load(json);
  }

  private static RekordboxChains FromEntry(ChainTableEntry entry) {
    if(!Version.TryParse(entry.Version, out Version? version) || version.Build < 0 || version.Revision >= 0) {
      throw new InvalidDataException($"'{entry.Version}' in the rekordbox chain table is not a three-part version.");
    }

    if(entry.Decks is not (>= 1 and <= 4) || entry.Decks is not int decks) {
      throw new InvalidDataException($"rekordbox {version}: decks must be 1 to 4, not {entry.Decks?.ToString() ?? "missing"}.");
    }

    return new RekordboxChains(
      version,
      decks,
      DeckChain.From(entry.Bpm, version, "bpm", decks),
      DeckChain.From(entry.SamplePosition, version, "samplePosition", decks),
      DeckChain.From(entry.AnlzPath, version, "anlzPath", decks),
      Chain(entry.MasterDeck?.Chain, version, "masterDeck"));
  }

  private static PointerChain Chain(string? text, Version version, string field) {
    if(string.IsNullOrWhiteSpace(text)) {
      throw new InvalidDataException($"rekordbox {version}: {field} has no chain.");
    }

    try {
      return PointerChain.Parse(text);
    } catch(Exception error) when(error is FormatException or ArgumentException) {
      throw new InvalidDataException($"rekordbox {version}: {field}: {error.Message}", error);
    }
  }

  /// <summary>
  /// A chain written for deck 1, and which hop moves by how much for each deck after it.
  /// </summary>
  private sealed class DeckChain {
    private readonly PointerChain[] perDeck;

    private DeckChain(PointerChain[] perDeck) => this.perDeck = perDeck;

    internal PointerChain For(int deck) => this.perDeck[deck];

    internal static DeckChain From(ChainTableDeckChain? entry, Version version, string field, int decks) {
      // Both are required rather than defaulted: a missing hop would read as 0, the
      // module offset, and every deck after the first would read from another static.
      PointerChain first = Chain(entry?.Chain, version, field);
      if(entry!.DeckHop is not int hop || hop < 0 || hop >= first.Offsets.Count) {
        throw new InvalidDataException(
          $"rekordbox {version}: {field}.deckHop {entry.DeckHop?.ToString() ?? "is missing and"} is not a hop of '{first}'.");
      }

      if(entry.DeckStride is not long stride || stride <= 0) {
        throw new InvalidDataException($"rekordbox {version}: {field}.deckStride must be given and positive.");
      }

      var perDeck = new PointerChain[decks];
      for(int deck = 0; deck < decks; deck++) {
        perDeck[deck] = first.WithOffsetAdded(hop, deck * stride);
      }

      return new DeckChain(perDeck);
    }
  }
}

internal sealed record ChainTableFile(List<ChainTableEntry>? Versions);

internal sealed record ChainTableEntry(
  string? Version,
  int? Decks,
  ChainTableDeckChain? Bpm,
  ChainTableDeckChain? SamplePosition,
  ChainTableDeckChain? AnlzPath,
  ChainTableChain? MasterDeck);

internal sealed record ChainTableDeckChain(string? Chain, int? DeckHop, long? DeckStride);

internal sealed record ChainTableChain(string? Chain);

/// <summary>
/// Source-generated, like the other JSON Flux reads, so the format is one declaration.
/// Unknown fields are an error, and the numeric fields are nullable so that a missing
/// one is too: a misspelt or absent <c>deckHop</c> would otherwise read as 0 and move
/// the wrong hop for every deck after the first.
/// </summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
  ReadCommentHandling = JsonCommentHandling.Disallow)]
[JsonSerializable(typeof(ChainTableFile))]
internal sealed partial class ChainTableJson: JsonSerializerContext {
}
