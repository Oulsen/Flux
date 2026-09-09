<script lang="ts">
  /*
   * How the stage draws, which is part of the show and is shared with every other page
   * on the socket.
   *
   * That is a reversal: these lived in this browser's own storage first, on the argument
   * that whether a phone draws a canvas is a fact about the phone. The argument survives
   * for exactly one control in here - the switch at the top, which is still local - and
   * fails for the rest, because the look of the stage is set by whoever is standing at
   * the page and should be the same on the laptop and in somebody's hand.
   *
   * The band count settles it on its own: it decides how many columns the analyzer
   * produces, so it could never have been a per-screen preference at all.
   */

  import {
    BAND_MAX,
    BAND_MIN,
    COLOR_STYLES,
    colorLabel,
    LAYOUTS,
    layoutLabel,
    SHAPES,
    shapeLabel,
  } from '../lib/scene.ts';
  import type { StageData } from '../lib/protocol.ts';
  import RangeSetting from './RangeSetting.svelte';

  interface Props {
    /** Whether this device draws the stage at all. The one local setting left in here. */
    on: boolean;
    /**
     * The look as this page believes it: what the server confirmed, with this page's own
     * changes over it until they come back. Already merged by the caller.
     */
    stage: StageData;
    /** Whether there is a real colour to draw, so the placeholder can be explained. */
    lit: boolean;
    setOn: (on: boolean) => void;
    /** Names the one field that changed. The caller turns it into the whole record. */
    setStage: (change: Partial<StageData>) => void;
  }

  const { on, stage, lit, setOn, setStage }: Props = $props();
</script>

<div class="stage-settings">
  <button type="button" class="disclosure" aria-pressed={!on} onclick={() => setOn(!on)}>
    <span class="heading">stage</span>
    <span class="toggle">{on ? 'numbers only' : 'show visualizer'}</span>
  </button>

  <!-- Nothing below configures a canvas that is not being drawn, and a phone that asked
       for numbers only asked to stop being offered a picture. -->
  {#if on}
    <!--
      The mirrored layout is the scene's and it is the option rather than the default,
      which reverses what this phase started out assuming. Mirroring draws every band
      twice, so it shows half as many of them across a window and the edge somebody
      watches travels half as far - and unlike SignalRGB, where the canvas is the device
      and the mirroring is how a symmetric strip gets sampled, here it buys nothing but
      the look.
    -->
    <div class="choice">
      <span class="name">layout</span>
      <div class="options">
        {#each LAYOUTS as layout (layout)}
          <button
            type="button"
            class="chip"
            class:chosen={layout === stage.layout}
            aria-pressed={layout === stage.layout}
            onclick={() => setStage({ layout })}
          >
            {layoutLabel(layout)}
          </button>
        {/each}
      </div>
    </div>

    <div class="choice">
      <span class="name">shape</span>
      <div class="options">
        {#each SHAPES as shape (shape)}
          <button
            type="button"
            class="chip"
            class:chosen={shape === stage.shape}
            aria-pressed={shape === stage.shape}
            onclick={() => setStage({ shape })}
          >
            {shapeLabel(shape)}
          </button>
        {/each}
      </div>
    </div>

    <div class="choice">
      <span class="name">colour</span>
      <div class="options">
        {#each COLOR_STYLES as color (color)}
          <button
            type="button"
            class="chip"
            class:chosen={color === stage.color}
            aria-pressed={color === stage.color}
            onclick={() => setStage({ color })}
          >
            {colorLabel(color)}
          </button>
        {/each}
      </div>
    </div>

    <!--
      The first option is the only one the room also does, and it is the default for
      that reason: it draws the colour the bridge was sent rather than one computed
      here. The other four are screen-only, which is what makes them safe to compute
      at all.
    -->
    <p class="hint">
      {#if stage.color !== 'randomBeat'}
        A colour this screen invents. The lights are unaffected and are still running
        the palette in the lights panel.
      {:else if lit}
        The colour the bridge was sent, read back. It arrives 20 times a second, so the
        stage trails the room by a frame or so by design.
      {:else}
        A placeholder, because nothing is streaming yet. Start the stream and this
        becomes the colour the bulbs are being sent.
      {/if}
    </p>

    <!--
      A slider rather than a set of chips, and it can be one because the count reaches
      the analyzer: the columns are computed at the source, so every value in the range
      is evenly spaced. Folding a fixed feed down in the browser would only be exact
      where the counts nest, which is what made this three buttons before the setting
      moved to the server.
    -->
    <RangeSetting
      id="stage-bands"
      name="bands"
      min={BAND_MIN}
      max={BAND_MAX}
      step={1}
      value={stage.bands}
      format={(bands) => `${bands} bars`}
      commit={(bands) => setStage({ bands })}
    />

    <p class="hint">
      How many bars the spectrum is divided into. The whole 30 Hz to 16 kHz range is
      drawn at every setting - this changes the resolution, not how far up it goes.
    </p>
  {/if}
</div>

<style>
  .stage-settings {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }

  .choice {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .name {
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink-faint);
  }

  .options {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  .chip {
    /* Thumb-sized: this gets pressed on a phone, in the dark, mid-set. */
    min-height: 2.25rem;
    padding: 0.4rem 0.6rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: 0.75rem;
    cursor: pointer;
  }

  .chosen {
    border-color: var(--accent);
    color: var(--ink);
  }

  .disclosure {
    width: 100%;
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: var(--gap);
    padding: 0;
    background: none;
    border: none;
    font: inherit;
    cursor: pointer;
  }

  .heading {
    margin: 0;
    font-size: 0.7rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--ink-faint);
    font-weight: 600;
  }

  .toggle {
    font-size: 0.7rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink-dim);
  }

  .hint {
    margin: 0;
    font-size: 0.7rem;
    line-height: 1.5;
    color: var(--ink-faint);
  }
</style>
