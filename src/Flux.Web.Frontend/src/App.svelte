<script lang="ts">
  import { onMount } from 'svelte';
  import BeatIndicator from './components/BeatIndicator.svelte';
  import BpmReadout from './components/BpmReadout.svelte';
  import ConnectionBadge from './components/ConnectionBadge.svelte';
  import Diagnostics from './components/Diagnostics.svelte';
  import { FluxStore } from './lib/flux.svelte.ts';
  import { PROTOCOL_VERSION } from './lib/protocol.ts';
  import { FluxSocket, socketUrl } from './lib/socket.ts';

  /** How often the round trip is measured. Often enough to notice, rarely enough to ignore. */
  const PING_INTERVAL_MS = 2000;

  const store = new FluxStore();

  const sourceName = $derived(store.source === 'link' ? 'Ableton Link' : store.source);

  onMount(() => {
    const socket = new FluxSocket(socketUrl(), {
      message: (message) => store.apply(message),
      connection: (state) => {
        store.connection = state;
        if (state !== 'open') {
          store.disconnected();
        }
      },
    });

    socket.start();

    const ping = setInterval(() => socket.send({ type: 'ping', data: { t: Date.now() } }), PING_INTERVAL_MS);

    return () => {
      clearInterval(ping);
      socket.stop();
    };
  });
</script>

<header>
  <h1>Flux</h1>
  <ConnectionBadge connection={store.connection} sourceState={store.sourceState} />
</header>

<main>
  {#if store.protocolMismatch}
    <p class="mismatch">
      This page speaks protocol {PROTOCOL_VERSION} and Flux speaks {store.protocol}. Reload to pick up the
      current page; until then the readings below may not mean what they say.
    </p>
  {/if}

  <section class="stage">
    <BpmReadout bpm={store.grid?.bpm ?? null} stale={store.sourceState === 'quiet'} />

    <BeatIndicator
      quantum={store.quantum}
      position={store.positionInBar}
      tick={store.beatTick}
      aligned={store.grid?.barAligned ?? false}
    />
  </section>

  <section class="panel">
    <h2>{sourceName}</h2>
    <Diagnostics
      diagnostics={store.diagnostics}
      beatIndex={store.grid?.beatIndex ?? null}
      latencyMs={store.latencyMs}
    />
  </section>
</main>

<footer>
  <span>Flux {store.app ?? '--'}</span>
  <span>protocol {store.protocol ?? '--'}</span>
</footer>

<style>
  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap);
    padding: var(--gap);
    border-bottom: 1px solid var(--edge);
  }

  h1 {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    letter-spacing: 0.4em;
    text-indent: 0.4em;
    text-transform: uppercase;
  }

  main {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: var(--gap);
    padding: var(--gap);
    max-width: 40rem;
    width: 100%;
    margin: 0 auto;
  }

  .stage {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2rem;
    flex: 1;
    min-height: 50vh;
    padding: 2rem 1rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
  }

  .panel {
    padding: var(--gap);
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
  }

  h2 {
    margin: 0 0 0.75rem;
    font-size: 0.7rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--ink-faint);
    font-weight: 600;
  }

  .mismatch {
    margin: 0;
    padding: 0.75rem var(--gap);
    border: 1px solid var(--alarm);
    border-radius: var(--radius);
    color: var(--alarm);
    font-size: 0.85rem;
  }

  footer {
    display: flex;
    justify-content: space-between;
    gap: var(--gap);
    padding: var(--gap);
    border-top: 1px solid var(--edge);
    font-size: 0.75rem;
    color: var(--ink-faint);
  }
</style>
