using Xunit;

namespace Flux.Beat.Tests;

public class PhraseMapTests {
  private static readonly PhraseMap Song = new PhraseMap(
    [
      new PhraseMark(PhraseKind.Intro, 1, StartMicros: 1_000),
      new PhraseMark(PhraseKind.Up, 2, StartMicros: 5_000),
      new PhraseMark(PhraseKind.Chorus, 1, StartMicros: 9_000),
    ],
    EndMicros: 13_000);

  [Theory]
  [InlineData(1_000, PhraseKind.Intro)]
  [InlineData(4_999, PhraseKind.Intro)]
  [InlineData(5_000, PhraseKind.Up)]
  [InlineData(12_999, PhraseKind.Chorus)]
  public void NamesThePhraseAMomentFallsIn(double micros, PhraseKind kind) {
    Assert.Equal(kind, Song.At(micros)?.Kind);
  }

  [Theory]
  [InlineData(999)]
  [InlineData(13_000)]
  public void NamesNoPhraseBeforeTheFirstOrFromTheEndOfTheLast(double micros) {
    Assert.Null(Song.At(micros));
  }

  [Fact]
  public void AMapWithoutPhrasesNamesNone() {
    Assert.Null(new PhraseMap([], EndMicros: 13_000).At(5_000));
  }
}
