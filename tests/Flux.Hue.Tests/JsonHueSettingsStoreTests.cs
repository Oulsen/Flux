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

  private JsonHueSettingsStore Store() {
    return new JsonHueSettingsStore(_directory, NullLogger<JsonHueSettingsStore>.Instance);
  }
}
