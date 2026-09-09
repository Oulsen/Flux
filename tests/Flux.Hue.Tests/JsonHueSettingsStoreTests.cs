using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flux.Hue.Tests;

public sealed class JsonHueSettingsStoreTests: IDisposable {
  private static readonly Guid Area = Guid.Parse("11111111-1111-1111-1111-111111111111");

  private readonly string _directory = Path.Combine(Path.GetTempPath(), "flux-hue-settings-" + Guid.NewGuid().ToString("n"));

  public void Dispose() {
    if(Directory.Exists(_directory)) {
      Directory.Delete(_directory, recursive: true);
    }
  }

  [Fact]
  public void StartsWithNothingChosen() {
    Assert.Equal(HueSettings.Empty, Store().Load());
  }

  [Fact]
  public void RemembersTheChosenAreaAcrossInstances() {
    Store().Save(new HueSettings(Area));

    Assert.Equal(Area, Store().Load().EntertainmentAreaId);
  }

  [Fact]
  public void ReadsAsChosenNothingAfterTheChoiceIsCleared() {
    Store().Save(new HueSettings(Area));

    Store().Save(HueSettings.Empty);

    Assert.Null(Store().Load().EntertainmentAreaId);
  }

  [Fact]
  public void SurvivesAFileSomebodyEditedByHand() {
    // This one is plain text on purpose, so it will be opened and edited. One
    // forgotten preference is the right price for that; refusing to start is not.
    Directory.CreateDirectory(_directory);
    File.WriteAllText(Path.Combine(_directory, "hue-settings.json"), "{ this is not json");

    Assert.Equal(HueSettings.Empty, Store().Load());
  }

  [Fact]
  public void WritesSomethingAPersonCanRead() {
    // The counterpart to the credential store's test: this file must be readable,
    // because it holds a choice its owner made rather than a secret.
    Store().Save(new HueSettings(Area));

    string written = File.ReadAllText(Path.Combine(_directory, "hue-settings.json"));

    Assert.Contains(Area.ToString(), written, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// A palette by name and not by number, which is a durability property rather than a
  /// cosmetic one: stored as <c>1</c> it means "whichever palette sits at index one",
  /// so inserting one into the middle of the enum would repaint a choice somebody had
  /// already made, with no compile error and no runtime error to show for it.
  /// </summary>
  [Fact]
  public void WritesAPaletteByNameSoReorderingTheEnumCannotRepaintIt() {
    Store().Save(HueSettings.Empty with { Light = LightSettings.Default with { Palette = LightPalette.Forest } });

    string written = File.ReadAllText(Path.Combine(_directory, "hue-settings.json"));

    Assert.Contains("\"Forest\"", written, StringComparison.Ordinal);
    Assert.Equal(LightPalette.Forest, Store().Load().Light?.Palette);
  }

  /// <summary>
  /// The migration this change needs, which is none: a file written before palettes
  /// were named still loads, because the converter reads numbers as well as names.
  /// Without this, the first launch after the change would silently forget a palette.
  /// </summary>
  [Fact]
  public void StillReadsAPaletteWrittenAsANumberByAnEarlierBuild() {
    Directory.CreateDirectory(_directory);
    File.WriteAllText(
      Path.Combine(_directory, "hue-settings.json"),
      """{ "Light": { "Palette": 5, "ColorChangeBars": 2, "PulseMs": 300, "Brightness": 70, "PulseDepth": 40 } }""");

    LightSettings? light = Store().Load().Light;

    // Five is Ocean's position, and reading it as anything else is the bug this guards.
    Assert.Equal(LightPalette.Ocean, light?.Palette);
    Assert.Equal(70, light?.Brightness);
  }

  /// <summary>
  /// The two settings that did not exist when a file was written come back as their
  /// defaults rather than as zero, which is the difference between a room that looks
  /// the same after an upgrade and one that comes up black.
  /// </summary>
  [Fact]
  public void AFileFromBeforeBrightnessExistedComesBackAtFullBrightnessRatherThanOff() {
    Directory.CreateDirectory(_directory);
    File.WriteAllText(
      Path.Combine(_directory, "hue-settings.json"),
      """{ "Light": { "Palette": "Warm", "ColorChangeBars": 2, "PulseMs": 300 } }""");

    LightSettings? light = Store().Load().Light;

    Assert.Equal(100, light?.Brightness);
    Assert.Equal(85, light?.PulseDepth);
    Assert.True(light?.IsValid());
  }

  private JsonHueSettingsStore Store() {
    return new JsonHueSettingsStore(_directory, NullLogger<JsonHueSettingsStore>.Instance);
  }
}
