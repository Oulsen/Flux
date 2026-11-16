<script lang="ts">
  import { qrModules, qrPath } from '../lib/qr.ts';

  interface Props {
    /** Where a phone on this network opens the page, from the server's greeting. */
    url: string;
  }

  const { url }: Props = $props();

  const modules = $derived(qrModules(url));
  const path = $derived(qrPath(modules));

  /*
   * The quiet zone the QR standard asks for, four modules of light on every side.
   * Without it a camera can find the code against a dark page and still not lock on,
   * which on a phone looks exactly like a code that is wrong.
   */
  const QUIET = 4;
  const box = $derived(modules.length + QUIET * 2);

  /** The address as it is read aloud or typed: no scheme, no trailing slash. */
  const shown = $derived(url.replace(/^https?:\/\//, '').replace(/\/$/, ''));
</script>

<!--
  Dark on light whatever the page's own theme is, because that is the way round every
  phone camera reads first; an inverted code is the one some of them do not read at all.
  The address stays under it in text, for a phone without a camera app that scans.
-->
<div class="phone">
  <svg viewBox="{-QUIET} {-QUIET} {box} {box}" role="img" aria-label="QR code for {shown}" shape-rendering="crispEdges">
    <rect x={-QUIET} y={-QUIET} width={box} height={box} fill="#fff" />
    <path d={path} fill="#000" />
  </svg>
  <p class="address">{shown}</p>
  <p class="note">Scan with a phone on the same network.</p>
</div>

<style>
  .phone {
    display: grid;
    justify-items: center;
    gap: 0.4rem;
  }

  svg {
    width: min(12rem, 100%);
    height: auto;
    border-radius: 0.25rem;
  }

  .address,
  .note {
    margin: 0;
    text-align: center;
  }

  .address {
    font-size: var(--text-sm);
    font-variant-numeric: tabular-nums;
    color: var(--ink);
    user-select: all;
  }

  .note {
    font-size: var(--text-xs);
    color: var(--ink-dim);
  }
</style>
