<script lang="ts">
  import {
    brightnessLabel,
    colorCountLabel,
    COLOR_COUNTS,
    COLOR_LAYOUTS,
    MIN_RENDERABLE_PULSE_MS,
    PERCENT_LIMIT,
    pulseDepthLabel,
    pulseLabel,
    PULSE_LIMIT_MS,
    renderablePulseMs,
    strobeCycleLabel,
    STROBE_CYCLE_MAX,
    STROBE_CYCLE_MIN,
  } from '../lib/hue.ts';
  import type { HueColorLayout, HueEngineData } from '../lib/protocol.ts';
  import LookChoices from './LookChoices.svelte';
  import RangeSetting from './RangeSetting.svelte';

  interface Props {
    /**
     * How the lights are set, as this page believes it: what the server confirmed, with
     * this page's own changes over it until they come back. Already merged by the
     * caller, so these controls render it straight.
     */
    engine: HueEngineData;
    /** Names the one field that changed. The caller turns it into the whole record. */
    setEngine: (change: Partial<HueEngineData>) => void;
  }

  const { engine, setEngine }: Props = $props();

  /*
   * Each control names only the field it owns, and the caller composes the message.
   *
   * What goes on the wire is still one complete statement of how the lights should look
   * rather than a patch - the server takes nothing less. But composing it here, from the
   * record this component was handed, is what made two phones undo each other: every
   * control would carry the other fields as this page last heard them, so a palette
   * chosen elsewhere half a second ago is overwritten by the next slider move. The store
   * holds what this page has asked for and has not seen come back, and that is where the
   * whole statement is put together.
   */
  function chooseFollowPhrases(followPhrases: boolean): void {
    setEngine({ followPhrases });
  }

  function chooseColorCount(colorCount: number): void {
    setEngine({ colorCount });
  }

  function chooseColorLayout(colorLayout: HueColorLayout): void {
    setEngine({ colorLayout });
  }

  /* A layout divides a room between hues, and one hue has nothing to divide. Shown
     disabled rather than hidden, so that the control does not appear from nowhere the
     moment somebody picks a second colour. */
  const oneColour = $derived(engine.colorCount <= 1);

  function setPulse(pulseMs: number): void {
    setEngine({ pulseMs });
  }

  function setBrightness(brightness: number): void {
    setEngine({ brightness });
  }

  function setDepth(pulseDepth: number): void {
    setEngine({ pulseDepth });
  }

  function chooseStrobe(strobe: boolean): void {
    setEngine({ strobe });
  }

  function setStrobeCycle(strobeCycle: number): void {
    setEngine({ strobeCycle });
  }

  /*
   * The dead band is snapped out of rather than warned about.
   *
   * A bulb samples the envelope about every 40 ms, so a pulse below two of those is not
   * a dimmer flash but an inconsistent one - some beats sampled near the peak, some near
   * the tail. Offering that and then explaining it was the wrong trade: nobody wants the
   * value, so the control should not stop there. Zero stays reachable because no pulse
   * is a real setting rather than a very short one.
   *
   * The note survives for a value that arrived from somewhere this slider is not, which
   * is a settings file written by an older build or another client on the same socket.
   * Rare, and better explained than left looking like a fault.
   */
  const tooShortToRender = $derived(engine.pulseMs > 0 && engine.pulseMs < MIN_RENDERABLE_PULSE_MS);
</script>

<!--
  Two layers at two rates, which is the design these controls exist to expose. A hue is
  chosen every few bars and slides to its new value; brightness is knocked to its
  ceiling on every beat and fades back by the depth. One setting doing both jobs gives
  either lights that sit still for eight seconds or lights that strobe through the
  palette four times a bar.
-->
<div class="engine">
  <LookChoices {engine} {setEngine} />

  <!-- Beside the bar count because it is a correction to it rather than a second rhythm:
       a phrase starting takes a colour and the count runs on from there. Only rekordbox
       offers phrases, so with any other source this changes nothing. -->
  <div class="choice">
    <span class="name">follow phrases</span>
    <div class="options">
      {#each [false, true] as on (on)}
        <button
          type="button"
          class="chip"
          class:chosen={on === engine.followPhrases}
          aria-pressed={on === engine.followPhrases}
          onclick={() => chooseFollowPhrases(on)}
        >
          {on ? 'on' : 'off'}
        </button>
      {/each}
    </div>
  </div>

  <!-- Split along the frequency axis, which is the same axis that already decides how
       hard each light is hit: the bass end of the room takes one hue and the top end
       another, so the two splits say one thing rather than two. -->
  <div class="choice">
    <span class="name">colours at once</span>
    <div class="options">
      {#each COLOR_COUNTS as count (count)}
        <button
          type="button"
          class="chip"
          class:chosen={count === engine.colorCount}
          aria-pressed={count === engine.colorCount}
          onclick={() => chooseColorCount(count)}
        >
          {colorCountLabel(count)}
        </button>
      {/each}
    </div>
  </div>

  <div class="choice" class:idle={oneColour}>
    <span class="name">colour layout</span>
    <div class="options">
      {#each COLOR_LAYOUTS as layout (layout)}
        <button
          type="button"
          class="chip"
          class:chosen={layout === engine.colorLayout}
          aria-pressed={layout === engine.colorLayout}
          disabled={oneColour}
          onclick={() => chooseColorLayout(layout)}
        >
          {layout}
        </button>
      {/each}
    </div>
  </div>

  <RangeSetting
    id="light-brightness"
    name="brightness"
    min={0}
    max={PERCENT_LIMIT}
    step={5}
    value={engine.brightness}
    format={brightnessLabel}
    commit={setBrightness}
  />

  <!-- Changes land on the downbeat rather than merely every fourth beat, so the
       interval above is counted in bars from the grid's own bar position. -->
  <RangeSetting
    id="light-pulse"
    name="beat pulse"
    min={0}
    max={PULSE_LIMIT_MS}
    step={10}
    value={engine.pulseMs}
    format={pulseLabel}
    note={tooShortToRender ? 'set elsewhere, and shorter than a bulb can render - the bridge forwards at about 25 Hz' : null}
    warn={tooShortToRender}
    snap={renderablePulseMs}
    commit={setPulse}
  />

  <RangeSetting
    id="light-depth"
    name="pulse depth"
    min={0}
    max={PERCENT_LIMIT}
    step={5}
    value={engine.pulseDepth}
    format={pulseDepthLabel}
    hint="How far the light drops between beats. Shallow keeps the room bright and lets a short pulse read as a flash rather than as darkness."
    commit={setDepth}
  />

  <!-- Two chips rather than a checkbox, matching every other choice here. The button on
       the stage runs the same strobe while it is held, whichever of these is chosen. -->
  <div class="choice">
    <span class="name">strobe</span>
    <div class="options">
      {#each [false, true] as on (on)}
        <button
          type="button"
          class="chip"
          class:chosen={on === engine.strobe}
          aria-pressed={on === engine.strobe}
          onclick={() => chooseStrobe(on)}
        >
          {on ? 'on' : 'off'}
        </button>
      {/each}
    </div>
  </div>

  <RangeSetting
    id="light-strobe-cycle"
    name="strobe speed"
    min={STROBE_CYCLE_MIN}
    max={STROBE_CYCLE_MAX}
    step={5}
    value={engine.strobeCycle}
    format={strobeCycleLabel}
    hint="For the switch above and the button on the stage alike. Faster than about twice a beat, Hue bulbs pulse rather than strobe: the bridge updates them about 25 times a second."
    commit={setStrobeCycle}
  />
</div>

<style>
  .engine {
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

  /* Dimmed rather than removed: a control that appears from nowhere when a neighbouring
     setting changes is harder to find again than one that was always there. */
  .idle {
    opacity: 0.45;
  }

  .chip:disabled {
    cursor: default;
  }
</style>
