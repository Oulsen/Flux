import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  approachColor,
  BAR_GAP_PX,
  barGeometry,
  barRect,
  barThickness,
  BAND_DEFAULT,
  brightnessOf,
  COLOR_RELEASE_REMAINDER,
  columnWidth,
  hueCss,
  hueShift,
  HUE_SPEED_DEFAULT,
  isSmooth,
  LAYOUTS,
  LAYOUT_DEFAULT,
  layoutLabel,
  MIN_BAR_WIDTH_PX,
  NEUTRAL_COLOR,
  PIXEL_CAP_HEIGHT_PX,
  pixelCapRects,
  rainbowStops,
  rgbCss,
  sectionColor,
  sectionOf,
  SMOOTH_SEAM_PX,
  SMOOTH_SIMPLE_STRIDE,
  smoothPoints,
  smoothStride,
  upwardBarRect,
  upwardPixelCapRect,
  upwardSectionOf,
  upwardSmoothPoints,
  type SceneColor,
  type StageSize,
} from './scene.ts';

/** A window shaped like a desk monitor, in round numbers. */
const SIZE: StageSize = { width: 1000, height: 400 };

function assertClose(actual: number, expected: number, what: string): void {
  assert.ok(Math.abs(actual - expected) < 1e-9, `${what}: expected ${expected}, got ${actual}`);
}

test('a column is half the canvas divided by however many are drawn', () => {
  // Half, because every column is drawn twice: once each side of the centre.
  assertClose(columnWidth(1000, 50), 10, 'fifty columns');
  assertClose(columnWidth(1000, 64), 1000 / 128, 'all sixty-four');
});

test('a canvas with no width or no columns yields no column, rather than infinity', () => {
  assert.equal(columnWidth(0, 64), 0);
  assert.equal(columnWidth(1000, 0), 0);
  assert.equal(columnWidth(Number.NaN, 64), 0);
});

test('bars grow outward from the centre and are centred on the axis', () => {
  const right = barRect(0, 0.5, SIZE, 50, barThickness('bars', 'mirrored'), 'right');
  const left = barRect(0, 0.5, SIZE, 50, barThickness('bars', 'mirrored'), 'left');

  // Half height, so the bar covers a quarter of the canvas above the axis and a
  // quarter below it.
  assertClose(right.height, 200, 'height');
  assertClose(right.y, 100, 'top edge');
  assertClose(right.y + right.height, 300, 'bottom edge');

  // The innermost pair straddles the centre line, which is what the scene's
  // asymmetric offsets produce and what makes the picture read as one shape.
  assertClose(right.x, 490, 'first bar on the right');
  assertClose(left.x, 490, 'first bar on the left');
});

test('the outermost bar lands exactly on the edge of the canvas', () => {
  // The reason the right side is offset by index - 1 rather than by index. At the
  // default thickness the far edge of the last bar is the far edge of the picture,
  // and a symmetric offset would leave a column of background there.
  const last = barRect(49, 1, SIZE, 50, barThickness('bars', 'mirrored'), 'right');

  assertClose(last.x + last.width, SIZE.width, 'right edge');

  const mirrored = barRect(49, 1, SIZE, 50, barThickness('bars', 'mirrored'), 'left');

  assertClose(mirrored.x, 0, 'and the left side starts on the left edge');
});

test('thickness is carried from the scene, and above one the bars overlap on purpose', () => {
  assert.equal(barThickness('thinBars', 'mirrored'), 1);
  assert.equal(barThickness('bars', 'mirrored'), 2);
  assert.equal(barThickness('chonkerBars', 'mirrored'), 5);

  const column = columnWidth(SIZE.width, 50);
  const chonky = barRect(10, 1, SIZE, 50, barThickness('chonkerBars', 'mirrored'), 'right');

  assertClose(chonky.width, column * 5, 'five columns wide');
});

test('a level outside 0 to 1 is clamped rather than drawn off the canvas', () => {
  assert.equal(barRect(0, 4, SIZE, 50, 2, 'right').height, SIZE.height);
  assert.equal(barRect(0, -1, SIZE, 50, 2, 'right').height, 0);
  assert.equal(barRect(0, Number.NaN, SIZE, 50, 2, 'right').height, 0);
});

test('the pixel caps hang off the tips rather than being centred on them', () => {
  const [top, bottom] = pixelCapRects(0, 0.5, SIZE, 50, 'right');

  assert.equal(top.height, PIXEL_CAP_HEIGHT_PX);
  assert.equal(bottom.height, PIXEL_CAP_HEIGHT_PX);

  // The tips of a half-height bar, and the caps start there and go outward.
  assertClose(top.y, 100, 'top cap at the upper tip');
  assertClose(bottom.y, 300, 'bottom cap at the lower tip');
});

test('a silent column draws both caps on the axis', () => {
  const [top, bottom] = pixelCapRects(20, 0, SIZE, 50, 'right');

  assertClose(top.y, SIZE.height / 2, 'top');
  assertClose(bottom.y, SIZE.height / 2, 'bottom');
});

test('the upward layout divides the width into bars and takes the gap out of each', () => {
  // Moved here from `spectrum.test.ts` with the arithmetic, because placement is this
  // module's job now and the envelope next door is only about height.
  const geometry = barGeometry(1280, 128, barThickness('bars', 'upward'));

  assert.equal(geometry.slot, 10);
  assert.equal(geometry.width, 10 - BAR_GAP_PX);
});

test('a crowded canvas draws touching bars rather than none at all', () => {
  // A hundred and twenty-eight bars across a hundred and twenty-eight pixels leaves a
  // slot of exactly one, and subtracting the gap from that would leave nothing to paint.
  const geometry = barGeometry(128, 128, 1);

  assert.equal(geometry.slot, 1);
  assert.equal(geometry.width, MIN_BAR_WIDTH_PX);
});

test('a canvas with no width and a frame with no bins both draw nothing', () => {
  // The first redraw can land before the resize observer has reported a size at all.
  assert.deepEqual(barGeometry(0, 128, 1), { slot: 0, width: 0 });
  assert.deepEqual(barGeometry(1280, 0, 1), { slot: 0, width: 0 });
  assert.deepEqual(barGeometry(-10, 128, 1), { slot: 0, width: 0 });
  assert.deepEqual(barGeometry(Number.NaN, 128, 1), { slot: 0, width: 0 });
});

test('an upward bar grows from the floor and gets the whole height to travel', () => {
  const bar = upwardBarRect(0, 0.5, SIZE, 50, barThickness('bars', 'upward'));

  // The mirrored layout gives a half-level bar a tip a quarter of the way from the
  // middle; here it is halfway up the window, which is the travel that went missing.
  assert.equal(bar.height, 200);
  assert.equal(bar.y, 200);
  assert.equal(bar.y + bar.height, SIZE.height);

  const full = upwardBarRect(0, 1, SIZE, 50, barThickness('bars', 'upward'));

  assert.equal(full.y, 0, 'a full level reaches the top of the window');
});

test('an upward bar is centred in its slot, so thin and fat both stay put', () => {
  const slot = SIZE.width / 50;

  const thin = upwardBarRect(10, 1, SIZE, 50, barThickness('thinBars', 'upward'));
  const bars = upwardBarRect(10, 1, SIZE, 50, barThickness('bars', 'upward'));
  const chonky = upwardBarRect(10, 1, SIZE, 50, barThickness('chonkerBars', 'upward'));

  const centre = (rect: { x: number; width: number }): number => rect.x + rect.width / 2;

  assertClose(centre(thin), 10 * slot + slot / 2, 'thin');
  assertClose(centre(bars), 10 * slot + slot / 2, 'default');
  assertClose(centre(chonky), 10 * slot + slot / 2, 'chonky');

  // And only the fat one is allowed to overlap its neighbours.
  assert.ok(thin.width < slot - BAR_GAP_PX);
  assert.equal(bars.width, slot - BAR_GAP_PX);
  assert.ok(chonky.width > slot);
});

test('the upward thicknesses belong to Flux and the mirrored ones to the scene', () => {
  // A bar has a whole slot to itself here, so 1 fills it bar the gap. The scene's 2 and
  // 5 are multiples of a half-width column and overlap on purpose.
  assert.equal(barThickness('bars', 'upward'), 1);
  assert.equal(barThickness('bars', 'mirrored'), 2);
  assert.equal(barThickness('chonkerBars', 'upward'), 2);
  assert.equal(barThickness('chonkerBars', 'mirrored'), 5);
});

test('an upward pixel cap sits on the tip and never below the floor', () => {
  const loud = upwardPixelCapRect(0, 0.5, SIZE, 50);

  assert.equal(loud.height, PIXEL_CAP_HEIGHT_PX);
  assert.equal(loud.y, 200, 'at the tip');

  // A silent column keeps its cap on the floor rather than three pixels under the
  // canvas, so the style reads as an outline with a baseline.
  const silent = upwardPixelCapRect(0, 0, SIZE, 50);

  assert.equal(silent.y, SIZE.height - PIXEL_CAP_HEIGHT_PX);
});

test('the upward outline runs edge to edge and closes along the floor', () => {
  const levels = new Array<number>(8).fill(0.5);
  const points = upwardSmoothPoints(levels, SIZE, 8, 1);
  const slot = SIZE.width / 8;

  // A tip per column, plus a left edge, a right edge and two corners on the floor.
  assert.equal(points.length, 1 + 8 + 3);
  assert.equal(points[0]?.x, 0);

  // Taken at the middle of each slot, so the outline sits over the bars the other
  // styles draw rather than half a bar out of step with them.
  assertClose(points[1]?.x ?? -1, slot / 2, 'first tip');
  assertClose(points[8]?.x ?? -1, 7 * slot + slot / 2, 'last tip');

  assert.equal(points[9]?.x, SIZE.width, 'runs out to the edge');
  assert.deepEqual(points[10], { x: SIZE.width, y: SIZE.height });
  assert.deepEqual(points[11], { x: 0, y: SIZE.height });
});

test('the upward outline follows the levels and thins out by the stride', () => {
  const levels = [1, 0, 0, 0];
  const points = upwardSmoothPoints(levels, SIZE, 4, 1);

  assert.equal(points[0]?.y, 0, 'the loud first column reaches the top');
  assert.equal(points[2]?.y, SIZE.height, 'and the quiet one is on the floor');

  const thinned = upwardSmoothPoints(new Array<number>(128).fill(0.5), SIZE, 128, 3);

  // 0, 3, 6 ... 126 is 43 tips.
  assert.equal(thinned.length, 1 + 43 + 3);
});

test('an upward stage with nothing to draw yields no path', () => {
  assert.deepEqual(upwardSmoothPoints([], SIZE, 0, 1), []);
  assert.deepEqual(upwardSmoothPoints([0.5], { width: 0, height: 0 }, 8, 1), []);
});

test('upward sections run across the whole width rather than over half of it twice', () => {
  // The mirrored halving is gone: there is one picture, so eight sections is eight
  // bands of colour left to right.
  assert.equal(upwardSectionOf(0, 128, 8), 0);
  assert.equal(upwardSectionOf(16, 128, 8), 1);
  assert.equal(upwardSectionOf(64, 128, 8), 4);
  assert.equal(upwardSectionOf(127, 128, 8), 7);

  assert.equal(upwardSectionOf(0, 128, 1), 0);
  assert.equal(upwardSectionOf(127, 128, 1), 0);
});

test('an upward section cannot run off the end of the colours', () => {
  // The last column of a full frame divides to exactly the section count, and an index
  // one past the end would wrap to the first colour and put a seam at the far edge.
  assert.equal(upwardSectionOf(128, 128, 8), 7);
  assert.equal(upwardSectionOf(200, 128, 4), 3);
});

test('the fallback look matches what the server starts from', () => {
  // These exist only for the frame or two before the first `stage` frame arrives, so
  // the one thing worth pinning is that they agree with StageSettings.Default - a
  // disagreement shows up as a flicker on every load rather than as a failure.
  assert.equal(LAYOUT_DEFAULT, 'upward');
  assert.equal(BAND_DEFAULT, 64);
  assert.deepEqual([...LAYOUTS], ['upward', 'mirrored']);
  assert.equal(layoutLabel('mirrored'), 'mirrored');
});

test('sections are spread over half the count on each side', () => {
  // Eight sections is four across the right half and four more across the left,
  // which is what makes the picture symmetric in shape and not in colour.
  assert.equal(sectionOf(0, 64, 8, 'right'), 0);
  assert.equal(sectionOf(16, 64, 8, 'right'), 1);
  assert.equal(sectionOf(32, 64, 8, 'right'), 2);
  assert.equal(sectionOf(63, 64, 8, 'right'), 3);

  assert.equal(sectionOf(0, 64, 8, 'left'), 4);
  assert.equal(sectionOf(63, 64, 8, 'left'), 7);
});

test('one section is one colour everywhere, on both sides', () => {
  assert.equal(sectionOf(0, 64, 1, 'right'), 0);
  assert.equal(sectionOf(63, 64, 1, 'right'), 0);
  assert.equal(sectionOf(63, 64, 1, 'left'), 0);
});

test('two sections is one each side', () => {
  assert.equal(sectionOf(0, 64, 2, 'right'), 0);
  assert.equal(sectionOf(63, 64, 2, 'right'), 0);
  assert.equal(sectionOf(0, 64, 2, 'left'), 1);
  assert.equal(sectionOf(63, 64, 2, 'left'), 1);
});

test('a section reaches for a colour that is not there by wrapping', () => {
  const one: SceneColor = { r: 1, g: 0, b: 0 };
  const two: SceneColor = { r: 0, g: 0, b: 1 };

  // Eight sections drawn from one colour is a solid picture, which is the state
  // the stage is in until the engine sends one colour per section.
  assert.deepEqual(sectionColor([one], 7), one);
  assert.deepEqual(sectionColor([one, two], 3), two);
  assert.deepEqual(sectionColor([one, two], -1), two);
  assert.deepEqual(sectionColor([], 0), NEUTRAL_COLOR);
});



test('the hue cycles on the clock rather than on the frame rate', () => {
  assert.equal(hueShift(0, HUE_SPEED_DEFAULT), 0);

  // The scene's arithmetic puts a full turn at 1000 steps of speed / 200 per
  // millisecond, which at the default is ten seconds.
  assertClose(hueShift(10_000, HUE_SPEED_DEFAULT), 0, 'one full turn');
  assertClose(hueShift(5_000, HUE_SPEED_DEFAULT), 0.5, 'half a turn');
  assert.equal(hueShift(5_000, 0), 0);
  assert.equal(hueShift(Number.NaN, HUE_SPEED_DEFAULT), 0);
});

test('the smooth path traces tips outward and closes along the axis', () => {
  const levels = new Array<number>(8).fill(0.5);
  const points = smoothPoints(levels, SIZE, 8, 1);
  const column = columnWidth(SIZE.width, 8);

  // Start on the centre line, one tip per column but the last, then two points
  // closing the shape along the axis so it can be filled at all.
  assert.equal(points.length, 1 + 7 + 2);
  assertClose(points[0]?.x ?? 0, SIZE.width / 2 - SMOOTH_SEAM_PX, 'starts at the centre');
  assertClose(points[1]?.x ?? 0, SIZE.width / 2 + column, 'first tip');
  assertClose(points[7]?.x ?? 0, SIZE.width / 2 + 7 * column, 'last tip');
  assertClose(points[8]?.x ?? 0, SIZE.width, 'closes at the far edge');
  assertClose(points[9]?.x ?? 0, SIZE.width / 2 - SMOOTH_SEAM_PX, 'and back to the centre');
});

test('the smooth path follows the levels it was given', () => {
  const levels = [0, 1, 0, 0];
  const points = smoothPoints(levels, SIZE, 4, 1);

  // Each point names the next column's tip, so the loud second column is the
  // first tip drawn.
  assertClose(points[1]?.y ?? 0, SIZE.height / 2 - SIZE.height / 2, 'the loud column reaches the top');
  assertClose(points[2]?.y ?? 0, SIZE.height / 2, 'the quiet one sits on the axis');
});

test('the simple smooth style keeps every third tip', () => {
  const levels = new Array<number>(64).fill(0.5);

  const all = smoothPoints(levels, SIZE, 64, smoothStride('smooth'));
  const simple = smoothPoints(levels, SIZE, 64, smoothStride('smoothSimple'));

  assert.equal(smoothStride('smoothSimple'), SMOOTH_SIMPLE_STRIDE);
  assert.equal(all.length, 1 + 63 + 2);
  assert.equal(simple.length, 1 + 21 + 2);
  assert.ok(isSmooth('smoothSimple') && isSmooth('smooth') && !isSmooth('bars'));
});

test('a stage with nothing to draw yields no path rather than a degenerate one', () => {
  assert.deepEqual(smoothPoints([], SIZE, 0, 1), []);
  assert.deepEqual(smoothPoints([0.5], { width: 0, height: 0 }, 8, 1), []);
});







test('a brighter colour is taken whole, so a beat is never drawn late', () => {
  const dim: SceneColor = { r: 0.2, g: 0, b: 0 };
  const bright: SceneColor = { r: 1, g: 0, b: 0 };

  // The leading edge of a pulse is the one thing this page must not smooth: it is
  // what somebody is comparing against the room.
  assert.deepEqual(approachColor(dim, bright, 1, 50), bright);
});

test('a dimmer colour is nearly arrived at by the time the next sample lands', () => {
  const bright: SceneColor = { r: 1, g: 0, b: 0 };
  const dim: SceneColor = { r: 0, g: 0, b: 0 };

  // Five samples of a fading pulse are drawn as a fade rather than as five steps.
  const whole = approachColor(bright, dim, 50, 50);
  assertClose(whole.r, COLOR_RELEASE_REMAINDER, 'one interval');

  const late = approachColor(bright, dim, 500, 50);
  assert.ok(late.r < 1e-9 && late.r >= 0, 'a frame that ran long does not overshoot');
});

test('the approach follows the clock, so two short frames match one long one', () => {
  const bright: SceneColor = { r: 1, g: 0, b: 0 };
  const dim: SceneColor = { r: 0, g: 0, b: 0 };

  // The trap `spectrum.ts` documents at REFERENCE_FPS, in the one place in this
  // module that moves a value over time: a fraction applied per frame compounds by
  // however often the monitor happens to redraw.
  const once = approachColor(bright, dim, 50, 50);
  const twice = approachColor(approachColor(bright, dim, 25, 50), dim, 25, 50);

  assertClose(twice.r, once.r, 'split across two frames');
});

test('time that did not pass moves nothing, and an unusable interval takes the sample', () => {
  const bright: SceneColor = { r: 1, g: 1, b: 1 };
  const dim: SceneColor = { r: 0.1, g: 0.1, b: 0.1 };

  // Two redraws can share a timestamp.
  assert.deepEqual(approachColor(bright, dim, 0, 50), bright);
  assert.deepEqual(approachColor(bright, dim, 16, 0), dim);
  assert.deepEqual(approachColor(bright, dim, Number.NaN, 50), dim);
});

test('brightness is the largest channel, which is the envelope the engine applied', () => {
  // The engine builds a colour as a fully saturated hue scaled by its envelope, so
  // the envelope survives as the maximum channel.
  assert.equal(brightnessOf({ r: 0.4, g: 0.9, b: 0 }), 0.9);
  assert.equal(brightnessOf({ r: 0, g: 0, b: 0 }), 0);
});

test('a wire colour becomes a canvas colour on the byte scale', () => {
  assert.equal(rgbCss({ r: 1, g: 0, b: 0.5 }), 'rgb(255 0 128)');
  assert.equal(rgbCss({ r: -1, g: 2, b: Number.NaN }), 'rgb(0 255 0)');
});

test('a hue is drawn fully saturated at half lightness, as the scene did', () => {
  assert.equal(hueCss(0), 'hsl(0.0deg 100% 50%)');
  assert.equal(hueCss(0.5), 'hsl(180.0deg 100% 50%)');

  // Wrapped rather than clamped: the cycling styles hand this a number that keeps
  // climbing, and a clamp would park the colour on red.
  assert.equal(hueCss(1.25), hueCss(0.25));
  assert.equal(hueCss(-0.75), hueCss(0.25));
  assert.equal(hueCss(Number.NaN), hueCss(0));
});

test('the rainbow covers two turns of the wheel across the picture', () => {
  const stops = rainbowStops(0);

  assert.equal(stops.length, 11);
  assert.equal(stops[0]?.offset, 0);
  assert.equal(stops[10]?.offset, 1);
  assert.equal(stops[0]?.hue, 0);
  assertClose(stops[5]?.hue ?? -1, 1, 'a full turn half way across');
  assertClose(stops[10]?.hue ?? -1, 2, 'two turns at the far edge');

  // The phase moves every stop together, which is the whole animation.
  const moved = rainbowStops(0.25);
  assertClose(moved[0]?.hue ?? -1, 0.25, 'phase applied');
});
