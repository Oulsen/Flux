using System.Text;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// Appends one version's entry to <c>src/Flux.Rekordbox/chains.json</c>, in the shape
/// <see cref="RekordboxChains"/> reads. It writes the file by hand rather than
/// re-serializing: the committed file's exact formatting is what the diff for a new
/// version should show, one block added and nothing else touched.
/// </summary>
internal static class ChainTableWriter {
  internal static string Entry(
    Version version, int decks, DeckChain bpm, DeckChain position, DeckChain anlz, PointerChain master, string newLine) {
    var text = new StringBuilder();
    text.Append("    {").Append(newLine);
    text.Append($"      \"version\": \"{version.Major}.{version.Minor}.{version.Build}\",").Append(newLine);
    text.Append($"      \"decks\": {decks},").Append(newLine);
    text.Append($"      {Deck("bpm", bpm)},").Append(newLine);
    text.Append($"      {Deck("samplePosition", position)},").Append(newLine);
    text.Append($"      {Deck("anlzPath", anlz)},").Append(newLine);
    text.Append($"      \"masterDeck\": {{ \"chain\": \"{master}\" }}").Append(newLine);
    text.Append("    }");
    return text.ToString();
  }

  /// <summary>The line ending the file already uses, so an added entry does not mix them.</summary>
  internal static string NewLineOf(string path) => File.ReadAllText(path).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

  /// <summary>
  /// Writes <paramref name="entry"/> into the table at <paramref name="path"/>, and only if
  /// the result is a table Flux would load: a file the finder wrote and Flux then refused
  /// would stop Flux at start.
  /// </summary>
  internal static void Insert(string path, Version version, string entry) {
    string updated = Inserted(File.ReadAllText(path), version, entry);
    using(var check = new MemoryStream(Encoding.UTF8.GetBytes(updated))) {
      RekordboxChains.Load(check);
    }

    File.WriteAllText(path, updated);
  }

  /// <summary>
  /// <paramref name="content"/> with <paramref name="entry"/> added to the end of the
  /// <c>versions</c> array. Fails rather than overwrites if the version is already there:
  /// an existing entry has passed a restart, and a rerun that lands on different numbers
  /// is a reason to look, not to replace. The duplicate check reads the table rather than
  /// matching text, so a reformatted file cannot slip a second entry past it.
  /// </summary>
  internal static string Inserted(string content, Version version, string entry) {
    IReadOnlyDictionary<Version, RekordboxChains> table;
    using(var current = new MemoryStream(Encoding.UTF8.GetBytes(content))) {
      table = RekordboxChains.Load(current);
    }

    if(RekordboxChains.TryGet(table, version, out _)) {
      throw new InvalidOperationException($"chains.json already has {version.Major}.{version.Minor}.{version.Build}.");
    }

    int close = content.LastIndexOf(']');
    int lastEntry = close < 0 ? -1 : content.LastIndexOf('}', close);
    if(lastEntry < 0) {
      throw new InvalidDataException("chains.json has no versions array to add to.");
    }

    string newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    return $"{content[..(lastEntry + 1)]},{newLine}{entry}{content[(lastEntry + 1)..]}";
  }

  private static string Deck(string name, DeckChain deck) =>
    $"\"{name}\": {{ \"chain\": \"{deck.Chain}\", \"deckHop\": {deck.DeckHop}, \"deckStride\": {deck.Stride} }}";
}
