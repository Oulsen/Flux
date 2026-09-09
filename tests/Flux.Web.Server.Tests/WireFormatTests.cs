using Flux.Web.Server.Protocol;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// Guards the wire shape against <c>docs/protocol.md</c>.
///
/// These assert on parsed JSON rather than on the C# records deliberately. The
/// records are one side of a contract whose other side is a TypeScript file in a
/// different project, and what those two must agree on is the bytes: a rename
/// that a C#-only test would sail through is exactly the change that breaks the
/// page.
/// </summary>
public sealed class WireFormatTests {
  [Fact]
  public void HelloCarriesTheProtocolVersionAndTheBuild() {
    JsonElement data = Payload(
      Frames.Hello("0.1.0", quantum: 4, stateHz: 20, spectrumHz: 30, devicesHz: 10),
      "hello");

    Assert.Equal(Frames.Version, data.GetProperty("protocol").GetInt32());
    Assert.Equal("0.1.0", data.GetProperty("app").GetString());
    Assert.Equal(4, data.GetProperty("quantum").GetInt32());
    Assert.Equal(20, data.GetProperty("stateHz").GetInt32());
    Assert.Equal(30, data.GetProperty("spectrumHz").GetInt32());
    Assert.Equal(10, data.GetProperty("devicesHz").GetInt32());
  }

  [Fact]
  public void StateNamesEveryFieldTheDocumentPromises() {
    StateData state = new StateData(
      "link",
      "live",
      [new SourceReportData("link", "live", 1.0), new SourceReportData("manual", "waiting", 0.0)],
      new GridData(128.5, 1024.25, 1024, 1, BarAligned: false),
      new AlignmentData(Aligned: false, Method: null),
      Light: null,
      new DiagnosticsData(0.0, Packets: 4821));

    JsonElement data = Payload(Frames.State(state), "state");

    Assert.Equal("link", data.GetProperty("source").GetString());
    Assert.Equal("live", data.GetProperty("sourceState").GetString());

    JsonElement grid = data.GetProperty("grid");
    Assert.Equal(128.5, grid.GetProperty("bpm").GetDouble());
    Assert.Equal(1024.25, grid.GetProperty("beat").GetDouble());
    Assert.Equal(1024, grid.GetProperty("beatIndex").GetInt64());
    Assert.Equal(1, grid.GetProperty("positionInBar").GetInt32());
    Assert.False(grid.GetProperty("barAligned").GetBoolean());

    JsonElement sources = data.GetProperty("sources");
    Assert.Equal(2, sources.GetArrayLength());
    Assert.Equal("link", sources[0].GetProperty("name").GetString());
    Assert.Equal("live", sources[0].GetProperty("state").GetString());
    Assert.Equal(1.0, sources[0].GetProperty("confidence").GetDouble());
    Assert.Equal("waiting", sources[1].GetProperty("state").GetString());

    JsonElement alignment = data.GetProperty("alignment");
    Assert.False(alignment.GetProperty("aligned").GetBoolean());
    Assert.Equal(JsonValueKind.Null, alignment.GetProperty("method").ValueKind);

    JsonElement diagnostics = data.GetProperty("diagnostics");
    Assert.Equal(4821, diagnostics.GetProperty("packets").GetInt64());
    Assert.Equal(0.0, diagnostics.GetProperty("reanchorMaxBeats").GetDouble());
  }

  /// <summary>
  /// The null is load-bearing. An absent tempo sent as zero renders as a
  /// confident 0.00 BPM, which is the one reading a beat display must never
  /// invent.
  /// </summary>
  [Fact]
  public void WaitingStateSendsANullGridRatherThanZeroes() {
    StateData state = new StateData(
      "link",
      "waiting",
      [],
      Grid: null,
      new AlignmentData(Aligned: false, Method: null),
      Light: null,
      new DiagnosticsData(0));

    JsonElement data = Payload(Frames.State(state), "state");

    Assert.Equal(JsonValueKind.Null, data.GetProperty("grid").ValueKind);
  }

  /// <summary>
  /// <c>light</c> is null exactly when Flux is not streaming, which is every frame
  /// this build sends. Present-and-null rather than omitted, on the same rule as
  /// <c>grid</c>: it is a documented value, not a counter that does not apply.
  /// </summary>
  [Fact]
  public void StateSendsLightAsANullWhileNothingIsStreaming() {
    StateData state = new StateData(
      "link",
      "live",
      [],
      new GridData(128.0, 4.0, 4, 1, BarAligned: true),
      new AlignmentData(Aligned: true, Method: "tap"),
      Light: null,
      new DiagnosticsData(0));

    JsonElement data = Payload(Frames.State(state), "state");

    Assert.True(data.TryGetProperty("light", out JsonElement light));
    Assert.Equal(JsonValueKind.Null, light.ValueKind);
  }

  [Fact]
  public void StateCarriesTheMeasuredFrameRateWhileStreaming() {
    StateData state = new StateData(
      "link",
      "live",
      [],
      new GridData(128.0, 4.0, 4, 1, BarAligned: true),
      new AlignmentData(Aligned: true, Method: "tap"),
      new LightData(49.7),
      new DiagnosticsData(0));

    JsonElement data = Payload(Frames.State(state), "state");

    Assert.Equal(49.7, data.GetProperty("light").GetProperty("fps").GetDouble());
  }

  /// <summary>
  /// The two nulls in a state frame are not the same kind of null, and the
  /// serializer has to keep them apart: <c>grid</c> is a documented value that
  /// must survive, while a diagnostic belonging to another source is left out
  /// entirely so a client renders a blank rather than a zero it will read as a
  /// measurement.
  /// </summary>
  [Fact]
  public void DiagnosticsOmitTheCountersTheActiveSourceDoesNotHave() {
    StateData state = new StateData(
      "manual",
      "live",
      [new SourceReportData("manual", "live", 1.0)],
      new GridData(128.0, 4.0, 4, 1, BarAligned: true),
      new AlignmentData(Aligned: true, Method: "tap"),
      Light: null,
      new DiagnosticsData(0.0, Taps: 4, LastTapAgoMs: 512.0));

    JsonElement data = Payload(Frames.State(state), "state");
    JsonElement diagnostics = data.GetProperty("diagnostics");

    Assert.Equal(4, diagnostics.GetProperty("taps").GetInt32());
    Assert.Equal(512.0, diagnostics.GetProperty("lastTapAgoMs").GetDouble());
    Assert.Equal(0.0, diagnostics.GetProperty("reanchorMaxBeats").GetDouble());

    Assert.False(diagnostics.TryGetProperty("packets", out _));
    Assert.False(diagnostics.TryGetProperty("fps", out _));
    Assert.False(diagnostics.TryGetProperty("bpmLocked", out _));

    Assert.Equal("tap", data.GetProperty("alignment").GetProperty("method").GetString());
  }

  [Fact]
  public void SpectrumCarriesTheBandsAndWhatTheDetectorMadeOfThem() {
    SpectrumData spectrum = new SpectrumData([214, 198, 120], 0.62, 0.24, 0.11, 0.031, "strong");

    JsonElement data = Payload(Frames.Spectrum(spectrum), "spectrum");

    JsonElement bins = data.GetProperty("bins");
    Assert.Equal(3, bins.GetArrayLength());
    Assert.Equal(214, bins[0].GetInt32());
    Assert.Equal(0.62, data.GetProperty("kick").GetDouble());
    Assert.Equal(0.24, data.GetProperty("mids").GetDouble());
    Assert.Equal(0.11, data.GetProperty("highs").GetDouble());
    Assert.Equal(0.031, data.GetProperty("flux").GetDouble());
    Assert.Equal("strong", data.GetProperty("beat").GetString());
  }

  [Fact]
  public void DevicesCarryTheirIdentityAndTheirLevels() {
    DevicesData devices = new DevicesData([
      new DeviceData("{0.0.1}", "Microfoon (2- DDJ-FLX10)", "capture", "signal", Selected: true, 0.12, 0.44),
    ]);

    JsonElement data = Payload(Frames.Devices(devices), "devices");
    JsonElement first = data.GetProperty("devices")[0];

    Assert.Equal("{0.0.1}", first.GetProperty("id").GetString());
    Assert.Equal("Microfoon (2- DDJ-FLX10)", first.GetProperty("name").GetString());
    Assert.Equal("capture", first.GetProperty("kind").GetString());
    Assert.Equal("signal", first.GetProperty("state").GetString());
    Assert.True(first.GetProperty("selected").GetBoolean());
    Assert.Equal(0.12, first.GetProperty("rms").GetDouble());
    Assert.Equal(0.44, first.GetProperty("peak").GetDouble());
  }

  /// <summary>
  /// A device nobody is listening to carries its identity and nothing else, in the
  /// bytes and not merely in the record: the whole point of omitting a meter is
  /// that the field is absent on the wire, so a page renders a blank instead of a
  /// zero it would read as a measurement.
  /// </summary>
  [Fact]
  public void AnUnmeteredDeviceOmitsItsStateAndItsLevelsEntirely() {
    DevicesData devices = new DevicesData([
      new DeviceData("{0.0.2}", "Speakers", "loopback", State: null, Selected: false, Rms: null, Peak: null),
    ]);

    JsonElement first = Payload(Frames.Devices(devices), "devices").GetProperty("devices")[0];

    Assert.Equal("{0.0.2}", first.GetProperty("id").GetString());
    Assert.False(first.GetProperty("selected").GetBoolean());

    Assert.False(first.TryGetProperty("state", out _));
    Assert.False(first.TryGetProperty("rms", out _));
    Assert.False(first.TryGetProperty("peak", out _));
  }

  [Fact]
  public void BeatCarriesTheIndexAndTheTempo() {
    JsonElement data = Payload(Frames.Beat(new BeatData(1024, 3, BarAligned: false, 128.0)), "beat");

    Assert.Equal(1024, data.GetProperty("index").GetInt64());
    Assert.Equal(3, data.GetProperty("positionInBar").GetInt32());
    Assert.False(data.GetProperty("barAligned").GetBoolean());
    Assert.Equal(128.0, data.GetProperty("bpm").GetDouble());
  }

  [Fact]
  public void PongEchoesTheClientTimestampUnchanged() {
    JsonElement data = Payload(Frames.Pong(1755950400123), "pong");

    Assert.Equal(1755950400123, data.GetProperty("t").GetInt64());
  }

  [Fact]
  public void ErrorCarriesACodeAndProse() {
    JsonElement data = Payload(Frames.Error("unknown_type", "Unknown message type 'x'."), "error");

    Assert.Equal("unknown_type", data.GetProperty("code").GetString());
    Assert.Equal("Unknown message type 'x'.", data.GetProperty("message").GetString());
  }

  /// <summary>
  /// Reads a frame, checks its type tag, and hands back the payload.
  /// </summary>
  private static JsonElement Payload(ReadOnlyMemory<byte> frame, string expectedType) {
    using JsonDocument document = JsonDocument.Parse(frame);
    JsonElement root = document.RootElement;

    Assert.Equal(expectedType, root.GetProperty("type").GetString());

    // Cloned because the document is disposed on the way out of this method.
    return root.GetProperty("data").Clone();
  }
}
