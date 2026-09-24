using Flux.Audio;
using Flux.Web.Server.Protocol;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The stage's look, from the wire in and the wire out.
///
/// It moved to the server rather than staying in each browser's storage, and these are
/// the claims that move makes: the look survives a restart, it is the same on every page
/// open at once, and the band count it carries reaches the analyzer rather than being
/// applied to columns it has already produced.
/// </summary>
public sealed class StageMessagesTests {
  [Fact]
  public void ComposesTheWireNamesFromTheEnumMembers() {
    // Derived rather than tabulated, so this is the test that would catch a member
    // whose camel-case spelling is not what a client sends.
    StageData composed = StageFrames.Compose(
      new StageSettings(StageLayout.Mirrored, StageShape.ChonkerBars, StageColor.HueWaveVert, 32));

    Assert.Equal("mirrored", composed.Layout);
    Assert.Equal("chonkerBars", composed.Shape);
    Assert.Equal("hueWaveVert", composed.Color);
    Assert.Equal(32, composed.Bands);
  }

  [Fact]
  public void TheTrailTravelsAndIsOffUnlessSomebodyAsks() {
    Assert.Equal(0, StageSettings.Default.TrailMs);

    Assert.Equal(350, StageFrames.Compose(StageSettings.Default with { TrailMs = 350 }).TrailMs);
  }

  [Theory]
  [InlineData(0, true)]
  [InlineData(StageSettings.MaxTrailMs, true)]
  [InlineData(-1, false)]
  [InlineData(StageSettings.MaxTrailMs + 1, false)]
  public void ATrailOutsideItsRangeIsRefused(int trailMs, bool accepted) {
    MemoryStageSettingsStore store = new MemoryStageSettingsStore();
    StageService service = new StageService(store);

    Assert.Equal(accepted, service.TrySet(StageSettings.Default with { TrailMs = trailMs }));
  }

  /// <summary>
  /// One complete statement of the look, like every other field: a page that left the
  /// trail out would be relying on the server to remember it, and two pages doing that
  /// produce a look neither chose.
  /// </summary>
  [Fact]
  public void ALookWithoutItsTrailIsNotAStatementOfTheLook() {
    using JsonDocument without = JsonDocument.Parse("""{"layout":"upward","shape":"bars","color":"randomBeat","bands":64}""");
    using JsonDocument with = JsonDocument.Parse("""{"layout":"upward","shape":"bars","color":"randomBeat","bands":64,"trailMs":200}""");

    Assert.False(Frames.TryReadStage(without.RootElement, out _));
    Assert.True(Frames.TryReadStage(with.RootElement, out StageData read));
    Assert.Equal(200, read.TrailMs);
  }

  [Fact]
  public void OpensOnTheLookThatWasLastChosen() {
    MemoryStageSettingsStore store = new MemoryStageSettingsStore();
    store.Save(new StageSettings(StageLayout.Mirrored, StageShape.Smooth, StageColor.HueCycle, 32));

    StageService service = new StageService(store);

    // Loaded at construction rather than on each read, because the analyzer asks for
    // the band count sixty times a second.
    Assert.Equal(StageLayout.Mirrored, service.Settings.Layout);
    Assert.Equal(32, service.Count);
  }

  [Fact]
  public void ADefaultLookIsTheOneTheStoreHasNever() {
    Assert.Equal(StageSettings.Default, new StageService(new MemoryStageSettingsStore()).Settings);
    Assert.Equal(SpectrumBands.Default, StageSettings.Default.Bands);
  }

  [Fact]
  public void AppliesALookAndAnnouncesIt() {
    MemoryStageSettingsStore store = new MemoryStageSettingsStore();
    StageService service = new StageService(store);

    StageSettings? announced = null;
    service.Changed += settings => announced = settings;

    StageSettings requested = new StageSettings(StageLayout.Mirrored, StageShape.Pixel, StageColor.Static, 128);

    Assert.True(service.TrySet(requested));
    Assert.Equal(requested, service.Settings);
    Assert.Equal(requested, announced);

    // Written the instant it is accepted rather than at shutdown, because Flux is
    // closed by killing a console window as often as not.
    Assert.Equal(1, store.Saves);
  }

  [Fact]
  public void ANoOpChangeIsNeitherWrittenNorAnnounced() {
    // A slider drag arrives ten times a second. Without this each one is a file write
    // and a broadcast to every open page, for a value that did not move.
    MemoryStageSettingsStore store = new MemoryStageSettingsStore();
    StageService service = new StageService(store);

    int announcements = 0;
    service.Changed += _ => announcements++;

    Assert.True(service.TrySet(StageSettings.Default));

    Assert.Equal(0, store.Saves);
    Assert.Equal(0, announcements);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(SpectrumBands.Min - 1)]
  [InlineData(SpectrumBands.Max + 1)]
  [InlineData(4096)]
  public void ABandCountOutsideTheBoundsIsRefusedRatherThanClamped(int bands) {
    // Refused rather than nudged, on the argument the light settings run on: a value
    // out of range is a client bug, and silently substituting a different one hides it
    // while leaving the page showing the number it asked for.
    MemoryStageSettingsStore store = new MemoryStageSettingsStore();
    StageService service = new StageService(store);

    Assert.False(service.TrySet(new StageSettings(StageLayout.Upward, StageShape.Bars, StageColor.RandomBeat, bands)));
    Assert.Equal(StageSettings.Default, service.Settings);
    Assert.Equal(0, store.Saves);
  }

  [Fact]
  public void ANameNoBuildHasIsRefusedBeforeItReachesTheStore() {
    // Enum.IsDefined rather than a cast that happens to land in range. A layout of 7
    // would otherwise be stored, echoed to every page, and drawn as whatever the
    // client's own fallback happened to be.
    MemoryStageSettingsStore store = new MemoryStageSettingsStore();
    StageService service = new StageService(store);

    Assert.False(service.TrySet(new StageSettings((StageLayout)7, StageShape.Bars, StageColor.RandomBeat, 64)));
    Assert.Equal(0, store.Saves);
  }

  [Theory]
  [InlineData("upward", StageLayout.Upward)]
  [InlineData("Mirrored", StageLayout.Mirrored)]
  public void ParsesALayoutNameCaseInsensitively(string name, StageLayout expected) {
    Assert.True(StageFrames.TryParse(name, out StageLayout parsed));
    Assert.Equal(expected, parsed);
  }

  [Fact]
  public void RefusesANumberWhereANameBelongs() {
    // Not Enum.Parse: it would accept "3", and the integer values are an implementation
    // detail no client should be able to reach.
    Assert.False(StageFrames.TryParse("3", out StageShape _));
    Assert.False(StageFrames.TryParse("wobblyBars", out StageShape _));
    Assert.False(StageFrames.TryParse(string.Empty, out StageColor _));
  }

  [Fact]
  public void AStoredLookOutsideTheBoundsFallsBackToTheDefaults() {
    // A file is the one route into this record that no client message passed through,
    // so it is validated on the way in as well. A hand-edited count would otherwise
    // reach the analyzer.
    Assert.False(new StageSettings(StageLayout.Upward, StageShape.Bars, StageColor.RandomBeat, 4096).IsValid);
    Assert.True(StageSettings.Default.IsValid);
  }
}
