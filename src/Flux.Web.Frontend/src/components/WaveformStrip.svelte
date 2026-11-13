<script lang="ts">
  /*
   * The deck being followed, as a strip across the stage: the track's waveform scrolling
   * under a fixed playhead, the track's beatgrid over it, and Flux's own bar 1 marked.
   *
   * What it is for is the mark. rekordbox's downbeats are the brighter grid lines and
   * Flux's bar 1 is a green line with a "1" on it; when it covers rekordbox's line the
   * lights count bars where the track does, and when it does not, the beat shift on the
   * stage is the fix. Green because neither palette's bands use it: a small blue tick,
   * the first try, disappeared into Flux's own blue bands. Drawn only while Flux's bar
   * is anchored, since an unanchored count has no bar 1 to show.
   * Before this, whether bar 1 was right was something to listen for.
   *
   * The waveform comes once per track over HTTP and the playhead with every state frame;
   * between frames the playhead is carried on at the deck's own rate and eased onto each
   * new frame, so the strip scrolls at the screen's rate rather than in 50 ms steps.
   * Three styles, after rekordbox's own views. RGB, the default, is rekordbox's colour
   * waveform, each column in the colour rekordbox gave it, so the strip looks like the
   * deck on the screen beside it. 3-band is rekordbox's other detail waveform in its blue,
   * amber and white, and flux is the same data in Flux's blues. All are mirrored about the
   * centre as rekordbox draws them; the 3-band ones low first and high last, so the
   * treble's detail stays on top. A style whose waveform the track lacks draws the other.
   */

  import type { GridData, TrackData } from '../lib/protocol.ts';
  import {
    beatAt,
    decodeTrack,
    type DeckTrack,
    easePlayhead,
    fluxDownbeat,
    type HeldDeck,
    isFluxDownbeat,
    MAX_HEIGHT,
    peakOver,
    playheadAt,
    RGB_MAX_COLOR,
    RGB_MAX_HEIGHT,
    rgbOver,
  } from '../lib/waveform.ts';
  import type { WaveformStyle } from '../lib/preferences.ts';

  interface Props {
    /** The deck and when its frame arrived. Never null here: the caller hides the strip without one. */
    held: HeldDeck;
    /** Flux's grid on the same state frame, for where its bar 1 falls. */
    grid: GridData | null;
    /** Whether Flux's bar position means anything; without it there is no bar 1 to mark. */
    aligned: boolean;
    quantum: number;
    /** Which waveform is drawn, and in whose colours. */
    style: WaveformStyle;
  }

  const { held, grid, aligned, quantum, style }: Props = $props();

  /**
   * rekordbox's 3-band colours as its screen shows them: dark blue lows, amber mids,
   * white highs. Picked by eye, not read from rekordbox, and drawn in order rather than
   * blended, so where low and mid overlap the mid shows: close to rekordbox's screen,
   * not a copy of how it mixes them.
   */
  const REKORDBOX_BANDS: [string, number][] = [
    ['#1d4ed8', 1],
    ['#f59e0b', 0.9],
    ['#ffffff', 0.9],
  ];

  /**
   * How many seconds of track the strip shows, by its width. Flux's own numbers: a
   * second for every 150 pixels keeps a beat at 120 BPM about 75 pixels wide, as rekordbox's
   * own detail view roughly is, within four seconds on a phone and twelve on a wide screen.
   */
  const PIXELS_PER_SECOND = 150;
  const MIN_SECONDS = 4;
  const MAX_SECONDS = 12;

  let canvas = $state<HTMLCanvasElement | null>(null);
  let track = $state<DeckTrack | null>(null);

  /*
   * The id alone, derived so that it notifies only when it changes. The deck itself is a
   * new object on every state frame, and an effect reading it directly re-ran 20 times a
   * second, each run's cleanup aborting the fetch the run before had started - so no
   * waveform ever arrived.
   */
  const trackId = $derived(held.deck.track);

  // One fetch per track id. The id changes with the track and with its analysis, and
  // the response for one never changes, so the browser may keep it for good.
  $effect(() => {
    const id = trackId;
    track = null;
    const controller = new AbortController();
    fetch(`/api/track/${encodeURIComponent(id)}`, { signal: controller.signal })
      .then((response) => (response.ok ? (response.json() as Promise<TrackData>) : null))
      .then((data) => {
        if (data !== null && !controller.signal.aborted) {
          track = decodeTrack(data);
        }
      })
      .catch(() => {
        // A track replaced before it arrived, or Flux gone for a moment: the next frame
        // with a new id asks again, and until then the strip shows the grid it has.
      });

    return () => controller.abort();
  });

  /** Grid beat of one of Flux's bar 1s, from this frame; null with nothing to pair. */
  const downbeat = $derived.by(() => {
    if (track === null || track.beatTimesMs.length < 2 || grid === null || !aligned) {
      return null;
    }

    return fluxDownbeat(beatAt(track.beatTimesMs, held.deck.positionMs), grid.beat, grid.positionInBar);
  });

  function paletteColor(element: Element, name: string, fallback: string): string {
    const value = getComputedStyle(element).getPropertyValue(name).trim();
    return value === '' ? fallback : value;
  }

  $effect(() => {
    const element = canvas;
    if (element === null) {
      return;
    }

    const context = element.getContext('2d');
    if (context === null) {
      return;
    }

    const ink = paletteColor(element, '--ink', '#e8ecf4');
    const dim = paletteColor(element, '--ink-dim', '#8b93a7');
    const faint = paletteColor(element, '--ink-faint', '#5b6274');
    const accent = paletteColor(element, '--accent', '#60a5fa');
    const live = paletteColor(element, '--live', '#4ade80');

    let cssWidth = element.clientWidth;
    let cssHeight = element.clientHeight;
    const observer = new ResizeObserver((entries) => {
      const box = entries[0]?.contentRect;
      if (box !== undefined) {
        cssWidth = box.width;
        cssHeight = box.height;
      }
    });
    observer.observe(element);

    let shown: number | null = null;
    let frame = 0;

    const draw = (): void => {
      frame = requestAnimationFrame(draw);

      const ratio = window.devicePixelRatio || 1;
      const width = Math.round(cssWidth * ratio);
      const height = Math.round(cssHeight * ratio);
      if (element.width !== width || element.height !== height) {
        element.width = width;
        element.height = height;
      }

      context.clearRect(0, 0, width, height);
      if (width === 0 || height === 0) {
        return;
      }

      shown = easePlayhead(shown, playheadAt(held, performance.now()));
      const spanMs = Math.min(MAX_SECONDS, Math.max(MIN_SECONDS, cssWidth / PIXELS_PER_SECOND)) * 1000;
      const msPerPixel = spanMs / width;
      const leftMs = shown - spanMs / 2;
      const centre = height / 2;
      const half = centre - 2 * ratio;

      const perMs = track === null ? 0 : track.entriesPerSecond / 1000;
      const drawsRgb = track !== null && track.heightRgb.length > 0 && (style === 'rgb' || track.lowMidHigh.length === 0);
      if (track !== null && drawsRgb) {
        // Each column in its own colour, so one fill per pixel: the colour is the column's
        // and changes from one to the next.
        for (let x = 0; x < width; x++) {
          const from = (leftMs + x * msPerPixel) * perMs;
          const column = rgbOver(track.heightRgb, from, from + msPerPixel * perMs);
          if (column === null || column[0] === 0) {
            continue;
          }

          const h = (column[0] / RGB_MAX_HEIGHT) * half;
          const scale = 255 / RGB_MAX_COLOR;
          context.fillStyle = `rgb(${Math.round(column[1] * scale)} ${Math.round(column[2] * scale)} ${Math.round(column[3] * scale)})`;
          context.fillRect(x, centre - h, 1, 2 * h);
        }
      } else if (track !== null && track.lowMidHigh.length > 0) {
        const palette: [string, number][] =
          style === '3-band'
            ? REKORDBOX_BANDS
            : [
                [accent, 0.35],
                [accent, 0.75],
                [ink, 0.85],
              ];

        for (let band = 0; band < 3; band++) {
          const [colour, alpha] = palette[band];
          context.globalAlpha = alpha;
          context.fillStyle = colour;
          for (let x = 0; x < width; x++) {
            const from = (leftMs + x * msPerPixel) * perMs;
            if (from + msPerPixel * perMs < 0) {
              continue;
            }

            const peak = peakOver(track.lowMidHigh, from, from + msPerPixel * perMs)[band];
            const h = (peak / MAX_HEIGHT) * half;
            if (h > 0) {
              context.fillRect(x, centre - h, 1, 2 * h);
            }
          }
        }

        context.globalAlpha = 1;
      }

      if (track !== null && track.beatTimesMs.length > 0) {
        const first = Math.max(0, Math.floor(beatAt(track.beatTimesMs, leftMs)));
        const last = Math.min(track.beatTimesMs.length - 1, Math.ceil(beatAt(track.beatTimesMs, leftMs + spanMs)));
        for (let beat = first; beat <= last; beat++) {
          const x = Math.round((track.beatTimesMs[beat] - leftMs) / msPerPixel);
          const trackDownbeat = track.beatsInBar[beat] === 1;

          context.fillStyle = trackDownbeat ? dim : faint;
          context.globalAlpha = trackDownbeat ? 0.9 : 0.5;
          context.fillRect(x, 0, Math.max(1, Math.round((trackDownbeat ? 2 : 1) * ratio)), height);
          context.globalAlpha = 1;

          if (downbeat !== null && isFluxDownbeat(beat, downbeat, quantum)) {
            const line = Math.max(2, Math.round(2 * ratio));
            context.fillStyle = live;
            context.fillRect(x - Math.floor(line / 2), 0, line, height);
            context.font = `600 ${Math.round(11 * ratio)}px system-ui, sans-serif`;
            context.textBaseline = 'top';
            context.fillText('1', x + 3 * ratio, 2 * ratio);
          }
        }
      }

      context.fillStyle = ink;
      context.fillRect(Math.round(width / 2) - Math.round(ratio), 0, Math.max(1, Math.round(2 * ratio)), height);
    };

    frame = requestAnimationFrame(draw);

    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
    };
  });
</script>

<div class="strip" role="img" aria-label="Deck {held.deck.number}: the track's waveform and beatgrid, with Flux's bar 1 marked">
  <span class="label">deck {held.deck.number}</span>
  <canvas bind:this={canvas}></canvas>
</div>

<style>
  .strip {
    position: relative;
    height: 4.5rem;
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    background: rgb(11 13 18 / 72%);
    overflow: hidden;
  }

  canvas {
    display: block;
    width: 100%;
    height: 100%;
  }

  .label {
    position: absolute;
    top: var(--space-1);
    left: var(--space-2);
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
    pointer-events: none;
  }

  @media (max-width: 40rem) {
    .strip {
      height: 3.5rem;
    }
  }
</style>
