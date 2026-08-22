using System.Buffers.Binary;
using System.Text;

namespace Flux.Beat;

/// <summary>
/// Decodes the one thing Flux needs out of an Ableton Link ALIVE datagram: the
/// <c>tmln</c> chunk carrying tempo and beat phase.
///
/// Flux is a read-only peer. It never announces itself, never answers a ping and
/// never becomes tempo master, so this is the whole protocol surface - a
/// deliberate trade documented under "Beat sync" in the findings.
///
/// Two things are known and neither is guessed at here. The packet carries no
/// meter or bar length, so bar 1 cannot come from Link at all; and the chunks
/// sit at fixed offsets on this hardware, but they are located by scanning
/// anyway, because a scan with a validated size field costs nothing and survives
/// a header this code has not seen.
/// </summary>
public static class LinkPacket {
  /// <summary>The literal that opens every Link datagram, ahead of a version byte.</summary>
  private static readonly byte[] ProtocolHeader = Encoding.ASCII.GetBytes("_asdp_v");

  /// <summary>Four-character chunk key, followed by a big-endian int32 length.</summary>
  private const int ChunkKeyLength = 4;

  private const int ChunkHeaderLength = 8;

  /// <summary>Three big-endian int64s: tempo, beat origin, time origin.</summary>
  private const int TimelineBodyLength = 24;

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
  /// <returns>False for anything that is not a Link packet carrying a plausible tempo.</returns>
  public static bool TryParseTimeline(
    ReadOnlySpan<byte> packet,
    double clockOffsetMicros,
    out LinkTimeline timeline) {
    timeline = default;

    if(!packet.StartsWith(ProtocolHeader)) {
      return false;
    }

    if(!TryFindChunk(packet, "tmln", TimelineBodyLength, out ReadOnlySpan<byte> body)) {
      return false;
    }

    long microsPerBeat = BinaryPrimitives.ReadInt64BigEndian(body);
    long beatOriginMicroBeats = BinaryPrimitives.ReadInt64BigEndian(body[8..]);
    long timeOriginMicros = BinaryPrimitives.ReadInt64BigEndian(body[16..]);

    LinkTimeline parsed = new LinkTimeline(
      microsPerBeat,
      beatOriginMicroBeats / MicroBeatsPerBeat,
      timeOriginMicros + (long)Math.Round(clockOffsetMicros));

    if(!parsed.IsPlausible) {
      return false;
    }

    timeline = parsed;
    return true;
  }

  /// <summary>
  /// Finds a chunk body by its four-character key and exact length.
  ///
  /// Scanning starts past the protocol literal rather than at a hardcoded chunk
  /// offset, and a candidate has to carry the expected length <em>and</em> fit
  /// inside the datagram to be accepted. Both filters exist for the same reason:
  /// four arbitrary bytes of node id can spell "tmln", and the scan has to walk
  /// past that rather than hand back sixteen bytes of identity as a tempo.
  /// </summary>
  private static bool TryFindChunk(
    ReadOnlySpan<byte> packet,
    string key,
    int expectedSize,
    out ReadOnlySpan<byte> body) {
    Span<byte> needle = stackalloc byte[ChunkKeyLength];
    Encoding.ASCII.GetBytes(key, needle);

    for(int offset = ProtocolHeader.Length; offset + ChunkHeaderLength <= packet.Length; offset++) {
      if(!packet.Slice(offset, ChunkKeyLength).SequenceEqual(needle)) {
        continue;
      }

      int size = BinaryPrimitives.ReadInt32BigEndian(packet[(offset + ChunkKeyLength)..]);
      if(size != expectedSize || offset + ChunkHeaderLength + size > packet.Length) {
        continue;
      }

      body = packet.Slice(offset + ChunkHeaderLength, size);
      return true;
    }

    body = default;
    return false;
  }
}
