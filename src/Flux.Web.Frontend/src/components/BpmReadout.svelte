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
  /* The caller decides, because the same readout is centred in the middle of a phone
     and dragged to a corner of a desk monitor - and a unit that stays centred under a
     left-aligned number reads as a caption that missed rather than as a label. */
  .readout {
    text-align: var(--bpm-align, center);
  }

  /* The caller sets the size, because the same number is read from two distances: a
     corner of the stage while somebody is working, and the middle of a phone propped
     on a shelf. The clamp is what it falls back to when nobody says. */
  .value {
    font-size: var(--bpm-size, clamp(4rem, 22vw, 9rem));
    font-weight: 200;
    line-height: 1;
    letter-spacing: -0.03em;
    transition: color 150ms ease;
  }

  /* Tracked wide enough to read as a label rather than as a word, but no longer
     faint: this sits over the stage now, and `--ink-faint` was chosen against a panel
     background that is no longer behind it. */
  .unit {
    margin-top: 0.35rem;
    font-size: var(--text-xs);
    letter-spacing: 0.3em;
    text-indent: 0.3em;
    color: var(--ink-dim);
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
