<script lang="ts">
  import {
    areaSummary,
    bridgeStatusLabel,
    canDiscover,
    countdownLabel,
    showsPairing,
    showsRooms,
  } from '../lib/hue.ts';
  import type { HueData } from '../lib/protocol.ts';

  interface Props {
    /** The bridge picture, or null before the first frame has arrived. */
    hue: HueData | null;
    discover: () => void;
    pair: (bridgeId: string) => void;
    selectArea: (areaId: string) => void;
    forget: () => void;
    /** Whether a refresh has been asked for and not yet answered. */
    refreshing: boolean;
  }

  const { hue, discover, pair, selectArea, forget, refreshing }: Props = $props();

  const status = $derived(bridgeStatusLabel(hue));
  const countdown = $derived(countdownLabel(hue?.pairingSecondsRemaining ?? null));
  const pairing = $derived(showsPairing(hue?.state ?? null));
  const rooms = $derived(showsRooms(hue));

  /*
   * Alarming only for a genuine failure.
   *
   * `unpaired` with a message is the ordinary end of a pairing attempt nobody
   * answered - the button is on a physical object in another room and it was not
   * pressed in time. Painting that red teaches somebody to distrust red.
   */
  const failed = $derived(hue?.state === 'failed');
</script>

<!--
  Setup, and only setup: finding a bridge, pairing it, choosing the room, and letting go
  of it. Everything that gets touched with music playing moved to the lights panel.

  This is deliberately the half that collapses once it is done. A bridge gets paired
  once and a room chosen once, and leaving that on screen for the rest of the year was
  most of why the page needed scrolling.
-->
<div class="bridge">
  <p class="status" class:failed>{status}</p>

  {#if hue !== null && hue.state !== 'disabled'}
    {#if countdown !== null}
      <!-- Redrawn from a real attempt on the server rather than from a timer running
           beside one, which is what makes it worth reading while walking. -->
      <p class="countdown">{countdown}</p>
    {/if}

    {#if pairing}
      <div class="found">
        {#if hue.discovered.length === 0}
          <p class="empty">
            {hue.state === 'discovering' ? 'listening' : 'no bridges found yet'}
          </p>
        {:else}
          {#each hue.discovered as found (found.id)}
            <button
              type="button"
              class="row"
              disabled={hue.state === 'pairing'}
              onclick={() => pair(found.id)}
            >
              <span class="name">{found.address}</span>
              <span class="detail">{found.id}</span>
            </button>
          {/each}
        {/if}
      </div>

      <button type="button" class="action" disabled={refreshing || !canDiscover(hue.state)} onclick={discover}>
        {refreshing ? 'looking' : 'look for bridges'}
      </button>
    {/if}

    {#if rooms}
      <div class="areas">
        {#each hue.areas as area (area.id)}
          <button
            type="button"
            class="row"
            class:chosen={area.id === hue.selectedAreaId}
            aria-pressed={area.id === hue.selectedAreaId}
            onclick={() => selectArea(area.id)}
          >
            <span class="name">{area.name}</span>
            <span class="detail" class:busy={area.streaming}>{areaSummary(area)}</span>
          </button>
        {/each}
      </div>

      <!--
        The ready state had no way to ask the bridge anything, so a room another
        application had taken went on reading as free until Flux was restarted. The
        busy flag has a lifetime of seconds and this is the control that refreshes it.
      -->
      <button type="button" class="action" disabled={refreshing || !canDiscover(hue.state)} onclick={discover}>
        {refreshing ? 'checking the bridge' : 'refresh rooms'}
      </button>
    {/if}

    {#if hue.bridge !== null}
      <!-- Worded as Flux forgetting the bridge, because that is all it is: the bridge
           keeps its whitelist entry until somebody deletes it in the Hue app, and
           pairing again makes a second one. -->
      <button type="button" class="action quiet" onclick={forget}>forget this bridge</button>
    {/if}
  {/if}
</div>

<style>
  .bridge {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }

  .status {
    margin: 0;
    font-size: var(--text-sm);
    line-height: 1.5;
    color: var(--ink-dim);
  }

  .failed {
    color: var(--alarm);
  }

  .countdown {
    margin: 0;
    font-size: 1.5rem;
    font-variant-numeric: tabular-nums;
    color: var(--accent);
  }

  .found,
  .areas {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }

  .row {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: var(--space-1);
    /* Thumb-sized: this gets pressed on a phone, in the dark, while walking. */
    min-height: var(--control-lg);
    padding: 0.6rem 0.7rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-sm);
    text-align: left;
    cursor: pointer;
  }

  .row:disabled {
    cursor: default;
    opacity: 0.5;
  }

  .chosen {
    border-color: var(--accent);
    color: var(--ink);
  }

  .name {
    letter-spacing: 0.06em;
  }

  .detail {
    font-size: var(--text-sm);
    color: var(--ink-dim);
  }

  /* The one detail worth spotting before pressing start, since the alternative is a
     DTLS timeout that looks like four other problems. */
  .busy {
    color: var(--quiet);
  }

  .action {
    min-height: var(--control);
    padding: 0.5rem 0.7rem;
    background: none;
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    cursor: pointer;
  }

  .action:disabled {
    cursor: default;
    opacity: 0.4;
  }

  .quiet {
    color: var(--ink-dim);
  }

  .empty {
    margin: 0;
    font-size: var(--text-sm);
    letter-spacing: 0.08em;
    color: var(--ink-dim);
  }
</style>
