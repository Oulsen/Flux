<script lang="ts">
  import { onMount } from 'svelte';
  import AlignmentBadge from './components/AlignmentBadge.svelte';
  import AnchorButton from './components/AnchorButton.svelte';
  import BeatIndicator from './components/BeatIndicator.svelte';
  import BpmReadout from './components/BpmReadout.svelte';
  import BridgePanel from './components/BridgePanel.svelte';
  import Drawer from './components/Drawer.svelte';
  import LightsPanel from './components/LightsPanel.svelte';
  import ConnectionBadge from './components/ConnectionBadge.svelte';
  import DevicePicker from './components/DevicePicker.svelte';
  import Diagnostics from './components/Diagnostics.svelte';
  import SourceSelector from './components/SourceSelector.svelte';
  import SpectrumCanvas from './components/SpectrumCanvas.svelte';
  import StagePanel from './components/StagePanel.svelte';
  import AnchorPad from './components/AnchorPad.svelte';
  import { FluxStore } from './lib/flux.svelte.ts';
  import { PROTOCOL_VERSION } from './lib/protocol.ts';
  import type { HueEngineData, StageData } from './lib/protocol.ts';
  import { bridgeNeedsSetup, colorChangeLabel, showsRooms } from './lib/hue.ts';
  import { browserStorage, readStageOn, writeStageOn } from './lib/preferences.ts';
  import { FluxSocket, socketUrl } from './lib/socket.ts';

  /** How often the round trip is measured. Often enough to notice, rarely enough to ignore. */
  const PING_INTERVAL_MS = 2000;

  const store = new FluxStore();

  const sourceName = $derived(store.source === 'link' ? 'Ableton Link' : store.source);

  // Held outside onMount because the controls send on it. Not reactive state:
  // nothing renders from the socket itself, only from what it delivers.
  let socket: FluxSocket | null = null;

  const storage = browserStorage();

  /*
   * Whether the visualizer is drawn, which is now the whole page rather than a panel
   * in it.
   *
   * Remembered per device and defaulting to on. **That default is the reverse of what
   * this page shipped with**, and the argument it replaces was a good one: while the
   * spectrum was one panel among five, leaving it closed kept the booth phone off a
   * 30 Hz feed it had no room to draw. Once the visualizer *is* the page, the same
   * default hands a phone an empty rectangle instead of an app. The switch stays, in
   * the drawer, for the phone that genuinely wants the numbers and nothing else.
   */
  let stageOn = $state(readStageOn(storage));

  /** Closed by default, so nothing subscribes to the device feed unasked. */
  let devicesOpen = $state(false);

  let drawerOpen = $state(false);

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

  const needsSetup = $derived(bridgeNeedsSetup(store.hue));
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

  /** The engine as this page believes it to be: the server's picture under its own unconfirmed changes. */
  const engine = $derived(store.hue === null ? null : { ...store.hue.engine, ...store.pendingEngine });

  /** The same, for the stage. Never null: the store seeds it with the server's own defaults. */
  const stage = $derived({ ...store.stage, ...store.pendingStage });

  /*
   * The drawer shows itself once, the first time Flux says the bridge needs setting up.
   *
   * Once rather than reactively, which is the difference between a prompt and a trap.
   * A refresh passes briefly through a state with no room selected, and a drawer that
   * tracked that condition would spring open over the stage every time somebody pressed
   * refresh - and could not be closed while it lasted.
   */
  let prompted = $state(false);

  $effect(() => {
    if (needsSetup && !prompted) {
      prompted = true;
      drawerOpen = true;
    }
  });

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

  function setStageOn(on: boolean): void {
    stageOn = on;
    writeStageOn(storage, on);
  }

  /**
   * Sends one complete statement of how the stage should draw, from a change to one part.
   *
   * Composed exactly as `setLightSettings` is, and for the same reason: built from the
   * server's picture alone, a control would carry the other three fields as this page
   * last heard them, so two phones inside one round trip undo each other field by field.
   */
  function setStage(change: Partial<StageData>): void {
    store.pendingStage = { ...store.pendingStage, ...change };
    socket?.send({ type: 'setStageSettings', data: { ...store.stage, ...store.pendingStage } });
  }

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
   * The spectrum now follows the stage rather than a disclosure panel, so on most
   * clients it is on from the first frame. What has not changed is why this is an
   * effect rather than a line in `onMount`: a subscription is a preference held by one
   * socket and deliberately does not survive a reconnect, so a page that subscribed
   * once would go quiet forever after the first Wi-Fi hiccup.
   *
   * The device feed still follows its own panel, because that one really is a panel:
   * nothing on screen draws a level meter unless the picker is showing.
   */
  $effect(() => {
    const wants = { spectrum: stageOn, deviceLevels: devicesOpen };

    if (store.connection === 'open') {
      socket?.send({ type: 'subscribe', data: wants });
    }
  });
</script>

<!--
  The stage, and it is the page rather than an element on it.

  Behind everything and never interactive: it carries no controls, and a canvas that
  swallowed a tap would take it from the HUD floating over it.
-->
<div class="stage" aria-hidden="true">
  {#if stageOn}
    <!-- The colour comes off the wire rather than being computed here, which is the
         whole reason it is on the wire: a second engine in the browser would drift from
         the one driving the bulbs and neither could be called wrong. -->
    <SpectrumCanvas
      frame={store.latestSpectrum}
      color={store.light?.color ?? null}
      lights={store.light?.channels ?? []}
      stateHz={store.stateHz}
      {stage}
    />
  {/if}
</div>

<!--
  Everything worth reading at a glance, floating over the picture.

  The layer ignores the pointer so that the stage below it is not covered by an
  invisible rectangle; the two things in here that are actually controls take it back.
-->
<div class="hud">
  <div class="row top">
    <div class="cluster">
      <h1>Flux</h1>
      <ConnectionBadge connection={store.connection} sourceState={store.sourceState} source={store.source} />
    </div>

    <button
      type="button"
      class="settings"
      class:wants={needsSetup}
      aria-expanded={drawerOpen}
      onclick={() => (drawerOpen = !drawerOpen)}
    >
      {needsSetup ? 'set up' : 'settings'}
    </button>
  </div>

  {#if store.protocolMismatch}
    <p class="mismatch">
      This page speaks protocol {PROTOCOL_VERSION} and Flux speaks {store.protocol}. Reload to pick up the
      current page; until then the readings below are frozen at the last values this page could trust.
    </p>
  {/if}

  <!--
    Three columns with equal sides rather than a flex row spread apart, because
    space-between only centres the middle item when the two beside it happen to be the
    same width - and they never are. The beat dots drifted right by however much wider
    the BPM was than the frame rate.
  -->
  <div class="row bottom">
    <div class="cluster left">
      <BpmReadout bpm={store.grid?.bpm ?? null} stale={store.sourceState === 'quiet'} />
    </div>

    <!--
      The anchor sits outside the boxed readings rather than in with them. It carries
      its own edge already, and a circle inside a rounded rectangle is two frames
      around one control.
    -->
    <div class="centre">
      <!--
        On the stage rather than only in the drawer: this is pressed on the downbeat
        while music is playing, which is exactly when nobody is opening a panel.

        The wrapper is what takes the pointer back from the HUD, and it is a wrapper
        rather than a rule on the button because the button's own class is scoped to its
        component: reaching it from here would need `:global`, which would then apply to
        the copy in the drawer that never wanted this. Without it the control is a
        painting of a button - no hover, no press, and no tap on a phone.
      -->
      <div class="control">
        <AnchorButton manual={store.source === 'manual'} {tap} />
      </div>

      <div class="cluster">
        <BeatIndicator quantum={store.quantum} position={store.positionInBar} tick={store.beatTick} />
        <AlignmentBadge aligned={store.alignment.aligned} method={store.alignment.method} />
      </div>
    </div>

    <div class="cluster right">
      <!-- `light` is the one field that says whether Flux is streaming, so the absence
           of a frame rate is the reading rather than a gap in one. -->
      <p class="reading numeric">{store.light === null ? 'lights idle' : `${store.light.fps.toFixed(1)} fps`}</p>

      {#if engine !== null}
        <p class="reading dim">{engine.palette} &middot; {colorChangeLabel(engine.colorChangeBars)}</p>
      {/if}
    </div>
  </div>
</div>

<Drawer open={drawerOpen} close={() => (drawerOpen = false)}>
  <div class="drawer-head">
    <h2 class="heading">settings</h2>
    <button type="button" class="settings" onclick={() => (drawerOpen = false)}>close</button>
  </div>

  <section class="panel">
    <h2>beat</h2>
    <SourceSelector
      sources={store.sources}
      active={store.source}
      pending={store.pendingSource}
      select={selectSource}
    />
    <AnchorPad manual={store.source === 'manual'} bpm={store.grid?.bpm ?? null} {tap} {setBpm} />
  </section>

  <!--
    The lights first among the panels, because this is the one somebody has the page
    open for: it is what gets touched with music playing.
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

  <!--
    The settings on this page that are about the device rather than about the show,
    which is why they are remembered here and never sent to the server.
  -->
  <section class="panel">
    <StagePanel
      on={stageOn}
      {stage}
      lit={store.light?.color !== undefined}
      setOn={setStageOn}
      {setStage}
    />
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

  <p class="version">Flux {store.app ?? '--'} &middot; protocol {store.protocol ?? '--'}</p>
</Drawer>

<style>
  .stage {
    position: fixed;
    inset: 0;
    z-index: 0;
    background: var(--surface);
  }

  /*
   * The readings, over the picture.
   *
   * A grid of three rows rather than four absolutely positioned corners, so the middle
   * row absorbs every spare pixel and the two that carry text stay where a person
   * learned to look for them regardless of window height.
   */
  .hud {
    position: fixed;
    inset: 0;
    z-index: 1;
    display: grid;
    grid-template-rows: auto 1fr auto;
    padding: var(--gap);
    padding-bottom: max(var(--gap), env(safe-area-inset-bottom));

    /* The layer is a sheet of glass. Without this it is an invisible rectangle over the
       whole window, and everything under it - the stage, and whatever an overlay puts
       there - stops receiving a pointer. The two things in here that are controls take
       it back individually. */
    pointer-events: none;
  }

  /*
   * A soft vignette at the two edges, under the boxed readings rather than instead of
   * them.
   *
   * It is deliberately weak. What it is there for is the anchor button, which carries
   * its own chrome and so has no box behind it, and the general business of keeping a
   * bright canvas from running right into the edge of the window. The readings get
   * their contrast from their own surfaces below.
   */
  .hud::before,
  .hud::after {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    height: 10rem;

    /* Behind the readings but still inside the HUD's own stacking context, so it
       covers the canvas and never the text. */
    z-index: -1;
    pointer-events: none;
  }

  .hud::before {
    top: 0;
    background: linear-gradient(to bottom, rgb(7 9 13 / 62%), rgb(7 9 13 / 0%));
  }

  .hud::after {
    bottom: 0;
    background: linear-gradient(to top, rgb(7 9 13 / 70%), rgb(7 9 13 / 0%));
  }

  .row {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: var(--gap);
  }

  .bottom {
    grid-row: 3;
    display: grid;
    grid-template-columns: 1fr auto 1fr;
    align-items: end;
  }

  /*
   * Each group of readings gets its own surface.
   *
   * The first attempt at this was the same idea at 55% opacity with a blur, and it
   * failed on real music for a reason worth keeping: blur removes detail, not
   * brightness. Over a saturated spectrum it produced a slightly softer green
   * rectangle with grey text on it. The shape was right and the number was not - so
   * the surface is nearly opaque now, and the blur stays for the edge of it.
   */
  .cluster {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    padding: 0.6rem 0.9rem;
    border-radius: var(--radius);
    background: rgb(7 9 13 / 88%);
    border: 1px solid var(--edge);
    backdrop-filter: blur(8px);
  }

  .top .cluster {
    flex-direction: row;
    align-items: center;
    gap: 0.9rem;
  }

  /* Not a cluster itself: it stacks a control above one, and only the readings want a
     surface. */
  .centre {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.6rem;

    /* Smaller than the drawer's, because the drawer has room for a thumb target beside
       a caption and this sits above the beat dots in the middle of a picture. */
    --anchor-size: 3.75rem;
  }

  .centre .cluster {
    align-items: center;
  }

  /* Taken back from the sheet of glass, and sized to the control so the reclaimed
     area is the circle rather than a rectangle around it. */
  .control {
    pointer-events: auto;
    display: flex;
  }

  /* Sized to their contents and pushed to their own edges. A grid stretches its
     children by default, which would run each surface across a full third of the
     window with a line of text adrift inside it. */
  .left {
    justify-self: start;
  }

  .centre {
    justify-self: center;
  }

  .right {
    justify-self: end;
    align-items: flex-end;
    text-align: right;
  }

  /*
   * A corner reading rather than the middle of a phone, so the clamp the component
   * falls back to would be two thirds of the window wide.
   *
   * The unit is dragged to the same edge as the number. Left in the component's own
   * centring it sat under the middle of "126.16", which reads as a caption that missed
   * rather than as a label.
   */
  .left {
    --bpm-size: clamp(2.5rem, 8vw, 4.5rem);
    --bpm-align: left;
  }

  h1 {
    margin: 0;
    font-size: 1rem;
    font-weight: 600;
    letter-spacing: 0.4em;
    text-indent: 0.4em;
    text-transform: uppercase;
  }

  /* A step brighter than the same text would be in a panel. `--ink-faint` is legible
     against `--panel` and disappears over a lit spectrum, which is what the first
     screenshot of this layout showed. */
  .reading {
    margin: 0;
    font-size: 0.85rem;
    letter-spacing: 0.06em;
    color: var(--ink);
  }

  .reading.dim {
    font-size: 0.8rem;
    color: var(--ink-dim);
  }

  .settings {
    /* Taken back from the sheet of glass above. */
    pointer-events: auto;
    padding: 0.4rem 0.9rem;

    /* Opaque, unlike the readings around it. A control needs a findable edge, and the
       scrim bands are a gradient - at the top of a tall window this button sits in the
       strong end of one and at the bottom of a short one it does not. */
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: 999px;
    color: var(--ink-dim);
    font: inherit;
    font-size: 0.75rem;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    cursor: pointer;
  }

  /* Nothing works until a bridge is chosen, and the drawer only prompts once. After
     that this is the only thing on screen saying so. */
  .settings.wants {
    border-color: var(--accent);
    color: var(--accent);
  }

  .drawer-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap);
  }

  .drawer-head .settings {
    background: none;
  }

  .panel {
    display: flex;
    flex-direction: column;
    gap: var(--gap);
    padding: var(--gap);
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
  }

  h2,
  .heading {
    margin: 0;
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

  .toggle {
    font-size: 0.7rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink-dim);
  }

  .mismatch {
    grid-row: 2;
    align-self: start;
    justify-self: center;
    margin: var(--gap) 0 0;
    padding: 0.75rem var(--gap);
    border: 1px solid var(--alarm);
    border-radius: var(--radius);
    background: rgb(11 13 18 / 80%);
    color: var(--alarm);
    font-size: 0.85rem;
    max-width: 40rem;
  }

  .version {
    margin: 0;
    font-size: 0.75rem;
    color: var(--ink-faint);
    text-align: center;
  }

  /*
   * A phone in the booth, where the bottom row will not fit across.
   *
   * The BPM leads because it is the reading somebody looks up for, and the frame rate
   * drops out of the row entirely rather than being squeezed: it is a diagnostic, and
   * the drawer has it.
   */
  @media (max-width: 40rem) {
    .bottom {
      grid-template-columns: 1fr;
    }

    /* Centred with everything else once it is the only thing in its row, unit
       included - the left edge it was dragged to no longer exists. */
    .left {
      justify-self: center;
      --bpm-align: center;
    }

    .right {
      display: none;
    }
  }
</style>
