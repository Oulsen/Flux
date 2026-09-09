<script lang="ts">
  import {
    brightnessLabel,
    colorChangeLabel,
    COLOR_CHANGE_BARS,
    PALETTES,
    MIN_RENDERABLE_PULSE_MS,
    PERCENT_LIMIT,
    pulseDepthLabel,
    pulseLabel,
    PULSE_LIMIT_MS,
    renderablePulseMs,
  } from '../lib/hue.ts';
  import type { HueEngineData, HuePalette } from '../lib/protocol.ts';
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
   * control would carry the other four fields as this page last heard them, so a palette
   * chosen elsewhere half a second ago is overwritten by the next slider move. The store
   * holds what this page has asked for and has not seen come back, and that is where the
   * five fields are put together.
   */
  function choosePalette(palette: HuePalette): void {
    setEngine({ palette });
  }

  function chooseInterval(bars: number): void {
    setEngine({ colorChangeBars: bars });
  }

  function setPulse(pulseMs: number): void {
    setEngine({ pulseMs });
  }

  function setBrightness(brightness: number): void {
    setEngine({ brightness });
  }

  function setDepth(pulseDepth: number): void {
    setEngine({ pulseDepth });
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
  <div class="choice">
    <span class="name">palette</span>
    <div class="options">
      {#each PALETTES as palette (palette)}
        <button
          type="button"
          class="chip"
          class:chosen={palette === engine.palette}
          aria-pressed={palette === engine.palette}
          onclick={() => choosePalette(palette)}
        >
          {palette}
        </button>
      {/each}
    </div>
  </div>

  <div class="choice">
    <span class="name">colour change</span>
    <div class="options">
      {#each COLOR_CHANGE_BARS as bars (bars)}
        <button
          type="button"
          class="chip"
          class:chosen={bars === engine.colorChangeBars}
          aria-pressed={bars === engine.colorChangeBars}
          onclick={() => chooseInterval(bars)}
        >
          {colorChangeLabel(bars)}
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
</div>

<style>
  .engine {
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
</style>
