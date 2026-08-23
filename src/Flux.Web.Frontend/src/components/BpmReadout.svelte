<script lang="ts">
  import { formatBpm, UNKNOWN } from '../lib/format.ts';

  interface Props {
    bpm: number | null;
    /** Dimmed when the tempo is being extrapolated rather than received. */
    stale: boolean;
  }

  const { bpm, stale }: Props = $props();

  const text = $derived(bpm === null ? UNKNOWN : formatBpm(bpm));
</script>

<div class="readout" class:stale class:unknown={bpm === null}>
  <div class="value numeric">{text}</div>
  <div class="unit">BPM</div>
</div>

<style>
  .readout {
    text-align: center;
  }

  .value {
    font-size: clamp(4rem, 22vw, 9rem);
    font-weight: 200;
    line-height: 1;
    letter-spacing: -0.03em;
    transition: color 150ms ease;
  }

  .unit {
    margin-top: 0.35rem;
    font-size: 0.75rem;
    letter-spacing: 0.35em;
    text-indent: 0.35em;
    color: var(--ink-faint);
  }

  /* Extrapolated, not measured. The tempo is still the best answer available,
     so it stays on screen - dimmed rather than hidden. */
  .stale .value {
    color: var(--quiet);
  }

  .unknown .value {
    color: var(--ink-faint);
  }
</style>
