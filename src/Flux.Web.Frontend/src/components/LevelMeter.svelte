<script lang="ts">
  import {
    advanceMeter,
    formatLevel,
    meterFraction,
    METER_FLOOR_DBFS,
    releaseTowards,
  } from '../lib/levels.ts';

  interface Props {
    /*
     * Absent for every device Flux is not capturing, which is nearly all of
     * them. Spelled with an explicit undefined because the protocol types are
     * optional under exactOptionalPropertyTypes, so a decoded frame hands over a
     * present key holding undefined rather than no key at all.
     */
    rms?: number | undefined;
    peak?: number | undefined;
  }

  const { rms, peak }: Props = $props();

  /*
   * Null means nobody measured this, and it is rendered as an absence rather than
   * as a bar of length zero. Drawing an unmeasured device at the bottom of the
   * scale would put a row of dead meters on screen and claim twenty silent
   * endpoints where there are nineteen unknown ones and one real reading.
   */
  const fill = $derived(rms === undefined ? null : meterFraction(rms));
  const mark = $derived(peak === undefined ? null : meterFraction(peak));

  /*
   * The bar is animated rather than stepped, and the reason is arithmetic: the
   * meter closes a 250 ms window, so four genuinely new numbers arrive a second,
   * while the device frame carries ten and the screen redraws sixty. Painting
   * each frame as it lands gives a staircase that repeats values and then jumps.
   * Ballistics is what every hardware meter does with the same problem.
   *
   * The number beside the bar is never animated. That one is a measurement and
   * is printed as it arrived.
   */
  let shownFill = $state(0);
  let shownMark = $state(0);

  $effect(() => {
    let previous = performance.now();
    let frame = requestAnimationFrame(step);

    function step(now: number): void {
      const elapsed = now - previous;
      previous = now;

      // Read here rather than in the effect body on purpose: a read inside an
      // animation callback is not tracked, so an arriving measurement updates
      // the target without tearing down and restarting the loop.
      // The average is eased both ways; the peak rises instantly, because
      // easing a peak understates a clip.
      shownFill = advanceMeter(shownFill, fill ?? 0, elapsed);
      shownMark = releaseTowards(shownMark, mark ?? 0, elapsed);

      frame = requestAnimationFrame(step);
    }

    return () => cancelAnimationFrame(frame);
  });
</script>

{#if fill === null}
  <p class="unmeasured">not measured</p>
{:else}
  <div class="meter" title="{METER_FLOOR_DBFS} dBFS to full scale">
    <div class="track">
      <div class="fill" style="width: {shownFill * 100}%"></div>
      {#if mark !== null}
        <!-- The peak rides separately because the pair is what identifies a
             fault: a quiet average under a pinned peak is clipping, and a low
             peak with a healthy average is not a thing a real signal does. -->
        <div class="peak" style="left: {shownMark * 100}%"></div>
      {/if}
    </div>
    <span class="value numeric">{formatLevel(rms)}</span>
  </div>
{/if}

<style>
  .meter {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  .track {
    position: relative;
    flex: 1;
    height: 0.4rem;
    min-width: 3rem;
    background: var(--surface);
    border: 1px solid var(--edge);
    border-radius: 999px;
    overflow: hidden;
  }

  .fill {
    height: 100%;
    background: var(--live);
  }

  .peak {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 2px;
    background: var(--ink);
  }

  .value {
    flex: none;
    min-width: 4.5rem;
    text-align: right;
    font-size: var(--text-sm);
    color: var(--ink-dim);
  }

  .unmeasured {
    margin: 0;
    font-size: var(--text-sm);
    color: var(--ink-dim);
  }
</style>
