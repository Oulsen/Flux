namespace Flux.Hue;

/// <summary>
/// Where the bridge says a light is, in the entertainment area's own coordinates.
///
/// Roughly a unit cube centred on the room, with x to the right, y toward the front
/// and z upward. Roughly, because these are not a measurement: they are where somebody
/// dragged a bulb onto a picture in the Hue app, and a probe against the two real areas
/// here found them plausible rather than accurate. Nothing downstream may treat a
/// coordinate as a distance in metres, and nothing does.
/// </summary>
public readonly record struct LightPosition(double X, double Y, double Z) {
  /// <summary>The middle of the room, and what an unplaced light reports.</summary>
  public static LightPosition Centre { get; } = new LightPosition(0, 0, 0);

  /// <summary>How far this light sits from the centre of the room.</summary>
  public double Radius => Math.Sqrt((this.X * this.X) + (this.Y * this.Y) + (this.Z * this.Z));
}

/// <summary>One channel of an entertainment area, as the light engine needs to see one.</summary>
/// <param name="Id">The channel id the bridge streams to, which is not necessarily its index.</param>
/// <param name="Position">Where it is, or <see cref="LightPosition.Centre"/> when the bridge did not say.</param>
public readonly record struct HueChannel(int Id, LightPosition Position);

/// <summary>
/// One light's place on the frequency axis: 0 follows the kick, 1 follows the top.
/// </summary>
/// <param name="ChannelId">Which channel this describes.</param>
/// <param name="At">
/// Where on the spectrum this light listens, 0 to 1.
///
/// This is what the page is sent, so that the stage can paint a column with the colour
/// of the light that owns it, and it is deliberately the server's answer rather than
/// something a browser recomputes from array order. The mapping is tuned in one place
/// or it disagrees with itself.
/// </param>
public readonly record struct LightPlacement(int ChannelId, double At);

/// <summary>
/// Turns a room's geometry into a frequency for each light.
///
/// Radial: the lights nearest the centre of the room follow the kick, the ones out at
/// the edges follow the highs, so a bass hit punches in the middle and the corners
/// shimmer. Chosen over a left-to-right sweep and over height, on 2026-09-10, and the
/// reason is a property rather than a taste - a radius does not care how somebody
/// rotated the room when they placed the lights, while an axis does, and nobody
/// arranging bulbs in a living room is thinking about which wall Flux considers front.
///
/// <b>Rank rather than raw distance, and that is the whole of why this file exists.</b>
/// The obvious implementation scales the distance into 0 to 1 directly, and it fails on
/// a real room: of the seven lights in this rig's Woonkamer area, four sit between 1.00
/// and 1.07 from the centre, so they would share a band while the remaining three
/// covered the rest of the spectrum between them. Ranking spreads whatever the room
/// happens to be across the whole range, and it keeps the one property that matters -
/// the ordering is still the room's, not an accident of channel numbering.
/// </summary>
internal static class LightPlacements {
  /// <summary>
  /// A frequency for each channel, nearest the centre first.
  ///
  /// The result is ordered by <see cref="LightPlacement.At"/> and not by channel id,
  /// which is worth knowing at the call sites: the stream writes colours by channel
  /// index, so it looks each one up rather than zipping two lists together.
  /// </summary>
  internal static IReadOnlyList<LightPlacement> Spread(IReadOnlyList<HueChannel> channels) {
    ArgumentNullException.ThrowIfNull(channels);

    if(channels.Count == 0) {
      return [];
    }

    if(channels.Count == 1) {
      // The kick rather than the middle of the spectrum. One lamp in a room is the
      // whole show, and the one band a person standing in front of it wants to see is
      // the one they can feel.
      return [new LightPlacement(channels[0].Id, 0)];
    }

    // Ordered by distance and then by id, so a room whose channels arrive in a
    // different order produces the same mapping. The bridge lists them consistently
    // today; a mapping that quietly depended on that would break on the day it stops.
    List<HueChannel> ordered = [.. channels.OrderBy(channel => channel.Position.Radius).ThenBy(channel => channel.Id)];

    LightPlacement[] placements = new LightPlacement[ordered.Count];
    int index = 0;

    while(index < ordered.Count) {
      // Lights at the same distance share one frequency, rather than being handed
      // adjacent ones by whichever id sorted first. This is what keeps the mapping a
      // function of the room instead of a function of the list: a pair of bulbs
      // flanking a booth is placed as a pair and should behave as one, and the
      // alternative has them drifting apart for a reason nobody can see from the
      // floor. Exact equality on purpose - a tolerance is not transitive, so it would
      // group a with b and b with c while leaving a and c apart.
      double radius = ordered[index].Position.Radius;
      int last = index;
      while(last + 1 < ordered.Count && ordered[last + 1].Position.Radius == radius) {
        last++;
      }

      // The middle of the ranks the tie spans, so a group of three sits where the
      // middle one would have and the spectrum either side of it is unaffected.
      double at = (index + last) / 2.0 / (ordered.Count - 1);

      for(int member = index; member <= last; member++) {
        placements[member] = new LightPlacement(ordered[member].Id, at);
      }

      index = last + 1;
    }

    return placements;
  }
}
