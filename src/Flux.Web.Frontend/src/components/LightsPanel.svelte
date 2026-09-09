<script lang="ts">
  import { formatRate } from '../lib/format.ts';
  import type { HueData, HueEngineData, LightData } from '../lib/protocol.ts';
  import LatencySlider from './LatencySlider.svelte';
  import LightEngineControls from './LightEngineControls.svelte';

  interface Props {
    /** The bridge picture. Only rendered when there is a room to stream to. */
    hue: HueData;
    /**
     * How the lights are set, as this page believes it: the server's answer with this
     * page's own unconfirmed changes over it. Separate from `hue.engine` so the
     * controls do not fight a value that is still in the post.
     */
    engine: HueEngineData;
    /** Non-null exactly while Flux is streaming. The only field that says so. */
    light: LightData | null;
    /** The tempo, so the latency offset can be said in beats. */
    bpm: number | null;
    setEngine: (change: Partial<HueEngineData>) => void;
    setOffset: (offsetMs: number) => void;
    startStream: () => void;
    stopStream: () => void;
  }

  const { hue, engine, light, bpm, setEngine, setOffset, startStream, stopStream }: Props = $props();

  const streaming = $derived(light !== null);
</script>

<!--
  What gets touched during a set, split out from what gets set up once.

  The two lived in one panel and that was the whole of why the page needed scrolling:
  pairing a bridge, choosing a room and forgetting one are a first-night errand, while
  the palette and the pulse are the controls somebody reaches for with music playing.
  Nothing here changes what the server does; it changes what is in front of you.
-->
<div class="lights">
  <!--
    Driven by state.light rather than by a flag of this page's own. The stream can also
    stop without anybody pressing anything - the bridge allows one session and another
    application can take it - so a button that tracked its own clicks would keep
    offering to stop something that had already stopped.
  -->
  <div class="stream">
    <button
      type="button"
      class="action"
      class:live={streaming}
      disabled={hue.selectedAreaId === null}
      onclick={streaming ? stopStream : startStream}
    >
      <!-- "sync" rather than "lights": the button does not switch anything on, it takes
           the room over from whatever had it and hands it back on stop. -->
      {streaming ? 'stop light sync' : 'start light sync'}
    </button>

    {#if light !== null}
      <!-- Measured, not the configured rate echoed back, which is the whole point of
           showing it: a stream quietly achieving half its rate is otherwise
           indistinguishable from lights that are less smooth than expected. -->
      <p class="rate">{formatRate(light.fps)} frames a second to the bridge</p>
    {/if}
  </div>

  <LightEngineControls {engine} {setEngine} />

  <LatencySlider offsetMs={hue.latencyOffsetMs} {bpm} {setOffset} />
</div>

<style>
  .lights {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }

  .stream {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
  }

  .action {
    min-height: 2.5rem;
    padding: 0.5rem 0.7rem;
    background: none;
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    cursor: pointer;
  }

  .action:disabled {
    cursor: default;
    opacity: 0.4;
  }

  .live {
    border-color: var(--accent);
    color: var(--accent);
  }

  .rate {
    margin: 0;
    font-size: 0.7rem;
    color: var(--ink-faint);
    font-variant-numeric: tabular-nums;
  }
</style>
