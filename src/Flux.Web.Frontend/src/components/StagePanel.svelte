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
    TRAIL_MAX_MS,
    trailMsLabel,
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
    /** Whether the colour is the engine's preview, because nothing is streaming. */
    previewing: boolean;
    setOn: (on: boolean) => void;
    /** Names the one field that changed. The caller turns it into the whole record. */
    setStage: (change: Partial<StageData>) => void;
  }

  const { on, stage, lit, previewing, setOn, setStage }: Props = $props();
</script>

<div class="stage-settings">
  <!-- A switch that shows its state, rather than a link naming the opposite action:
       "numbers only" under a drawn picture read as what the screen was doing. Local to
       this device, unlike everything under it. -->
  <button type="button" class="switch" role="switch" aria-checked={on} onclick={() => setOn(!on)}>
    <span class="heading">visualizer</span>
    <span class="state" class:on>{on ? 'on' : 'off'}</span>
  </button>

  {#if !on}
    <p class="hint">Numbers only on this device. Other screens keep their picture.</p>
  {/if}

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
      {:else if previewing}
        A preview: the colour the engine would send, with nothing streaming. Start the
        stream and the bulbs pick it up from here.
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
      hint="How many bars the spectrum is divided into."
      detail="The whole 30 Hz to 16 kHz range is drawn at every setting - this changes the resolution, not how far up it goes."
      commit={(bands) => setStage({ bands })}
    />

    <RangeSetting
      id="stage-trail"
      name="trail"
      min={0}
      max={TRAIL_MAX_MS}
      step={50}
      value={stage.trailMs}
      format={trailMsLabel}
      hint="How long the picture leaves a fading afterimage behind it."
      detail="Off draws every frame clean. On the screen only - the lights trail a beat by their pulse length."
      commit={(trailMs) => setStage({ trailMs })}
    />
  {/if}
</div>

<style>
  .stage-settings {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }

  .choice {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
  }

  .name {
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
  }

  .options {
    display: flex;
    flex-wrap: wrap;
    gap: var(--space-1);
  }

  .chip {
    /* Thumb-sized on touch: this gets pressed on a phone, in the dark, mid-set. A mouse
       gets less, which `--chip` explains. */
    min-height: var(--chip);
    padding: 0.4rem 0.6rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-sm);
    cursor: pointer;
  }

  .chosen {
    border-color: var(--accent);
    color: var(--ink);
  }

  .switch {
    width: 100%;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap);
    padding: 0;
    background: none;
    border: none;
    font: inherit;
    cursor: pointer;
    min-height: var(--control);
  }

  .heading {
    margin: 0;
    font-size: var(--text-sm);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink);
    font-weight: 600;
  }

  /* A pill that says the state, lit while the picture is drawn. */
  .state {
    min-width: 3.5rem;
    padding: var(--space-1) var(--space-3);
    border: 1px solid var(--edge);
    border-radius: 999px;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    text-align: center;
    color: var(--ink-dim);
  }

  .state.on {
    border-color: var(--accent);
    color: var(--accent);
  }

  .hint {
    margin: 0;
    font-size: var(--text-sm);
    line-height: 1.5;
    color: var(--ink-dim);
  }
</style>
