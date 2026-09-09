<script lang="ts">
  import { alignmentLabel } from '../lib/format.ts';
  import type { AlignmentMethod } from '../lib/protocol.ts';

  interface Props {
    aligned: boolean;
    /** What anchored the bar, or null while it is only a count. */
    method: AlignmentMethod | null;
  }

  const { aligned, method }: Props = $props();

  const label = $derived(alignmentLabel(aligned, method));
</script>

<!--
  Reads from `state.alignment` rather than from `grid.barAligned`, which carries
  the same fact without the method. The pair belongs together: an anchor is only
  worth trusting once you can see what claimed it.
-->
<p class="badge" class:anchored={aligned}>{label}</p>

<style>
  /* Over the stage rather than inside a panel, so a step brighter than the same text
     would be there: `--ink-faint` vanished against a lit spectrum. */
  .badge {
    margin: 0;
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    color: var(--ink-dim);
    text-align: center;
  }

  /* Anchored is the state worth spotting from across the booth, so it is the
     one that gets the brighter ink. */
  .anchored {
    color: var(--accent);
  }
</style>
