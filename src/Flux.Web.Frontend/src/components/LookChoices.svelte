<script lang="ts">
  /*
   * The palette and how often it changes: the two light settings reached for mid-set.
   *
   * Its own component because it is offered in two places - the drawer, among the rest
   * of the light settings, and the stage's look panel, where it is one tap from the
   * music. One copy of the markup is what keeps the two from drifting apart.
   */
  import { colorChangeLabel, COLOR_CHANGE_BARS, PALETTES } from '../lib/hue.ts';
  import type { HueEngineData, HuePalette } from '../lib/protocol.ts';

  interface Props {
    engine: HueEngineData;
    /** Names the one field that changed, as `LightEngineControls` does. */
    setEngine: (change: Partial<HueEngineData>) => void;
  }

  const { engine, setEngine }: Props = $props();

  function choosePalette(palette: HuePalette): void {
    setEngine({ palette });
  }

  function chooseInterval(bars: number): void {
    setEngine({ colorChangeBars: bars });
  }
</script>

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

<style>
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
</style>
