<script lang="ts">
  /*
   * The drawing half of the stage, and deliberately only that half.
   *
   * What a bar's height should be lives in `lib/spectrum.ts` and where it should go
   * lives in `lib/scene.ts`, both under tests, because those are the parts that can be
   * wrong without looking wrong. What is left here is the part no test can settle: a
   * canvas, a redraw loop, the two pieces of browser bookkeeping a canvas always needs,
   * and the handful of gradients that are objects a context has to make.
   *
   * This is a test instrument as well as the show, which is what `docs/protocol.md`
   * says of the spectrum feed and what governs two choices below. The beat marker is
   * drawn from what the detector decided rather than from the beat grid, so the picture
   * can disagree with the grid and show that it does. And under `randomBeat` the colour
   * is the colour the bridge was sent, read back off the wire - never a second engine
   * computed here, because two implementations of one tuned behaviour would put a
   * screen and a room in disagreement with nobody able to say which was right.
   *
   * The picture lags the room slightly and by construction: the bridge gets 50 frames a
   * second and this gets the 20 Hz state feed. `approachColor` is what keeps that from
   * reading as a staircase, and the residual offset is a property of the design rather
   * than a fault to be chased.
   *
   * prefers-reduced-motion is not honoured, on the same argument `BeatIndicator`
   * makes: a still spectrum is not a calmer spectrum, it is no instrument at all.
   */

  import type {
    LightChannelData,
    LightColorData,
    SpectrumData,
    StageData,
  } from '../lib/protocol.ts';
  import {
    type Afterimage,
    afterimageAlpha,
    approachColor,
    approachLights,
    barRect,
    barThickness,
    dampPulse,
    HUE_SPEED_DEFAULT,
    hueCss,
    hueShift,
    isSmooth,
    keepAfterimages,
    lightAt,
    NEUTRAL_COLOR,
    pixelCapRects,
    rainbowStops,
    rgbCss,
    type SceneColor,
    sectionColor,
    sectionOf,
    type Side,
    smoothPoints,
    smoothStride,
    type StageSize,
    STATIC_STOPS,
    upwardBarRect,
    upwardPixelCapRect,
    upwardSectionOf,
    upwardSmoothPoints,
    WAVE_PHASE_TURNS,
} from '../lib/scene.ts';
  import { advanceVisual, emptyVisual, receiveFrame } from '../lib/spectrum.ts';

  interface Props {
    /**
     * The newest analyzer frame, or null while none has arrived.
     *
     * Null is not silence: it is what an unsubscribed page, a page opened a
     * moment ago and a dropped socket all look like, so it is drawn as an
     * absence rather than as a flat spectrum.
     */
    frame: SpectrumData | null;
    /**
     * The colour the bridge was last sent, or null while Flux is not streaming.
     *
     * Null draws a neutral colour rather than black. A dark stage during setup is
     * indistinguishable from a broken page, and the HUD already says the lights are
     * idle - so the absence is stated in words and not by turning the picture off.
     */
    color: LightColorData | null;
    /**
     * What each light was sent, or empty while Flux is not streaming.
     *
     * Empty falls back to painting the whole picture in `color`, which is what the
     * stage did before the engine had a colour per light and is still right for a
     * stream that has opened and not yet sent a frame.
     */
    lights: LightChannelData[];
    /** The rate that colour arrives at, which is what it is interpolated over. */
    stateHz: number;
    /**
     * How to draw, as the server has it with this page's own unconfirmed changes over
     * it.
     *
     * On the wire rather than in local storage because the look is part of the show. Its
     * `bands` is deliberately *not* read here: the count is what the analyzer produced,
     * so the frame's own length is the authority and a frame in flight when the setting
     * changed still draws correctly.
     */
    stage: StageData;
  }

  const { frame, color, lights, stateHz, stage }: Props = $props();

  /** Below this the flash is already invisible and not worth the draw call. */
  const FLASH_FLOOR = 0.01;

  /** How tall the beat marker is drawn along the top edge, in CSS pixels. */
  const FLASH_RIM_PX = 3;

  /** Both halves of the mirrored picture, in the order they are drawn. */
  const SIDES: readonly Side[] = ['right', 'left'];

  /**
   * The four quadrants a smooth outline is filled into, as sign pairs.
   *
   * One path filled four times under mirroring transforms rather than four paths, which
   * is both cheaper and the only arrangement in which the quadrants cannot disagree
   * about where a bar tip was.
   */
  const QUADRANTS: readonly { x: number; y: number }[] = [
    { x: 1, y: 1 },
    { x: 1, y: -1 },
    { x: -1, y: 1 },
    { x: -1, y: -1 },
  ];

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
    // blank stage rather than a broken page: everything else on it still works.
    const context = element.getContext('2d');
    if (context === null) {
      return;
    }

    const liveColor = paletteColor(element, '--live', '#4ade80');
    const accentColor = paletteColor(element, '--accent', '#60a5fa');
    const surfaceColor = paletteColor(element, '--surface', '#0b0d12');

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

    /** The colour on screen, which is behind the wire by up to one feed interval. */
    let shown: SceneColor = NEUTRAL_COLOR;

    /** Each light's colour on screen, smoothed the same way and separately. */
    let shownLights: SceneColor[] = [];
    let afterimages: Afterimage[] = [];

    /** Where each light listens, which the wire repeats every frame and only changes with the room. */
    let placements: number[] = [];

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
     * The functions below are consts holding functions rather than declarations, and
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

    /**
     * A gradient along the frequency axis, whichever way the picture is built.
     *
     * Upward spans the width once and shows every stop. Mirrored runs from the centre
     * outward over twice the half it covers, which is the scene's arrangement and is
     * load-bearing rather than incidental: only the first half of the stops is ever
     * visible, so eleven stops covering two turns of the wheel show one turn across
     * each half. Either way the window ends up with the same two turns across it, which
     * is what keeps switching layout from also changing how busy the colour is.
     */
    const axisGradient = (side: Side): CanvasGradient => {
      if (stage.layout === 'upward') {
        return context.createLinearGradient(0, 0, cssWidth, 0);
      }

      const centre = cssWidth / 2;
      const far = side === 'right' ? centre + cssWidth : centre - cssWidth;

      return context.createLinearGradient(centre, 0, far, 0);
    };

    const waveGradient = (side: Side, now: number): CanvasGradient => {
      const phase = hueShift(now, HUE_SPEED_DEFAULT) * WAVE_PHASE_TURNS;
      const gradient = axisGradient(side);

      for (const stop of rainbowStops(phase)) {
        gradient.addColorStop(stop.offset, hueCss(stop.hue));
      }

      return gradient;
    };

    /** The same rainbow down the picture instead of across it, and not mirrored. */
    const verticalWaveGradient = (now: number): CanvasGradient => {
      const phase = hueShift(now, HUE_SPEED_DEFAULT) * WAVE_PHASE_TURNS;
      const gradient = context.createLinearGradient(0, 0, 0, cssHeight);

      for (const stop of rainbowStops(phase)) {
        gradient.addColorStop(stop.offset, hueCss(stop.hue));
      }

      return gradient;
    };

    const staticGradient = (side: Side): CanvasGradient => {
      const gradient = axisGradient(side);

      for (const stop of STATIC_STOPS) {
        gradient.addColorStop(stop.offset, stop.first ? liveColor : accentColor);
      }

      return gradient;
    };

    /**
     * What one side is painted with, for the four styles the room has no equivalent of.
     *
     * `randomBeat` is absent on purpose: it is painted per column, because a column is
     * what knows which colour section it belongs to.
     */
    const sidePaint = (side: Side, now: number): string | CanvasGradient => {
      switch (stage.color) {
        case 'hueCycle':
          return hueCss(hueShift(now, HUE_SPEED_DEFAULT));
        case 'hueWave':
          return waveGradient(side, now);
        case 'hueWaveVert':
          return verticalWaveGradient(now);
        case 'static':
          return staticGradient(side);
        case 'randomBeat':
          return rgbCss(shown);
      }
    };

    /*
     * The fill style is only assigned when it changes, and that is worth the two lines.
     * With one colour section every bar on both sides is the same paint, so the
     * straightforward version reassigns it a hundred and twenty-eight times a frame on
     * a phone that is already filling the whole canvas twice.
     */
    let painted: string | CanvasGradient | null = null;

    const paintWith = (paint: string | CanvasGradient): void => {
      if (paint !== painted) {
        context.fillStyle = paint;
        painted = paint;
      }
    };

    const draw = (now: number): void => {
      const size: StageSize = { width: cssWidth, height: cssHeight };

      /*
       * Filled rather than cleared, so the canvas is opaque over whatever the page puts
       * behind it. Always fully, trail or no trail: the reference scene repainted at
       * partial opacity here to smear the picture, and the afterimage below replaces
       * that - an 8-bit canvas cannot quite fade a faint smear out, and a trail drawn from
       * the spectrum is simply not drawn once it has run out.
       */
      painted = null;
      context.fillStyle = surfaceColor;
      context.fillRect(0, 0, size.width, size.height);

      /*
       * However many the frame carries, which is the count the analyzer was asked for.
       * Read from the array rather than from `stage.bands` because a frame already in
       * flight when the setting changed carries the old count, and the array is the
       * thing being drawn.
       */
      const columns = visual.levels.length;
      if (columns === 0 || size.width <= 0 || size.height <= 0) {
        return;
      }

      const bars = visual.levels;

      const drawShape = (levels: readonly number[]): void => {
        if (isSmooth(stage.shape)) {
          drawSmooth(levels, size, columns, now);
        } else {
          drawBars(levels, size, columns, now);
        }
      };

      /*
       * The trail, drawn under the live picture in its own paint, oldest first so each
       * fresher copy lands over the one before it. A copy from before the column count
       * changed is skipped rather than stretched onto columns it does not belong to.
       */
      for (const copy of afterimages) {
        if (copy.levels.length === columns) {
          context.globalAlpha = afterimageAlpha(now - copy.atMs, stage.trailMs);
          drawShape(copy.levels);
        }
      }

      context.globalAlpha = 1;

      drawShape(bars);

      // Intensity is the whole distinction between a beat and a strong one:
      // `flashFor` hands them 0.5 and 1, and both then fall on the same curve.
      // Drawn as an edge rather than a wash over the picture, because the picture is
      // the evidence the marker is meant to be judged against.
      if (visual.flash > FLASH_FLOOR) {
        context.globalAlpha = Math.min(1, visual.flash);
        context.fillStyle = accentColor;
        context.fillRect(0, 0, size.width, FLASH_RIM_PX);
        context.globalAlpha = 1;
        painted = null;
      }
    };

    const drawBars = (
      bars: readonly number[],
      size: StageSize,
      columns: number,
      now: number,
    ): void => {
      /*
       * One colour per light, or the room's one colour where there are no lights to
       * draw - a stream that is not running, or one that has not sent its first frame.
       *
       * The section mapping the previous step left here turned out to be the wrong
       * shape by one step: the engine sends each light a place on the frequency axis
       * rather than a section index, so a column asks which light owns its frequency
       * instead of which section it falls in. Resolved to strings once a frame rather
       * than per bar, because that is a hundred and twenty-eight allocations a frame
       * for a handful of values.
       */
      /*
       * Damped on the way in, so that everything downstream - the section mapping, the
       * per-column lookup, the smooth and pixel paths - draws the same range without
       * each having to remember to. What it costs is one small array a frame, against a
       * whole-screen strobe at fifty frames a second, which is what the room's own
       * pulse depth looks like on a monitor a metre away.
       */
      const colors: SceneColor[] = (shownLights.length > 0 ? shownLights : [shown]).map((light) =>
        dampPulse(light),
      );
      const sections = colors.length;
      const sectionPaints = colors.map((_, index) => rgbCss(sectionColor(colors, index)));

      /*
       * Which light paints each column, resolved once a frame.
       *
       * Null when the room is one colour, which keeps the scene's own section mapping
       * as the fallback rather than making every drawing path ask whether lights exist.
       * The column's centre and not its left edge: a column covers a span of the
       * spectrum, and asking about its edge biases every one of them downward by half a
       * column.
       */
      const columnLights = shownLights.length > 0
        ? Array.from({ length: columns }, (_, index) =>
            lightAt(placements, (index + 0.5) / Math.max(1, columns)),
          )
        : null;

      const pixel = stage.shape === 'pixel';
      const thickness = barThickness(stage.shape, stage.layout);

      /*
       * The paint per side, resolved once a frame, or null where a column decides it.
       *
       * Only `randomBeat` is per column, because only it has sections: the other four
       * are one colour or one gradient across a whole side, and rebuilding a gradient
       * inside the column loop would make a hundred and twenty-eight of them a frame.
       */
      const sides = SIDES.map((side) => ({
        side,
        paint: stage.color === 'randomBeat' ? null : sidePaint(side, now),
      }));

      // One picture, one pass, every band its own bar. The mirrored path below draws
      // each band twice and is the reason it shows half as many of them.
      if (stage.layout === 'upward') {
        const paint = sides[0]?.paint ?? null;

        for (let index = 0; index < columns; index++) {
          const level = bars[index] ?? 0;

          if (level <= 0 && !pixel) {
            continue;
          }

          paintWith(
            paint
              ?? sectionPaints[columnLights?.[index] ?? upwardSectionOf(index, columns, sections)]
              ?? liveColor,
          );

          const rect = pixel
            ? upwardPixelCapRect(index, level, size, columns)
            : upwardBarRect(index, level, size, columns, thickness);

          context.fillRect(rect.x, rect.y, rect.width, rect.height);
        }

        return;
      }

      for (let index = 0; index < columns; index++) {
        const level = bars[index] ?? 0;

        // A silent column has nothing to paint under the bar styles, where a
        // zero-height rectangle is a wasted call. `pixel` is the exception: its caps
        // sit on the axis and are the baseline of the picture.
        if (level <= 0 && !pixel) {
          continue;
        }

        for (const { side, paint } of sides) {
          /*
           * Mirrored, both halves show the same light for a column, where the scene's
           * sections deliberately offset the left half by half of them. The scene was
           * colouring a strip by position and Flux is previewing a frequency: the
           * picture is mirrored about the centre, so the same column is the same part
           * of the spectrum on both sides and therefore the same light.
           */
          paintWith(
            paint
              ?? sectionPaints[columnLights?.[index] ?? sectionOf(index, columns, sections, side)]
              ?? liveColor,
          );

          if (pixel) {
            for (const cap of pixelCapRects(index, level, size, columns, side)) {
              context.fillRect(cap.x, cap.y, cap.width, cap.height);
            }
          } else {
            const bar = barRect(index, level, size, columns, thickness, side);
            context.fillRect(bar.x, bar.y, bar.width, bar.height);
          }
        }
      }
    };

    /**
     * One outline through the bar tips, filled once upward or into four quadrants
     * mirrored.
     *
     * Mirrored, the paint is the right-hand one in every quadrant: the transform is
     * what puts a gradient on the left rather than a second gradient built backwards,
     * and two of them would be two chances to disagree.
     */
    const drawSmooth = (
      bars: readonly number[],
      size: StageSize,
      columns: number,
      now: number,
    ): void => {
      const stride = smoothStride(stage.shape);
      const points =
        stage.layout === 'upward'
          ? upwardSmoothPoints(bars, size, columns, stride)
          : smoothPoints(bars, size, columns, stride);

      const first = points[0];
      if (first === undefined) {
        return;
      }

      const path = new Path2D();
      path.moveTo(first.x, first.y);

      for (let index = 1; index < points.length; index++) {
        const point = points[index];
        if (point !== undefined) {
          path.lineTo(point.x, point.y);
        }
      }

      path.closePath();

      const paint = sidePaint('right', now);

      // Nothing to mirror, so one fill and no transform bookkeeping at all.
      if (stage.layout === 'upward') {
        paintWith(paint);
        context.fill(path);

        return;
      }

      for (const quadrant of QUADRANTS) {
        context.save();
        context.scale(quadrant.x, quadrant.y);

        // Applied after the scale and therefore in the flipped space, which is what
        // brings the mirrored copy back over the canvas instead of off the edge of it.
        context.translate(quadrant.x < 0 ? -size.width : 0, quadrant.y < 0 ? -size.height : 0);

        context.fillStyle = paint;
        context.fill(path);
        context.restore();
      }

      // The saves restored a transform, not a fill style.
      painted = null;
    };

    const step = (now: number): void => {
      const elapsed = now - previous;
      previous = now;

      /*
       * The props are read here, inside the callback, and that is load-bearing rather
       * than incidental. A read in the effect body would make every arriving frame and
       * every setting a dependency, so the loop and the observer would be torn down and
       * rebuilt thirty times a second.
       */
      const arrived = frame;
      if (arrived !== null && arrived !== taken) {
        taken = arrived;
        visual = receiveFrame(visual, arrived);
      }

      visual = advanceVisual(visual, elapsed);

      const interval = 1000 / Math.max(1, stateHz);
      shown = approachColor(shown, color ?? NEUTRAL_COLOR, elapsed, interval);

      /*
       * Rebuilt from the frame every pass rather than kept in sync by hand. The array
       * is a handful of entries at 60 Hz, and the alternative is a second piece of
       * state that has to be invalidated whenever a stream opens against another room.
       */
      shownLights = approachLights(shownLights, lights, elapsed, interval);
      placements = lights.map((light) => light.at);

      // Emptied by the helper when the trail is off, so turning it back on starts from
      // the picture as it is rather than from one minutes old.
      afterimages = keepAfterimages(afterimages, visual.levels, now, stage.trailMs);

      resize();
      draw(now);

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
  /* Fills whatever box it is given and decides nothing about its own size, which is
     the caller's business now that the caller is the whole window. */
  .visualizer {
    position: relative;
    width: 100%;
    height: 100%;
  }

  canvas {
    display: block;
    width: 100%;
    height: 100%;
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
