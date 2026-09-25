using Xunit;
using static Flux.Rekordbox.Tests.AnlzFixture;

namespace Flux.Rekordbox.Tests;

public class AnlzPhrasesTests {
  [Fact]
  public void ReadsStartBeatsEndAndFillsOfAPlainTag() {
    // Plain is what rekordbox's own library holds; every file in it read that way.
    AnlzPhraseList phrases = AnlzPhrases.Read(Phrases(2, 97, new RawPhrase(1, 1), new RawPhrase(33, 9, Fill: 61), new RawPhrase(65, 10)))!;

    Assert.Equal(PhraseMood.Mid, phrases.Mood);
    Assert.Equal(97, phrases.EndBeat);
    Assert.Equal(
      [new AnlzPhrase(1, PhraseKind.Intro, 0, null), new AnlzPhrase(33, PhraseKind.Chorus, 0, 61), new AnlzPhrase(65, PhraseKind.Outro, 0, null)],
      phrases.Phrases);
  }

  [Fact]
  public void ReadsAMaskedTagAsTheSamePhrases() {
    RawPhrase[] raw = [new(1, 1, K1: 1), new(17, 2, K2: 1), new(49, 5, K1: 1, Fill: 61), new(65, 6)];

    Assert.Equal(AnlzPhrases.Read(Phrases(1, 97, raw))!.Phrases, AnlzPhrases.Read(MaskedPhrases(1, 97, raw))!.Phrases);
  }

  [Fact]
  public void LabelsTheMidMoodsSixVerses() {
    RawPhrase[] raw = [.. Enumerable.Range(1, 10).Select(kind => new RawPhrase(1 + ((kind - 1) * 16), kind))];

    Assert.Equal(
      [
        (PhraseKind.Intro, 0), (PhraseKind.Verse, 1), (PhraseKind.Verse, 2), (PhraseKind.Verse, 3), (PhraseKind.Verse, 4),
        (PhraseKind.Verse, 5), (PhraseKind.Verse, 6), (PhraseKind.Bridge, 0), (PhraseKind.Chorus, 0), (PhraseKind.Outro, 0),
      ],
      Labels(Phrases(2, 200, raw)));
  }

  [Fact]
  public void LabelsTheLowMoodsThreeNumbersEachAsVerseOneAndTwo() {
    RawPhrase[] raw = [.. Enumerable.Range(1, 10).Select(kind => new RawPhrase(1 + ((kind - 1) * 16), kind))];

    Assert.Equal(
      [
        (PhraseKind.Intro, 0), (PhraseKind.Verse, 1), (PhraseKind.Verse, 1), (PhraseKind.Verse, 1), (PhraseKind.Verse, 2),
        (PhraseKind.Verse, 2), (PhraseKind.Verse, 2), (PhraseKind.Bridge, 0), (PhraseKind.Chorus, 0), (PhraseKind.Outro, 0),
      ],
      Labels(Phrases(3, 200, raw)));
  }

  [Fact]
  public void LabelsTheHighMoodsVariantsFromTheFlagBytes() {
    // The published table, every row; the flag combinations are the ones the library held.
    byte[] file = Phrases(1, 200,
      new RawPhrase(1, 1, K1: 1), new RawPhrase(17, 1),
      new RawPhrase(33, 2), new RawPhrase(49, 2, K3: 1), new RawPhrase(65, 2, K2: 1),
      new RawPhrase(81, 3),
      new RawPhrase(97, 5, K1: 1), new RawPhrase(113, 5),
      new RawPhrase(129, 6, K1: 1), new RawPhrase(145, 6));

    Assert.Equal(
      [
        (PhraseKind.Intro, 1), (PhraseKind.Intro, 2),
        (PhraseKind.Up, 1), (PhraseKind.Up, 2), (PhraseKind.Up, 3),
        (PhraseKind.Down, 0),
        (PhraseKind.Chorus, 1), (PhraseKind.Chorus, 2),
        (PhraseKind.Outro, 1), (PhraseKind.Outro, 2),
      ],
      Labels(file));
  }

  [Theory]
  [InlineData(1, 4)]
  [InlineData(1, 7)]
  [InlineData(2, 11)]
  [InlineData(3, 0)]
  public void RefusesAKindOutsideTheMoodsTable(int mood, int kind) {
    Assert.Null(AnlzPhrases.Read(Phrases(mood, 100, new RawPhrase(1, 1), new RawPhrase(33, kind))));
  }

  [Fact]
  public void RefusesAnUpWithAFlagCombinationTheTableDoesNotHave() {
    Assert.Null(AnlzPhrases.Read(Phrases(1, 100, new RawPhrase(1, 2, K2: 1, K3: 1))));
    Assert.Null(AnlzPhrases.Read(Phrases(1, 100, new RawPhrase(1, 5, K1: 2))));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(4)]
  public void RefusesAnUnknownMood(int mood) {
    Assert.Null(AnlzPhrases.Read(Phrases(mood, 100, new RawPhrase(1, 1))));
  }

  [Fact]
  public void RefusesPhrasesThatDoNotMoveForward() {
    Assert.Null(AnlzPhrases.Read(Phrases(2, 100, new RawPhrase(33, 1), new RawPhrase(33, 9))));
    Assert.Null(AnlzPhrases.Read(Phrases(2, 100, new RawPhrase(0, 1))));
    Assert.Null(AnlzPhrases.Read(Phrases(2, 20, new RawPhrase(1, 1), new RawPhrase(33, 9))));
  }

  [Fact]
  public void RefusesATagNumberedNeitherPlainNorMasked() {
    byte[] file = Phrases(2, 100, new RawPhrase(1, 1), new RawPhrase(33, 9));
    int secondIndex = file.Length - 24;
    file[secondIndex + 1] = 7;

    Assert.Null(AnlzPhrases.Read(file));
  }

  [Fact]
  public void HasNoPhrasesForAFileWithoutTheTag() {
    Assert.Null(AnlzPhrases.Read(SteadyGrid(174.0f, 8)));
    Assert.Null(AnlzPhrases.Read(Phrases(2, 1)));
  }

  private static IEnumerable<(PhraseKind, int)> Labels(byte[] file) =>
    AnlzPhrases.Read(file)!.Phrases.Select(phrase => (phrase.Kind, phrase.Variant));
}
