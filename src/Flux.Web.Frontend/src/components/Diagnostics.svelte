<script lang="ts">
  import { sourceRows } from '../lib/diagnostics.ts';
  import { formatDrift, formatLatency, UNKNOWN } from '../lib/format.ts';
  import type { DiagnosticsData } from '../lib/protocol.ts';

  interface Props {
    /** Which source these counters belong to, since most of them belong to one. */
    source: string;
    diagnostics: DiagnosticsData;
    beatIndex: number | null;
    latencyMs: number | null;
  }

  const { source, diagnostics, beatIndex, latencyMs }: Props = $props();

  const rows = $derived(sourceRows(source, diagnostics));
</script>

<!--
  The diagnostics that earned their place by being the ones actually consulted
  while the Link source was built. Re-anchor drift above all: it is the single
  number that says whether the grid is being confirmed or corrected, and it is
  meaningless the moment it is off screen.

  The row order is deliberate and outlives any one source: what the grid is
  doing, then what the active source is doing, then the grid's own health, then
  the connection carrying all of it.
-->
<dl class="grid numeric">
  <div>
    <dt>beat</dt>
    <dd>{beatIndex ?? UNKNOWN}</dd>
  </div>

  {#each rows as row (row.label)}
    <div>
      <dt>{row.label}</dt>
      <dd>{row.value}</dd>
    </div>
  {/each}

  <div>
    <dt>re-anchor</dt>
    <dd>{formatDrift(diagnostics.reanchorMaxBeats)}</dd>
  </div>
  <div>
    <dt>round trip</dt>
    <dd>{formatLatency(latencyMs)}</dd>
  </div>
</dl>

<style>
  .grid {
    margin: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(7rem, 1fr));
    gap: var(--space-3) var(--space-4);
  }

  dt {
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
  }

  dd {
    margin: 0.15rem 0 0;
    font-size: var(--text-md);
    color: var(--ink-dim);
  }
</style>
