<script lang="ts">
  /*
   * The drawing half of the visualizer, and deliberately only that half.
   *
   * Every decision about what a bar's height should be at this instant lives in
   * `lib/spectrum.ts`, under tests, because that is the part that can be wrong
   * without looking wrong. What is left here is the part no test can settle: a
   * canvas, a redraw loop and the two pieces of browser bookkeeping a canvas
   * always needs, which are the pixel ratio and its own size.
   *
   * This is a test instrument rather than decoration, which is what
   * `docs/protocol.md` says of the feed and what governs the choices below: the
   * beat marker is drawn from what the detector decided, not from the beat grid,
   * so the picture can disagree with the grid and show that it does. Comparing
   * the two is the entire reason the feed exists.
   *
   * prefers-reduced-motion is not honoured, on the same argument `BeatIndicator`
   * makes: a still spectrum is not a calmer spectrum, it is no instrument at all.
   */

  import type { SpectrumData } from '../lib/protocol.ts';
  import { advanceVisual, barGeometry, emptyVisual, receiveFrame } from '../lib/spectrum.ts';

  interface Props {
    /**
     * The newest analyzer frame, or null while none has arrived.
     *
     * Null is not silence: it is what an unsubscribed page, a page opened a
     * moment ago and a dropped socket all look like, so it is drawn as an
     * absence rather than as a flat spectrum.
     */
    frame: SpectrumData | null;
  }

  const { frame }: Props = $props();

  /** Below this the flash is already invisible and not worth the draw call. */
  const FLASH_FLOOR = 0.01;

  /** How tall the beat marker is drawn along the top edge, in CSS pixels. */
  const FLASH_RIM_PX = 3;

  let canvas = $state<HTMLCanvasElement | null>(null);

  /*
   * The palette lives in `app.css` and a canvas cannot read a custom property,
   * so it is resolved from the element once rather than restated here. A second
   * copy of these colours would drift the first time one of them is retuned.
   */
  function paletteColor(element: Element, name: string, fallback: string): string {
    const value = getComputedStyle(element).getPropertyValue(name).trim();

    return value === '' ? fallback : value;
  }

  $effect(() => {
    const element = canvas;
    if (element === null) {
      return;
    }

    // A machine that cannot give a 2d context cannot be drawn on, and that is a
    // blank panel rather than a broken page: everything else on it still works.
    const context = element.getContext('2d');
    if (context === null) {
      return;
    }

    const barColor = paletteColor(element, '--live', '#4ade80');
    const flashColor = paletteColor(element, '--accent', '#60a5fa');

    let visual = emptyVisual(0);

    /*
     * Which frame has already been taken, compared by identity and never by
     * contents.
     *
     * Frames arrive at 30 Hz and the screen redraws at roughly twice that, so
     * most redraws see a frame they have seen before. Taking one twice would
     * re-trigger its flash on every redraw and the flash would never decay at
     * all. Identity is the right test because two frames carrying the same
     * numbers are still two separate statements about the music.
     */
    let taken: SpectrumData | null = null;

    let cssWidth = element.clientWidth;
    let cssHeight = element.clientHeight;
    let previous = performance.now();

    const observer = new ResizeObserver((entries) => {
      const box = entries[0]?.contentRect;

      if (box !== undefined) {
        cssWidth = box.width;
        cssHeight = box.height;
      }
    });

    observer.observe(element);

    /*
     * The three below are consts holding functions rather than declarations, and
     * that is a type-system requirement rather than a style choice: a hoisted
     * declaration could in principle run before the two null guards above, so
     * TypeScript refuses to carry the narrowing into one and every use of the
     * element and the context inside it is an error.
     */

    /** Matches the backing store to the CSS size at the current pixel ratio. */
    const resize = (): void => {
      const ratio = window.devicePixelRatio || 1;
      const backingWidth = Math.max(0, Math.round(cssWidth * ratio));
      const backingHeight = Math.max(0, Math.round(cssHeight * ratio));

      if (element.width === backingWidth && element.height === backingHeight) {
        return;
      }

      element.width = backingWidth;
      element.height = backingHeight;

      // Checked every redraw rather than only when the observer fires, because
      // dragging the window to a second monitor changes the pixel ratio while
      // the CSS size stays exactly the same, and the observer never fires for
      // it. Assigning either dimension also resets the context, so the
      // transform that lets everything below draw in CSS pixels belongs here
      // and only here.
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
    };

    const draw = (): void => {
      context.clearRect(0, 0, cssWidth, cssHeight);

      const columns = visual.levels.length;
      const { slot, width } = barGeometry(cssWidth, columns);

      if (slot > 0) {
        context.fillStyle = barColor;

        for (let index = 0; index < columns; index++) {
          const height = (visual.levels[index] ?? 0) * cssHeight;

          if (height > 0) {
            context.fillRect(index * slot, cssHeight - height, width, height);
          }
        }
      }

      // Intensity is the whole distinction between a beat and a strong one:
      // `flashFor` hands them 0.5 and 1, and both then fall on the same curve.
      // Drawn as an edge rather than a wash over the bars, because the bars are
      // the evidence the marker is meant to be judged against.
      if (visual.flash > FLASH_FLOOR) {
        context.globalAlpha = Math.min(1, visual.flash);
        context.fillStyle = flashColor;
        context.fillRect(0, 0, cssWidth, FLASH_RIM_PX);
        context.globalAlpha = 1;
      }
    };

    const step = (now: number): void => {
      const elapsed = now - previous;
      previous = now;

      /*
       * The prop is read here, inside the callback, and that is load-bearing
       * rather than incidental. A read in the effect body would make every
       * arriving frame a dependency, so the loop and the observer would be torn
       * down and rebuilt thirty times a second.
       */
      const arrived = frame;
      if (arrived !== null && arrived !== taken) {
        taken = arrived;
        visual = receiveFrame(visual, arrived);
      }

      visual = advanceVisual(visual, elapsed);

      resize();
      draw();

      handle = requestAnimationFrame(step);
    };

    let handle = requestAnimationFrame(step);

    return () => {
      cancelAnimationFrame(handle);
      observer.disconnect();
    };
  });
</script>

<div class="visualizer">
  <!-- The text inside is the canvas element's own fallback content, which is
       how the HTML specification says to describe one. An ARIA role would say
       the same thing and is invalid here: a canvas is interactive content. -->
  <canvas bind:this={canvas}>A live audio spectrum, marked at each detected beat.</canvas>

  {#if frame === null}
    <p class="waiting">waiting for analyzer frames</p>
  {/if}
</div>

<style>
  /* Grows with the window, within bounds: below about nine rems the bands stop
     being distinguishable, and above sixteen the picture gains height without
     gaining information, since a spectrum is read across and not up. */
  .visualizer {
    position: relative;
    height: clamp(9rem, 22vh, 16rem);
  }

  canvas {
    display: block;
    width: 100%;
    height: 100%;
    background: var(--surface);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
  }

  .waiting {
    position: absolute;
    inset: 0;
    margin: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    color: var(--ink-faint);
  }
</style>
