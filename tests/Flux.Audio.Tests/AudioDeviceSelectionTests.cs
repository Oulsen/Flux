using Xunit;

namespace Flux.Audio.Tests;

/// <summary>
/// The device choice, which is the part of the audio engine with an opinion.
///
/// The names in here are the real ones from this rig, because they are the whole
/// difficulty: the endpoint carrying the master mix is an input called
/// <c>Microfoon (2- DDJ-FLX10)</c>, and the identically named render endpoint
/// beside it produced no data at all while a track was audible.
/// </summary>
public class AudioDeviceSelectionTests {
  private static readonly AudioDevice ControllerInput =
    new AudioDevice("cap-flx", "Microfoon (2- DDJ-FLX10)", AudioDeviceKind.Capture);

  private static readonly AudioDevice OnboardInput =
    new AudioDevice("cap-realtek", "Microfoon (Realtek Audio)", AudioDeviceKind.Capture);

  private static readonly AudioDevice ControllerOutput =
    new AudioDevice("ren-flx", "Luidsprekers (2- DDJ-FLX10)", AudioDeviceKind.Loopback);

  private static readonly AudioDevice OnboardOutput =
    new AudioDevice("ren-realtek", "Speakers (Realtek Audio)", AudioDeviceKind.Loopback);

  private static readonly AudioDevice[] Present =
    [ControllerInput, OnboardInput, ControllerOutput, OnboardOutput];

  [Fact]
  public void PrefersTheInputOverTheIdenticallyNamedOutput() {
    // The finding this encodes: ASIO keeps rekordbox's audio out of the Windows
    // mixer, so the render endpoint with the same name in it hears nothing at all
    // while the input hears the whole mix.
    Assert.Same(ControllerInput, AudioDeviceCatalog.Select(Present, deviceId: null, "DDJ-FLX10"));
  }

  [Theory]
  [InlineData("ddj-flx10")]
  [InlineData("DDJ-FLX10")]
  [InlineData("flx")]
  public void MatchesPartOfANameWithoutRegardForCase(string fragment) {
    Assert.Same(ControllerInput, AudioDeviceCatalog.Select(Present, deviceId: null, fragment));
  }

  [Fact]
  public void TakesAnExactIdOverTheNamePreference() {
    Assert.Same(OnboardOutput, AudioDeviceCatalog.Select(Present, "ren-realtek", "DDJ-FLX10"));
  }

  [Fact]
  public void FallsBackToTheNamePreferenceForAnIdThatIsNotHere() {
    // A configured id belongs to one machine. A settings file copied to a second
    // one names an endpoint that does not exist there, and there is usually a
    // perfectly good controller plugged into that machine too.
    Assert.Same(ControllerInput, AudioDeviceCatalog.Select(Present, "from-another-pc", "DDJ-FLX10"));
  }

  [Fact]
  public void SettlesForALoopbackDeviceWhenOnlyItsNameMatches() {
    // The standalone case: no controller, and the PC is playing music to its own
    // speakers. Chosen because it was asked for by name, which is the difference
    // from choosing it by default.
    Assert.Same(OnboardOutput, AudioDeviceCatalog.Select(Present, deviceId: null, "Speakers"));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void CapturesNothingRatherThanGuessing(string? preference) {
    // The rule Phase 0 paid for: with no preference there is no default, and in
    // particular not the default render endpoint. Sweeping every render endpoint
    // on this machine while a track played found exactly one with audio on it, and
    // it was a browser playing Netflix - finding audio is not finding the audio.
    Assert.Null(AudioDeviceCatalog.Select(Present, deviceId: null, preference));
  }

  [Fact]
  public void TreatsAnEmptyIdAsNoIdAtAll() {
    Assert.Same(OnboardInput, AudioDeviceCatalog.Select(Present, deviceId: "", "Realtek"));
  }

  [Fact]
  public void FindsNothingWhenNothingMatches() {
    Assert.Null(AudioDeviceCatalog.Select(Present, deviceId: null, "Pioneer XDJ"));
    Assert.Null(AudioDeviceCatalog.Select([], deviceId: null, "DDJ-FLX10"));
  }
}
