using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Flux.Web.Protocol;

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
  /// </summary>
  public const int Version = 1;

  /// <summary>
  /// Longest inbound message accepted, in bytes. Version 1's largest client
  /// message is under fifty bytes, so this is generous by two orders of
  /// magnitude and still small enough that a connection cannot be used to make
  /// Flux allocate.
  /// </summary>
  public const int MaxInboundBytes = 4096;

  /// <summary>How much of a rejected type name is quoted back, so a client cannot have its own input amplified at it.</summary>
  private const int MaxQuotedTypeLength = 40;

  public static ReadOnlyMemory<byte> Hello(string app, int quantum, int stateHz) {
    return Encode("hello", new HelloData(Version, app, quantum, stateHz), ProtocolJson.Default.EnvelopeHelloData);
  }

  public static ReadOnlyMemory<byte> State(StateData state) {
    return Encode("state", state, ProtocolJson.Default.EnvelopeStateData);
  }

  public static ReadOnlyMemory<byte> Beat(BeatData beat) {
    return Encode("beat", beat, ProtocolJson.Default.EnvelopeBeatData);
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
  /// Shortens a client-supplied string before it is quoted back in an error.
  /// </summary>
  public static string Quote(string value) {
    ArgumentNullException.ThrowIfNull(value);

    return value.Length <= MaxQuotedTypeLength
      ? value
      : string.Concat(value.AsSpan(0, MaxQuotedTypeLength), "...");
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
[JsonSerializable(typeof(Envelope<EchoData>))]
[JsonSerializable(typeof(Envelope<ErrorData>))]
[JsonSerializable(typeof(ClientEnvelope))]
internal sealed partial class ProtocolJson: JsonSerializerContext {
}
