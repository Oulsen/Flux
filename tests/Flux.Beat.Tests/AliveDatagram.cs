using System.Buffers.Binary;
using System.Text;

namespace Flux.Beat.Tests;

/// <summary>
/// Builds Ableton Link ALIVE datagrams byte for byte.
///
/// The defaults reproduce the exact packet Phase 0 measured on this machine: 107
/// bytes total, a 20-byte header, then <c>tmln</c> at offset 20 (24 B),
/// <c>sess</c> at 52 (8 B), <c>stst</c> at 68 (17 B) and <c>mep4</c> at 93
/// (6 B). Those offsets are arithmetic consequences of the chunk sizes, which is
/// itself corroboration that the observed layout was read correctly.
///
/// Every departure from that shape is a named property, so a test that bends the
/// packet says in one line exactly which assumption it is attacking.
/// </summary>
internal sealed class AliveDatagram {
  /// <summary>The full length of the packet Phase 0 observed.</summary>
  public const int ObservedLength = 107;

  /// <summary>Where the timeline chunk header starts in that packet.</summary>
  public const int ObservedTimelineOffset = 20;

  private const int HeaderLength = 20;

  private static readonly byte[] ProtocolLiteral = Encoding.ASCII.GetBytes("_asdp_v");

  /// <summary>175.00 BPM - one of the two tempi cross-checked against rekordbox's readout.</summary>
  public long MicrosPerBeat { get; init; } = 342_857;

  /// <summary>Link scales the beat origin by a million.</summary>
  public long BeatOriginMicroBeats { get; init; }

  public long TimeOriginMicros { get; init; }

  /// <summary>Set false to build a packet with no timeline in it at all.</summary>
  public bool IncludeTimeline { get; init; } = true;

  /// <summary>The id every peer in the session republishes.</summary>
  public ulong SessionId { get; init; } = 0x1111_1111_1111_1111;

  /// <summary>Set false to build a packet that names no session.</summary>
  public bool IncludeSession { get; init; } = true;

  /// <summary>The length written into the timeline chunk header, which is not always the truth.</summary>
  public int DeclaredTimelineSize { get; init; } = 24;

  /// <summary>Overrides the opening literal, to build something that is not a Link packet.</summary>
  public byte[]? Literal { get; init; }

  /// <summary>Bytes injected between the header and the first chunk.</summary>
  public byte[] HeaderNoise { get; init; } = [];

  public byte[] ToBytes() {
    List<byte> packet = new List<byte>(ObservedLength);

    byte[] literal = this.Literal ?? ProtocolLiteral;
    packet.AddRange(literal);

    // Version, message type, ttl, group id and node id. The parser never reads
    // these, but the packet has to be the right shape to be worth testing.
    packet.AddRange(Enumerable.Repeat((byte)0x2A, HeaderLength - literal.Length));
    packet.AddRange(this.HeaderNoise);

    if(this.IncludeTimeline) {
      List<byte> timeline = new List<byte>(24);
      AppendInt64(timeline, this.MicrosPerBeat);
      AppendInt64(timeline, this.BeatOriginMicroBeats);
      AppendInt64(timeline, this.TimeOriginMicros);
      AppendChunk(packet, "tmln", this.DeclaredTimelineSize, timeline);
    }

    if(this.IncludeSession) {
      List<byte> session = new List<byte>(8);
      AppendUInt64(session, this.SessionId);
      AppendChunk(packet, "sess", 8, session);
    }

    AppendChunk(packet, "stst", 17, Enumerable.Repeat((byte)0x22, 17));
    AppendChunk(packet, "mep4", 6, Enumerable.Repeat((byte)0x33, 6));

    return [.. packet];
  }

  /// <summary>
  /// Writes a chunk whose declared size may deliberately disagree with the body
  /// it is given - the shape of a truncated or lying packet.
  /// </summary>
  private static void AppendChunk(List<byte> packet, string key, int declaredSize, IEnumerable<byte> body) {
    packet.AddRange(Encoding.ASCII.GetBytes(key));

    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(size, declaredSize);
    packet.AddRange(size);

    packet.AddRange(body);
  }

  private static void AppendInt64(List<byte> target, long value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(bytes, value);
    target.AddRange(bytes);
  }

  private static void AppendUInt64(List<byte> target, ulong value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
    target.AddRange(bytes);
  }
}
