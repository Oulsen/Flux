using Xunit;

namespace Flux.Beat.Tests;

public class LinkPacketTests {
  /// <summary>
  /// The two tempi Phase 0 cross-checked against rekordbox's on-screen readout,
  /// plus the one from the very first successful decode. These are the closest
  /// thing this project has to a golden fixture: each pairs a wire value with a
  /// BPM that a human read off another program's UI at the same moment.
  /// </summary>
  [Theory]
  [InlineData(342_857, 175.00)]
  [InlineData(334_504, 179.37)]
  [InlineData(336_436, 178.34)]
  public void DecodesTempiThatWereVerifiedAgainstRekordbox(long microsPerBeat, double expectedBpm) {
    byte[] packet = new AliveDatagram { MicrosPerBeat = microsPerBeat }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out LinkTimeline timeline));
    Assert.Equal(expectedBpm, timeline.Bpm, precision: 2);
  }

  [Fact]
  public void BuildsTheDatagramShapeObservedInPhaseZero() {
    byte[] packet = new AliveDatagram().ToBytes();

    Assert.Equal(AliveDatagram.ObservedLength, packet.Length);
    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out _));
  }

  [Fact]
  public void ScalesTheBeatOriginOutOfMicroBeats() {
    byte[] packet = new AliveDatagram { BeatOriginMicroBeats = 12_500_000 }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out LinkTimeline timeline));
    Assert.Equal(12.5, timeline.BeatOrigin, precision: 6);
  }

  /// <summary>
  /// Order A is Link's own struct order, and getting it wrong is the failure
  /// this project already walked into once: the swapped order yields an
  /// identical tempo while silently corrupting phase, so a BPM check passes and
  /// the bar marker lands nowhere.
  /// </summary>
  [Fact]
  public void ReadsTheSecondFieldAsBeatOriginAndTheThirdAsTimeOrigin() {
    byte[] packet = new AliveDatagram {
      MicrosPerBeat = 500_000,
      BeatOriginMicroBeats = 8_000_000,
      TimeOriginMicros = 1_000_000,
    }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out LinkTimeline timeline));

    Assert.Equal(8.0, timeline.BeatOrigin, precision: 6);
    Assert.Equal(1_000_000, timeline.TimeOriginMicros);

    // Order B would have anchored beat 1,000,000 at 8 seconds instead.
    Assert.Equal(8.0, timeline.BeatAt(1_000_000), precision: 6);
  }

  [Fact]
  public void ShiftsTheTimeOriginByTheClockOffset() {
    byte[] packet = new AliveDatagram { TimeOriginMicros = 1_000_000 }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 250_000, out LinkTimeline timeline));
    Assert.Equal(1_250_000, timeline.TimeOriginMicros);
  }

  [Fact]
  public void RejectsADatagramThatIsNotLink() {
    byte[] packet = new AliveDatagram { Literal = "_notlnk"u8.ToArray() }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out _));
  }

  [Fact]
  public void RejectsALinkDatagramCarryingNoTimeline() {
    byte[] packet = new AliveDatagram { IncludeTimeline = false }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out _));
  }

  [Fact]
  public void RejectsATimelineChunkThatOverrunsTheDatagram() {
    byte[] packet = new AliveDatagram { DeclaredTimelineSize = 24 }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet.AsSpan(0, 30), clockOffsetMicros: 0, out _));
  }

  /// <summary>
  /// A tempo outside the plausible band means the bytes were not a timeline,
  /// whatever the chunk header claimed. Letting one through would put a
  /// nonsense BPM on screen and get blamed on Link.
  /// </summary>
  [Theory]
  [InlineData(0)]
  [InlineData(-342_857)]
  [InlineData(long.MaxValue)]
  [InlineData(LinkTimeline.MaxMicrosPerBeat + 1)]
  [InlineData(LinkTimeline.MinMicrosPerBeat - 1)]
  public void RejectsAnImplausibleTempo(long microsPerBeat) {
    byte[] packet = new AliveDatagram { MicrosPerBeat = microsPerBeat }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out _));
  }

  /// <summary>
  /// The header length is treated as unknown on purpose - the chunk is located
  /// by scanning, so a header this code has never seen does not break the
  /// decode.
  /// </summary>
  [Fact]
  public void FindsTheTimelineEvenWhenTheHeaderIsLongerThanExpected() {
    byte[] packet = new AliveDatagram {
      HeaderNoise = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05],
      MicrosPerBeat = 342_857,
    }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out LinkTimeline timeline));
    Assert.Equal(175.00, timeline.Bpm, precision: 2);
  }

  /// <summary>
  /// Four bytes of node id can spell "tmln" by chance. The scan has to walk past
  /// a match whose declared size is wrong instead of handing back identity bytes
  /// as a tempo.
  /// </summary>
  [Fact]
  public void WalksPastAChunkKeyThatIsNotAChunk() {
    byte[] decoy = [
      (byte)'t', (byte)'m', (byte)'l', (byte)'n',
      0x00, 0x00, 0x00, 0x08,
      0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];

    byte[] packet = new AliveDatagram { HeaderNoise = decoy, MicrosPerBeat = 342_857 }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out LinkTimeline timeline));
    Assert.Equal(175.00, timeline.Bpm, precision: 2);
  }

  /// <summary>
  /// The harder decoy: one that declares the right size too, so it is
  /// indistinguishable from the real chunk by shape alone. Only its contents give
  /// it away, which is why a candidate that fails to decode has to be walked past
  /// rather than treated as the answer.
  /// </summary>
  [Fact]
  public void WalksPastADecoyThatDeclaresTheRightSize() {
    byte[] decoy = [
      (byte)'t', (byte)'m', (byte)'l', (byte)'n',
      0x00, 0x00, 0x00, 0x18,
      .. Enumerable.Repeat((byte)0xFF, 24),
    ];

    byte[] packet = new AliveDatagram { HeaderNoise = decoy, MicrosPerBeat = 342_857 }.ToBytes();

    Assert.True(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out LinkTimeline timeline));
    Assert.Equal(175.00, timeline.Bpm, precision: 2);
  }

  /// <summary>
  /// An anchor is two thirds of the timeline and was previously taken on trust.
  /// A wild one paired with a plausible tempo puts the beat number somewhere
  /// around 1e13, which rotates the bar readout at random and gets blamed on
  /// Link rather than on the packet.
  /// </summary>
  [Fact]
  public void RejectsATimeOriginThatCannotBeAHostClock() {
    byte[] packet = new AliveDatagram { TimeOriginMicros = long.MaxValue }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out _));
  }

  [Fact]
  public void RejectsABeatOriginNoSessionCouldReach() {
    byte[] packet = new AliveDatagram { BeatOriginMicroBeats = long.MaxValue }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet, clockOffsetMicros: 0, out _));
  }

  /// <summary>
  /// Adding the offset is checked, so an offset that cannot be applied is a
  /// rejected packet rather than an anchor that silently wrapped to a large
  /// negative number and then passed the bounds test.
  /// </summary>
  [Fact]
  public void RejectsAClockOffsetThatWouldWrapTheAnchor() {
    byte[] packet = new AliveDatagram { TimeOriginMicros = 1_000_000 }.ToBytes();

    Assert.False(LinkPacket.TryParseTimeline(packet, double.MaxValue, out _));
    Assert.False(LinkPacket.TryParseTimeline(packet, double.NaN, out _));
  }

  /// <summary>
  /// The session id is the only stable identity a Link session has: every peer in
  /// it republishes the same value, which is what makes it followable when a
  /// second session is on the same network.
  /// </summary>
  [Fact]
  public void ReadsTheSessionIdEveryPeerRepublishes() {
    byte[] packet = new AliveDatagram { SessionId = 0xDEAD_BEEF_0000_0001 }.ToBytes();

    Assert.True(LinkPacket.TryParseSessionId(packet, out ulong session));
    Assert.Equal(0xDEAD_BEEF_0000_0001, session);
  }

  [Fact]
  public void ReportsNoSessionIdWhenTheDatagramNamesNoSession() {
    byte[] packet = new AliveDatagram { IncludeSession = false }.ToBytes();

    Assert.False(LinkPacket.TryParseSessionId(packet, out _));
    Assert.False(LinkPacket.TryParseSessionId([0x5F, 0x61], out _));
  }

  [Fact]
  public void RejectsADatagramTooShortToHoldAHeader() {
    Assert.False(LinkPacket.TryParseTimeline([0x5F, 0x61], clockOffsetMicros: 0, out _));
    Assert.False(LinkPacket.TryParseTimeline([], clockOffsetMicros: 0, out _));
  }
}
