<script lang="ts">
  import { formatDrift, formatLatency, UNKNOWN } from '../lib/format.ts';
  import type { DiagnosticsData } from '../lib/protocol.ts';

  interface Props {
    diagnostics: DiagnosticsData;
    beatIndex: number | null;
    latencyMs: number | null;
  }

  const { diagnostics, beatIndex, latencyMs }: Props = $props();
</script>

<!--
  The diagnostics that earned their place by being the ones actually consulted
  while the Link source was built. Re-anchor drift above all: it is the single
  number that says whether the grid is being confirmed or corrected, and it is
  meaningless the moment it is off screen.
-->
<dl class="grid numeric">
  <div>
    <dt>beat</dt>
    <dd>{beatIndex ?? UNKNOWN}</dd>
  </div>
  <div>
    <dt>packets</dt>
    <dd>{diagnostics.packets}</dd>
  </div>
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
    gap: 0.75rem 1rem;
  }

  dt {
    font-size: 0.7rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--ink-faint);
  }

  dd {
    margin: 0.15rem 0 0;
    font-size: 1rem;
    color: var(--ink-dim);
  }
</style>
