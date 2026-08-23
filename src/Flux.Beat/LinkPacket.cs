using System.Buffers.Binary;
using System.Text;

namespace Flux.Beat;

/// <summary>
/// Decodes the two things Flux needs out of an Ableton Link ALIVE datagram: the
/// <c>tmln</c> chunk carrying tempo and beat phase, and the <c>sess</c> chunk
/// naming the session that chunk belongs to.
///
/// Flux is a read-only peer. It never announces itself, never answers a ping and
/// never becomes tempo master, so this is the whole protocol surface - a
/// deliberate trade documented under "Beat sync" in the findings.
///
/// Two things are known and neither is guessed at here. The packet carries no
/// meter or bar length, so bar 1 cannot come from Link at all; and the chunks
/// sit at fixed offsets on this hardware, but they are located by scanning
/// anyway, because a scan that validates what it finds costs nothing and
/// survives a header this code has not seen.
/// </summary>
public static class LinkPacket {
  /// <summary>The literal that opens every Link datagram, ahead of a version byte.</summary>
  private static readonly byte[] ProtocolHeader = Encoding.ASCII.GetBytes("_asdp_v");

  private static readonly byte[] TimelineKey = Encoding.ASCII.GetBytes("tmln");

  private static readonly byte[] SessionKey = Encoding.ASCII.GetBytes("sess");

  /// <summary>Four-character chunk key, followed by a big-endian int32 length.</summary>
  private const int ChunkKeyLength = 4;

  private const int ChunkHeaderLength = 8;

  /// <summary>Three big-endian int64s: tempo, beat origin, time origin.</summary>
  private const int TimelineBodyLength = 24;

  /// <summary>One big-endian uint64: the id of the session's founding peer.</summary>
  private const int SessionBodyLength = 8;

  /// <summary>Link publishes the beat origin scaled by a million.</summary>
  private const double MicroBeatsPerBeat = 1_000_000.0;

  /// <summary>
  /// Reads the timeline out of a datagram, rebasing it onto the caller's
  /// monotonic clock domain.
  ///
  /// On this machine that rebasing is a no-op - rekordbox and Flux share a
  /// clock, so Link's host time already <em>is</em> QPC microseconds, which is
  /// what makes the <c>mep4</c> ping/pong exchange skippable. The parameter
  /// exists because that shortcut is a same-machine luxury: listening from a
  /// different PC would need a measured offset here, and it is far cheaper to
  /// leave the seam open than to cut one later.
  /// </summary>
  /// <param name="packet">A received datagram, of any length.</param>
  /// <param name="clockOffsetMicros">Added to Link's time origin to reach local time.</param>
  /// <param name="timeline">The decoded timeline, when this returns true.</param>
  /// <returns>False for anything that is not a Link packet carrying a plausible timeline.</returns>
  public static bool TryParseTimeline(
    ReadOnlySpan<byte> packet,
    double clockOffsetMicros,
    out LinkTimeline timeline) {
    timeline = default;

    if(!packet.StartsWith(ProtocolHeader)) {
      return false;
    }

    // Every well-sized candidate gets a turn, not just the first. A chunk header
    // is eight bytes of shape and a decoy can match all of it; what a decoy
    // cannot do is decode to a plausible timeline, so contents are the filter
    // and the scan continues until one passes.
    int offset = ProtocolHeader.Length;
    while(TryFindChunk(packet, TimelineKey, TimelineBodyLength, ref offset, out ReadOnlySpan<byte> body)) {
      if(TryReadTimeline(body, clockOffsetMicros, out timeline)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Reads the id of the session a datagram belongs to.
  ///
  /// Every peer in a Link session republishes the same id, which is what makes it
  /// the right thing to follow. A sender address is not: it drops to half the
  /// traffic the moment a second peer joins the session, and it still says
  /// nothing about whether the timeline arriving is the one being followed.
  ///
  /// The first well-sized match wins here, unlike the timeline scan. An opaque id
  /// has no plausible band to test against, and the cost of a decoy is a stable
  /// but wrong identity rather than a wrong tempo.
  /// </summary>
  /// <param name="packet">A received datagram, of any length.</param>
  /// <param name="sessionId">The session id, when this returns true.</param>
  /// <returns>False when the datagram is not Link, or carries no session chunk.</returns>
  public static bool TryParseSessionId(ReadOnlySpan<byte> packet, out ulong sessionId) {
    sessionId = 0;

    if(!packet.StartsWith(ProtocolHeader)) {
      return false;
    }

    int offset = ProtocolHeader.Length;
    if(!TryFindChunk(packet, SessionKey, SessionBodyLength, ref offset, out ReadOnlySpan<byte> body)) {
      return false;
    }

    sessionId = BinaryPrimitives.ReadUInt64BigEndian(body);
    return true;
  }

  /// <summary>
  /// Turns a timeline chunk body into a timeline, or rejects it.
  ///
  /// The clock offset is added under <c>checked</c> deliberately. Left unchecked,
  /// a time origin near <c>long.MaxValue</c> wraps to a large negative anchor,
  /// and the plausibility bounds then judge a number that was never on the wire.
  /// </summary>
  private static bool TryReadTimeline(
    ReadOnlySpan<byte> body,
    double clockOffsetMicros,
    out LinkTimeline timeline) {
    timeline = default;

    long microsPerBeat = BinaryPrimitives.ReadInt64BigEndian(body);
    long beatOriginMicroBeats = BinaryPrimitives.ReadInt64BigEndian(body[8..]);
    long timeOriginMicros = BinaryPrimitives.ReadInt64BigEndian(body[16..]);

    long anchorMicros;
    try {
      anchorMicros = checked(timeOriginMicros + (long)Math.Round(clockOffsetMicros));
    } catch(OverflowException) {
      return false;
    }

    LinkTimeline parsed = new LinkTimeline(
      microsPerBeat,
      beatOriginMicroBeats / MicroBeatsPerBeat,
      anchorMicros);

    if(!parsed.IsPlausible) {
      return false;
    }

    timeline = parsed;
    return true;
  }

  /// <summary>
  /// Finds the next chunk body carrying the given key and exact length, leaving
  /// <paramref name="offset"/> one byte past the match it returns.
  ///
  /// Scanning starts past the protocol literal rather than at a hardcoded chunk
  /// offset, and a candidate has to carry the expected length <em>and</em> fit
  /// inside the datagram to be accepted. Both filters exist for the same reason:
  /// four arbitrary bytes of node id can spell "tmln", and the scan has to walk
  /// past that rather than hand back sixteen bytes of identity as a tempo.
  ///
  /// It resumes one byte on rather than past the whole body, because a body it
  /// has just rejected is not a body - the real chunk may start inside those
  /// bytes, and skipping them would lose it.
  /// </summary>
  private static bool TryFindChunk(
    ReadOnlySpan<byte> packet,
    ReadOnlySpan<byte> key,
    int expectedSize,
    ref int offset,
    out ReadOnlySpan<byte> body) {
    while(offset + ChunkHeaderLength <= packet.Length) {
      int candidate = offset;
      offset++;

      if(!packet.Slice(candidate, ChunkKeyLength).SequenceEqual(key)) {
        continue;
      }

      int size = BinaryPrimitives.ReadInt32BigEndian(packet[(candidate + ChunkKeyLength)..]);
      if(size != expectedSize || candidate + ChunkHeaderLength + size > packet.Length) {
        continue;
      }

      body = packet.Slice(candidate + ChunkHeaderLength, size);
      return true;
    }

    body = default;
    return false;
  }
}
