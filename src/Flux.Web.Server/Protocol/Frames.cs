using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Flux.Web.Server.Protocol;

/// <summary>
/// Turns messages into wire frames and inbound bytes back into a type tag.
///
/// Every frame is encoded exactly once here and then handed to every client as
/// the same block of bytes, which is the reason this returns
/// <see cref="ReadOnlyMemory{T}"/> rather than a string: at 20 snapshots a
/// second with several devices watching, re-serialising per client would be the
/// only part of the pipeline doing avoidable work.
/// </summary>
public static class Frames {
  /// <summary>
  /// The version of <c>docs/protocol.md</c> this build speaks. Increment only
  /// for a change an existing client could misread - a removed or renamed field,
  /// a changed unit, a changed meaning. Adding a message type or a field does
  /// not count.
  ///
  /// Moved to 2 when the light engine started sending a colour per light. Nothing was
  /// removed and nothing was renamed, so this is the third case: <c>state.light.color</c>
  /// was the colour every light had been sent and is now the colour before each light's
  /// own band is applied. A version 1 page would keep drawing it and would keep looking
  /// correct while being slightly brighter than the room, which is the failure this
  /// number exists to turn into a refusal.
  /// </summary>
  public const int Version = 2;

  /// <summary>
  /// Longest inbound message accepted, in bytes. Version 1's largest client
  /// message is under fifty bytes, so this is generous by two orders of
  /// magnitude and still small enough that a connection cannot be used to make
  /// Flux allocate.
  /// </summary>
  public const int MaxInboundBytes = 4096;

  /// <summary>How much of a rejected type name is quoted back, so a client cannot have its own input amplified at it.</summary>
  private const int MaxQuotedTypeLength = 40;

  public static ReadOnlyMemory<byte> Hello(string app, int quantum, int stateHz, int spectrumHz, int devicesHz) {
    return Encode(
      "hello",
      new HelloData(Version, app, quantum, stateHz, spectrumHz, devicesHz),
      ProtocolJson.Default.EnvelopeHelloData);
  }

  public static ReadOnlyMemory<byte> State(StateData state) {
    return Encode("state", state, ProtocolJson.Default.EnvelopeStateData);
  }

  public static ReadOnlyMemory<byte> Beat(BeatData beat) {
    return Encode("beat", beat, ProtocolJson.Default.EnvelopeBeatData);
  }

  public static ReadOnlyMemory<byte> Spectrum(SpectrumData spectrum) {
    return Encode("spectrum", spectrum, ProtocolJson.Default.EnvelopeSpectrumData);
  }

  public static ReadOnlyMemory<byte> Devices(DevicesData devices) {
    return Encode("devices", devices, ProtocolJson.Default.EnvelopeDevicesData);
  }

  public static ReadOnlyMemory<byte> Hue(HueData hue) {
    return Encode("hue", hue, ProtocolJson.Default.EnvelopeHueData);
  }

  public static ReadOnlyMemory<byte> Stage(StageData stage) {
    return Encode("stage", stage, ProtocolJson.Default.EnvelopeStageData);
  }

  public static ReadOnlyMemory<byte> Pong(long t) {
    return Encode("pong", new EchoData(t), ProtocolJson.Default.EnvelopeEchoData);
  }

  public static ReadOnlyMemory<byte> Error(string code, string message) {
    return Encode("error", new ErrorData(code, message), ProtocolJson.Default.EnvelopeErrorData);
  }

  /// <summary>
  /// Reads an inbound message as far as its type tag.
  /// </summary>
  /// <returns>False when the bytes are not JSON, or not an object carrying a string type.</returns>
  public static bool TryDecode(ReadOnlySpan<byte> message, out ClientEnvelope decoded) {
    try {
      ClientEnvelope? envelope = JsonSerializer.Deserialize(message, ProtocolJson.Default.ClientEnvelope);
      if(envelope?.Type is { Length: > 0 }) {
        decoded = envelope;
        return true;
      }
    } catch(JsonException) {
      // Malformed input from a client is an ordinary event on an open port, not
      // an exceptional one. The caller answers it with an error frame.
    }

    decoded = new ClientEnvelope(null, default);
    return false;
  }

  /// <summary>
  /// Reads the timestamp out of a <c>ping</c> payload.
  ///
  /// Deliberately strict rather than defaulting a missing value to zero: a pong
  /// echoing a timestamp the client never sent would read as a round trip of
  /// several decades, which is a worse answer than an error.
  /// </summary>
  public static bool TryReadEcho(JsonElement data, out long t) {
    t = 0;

    return data.ValueKind == JsonValueKind.Object
      && data.TryGetProperty("t", out JsonElement value)
      && value.ValueKind == JsonValueKind.Number
      && value.TryGetInt64(out t);
  }

  /// <summary>
  /// Reads a <c>subscribe</c> payload.
  ///
  /// Both flags are required, because the message is defined as a complete
  /// statement of what the client wants rather than a delta. Defaulting a
  /// missing one to false would turn a misspelled field name into a silent
  /// unsubscribe, and the symptom of that is a visualiser that mysteriously
  /// never draws.
  /// </summary>
  public static bool TryReadSubscribe(JsonElement data, out bool spectrum, out bool deviceLevels) {
    spectrum = false;
    deviceLevels = false;

    return data.ValueKind == JsonValueKind.Object
      && TryReadBool(data, "spectrum", out spectrum)
      && TryReadBool(data, "deviceLevels", out deviceLevels);
  }

  /// <summary>Reads the source name out of a <c>selectSource</c> payload.</summary>
  public static bool TryReadSource(JsonElement data, out string source) {
    return TryReadString(data, "source", out source);
  }

  /// <summary>Reads the device id out of a <c>selectAudioDevice</c> payload.</summary>
  public static bool TryReadDeviceId(JsonElement data, out string deviceId) {
    return TryReadString(data, "deviceId", out deviceId);
  }

  /// <summary>
  /// Reads a <c>setBpm</c> payload, where the field is present and may be null.
  ///
  /// Null is a value here rather than an absence: it clears the manual tempo.
  /// So a missing <c>bpm</c> is refused instead of being read as null, which
  /// would let a typo'd field name silently switch the tempo off.
  /// </summary>
  /// <param name="data">The <c>data</c> object of the inbound message.</param>
  /// <param name="bpm">The tempo asked for, or null to clear it.</param>
  public static bool TryReadSetBpm(JsonElement data, out double? bpm) {
    bpm = null;

    if(data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("bpm", out JsonElement value)) {
      return false;
    }

    if(value.ValueKind == JsonValueKind.Null) {
      return true;
    }

    if(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) {
      bpm = number;
      return true;
    }

    return false;
  }

  /// <summary>Reads the bridge id out of a <c>pairBridge</c> payload.</summary>
  public static bool TryReadBridgeId(JsonElement data, out string bridgeId) {
    return TryReadString(data, "bridgeId", out bridgeId);
  }

  /// <summary>
  /// Reads the area id out of a <c>selectArea</c> payload.
  ///
  /// The id is a uuid on the wire and a <see cref="Guid"/> here, so a value that is
  /// not one is <c>invalid_data</c> rather than <c>rejected</c>: the two codes mean
  /// different things to whoever is reading, and a malformed id is a bug in the
  /// client rather than a race it lost.
  /// </summary>
  public static bool TryReadAreaId(JsonElement data, out Guid areaId) {
    areaId = Guid.Empty;

    return TryReadString(data, "areaId", out string text) && Guid.TryParse(text, out areaId);
  }

  /// <summary>
  /// Reads a <c>setLatencyOffset</c> payload.
  ///
  /// Integer milliseconds, strictly. A fractional offset is refused rather than
  /// rounded, because the slider that sends this deals in whole milliseconds and
  /// anything else is a client that has stopped agreeing with the document.
  /// </summary>
  public static bool TryReadLatencyOffset(JsonElement data, out int offsetMs) {
    offsetMs = 0;

    return data.ValueKind == JsonValueKind.Object
      && data.TryGetProperty("offsetMs", out JsonElement value)
      && value.ValueKind == JsonValueKind.Number
      && value.TryGetInt32(out offsetMs);
  }

  /// <summary>
  /// Reads a <c>setLightSettings</c> payload, as far as its shape.
  ///
  /// Every field is required, because the message is one complete statement of how the
  /// lights should look rather than a patch: a page that could send a palette alone
  /// would be relying on the server to remember the rest, and two pages doing that at
  /// once produce a combination neither of them chose.
  ///
  /// The palette comes back as the string it arrived as, unparsed. Whether it names
  /// anything is not a question about shape, and the answer to it is <c>rejected</c>
  /// with the name quoted - which this cannot produce and the caller can.
  /// </summary>
  public static bool TryReadLightSettings(JsonElement data, out HueEngineData engine) {
    engine = new HueEngineData(string.Empty, 0, 0, 0, 0, 1, string.Empty, false, 0);

    if(data.ValueKind != JsonValueKind.Object
      || !TryReadString(data, "palette", out string palette)
      || !TryReadInt(data, "colorChangeBars", out int bars)
      || !TryReadInt(data, "pulseMs", out int pulseMs)
      || !TryReadInt(data, "brightness", out int brightness)
      || !TryReadInt(data, "pulseDepth", out int pulseDepth)
      || !TryReadInt(data, "colorCount", out int colorCount)
      || !TryReadString(data, "colorLayout", out string colorLayout)
      || !TryReadBool(data, "strobe", out bool strobe)
      || !TryReadInt(data, "strobeCycle", out int strobeCycle)) {
      return false;
    }

    engine = new HueEngineData(palette, bars, pulseMs, brightness, pulseDepth, colorCount, colorLayout, strobe, strobeCycle);
    return true;
  }

  /// <summary>
  /// Reads a <c>strobe</c> payload: whether the sender's finger is on the button.
  ///
  /// A boolean rather than two message types, because press and release are one fact
  /// changing, and a page repeating its press to keep a hold alive is sending the same
  /// fact again rather than pressing twice.
  /// </summary>
  public static bool TryReadStrobe(JsonElement data, out bool held) {
    held = false;

    return data.ValueKind == JsonValueKind.Object
      && TryReadBool(data, "held", out held);
  }

  /// <summary>
  /// Reads a complete statement of how the stage should look.
  ///
  /// Every field required, on the argument the light settings run on: a page that could
  /// send a shape alone would be relying on the server for the rest, and two pages doing
  /// that at once produce a look neither chose.
  ///
  /// The three names come back as the strings they arrived as. Whether they name
  /// anything is not a question about shape, and its answer is <c>rejected</c> with the
  /// offending name quoted - which this cannot produce and the caller can.
  /// </summary>
  public static bool TryReadStage(JsonElement data, out StageData stage) {
    stage = new StageData(string.Empty, string.Empty, string.Empty, 0);

    if(data.ValueKind != JsonValueKind.Object
      || !TryReadString(data, "layout", out string layout)
      || !TryReadString(data, "shape", out string shape)
      || !TryReadString(data, "color", out string color)
      || !TryReadInt(data, "bands", out int bands)) {
      return false;
    }

    stage = new StageData(layout, shape, color, bands);
    return true;
  }

  /// <summary>
  /// Shortens a client-supplied string before it is quoted back in an error.
  /// </summary>
  public static string Quote(string value) {
    ArgumentNullException.ThrowIfNull(value);

    return value.Length <= MaxQuotedTypeLength
      ? value
      : string.Concat(value.AsSpan(0, MaxQuotedTypeLength), "...");
  }

  /// <summary>Reads a required boolean field.</summary>
  private static bool TryReadBool(JsonElement data, string name, out bool value) {
    value = false;

    if(!data.TryGetProperty(name, out JsonElement field)) {
      return false;
    }

    switch(field.ValueKind) {
      case JsonValueKind.True:
        value = true;
        return true;

      case JsonValueKind.False:
        return true;

      default:
        // Deliberately not accepting 0, 1 or "true". The client half of this
        // protocol is one TypeScript file, so anything else is a bug in it and
        // is worth an error rather than a guess.
        return false;
    }
  }

  /// <summary>
  /// Reads a required whole-number field.
  ///
  /// Strict about integers for the reason the latency offset is: every number this
  /// protocol accepts from a client comes off a control that deals in whole units, so
  /// a fraction is a client that has stopped agreeing with the document rather than a
  /// value to round.
  /// </summary>
  private static bool TryReadInt(JsonElement data, string name, out int value) {
    value = 0;

    return data.TryGetProperty(name, out JsonElement field)
      && field.ValueKind == JsonValueKind.Number
      && field.TryGetInt32(out value);
  }

  /// <summary>Reads a required, non-empty string field.</summary>
  private static bool TryReadString(JsonElement data, string name, out string value) {
    value = string.Empty;

    if(data.ValueKind != JsonValueKind.Object
      || !data.TryGetProperty(name, out JsonElement field)
      || field.ValueKind != JsonValueKind.String) {
      return false;
    }

    string? text = field.GetString();
    if(string.IsNullOrWhiteSpace(text)) {
      return false;
    }

    value = text;
    return true;
  }

  private static ReadOnlyMemory<byte> Encode<TData>(
    string type,
    TData data,
    JsonTypeInfo<Envelope<TData>> shape) {
    return JsonSerializer.SerializeToUtf8Bytes(new Envelope<TData>(type, data), shape);
  }
}

/// <summary>
/// The serializer shapes for every message on the wire.
///
/// Source-generated rather than reflected, so the contract is fixed at compile
/// time: a payload type that was never registered here fails the build instead
/// of failing on a phone.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Envelope<HelloData>))]
[JsonSerializable(typeof(Envelope<StateData>))]
[JsonSerializable(typeof(Envelope<BeatData>))]
[JsonSerializable(typeof(Envelope<SpectrumData>))]
[JsonSerializable(typeof(Envelope<DevicesData>))]
[JsonSerializable(typeof(Envelope<HueData>))]
[JsonSerializable(typeof(Envelope<StageData>))]
[JsonSerializable(typeof(Envelope<EchoData>))]
[JsonSerializable(typeof(Envelope<ErrorData>))]
[JsonSerializable(typeof(ClientEnvelope))]
internal sealed partial class ProtocolJson: JsonSerializerContext {
}
