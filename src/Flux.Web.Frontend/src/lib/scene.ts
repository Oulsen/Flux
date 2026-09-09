/**
 * The geometry behind the stage, with no canvas anywhere near it.
 *
 * The same split `spectrum.ts` already argues for, applied to the part of the
 * picture that is shape rather than height: where a bar goes, which colour section
 * it belongs to, and where the smooth outline turns. Every one of those can be wrong
 * arithmetically while still looking like a plausible visualizer, which is exactly
 * the class of mistake a screenshot cannot settle and a test can.
 *
 * Heights are not here. They arrive already enveloped from `spectrum.ts`, whose
 * attack and release were measured against real music and confirmed at the desk;
 * this module takes a level from 0 to 1 and decides only where to put it.
 *
 * The constants are the scene's, transcribed as coded from
 * `.plan/reference/oulsen-dj-scene.html` (the drawing half, lines 986 to 1351).
 * Four places depart from it, and each says so where it is declared: the level curve,
 * the default layout, what the band control means, and the two static colours.
 */

import type { LightColorData, StageColorStyle, StageLayout, StageShape } from './protocol.ts';

/**
 * Why the default layout is Flux's own rather than the scene's, which is the one
 * departure this file has to argue rather than declare.
 *
 * `mirrored` is the scene's: bars grow outward from the centre and each is centred on
 * the horizontal axis. In SignalRGB that is not only a look - the canvas *is* the
 * device, sampled per LED by position, so mirroring is how a symmetric strip ends up
 * looking symmetric. Flux drives bulbs from an entertainment configuration that reports
 * a real position per channel, so none of that carries over and the mirroring here is
 * taste.
 *
 * It also costs something measurable, which is why `upward` leads: every band is drawn
 * twice, so a window's width shows half as many distinct values, and the edge a person
 * watches travels half as far as it does growing up from the floor.
 */
export const LAYOUTS: readonly StageLayout[] = ['upward', 'mirrored'];

/*
 * What to draw before the server has said.
 *
 * These four mirror `StageSettings.Default` on the server, which is the authority: a
 * `stage` frame arrives within a handshake and replaces them. They exist because a
 * canvas that waited for it would flash an empty window on every load, which is the
 * same argument the store's `quantum` of 4 already runs on. Where they and the server
 * disagree, the wire wins within a frame or two and nobody sees it - so this is a
 * duplicate that cannot drift into a bug, only into a flicker.
 */

/** Flux's own layout leads, since it is the one that reads better at a full window. */
export const LAYOUT_DEFAULT: StageLayout = 'upward';

/** The scene's own default shape, and the one that reads as bars rather than as a mass. */
export const SHAPE_DEFAULT: StageShape = 'bars';

/**
 * The stage previews the room by default, which **departs from the scene's
 * `HueCycle`**.
 *
 * The scene had no room to preview: in SignalRGB the canvas is the lights. Here the
 * whole argument for putting the engine's colour on the wire was that the screen should
 * show what the bulbs were sent, so a page that opened on a hue cycle would open
 * showing something the room is demonstrably not doing.
 */
export const COLOR_DEFAULT: StageColorStyle = 'randomBeat';

/**
 * Half the ceiling, which is where a picture reads best at a window's width.
 *
 * The ceiling is genuinely more detail on a wide monitor rather than more noise, so
 * this is taste and not a limit - it is the default because a bar has to be wide enough
 * to watch from across a booth.
 */
export const BAND_DEFAULT = 64;

export function layoutLabel(layout: StageLayout): string {
  return layout === 'upward' ? 'upward' : 'mirrored';
}

export const SHAPES: readonly StageShape[] = [
  'bars',
  'thinBars',
  'chonkerBars',
  'pixel',
  'smooth',
  'smoothSimple',
];

export const COLOR_STYLES: readonly StageColorStyle[] = [
  'randomBeat',
  'hueCycle',
  'hueWave',
  'hueWaveVert',
  'static',
];

/**
 * How wide a bar is drawn, as a multiple of the column width.
 *
 * The scene's 2, 1 and 5. Above 1 the bars deliberately overlap their neighbours,
 * which is what makes `chonkerBars` read as a solid moving mass rather than as
 * sixty-four separate columns, and it is why this is a multiplier rather than a gap.
 */
const BAR_THICKNESS: Record<StageShape, number> = {
  bars: 2,
  thinBars: 1,
  chonkerBars: 5,
  pixel: 1.5,
  // The smooth styles trace tips instead of filling columns, so no thickness
  // applies. Zero rather than absent so the record stays total and a new style
  // cannot be added without deciding this.
  smooth: 0,
  smoothSimple: 0,
};

/**
 * How wide a bar is in the upward layout, as a multiple of its slot.
 *
 * Flux's own numbers rather than the scene's, because the layout is Flux's own and
 * nothing was carried across for it. The scene's 2 and 5 are multiples of a column in a
 * picture drawn at half width, where overlapping neighbours is what makes the mass read
 * as one shape. Here a bar has a whole slot to itself, so 1 is a bar that fills its
 * slot bar the gap - which is exactly what this page drew before the rebuild, and what
 * "responsive" turned out to mean - and the other two are a deliberately airy and a
 * deliberately fat version of it.
 */
const UPWARD_THICKNESS: Record<StageShape, number> = {
  thinBars: 0.5,
  bars: 1,
  chonkerBars: 2,
  pixel: 1,
  smooth: 0,
  smoothSimple: 0,
};

/**
 * A gap between bars, so a hundred and twenty-eight of them read as bars and not as a
 * block.
 *
 * The reason the upward layout gets one and the mirrored layout does not: the scene's
 * thickness of 2 makes every bar overlap its neighbour by half, which is how it builds
 * a continuous moving mass. That mass is legible as a shape and illegible as a set of
 * bands, and losing the gap was most of what read as the picture becoming less
 * responsive.
 */
export const BAR_GAP_PX = 1;

/** Any narrower and a bar would vanish rather than read as quiet. */
export const MIN_BAR_WIDTH_PX = 1;

/** How tall the cap at a bar's tip is drawn under `pixel`, in CSS pixels. */
export const PIXEL_CAP_HEIGHT_PX = 3;

/** `SmoothSimple` keeps every third tip. */
export const SMOOTH_SIMPLE_STRIDE = 3;

/** `Smooth` keeps all of them. */
export const SMOOTH_STRIDE = 1;

/**
 * The half-pixel the scene starts and closes the smooth path with.
 *
 * It exists to stop a hairline of background showing along the centre line where the
 * two vertical mirrors meet, and it is carried across because the seam it hides is
 * still there: the mirrors are exact, so a path that stops exactly on the axis
 * leaves the axis itself unpainted.
 */
export const SMOOTH_SEAM_PX = 0.5;

/**
 * How fast the hue travels under the three cycling styles, and the scene's default.
 *
 * Only the screen-only styles use it. The room's own colour changes are the engine's
 * business and arrive on the wire already timed to the bar.
 */
export const HUE_SPEED_DEFAULT = 20;

/** The scene's `((now * speed) / 200 % 1000) / 1000`, split so it can be named. */
const HUE_SPEED_DIVISOR = 200;
const HUE_CYCLE_STEPS = 1000;

/**
 * The bounds on how many bars the spectrum is drawn as.
 *
 * A mirror of `SpectrumBands` on the server, which is where they are argued, and only
 * the ends of the control: the count itself arrives on the wire, because it decides how
 * many columns the analyzer produces rather than how a browser reduces the ones it was
 * sent. That is what makes every value in this range exact and evenly spaced - see the
 * `stage` message in `docs/protocol.md`.
 *
 * **This replaces the scene's control rather than transcribing it.** Its
 * `usedFreqSector` decides how far *up* the spectrum is drawn at all, so lowering it
 * makes the treble disappear. That is a defensible knob for a decoration and the wrong
 * one here, because the same movement is what somebody reaches for when a picture looks
 * too finely divided, and cutting the top off an instrument is not what they meant.
 */
export const BAND_MIN = 16;

/** @see BAND_MIN */
export const BAND_MAX = 128;

/** A colour with each channel from 0 to 1, which is how the wire carries one. */
export type SceneColor = LightColorData;

/** What the stage falls back to with no engine colour to draw. */
export const NEUTRAL_COLOR: SceneColor = { r: 0.29, g: 0.87, b: 0.5 };

export interface StageSize {
  width: number;
  height: number;
}

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface Point {
  x: number;
  y: number;
}

/** Which half of the mirrored picture a bar is on. */
export type Side = 'left' | 'right';

export function shapeLabel(style: StageShape): string {
  switch (style) {
    case 'bars':
      return 'bars';
    case 'thinBars':
      return 'thin';
    case 'chonkerBars':
      return 'chonky';
    case 'pixel':
      return 'pixel';
    case 'smooth':
      return 'smooth';
    case 'smoothSimple':
      return 'simple';
  }
}

export function colorLabel(style: StageColorStyle): string {
  switch (style) {
    case 'randomBeat':
      return 'the lights';
    case 'hueCycle':
      return 'hue cycle';
    case 'hueWave':
      return 'hue wave';
    case 'hueWaveVert':
      return 'hue wave (vertical)';
    case 'static':
      return 'static';
  }
}

/** Whether a style traces tips rather than filling columns. */
export function isSmooth(style: StageShape): boolean {
  return style === 'smooth' || style === 'smoothSimple';
}

/** How many tips a smooth style keeps. */
export function smoothStride(style: StageShape): number {
  return style === 'smoothSimple' ? SMOOTH_SIMPLE_STRIDE : SMOOTH_STRIDE;
}

/** How wide to paint a bar, as a multiple of the column width, in the layout given. */
export function barThickness(style: StageShape, layout: StageLayout): number {
  return layout === 'upward' ? UPWARD_THICKNESS[style] : BAR_THICKNESS[style];
}

/** Where one bar of the upward layout goes horizontally, in CSS pixels. */
export interface BarGeometry {
  /** Left edge to left edge, so bar n starts at n * slot. */
  slot: number;
  /** How wide to paint one bar. */
  width: number;
}

/**
 * How the width divides into bars, with the gap taken out of each one.
 *
 * Here rather than in the component because it is the part of the drawing that can be
 * wrong arithmetically rather than merely ugly: subtracting a gap without a floor under
 * it leaves nothing to paint once the canvas is narrow enough, and a spectrum that has
 * silently stopped drawing looks exactly like one that is hearing silence.
 */
export function barGeometry(canvasWidth: number, count: number, thickness: number): BarGeometry {
  if (!Number.isFinite(canvasWidth) || !Number.isFinite(count) || canvasWidth <= 0 || count <= 0) {
    return { slot: 0, width: 0 };
  }

  const slot = canvasWidth / count;

  // Bars touch rather than disappear once the slot is down to a pixel. A solid block
  // says "too narrow to resolve one band", which is true; a blank rectangle says the
  // analyzer stopped, which is not.
  return { slot, width: Math.max(MIN_BAR_WIDTH_PX, slot * thickness - BAR_GAP_PX) };
}

/**
 * Where one bar goes in the upward layout: up from the floor, centred in its slot.
 *
 * Centred rather than left-aligned so that a thin bar sits evenly between its
 * neighbours and a deliberately fat one overhangs both ways instead of leaning right.
 * At the default thickness the difference is half a pixel and the picture is the one
 * this page drew before the rebuild.
 */
export function upwardBarRect(
  index: number,
  level: number,
  size: StageSize,
  columns: number,
  thickness: number,
): Rect {
  const { slot, width } = barGeometry(size.width, columns, thickness);
  const height = clampLevel(level) * size.height;

  return {
    x: index * slot + (slot - width) / 2,
    y: size.height - height,
    width,
    height,
  };
}

/**
 * The cap at a bar's tip in the upward layout, and only one of them.
 *
 * The mirrored layout has two tips per bar and the upward layout has one, so `pixel`
 * here is a row of marks tracing the spectrum's outline rather than a pair of them.
 * A silent column puts its cap on the floor rather than three pixels below the canvas,
 * which is what makes the style read as an outline with a baseline instead of a picture
 * that vanishes in the quiet parts.
 */
export function upwardPixelCapRect(
  index: number,
  level: number,
  size: StageSize,
  columns: number,
): Rect {
  const bar = upwardBarRect(index, level, size, columns, barThickness('pixel', 'upward'));

  return {
    x: bar.x,
    y: Math.min(bar.y, size.height - PIXEL_CAP_HEIGHT_PX),
    width: bar.width,
    height: PIXEL_CAP_HEIGHT_PX,
  };
}

/**
 * The outline of the upward layout, left to right along the tips and closed along the
 * floor.
 *
 * One path and one fill, where the mirrored version needs four of each: there is
 * nothing to mirror, which makes this the cheaper of the two on a phone as well as the
 * one that shows every band.
 *
 * The tips are taken at the middle of each slot rather than at its edge, because a
 * polyline through slot edges is half a bar out of step with the bars the other styles
 * draw - the same picture would appear to shift sideways when the style changed. Both
 * ends run to the canvas edge so the shape has no sliver of background beside it.
 */
export function upwardSmoothPoints(
  levels: readonly number[],
  size: StageSize,
  columns: number,
  stride: number,
): Point[] {
  if (columns <= 0 || size.width <= 0) {
    return [];
  }

  const slot = size.width / columns;
  const step = Math.max(1, Math.trunc(stride));
  const tipY = (index: number): number => size.height - clampLevel(levels[index] ?? 0) * size.height;

  const points: Point[] = [{ x: 0, y: tipY(0) }];

  for (let index = 0; index < columns; index++) {
    if (index % step === 0) {
      points.push({ x: index * slot + slot / 2, y: tipY(index) });
    }
  }

  points.push({ x: size.width, y: tipY(columns - 1) });
  points.push({ x: size.width, y: size.height });
  points.push({ x: 0, y: size.height });

  return points;
}

/**
 * Which colour section a column belongs to in the upward layout.
 *
 * The sections run across the whole width rather than being spread over half of it
 * twice, which is the mirrored layout's halving gone: there is one picture here, so
 * eight sections is eight bands of colour left to right.
 */
export function upwardSectionOf(index: number, columns: number, sections: number): number {
  if (columns <= 0 || sections <= 1) {
    return 0;
  }

  return Math.min(sections - 1, Math.floor((index / columns) * sections));
}

/**
 * How wide one column is: half the canvas divided by however many are drawn.
 *
 * Half, because the picture is mirrored about the centre and each column is drawn
 * twice.
 */
export function columnWidth(canvasWidth: number, columns: number): number {
  if (!Number.isFinite(canvasWidth) || canvasWidth <= 0 || columns <= 0) {
    return 0;
  }

  return (0.5 * canvasWidth) / columns;
}

/**
 * Where one bar goes, growing outward from the centre and centred on the axis.
 *
 * The two offsets are the scene's and they are not symmetric: the right side starts
 * at `index - 1` and the left at `index + 1`, so with the default thickness of 2 the
 * innermost pair straddles the centre line and the outermost bar's far edge lands
 * exactly on the edge of the canvas. Making them symmetric is the obvious tidy-up and
 * it costs the picture its clean outer edge.
 */
export function barRect(
  index: number,
  level: number,
  size: StageSize,
  columns: number,
  thickness: number,
  side: Side,
): Rect {
  const column = columnWidth(size.width, columns);
  const height = clampLevel(level) * size.height;
  const centreX = size.width / 2;
  const centreY = size.height / 2;

  return {
    x: side === 'right' ? centreX + column * (index - 1) : centreX - column * (index + 1),
    y: centreY - height / 2,
    width: column * thickness,
    height,
  };
}

/**
 * The two caps at a bar's tips under `pixel`, top first.
 *
 * The caps hang off the tips rather than being centred on them, exactly as the scene
 * draws them: the top cap's upper edge is the tip and the bottom cap's lower edge is
 * three pixels past it. At full height that puts three pixels outside the canvas,
 * which is the scene's behaviour and reads as a bar that has hit the ceiling.
 */
export function pixelCapRects(
  index: number,
  level: number,
  size: StageSize,
  columns: number,
  side: Side,
): [Rect, Rect] {
  const bar = barRect(index, level, size, columns, barThickness('pixel', 'mirrored'), side);
  const centreY = size.height / 2;
  const half = (clampLevel(level) * size.height) / 2;

  return [
    { x: bar.x, y: centreY - half, width: bar.width, height: PIXEL_CAP_HEIGHT_PX },
    { x: bar.x, y: centreY + half, width: bar.width, height: PIXEL_CAP_HEIGHT_PX },
  ];
}

/**
 * Which colour section a column belongs to.
 *
 * The scene's mapping, and the halving in it is the part worth reading twice: the
 * sections are spread over half the count on each side, so eight sections means four
 * across the right half and four more across the left. That is what makes the picture
 * symmetric in shape while being asymmetric in colour, and it is why a section index
 * is not simply a column divided by a width.
 *
 * With one colour to draw this returns 0 everywhere, which is correct rather than
 * degenerate: the sections become visible when the engine starts sending one colour
 * per section, and this mapping is what it will be read through.
 */
export function sectionOf(index: number, columns: number, sections: number, side: Side): number {
  if (columns <= 0 || sections <= 1) {
    return 0;
  }

  const half = sections / 2;
  const across = Math.floor((index / columns) * half);

  return side === 'right' ? across : Math.floor(half) + across;
}

/**
 * The colour for a section, wrapping rather than running out.
 *
 * Wrapping is the scene's `displayColors[sectionIndex % displayColors.length]` and it
 * is what makes a colour count and a section count independent: eight sections drawn
 * from one colour is a solid picture, not seven blank ones.
 */
export function sectionColor(colors: readonly SceneColor[], section: number): SceneColor {
  if (colors.length === 0) {
    return NEUTRAL_COLOR;
  }

  const index = ((Math.trunc(section) % colors.length) + colors.length) % colors.length;

  return colors[index] ?? NEUTRAL_COLOR;
}

/**
 * Where the hue has got to, from the clock alone.
 *
 * Time rather than frames, unlike the decay factors in `spectrum.ts`: the scene reads
 * `performance.now()` here, so this is the one animation in it that already ran at the
 * same speed on any monitor and needs no correction.
 */
export function hueShift(nowMs: number, speed: number): number {
  if (!Number.isFinite(nowMs) || !Number.isFinite(speed)) {
    return 0;
  }

  return (((nowMs * speed) / HUE_SPEED_DIVISOR) % HUE_CYCLE_STEPS) / HUE_CYCLE_STEPS;
}

/**
 * The outline of the right-hand half, from the centre outward along the bar tips.
 *
 * One quadrant only. The scene fills this path four times under mirroring transforms
 * rather than tracing four paths, which is both cheaper and the only way the four
 * quadrants cannot disagree about where a tip was.
 *
 * The path is closed along the centre line so it can be filled: a polyline through the
 * tips has no inside. Both ends carry `SMOOTH_SEAM_PX` for the reason given there.
 */
export function smoothPoints(
  levels: readonly number[],
  size: StageSize,
  columns: number,
  stride: number,
): Point[] {
  if (columns <= 0 || size.width <= 0) {
    return [];
  }

  const column = columnWidth(size.width, columns);
  const centreX = size.width / 2;
  const centreY = size.height / 2;
  const step = Math.max(1, Math.trunc(stride));

  const tipY = (index: number): number => centreY - (clampLevel(levels[index] ?? 0) * size.height) / 2;

  const points: Point[] = [{ x: centreX - SMOOTH_SEAM_PX, y: tipY(0) }];

  // Every point names the *next* column's tip, which is the scene's indexing and the
  // reason the loop stops one short: the last column closes the path along the axis
  // instead of contributing a tip of its own.
  for (let index = 0; index < columns - 1; index++) {
    if (index % step === 0) {
      points.push({ x: centreX + (index + 1) * column, y: tipY(index + 1) });
    }
  }

  points.push({ x: size.width, y: centreY + SMOOTH_SEAM_PX });
  points.push({ x: centreX - SMOOTH_SEAM_PX, y: centreY + SMOOTH_SEAM_PX });

  return points;
}

/**
 * How much of the distance to a dimmer colour is still left after one feed interval.
 *
 * Flux's own number rather than the scene's, because the scene had no wire: its canvas
 * was the lights. Five per cent is "visibly arrived before the next sample" without
 * pretending to arrive exactly, which nothing here could honestly do - frames and
 * samples are unsynchronised, so there is no moment this could aim at.
 */
export const COLOR_RELEASE_REMAINDER = 0.05;

/**
 * The engine's colour as the stage should draw it now, given the last one drawn.
 *
 * This exists because of the rate the colour arrives at. It rides the 20 Hz state
 * feed, deliberately - a bulb renders about 25 updates a second whatever the stream
 * sends, so the preview is within a hair of the fidelity the room has - but a 240 ms
 * pulse is then five samples, and five samples drawn as five held values is a
 * staircase where the room has a fade.
 *
 * The rule is `spectrum.ts`'s and for the same reason: instant on the way up, smoothed
 * on the way down. A beat's leading edge is the one thing this page must not draw
 * late, so a sample brighter than what is on screen is taken whole. Anything dimmer is
 * approached, and approached against the clock rather than per frame - the compounding
 * of a per-frame fraction depends on the frame rate, which is the exact trap
 * `spectrum.ts` documents at `REFERENCE_FPS`.
 *
 * Brightness is the largest channel, and that is not an approximation: the engine
 * builds a colour as a fully saturated hue scaled by its envelope, so the envelope is
 * recoverable as the maximum channel and nothing else has to be agreed with it.
 */
export function approachColor(
  shown: SceneColor,
  target: SceneColor,
  elapsedMs: number,
  intervalMs: number,
): SceneColor {
  if (brightnessOf(target) >= brightnessOf(shown)) {
    return target;
  }

  if (!Number.isFinite(elapsedMs) || !Number.isFinite(intervalMs) || intervalMs <= 0) {
    return target;
  }

  // Time that did not pass moves nothing, which matters because two redraws can share
  // a timestamp.
  if (elapsedMs <= 0) {
    return shown;
  }

  const remaining = Math.pow(COLOR_RELEASE_REMAINDER, elapsedMs / intervalMs);

  return {
    r: target.r + (shown.r - target.r) * remaining,
    g: target.g + (shown.g - target.g) * remaining,
    b: target.b + (shown.b - target.b) * remaining,
  };
}

/** The envelope the engine applied, read back off the colour it produced. */
export function brightnessOf(color: SceneColor): number {
  return Math.max(color.r, color.g, color.b);
}

/** A wire colour as something a canvas will accept. */
export function rgbCss(color: SceneColor): string {
  return `rgb(${channel(color.r)} ${channel(color.g)} ${channel(color.b)})`;
}

/**
 * A fully saturated hue at half lightness, which is the scene's `hslToRgb(hue, 1, 0.5)`.
 *
 * Handed to the canvas as a colour string rather than converted here. The conversion is
 * structure and the browser already has it; what had to be carried across is the
 * saturation and the lightness, because those are what make the scene's colours the
 * colours they are.
 */
export function hueCss(hue: number): string {
  const wrapped = Number.isFinite(hue) ? hue - Math.floor(hue) : 0;

  return `hsl(${(wrapped * 360).toFixed(1)}deg 100% 50%)`;
}

/**
 * How much faster the wave sweeps than the hue itself travels.
 *
 * The scene's `(currentHue * 360 * 4) % 360`: the wave styles move four times per turn
 * of the underlying hue. Kept because it is what makes the wave read as motion rather
 * than as a slow colour drift, and it is a number somebody arrived at by watching.
 */
export const WAVE_PHASE_TURNS = 4;

/**
 * The rainbow the wave styles sweep, as offsets and hues.
 *
 * Eleven stops covering two full turns of the wheel, so the gradient repeats once
 * across the picture - the scene's `((2 * i) / 10) * 360`. Rebuilt each frame with the
 * phase added rather than translated under a transform: the scene moves it with a
 * matrix because its engine offered no other way, and the transform it needs then has
 * to be inverted by hand for every rectangle drawn through it. Adding the phase to
 * eleven numbers is the same picture without the matrix.
 */
export function rainbowStops(phase: number): { offset: number; hue: number }[] {
  const stops: { offset: number; hue: number }[] = [];

  for (let index = 0; index <= 10; index++) {
    stops.push({ offset: index / 10, hue: (2 * index) / 10 + phase });
  }

  return stops;
}

/**
 * Where the two static colours sit across the picture.
 *
 * The offsets are the scene's five, which alternate twice; the colours are Flux's own
 * `--live` and `--accent` rather than two colour pickers. That is a deliberate
 * reduction: the scene defaults both of its pickers to the same cyan, so out of the box
 * it draws a flat colour, and two colour inputs in a dark booth to reach an effect the
 * hue styles already cover was not worth the drawer space.
 */
export const STATIC_STOPS: readonly { offset: number; first: boolean }[] = [
  { offset: 0, first: true },
  { offset: 0.25, first: false },
  { offset: 0.5, first: true },
  { offset: 0.75, first: false },
  { offset: 1, first: true },
];

function clampLevel(level: number): number {
  if (!Number.isFinite(level)) {
    return 0;
  }

  return Math.min(1, Math.max(0, level));
}

function channel(value: number): number {
  if (!Number.isFinite(value)) {
    return 0;
  }

  return Math.round(Math.min(1, Math.max(0, value)) * 255);
}
