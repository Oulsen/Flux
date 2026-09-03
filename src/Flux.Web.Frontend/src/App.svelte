<script lang="ts">
  import { onMount } from 'svelte';
  import BeatIndicator from './components/BeatIndicator.svelte';
  import BpmReadout from './components/BpmReadout.svelte';
  import ConnectionBadge from './components/ConnectionBadge.svelte';
  import DevicePicker from './components/DevicePicker.svelte';
  import Diagnostics from './components/Diagnostics.svelte';
  import SourceSelector from './components/SourceSelector.svelte';
  import TapTempo from './components/TapTempo.svelte';
  import { FluxStore } from './lib/flux.svelte.ts';
  import { PROTOCOL_VERSION } from './lib/protocol.ts';
  import { FluxSocket, socketUrl } from './lib/socket.ts';

  /** How often the round trip is measured. Often enough to notice, rarely enough to ignore. */
  const PING_INTERVAL_MS = 2000;

  const store = new FluxStore();

  const sourceName = $derived(store.source === 'link' ? 'Ableton Link' : store.source);

  // Held outside onMount because the controls send on it. Not reactive state:
  // nothing renders from the socket itself, only from what it delivers.
  let socket: FluxSocket | null = null;

  /** Closed by default, so nothing subscribes to the device feed unasked. */
  let devicesOpen = $state(false);

  onMount(() => {
    socket = new FluxSocket(socketUrl(), {
      message: (message) => store.apply(message),
      connection: (state) => {
        store.connection = state;
        if (state !== 'open') {
          store.disconnected();
        }
      },
    });

    socket.start();

    const ping = setInterval(() => socket?.send({ type: 'ping', data: { t: Date.now() } }), PING_INTERVAL_MS);

    return () => {
      clearInterval(ping);
      socket?.stop();
      socket = null;
    };
  });

  function selectSource(source: string): void {
    store.pendingSource = source;
    socket?.send({ type: 'selectSource', data: { source } });
  }

  function tap(): void {
    socket?.send({ type: 'tap', data: {} });
  }

  function setBpm(bpm: number | null): void {
    socket?.send({ type: 'setBpm', data: { bpm } });
  }

  function selectAudioDevice(deviceId: string): void {
    store.pendingDevice = deviceId;
    socket?.send({ type: 'selectAudioDevice', data: { deviceId } });
  }

  /*
   * What this client wants, restated in full whenever it changes.
   *
   * Driven by whether the panel is open rather than sent once at startup, which
   * is what keeps a phone in the booth off the device feed entirely. It re-sends
   * on every new connection on purpose: a subscription is a preference held by
   * one socket and deliberately does not survive a reconnect, so a page that
   * subscribed once would go quiet forever after the first Wi-Fi hiccup.
   *
   * Spectrum stays false until there is a canvas to draw it on. Asking for
   * frames nobody renders would be paying the bandwidth for a blank rectangle.
   */
  $effect(() => {
    const wants = { spectrum: false, deviceLevels: devicesOpen };

    if (store.connection === 'open') {
      socket?.send({ type: 'subscribe', data: wants });
    }
  });
</script>

<header>
  <h1>Flux</h1>
  <ConnectionBadge connection={store.connection} sourceState={store.sourceState} source={store.source} />
</header>

<main>
  {#if store.protocolMismatch}
    <p class="mismatch">
      This page speaks protocol {PROTOCOL_VERSION} and Flux speaks {store.protocol}. Reload to pick up the
      current page; until then the readings below are frozen at the last values this page could trust.
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

  <section class="controls">
    <SourceSelector
      sources={store.sources}
      active={store.source}
      pending={store.pendingSource}
      select={selectSource}
    />
    <TapTempo manual={store.source === 'manual'} bpm={store.grid?.bpm ?? null} {tap} {setBpm} />
  </section>

  <section class="panel">
    <button
      type="button"
      class="disclosure"
      class:open={devicesOpen}
      aria-expanded={devicesOpen}
      onclick={() => (devicesOpen = !devicesOpen)}
    >
      <span class="heading">audio device</span>
      <span class="toggle">{devicesOpen ? 'hide' : 'show'}</span>
    </button>

    {#if devicesOpen}
      <DevicePicker
        devices={store.devices}
        pending={store.pendingDevice}
        select={selectAudioDevice}
      />
    {/if}
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

  /* No surface of its own: the controls carry theirs, and a panel behind them
     would sink the buttons into the background they are meant to sit on. */
  .controls {
    display: flex;
    flex-direction: column;
    gap: var(--gap);
  }

  .panel {
    padding: var(--gap);
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
  }

  h2,
  .heading {
    margin: 0 0 0.75rem;
    font-size: 0.7rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--ink-faint);
    font-weight: 600;
  }

  .disclosure {
    width: 100%;
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: var(--gap);
    padding: 0;
    background: none;
    border: none;
    font: inherit;
    cursor: pointer;
  }

  .disclosure .heading {
    margin: 0;
  }

  .disclosure.open {
    margin-bottom: 0.75rem;
  }

  .toggle {
    font-size: 0.7rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink-dim);
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
