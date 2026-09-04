using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Midi.Tests;

public class MidiOptionsValidatorTests {
  [Fact]
  public void AcceptsTheDefaults() {
    Assert.True(Validate(new MidiOptions()).Succeeded);
  }

  [Fact]
  public void AcceptsAnEmptyPreferenceAsWatchingNothing() {
    // Not a mistake: it is how a machine with no controller says so, and the same
    // semantics the audio device preference has.
    Assert.True(Validate(new MidiOptions { PreferDeviceNamed = string.Empty }).Succeeded);
  }

  [Fact]
  public void RefusesAPreferenceMadeOnlyOfSpaces() {
    // The one mistake here that does not look like one. It is not empty, so it
    // counts as a real preference, and nearly every device name contains a space -
    // so it would silently hand an unknown input port the authority to move the
    // downbeat.
    ValidateOptionsResult result = Validate(new MidiOptions { PreferDeviceNamed = "   " });

    Assert.True(result.Failed);
    Assert.Contains("Midi:PreferDeviceNamed", result.FailureMessage, StringComparison.Ordinal);
  }

  private static ValidateOptionsResult Validate(MidiOptions options) {
    return new MidiOptionsValidator().Validate(Options.DefaultName, options);
  }
}
