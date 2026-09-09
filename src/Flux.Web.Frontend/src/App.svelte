<script lang="ts">
  import { onMount } from 'svelte';
  import AlignmentBadge from './components/AlignmentBadge.svelte';
  import BeatIndicator from './components/BeatIndicator.svelte';
  import BpmReadout from './components/BpmReadout.svelte';
  import BridgePanel from './components/BridgePanel.svelte';
  import LightsPanel from './components/LightsPanel.svelte';
  import ConnectionBadge from './components/ConnectionBadge.svelte';
  import DevicePicker from './components/DevicePicker.svelte';
  import Diagnostics from './components/Diagnostics.svelte';
  import SourceSelector from './components/SourceSelector.svelte';
  import SpectrumCanvas from './components/SpectrumCanvas.svelte';
  import AnchorPad from './components/AnchorPad.svelte';
  import { FluxStore } from './lib/flux.svelte.ts';
  import { PROTOCOL_VERSION } from './lib/protocol.ts';
  import type { HueEngineData } from './lib/protocol.ts';
  import { showsRooms } from './lib/hue.ts';
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

  /*
   * Whether somebody has asked to see the bridge setup, which is not the same as
   * whether it is showing.
   *
   * Setup collapses once it is done and is forced open while there is anything to do
   * there, so the toggle only decides the case where the panel is genuinely finished:
   * a bridge paired, a room chosen, nothing to fix. Collapsing a panel that still needs
   * attention would hide the pairing screen from the person walking to the cupboard.
   */
  let bridgeToggled = $state(false);

  const bridgeNeedsAttention = $derived(!showsRooms(store.hue) || store.hue?.selectedAreaId === null);
  const bridgeOpen = $derived(bridgeNeedsAttention || bridgeToggled);

  /*
   * What the collapsed row says, so closing the panel does not also hide the answer to
   * "which room is this driving". That question is asked far more often than the room
   * is changed, which is the whole argument for collapsing the rest of it.
   */
  const bridgeSummary = $derived(
    store.hue?.areas.find((area) => area.id === store.hue?.selectedAreaId)?.name ?? 'ready',
  );

  /** Wide enough that the spectrum is an instrument rather than a stripe. */
  const VISUALIZER_WIDTH = '(min-width: 640px)';

  /*
   * Open where there is room for it and closed on a phone, which is the whole
   * subscription policy: the booth phone is on the same Wi-Fi as the show and
   * never asks for a 30 Hz feed it has no space to draw.
   *
   * Read once, at startup, and deliberately not kept in step with the window
   * afterwards. Following a later resize would reopen a panel somebody had just
   * closed, and the phone is not going to change width mid-set anyway.
   */
  let visualizerOpen = $state(window.matchMedia(VISUALIZER_WIDTH).matches);

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
   * Most bridge controls set no pending flag, and that is not an omission: pairing and
   * the first discovery are answered by a `hue` frame carrying the state they moved to,
   * so the screen already shows the request having been taken.
   *
   * Refreshing is the exception, and it earned one. For a paired bridge it deliberately
   * does not move the state - moving it was what made the whole panel vanish for five
   * seconds - and it answers in about eighty milliseconds with a room list that is
   * usually identical. Nothing on screen changes, so without this the button reads as
   * broken while working perfectly.
   */
  let refreshing = $state(false);

  /** So a refresh that is never answered cannot leave the button stuck. */
  const REFRESH_TIMEOUT_MS = 15000;

  function discoverBridges(): void {
    refreshing = true;
    setTimeout(() => (refreshing = false), REFRESH_TIMEOUT_MS);
    socket?.send({ type: 'discoverBridges', data: {} });
  }

  /*
   * Cleared by the frame rather than by a timer, so the acknowledgement is the server's
   * answer and not this page's guess about how long one takes.
   */
  $effect(() => {
    if (store.hue !== null) {
      refreshing = false;
    }
  });

  function pairBridge(bridgeId: string): void {
    socket?.send({ type: 'pairBridge', data: { bridgeId } });
  }

  function selectArea(areaId: string): void {
    socket?.send({ type: 'selectArea', data: { areaId } });
  }

  function forgetBridge(): void {
    socket?.send({ type: 'forgetBridge', data: {} });
  }

  function setLatencyOffset(offsetMs: number): void {
    socket?.send({ type: 'setLatencyOffset', data: { offsetMs } });
  }

  /**
   * Sends one complete statement of how the lights should look, from a change to one
   * part of it.
   *
   * The whole record goes on the wire because that is what the message is, but it is
   * composed from the server's picture with this page's own outstanding changes over
   * it. Composed from the server's picture alone, a control carries the other four
   * fields as this page last heard them, so two phones inside one round trip undo each
   * other field by field.
   */
  function setLightSettings(change: Partial<HueEngineData>): void {
    if (store.hue === null) {
      return;
    }

    store.pendingEngine = { ...store.pendingEngine, ...change };
    socket?.send({ type: 'setLightSettings', data: { ...store.hue.engine, ...store.pendingEngine } });
  }

  function startStream(): void {
    socket?.send({ type: 'startStream', data: {} });
  }

  function stopStream(): void {
    socket?.send({ type: 'stopStream', data: {} });
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
   * Both feeds follow their own panel rather than the page, so a client only
   * ever pays for frames something on screen is drawing.
   */
  $effect(() => {
    const wants = { spectrum: visualizerOpen, deviceLevels: devicesOpen };

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

  <!--
    Two groups rather than six loose sections, because the wide layout moves
    them as groups: what a DJ watches stays together on one side, and the
    instruments and settings they consult stack on the other.
  -->
  <div class="watch">
    <section class="stage">
      <BpmReadout bpm={store.grid?.bpm ?? null} stale={store.sourceState === 'quiet'} />

      <BeatIndicator quantum={store.quantum} position={store.positionInBar} tick={store.beatTick} />

      <AlignmentBadge aligned={store.alignment.aligned} method={store.alignment.method} />
    </section>

    <section class="controls">
      <SourceSelector
        sources={store.sources}
        active={store.source}
        pending={store.pendingSource}
        select={selectSource}
      />
      <AnchorPad manual={store.source === 'manual'} bpm={store.grid?.bpm ?? null} {tap} {setBpm} />
    </section>
  </div>

  <div class="consult">
    <!--
      The lights first, because this is the panel somebody has the page open for: it is
      what gets touched with music playing. Not a disclosure, unlike the feeds below -
      those are closed by default because opening them costs traffic, and the bridge
      frame is pushed to every client whether or not anything draws it.
    -->
    {#if showsRooms(store.hue) && store.hue !== null}
      <section class="panel">
        <h2>lights</h2>
        <LightsPanel
          hue={store.hue}
          engine={{ ...store.hue.engine, ...store.pendingEngine }}
          light={store.light}
          bpm={store.grid?.bpm ?? null}
          setEngine={setLightSettings}
          setOffset={setLatencyOffset}
          {startStream}
          {stopStream}
        />
      </section>
    {/if}

    <!--
      Setup, and collapsed once it is done. Forced open whenever there is something to
      do here - unpaired, pairing, failed, or paired with no room chosen - because a
      collapsed panel is only an improvement when the thing inside it is finished.
    -->
    <section class="panel">
      <button
        type="button"
        class="disclosure"
        class:open={bridgeOpen}
        aria-expanded={bridgeOpen}
        disabled={bridgeNeedsAttention}
        onclick={() => (bridgeToggled = !bridgeToggled)}
      >
        <span class="heading">bridge</span>
        <span class="toggle">{bridgeNeedsAttention ? 'set up' : bridgeOpen ? 'hide' : bridgeSummary}</span>
      </button>

      {#if bridgeOpen}
        <BridgePanel
          hue={store.hue}
          discover={discoverBridges}
          pair={pairBridge}
          {selectArea}
          forget={forgetBridge}
          {refreshing}
        />
      {/if}
    </section>

    <section class="panel">
      <button
        type="button"
        class="disclosure"
        class:open={visualizerOpen}
        aria-expanded={visualizerOpen}
        onclick={() => (visualizerOpen = !visualizerOpen)}
      >
        <span class="heading">spectrum</span>
        <span class="toggle">{visualizerOpen ? 'hide' : 'show'}</span>
      </button>

      <!-- Unmounted rather than hidden when closed, which is what stops the
           redraw loop as well as the traffic. -->
      {#if visualizerOpen}
        <SpectrumCanvas frame={store.latestSpectrum} />
      {/if}
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
        <DevicePicker devices={store.devices} pending={store.pendingDevice} select={selectAudioDevice} />
      {/if}
    </section>

    <section class="panel">
      <h2>{sourceName}</h2>
      <Diagnostics
        source={store.source}
        diagnostics={store.diagnostics}
        beatIndex={store.grid?.beatIndex ?? null}
        latencyMs={store.latencyMs}
      />
    </section>
  </div>
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

  /*
   * One column on a phone and two once the screen can hold them, filling
   * whatever width there is rather than a fixed centred strip. The strip was
   * right while the page was a BPM readout and four dots; it is wrong now that
   * the page carries a 64-column instrument, which is only ever as good as the
   * pixels it gets.
   *
   * The row is left to stretch to the window rather than sitting at its natural
   * height, and the stage takes the slack. Spare height at the bottom of a desk
   * monitor is height the one number read from across the booth did not get.
   */
  main {
    flex: 1;
    display: grid;
    grid-template-columns: 1fr;
    gap: var(--gap);
    padding: var(--gap);
    width: 100%;
  }

  .watch,
  .consult {
    display: flex;
    flex-direction: column;
    gap: var(--gap);
    /* Without this a grid column refuses to shrink below its content, and the
       spectrum canvas is what would push it wider than the window. */
    min-width: 0;
  }

  /*
   * Two columns once there is room for the instrument beside the stage.
   *
   * A wider threshold than the 640px the visualizer subscribes at, and
   * deliberately so: those answer different questions. 640px asks whether this
   * is a phone at all, and this one asks whether the spectrum can sit beside
   * the BPM without either being squeezed.
   */
  @media (min-width: 60rem) {
    main {
      grid-template-columns: minmax(20rem, 2fr) minmax(26rem, 3fr);
    }

    .mismatch {
      grid-column: 1 / -1;
    }
  }

  .stage {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2rem;
    /*
     * Tall enough to be the thing you look at from across the booth, and capped
     * so it stops there. Half the viewport alone made a 450px void with a BPM
     * readout adrift in the middle of it on any full-height desktop window.
     */
    min-height: min(50vh, 20rem);
    flex: 1;
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
