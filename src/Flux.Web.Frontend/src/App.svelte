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
  import LookChoices from './components/LookChoices.svelte';
  import Diagnostics from './components/Diagnostics.svelte';
  import LatencySlider from './components/LatencySlider.svelte';
  import SourceSelector from './components/SourceSelector.svelte';
  import SpectrumCanvas from './components/SpectrumCanvas.svelte';
  import StrobeButton from './components/StrobeButton.svelte';
  import StagePanel from './components/StagePanel.svelte';
  import AnchorPad from './components/AnchorPad.svelte';
  import BarShift from './components/BarShift.svelte';
  import UpdateNotice from './components/UpdateNotice.svelte';
  import { stagePhrase } from './lib/diagnostics.ts';
  import { FluxStore } from './lib/flux.svelte.ts';
  import { PROTOCOL_VERSION } from './lib/protocol.ts';
  import type { DiagnosticsData, HueEngineData, StageData } from './lib/protocol.ts';
  import { bridgeNeedsSetup, colorChangeLabel, showsRooms } from './lib/hue.ts';
  import {
    DRAWER_TABS,
    browserStorage,
    readDrawerTab,
    readStageOn,
    writeDrawerTab,
    writeStageOn,
    type DrawerTab,
  } from './lib/preferences.ts';
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
   * Which part of the drawer is showing.
   *
   * Tabs rather than one long column, because the column had grown to six panels in a
   * strip a phone's width wide, and on the desktop that meant scrolling a narrow ribbon
   * beside a screen of empty stage. Grouped by how often each part is touched, so the
   * tab open with music playing holds everything reached for then and nothing else.
   */
  let drawerTab = $state<DrawerTab>(readDrawerTab(storage));

  function showTab(tab: DrawerTab): void {
    drawerTab = tab;
    writeDrawerTab(storage, tab);
  }

  /** Left and right move between tabs, as they do in any tab strip a keyboard reaches. */
  function tabKeydown(event: KeyboardEvent): void {
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (step === 0) {
      return;
    }

    event.preventDefault();
    const next = DRAWER_TABS[(DRAWER_TABS.indexOf(drawerTab) + step + DRAWER_TABS.length) % DRAWER_TABS.length];
    showTab(next);
    document.getElementById(`tab-${next}`)?.focus();
  }

  /*
   * Opens the drawer, on setup when that is what the button is asking for. Not
   * remembered as the chosen tab: it is Flux choosing, and once the bridge is done the
   * drawer goes back to opening where this device left it - read back on every open,
   * because a setup tab Flux forced earlier in the session is still the one in hand.
   */
  function toggleDrawer(): void {
    if (!drawerOpen) {
      drawerTab = needsSetup ? 'setup' : readDrawerTab(storage);
    }

    drawerOpen = !drawerOpen;
  }

  let stageEl: HTMLDivElement | undefined = $state();

  /*
   * A press on the picture closes the drawer, and a press on a stage control does not.
   * No hit-testing is needed for that: the HUD is a sheet of glass, so a press on empty
   * stage or on a reading falls through to the stage, while a control that took the
   * pointer back receives the press itself - and the HUD is the stage's sibling, not
   * its child. A new stage control therefore has to take the pointer back the way
   * `.control` and `.settings` do, or it will close the drawer instead of working.
   *
   * On the window rather than on the stage, because the stage is hidden from assistive
   * technology and a handler on it would be a control nobody but a mouse can find.
   */
  function closeFromStage(event: PointerEvent): void {
    if (event.target instanceof Node && stageEl?.contains(event.target)) {
      drawerOpen = false;
      lookOpen = false;
    }
  }

  /*
   * The source and the look, one tap from the stage.
   *
   * Opened from the corner reading that already names the palette, so the thing that
   * says what the look is is also the way to change it. A panel rather than a standing
   * row of chips, because eight palettes, six intervals and four sources laid across the
   * picture would cover the spectrum they are chosen by, and on a phone would push the
   * beat off the bottom. It closes the way the drawer does - a press on the stage, or
   * Escape - and stays open while choices are made, since a palette is often tried
   * twice before it stays.
   */
  let lookOpen = $state(false);
  let lookPanel: HTMLDivElement | undefined = $state();
  let lookToggle: HTMLButtonElement | undefined = $state();

  /*
   * Escape closes the panel, and only the panel: listened for on the way down rather
   * than up, so it runs before the drawer's own Escape and can mark the key handled -
   * otherwise one press with both open would close both. Focus inside the panel goes
   * back to the corner it was opened from rather than falling to the page.
   */
  function closeLookOnEscape(event: KeyboardEvent): void {
    if (!lookOpen || event.key !== 'Escape' || document.querySelector(':popover-open') !== null) {
      return;
    }

    event.preventDefault();

    if (lookPanel?.contains(document.activeElement)) {
      lookToggle?.focus();
    }

    lookOpen = false;
  }

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

  /*
   * Whether to say on the stage that the lights are off. The stage looks the same drawn
   * from the preview as from a running stream, so without this nothing on the picture
   * says whether the room is lit - and the corner reading is too small to be the only
   * answer to the one question somebody glances at the page to ask. Only in a build that
   * can light a room at all; a beat lab has no lights to be off.
   */
  const lightsOff = $derived(store.hue !== null && store.light === null && !store.protocolMismatch);

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
      drawerTab = 'setup';
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

  function anchor(): void {
    socket?.send({ type: 'anchor', data: {} });
  }

  /* The source's reason in words while it has one, and which deck it follows while it
     does not - diagnostics carry only the active source's fields, which is rekordbox
     whenever this is shown. */
  function rekordboxStatus(diagnostics: DiagnosticsData): string | null {
    if (diagnostics.detail !== undefined) {
      return diagnostics.detail;
    }

    return diagnostics.deck !== undefined ? `following deck ${diagnostics.deck}` : null;
  }

  function shiftBar(beats: number): void {
    socket?.send({ type: 'shiftBar', data: { beats } });
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

  function checkUpdate(): void {
    socket?.send({ type: 'checkUpdate', data: {} });
  }

  function applyUpdate(): void {
    socket?.send({ type: 'applyUpdate', data: {} });
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

  function holdStrobe(held: boolean): void {
    socket?.send({ type: 'strobe', data: { held } });
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
  swallowed a tap would take it from the HUD floating over it. A press that reaches it
  closes the drawer, which is the window listening rather than the stage.
-->
<svelte:window onpointerdown={closeFromStage} onkeydowncapture={closeLookOnEscape} />

<div class="stage" aria-hidden="true" bind:this={stageEl}>
  {#if stageOn}
    <!-- The colour comes off the wire rather than being computed here, which is the
         whole reason it is on the wire: a second engine in the browser would drift from
         the one driving the bulbs and neither could be called wrong. With the lights off
         it is the server's preview of the same engine, so the look can be judged before
         the room is lit; the HUD says which of the two is being drawn. -->
    <SpectrumCanvas
      frame={store.latestSpectrum}
      color={store.light?.color ?? store.preview?.color ?? null}
      lights={store.light?.channels ?? store.preview?.channels ?? []}
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
<div class="hud" class:beside={drawerOpen}>
  <div class="row top">
    <div class="cluster">
      <h1>Flux</h1>
      <ConnectionBadge connection={store.connection} sourceState={store.sourceState} source={store.source} />
    </div>

    <!-- The reading is also the switch, where there is a room to start: saying the lights
         are off and then sending somebody into the drawer to change it is two steps for
         the one thing they glanced at the page to do. Without a room it stays a reading,
         since a start would be refused. Stopping is offered in the same place, so the
         switch does not move between the two states.

         The dot says the state before the words do, because on and off were the same
         grey pill with one word different: green while the room is lit, amber while the
         stage shows a preview of what it would be, grey with nothing to show. A phone
         gets the short form, which says the same in a third of the width. -->
    {#if lightsOff && store.hue?.selectedAreaId != null}
      <button type="button" class="sync switch" onclick={startStream}>
        <span class="dot" class:quiet={store.preview !== null}></span>
        <span class="long">light sync off{store.preview !== null ? ' · showing a preview' : ''}</span>
        <span class="short">lights off</span>
        · <span class="go">start</span>
      </button>
    {:else if lightsOff}
      <p class="sync">
        <span class="dot" class:quiet={store.preview !== null}></span>
        <span class="long">light sync off{store.preview !== null ? ' · showing a preview' : ''}</span>
        <span class="short">lights off</span>
      </p>
    {:else if store.hue !== null && store.light !== null && !store.protocolMismatch}
      <button type="button" class="sync switch" onclick={stopStream}>
        <span class="dot live"></span>
        <span class="long">light sync on</span>
        <span class="short">lights on</span>
        · <span class="go">stop</span>
      </button>
    {/if}

    <button
      type="button"
      class="settings"
      class:wants={needsSetup}
      aria-expanded={drawerOpen}
      onclick={toggleDrawer}
    >
      settings
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
        {#if store.source === 'rekordbox'}
          <BarShift shift={shiftBar} />
        {:else}
          <AnchorButton manual={store.source === 'manual'} {tap} />
          {#if store.alignment.method === 'rekordbox'}
            <BarShift shift={shiftBar} />
          {/if}
        {/if}

        <!-- Only where there is a room to strobe. Offered without one, the press would
             be refused by a build with no bridge and would light nothing in one with. -->
        {#if showsRooms(store.hue)}
          <StrobeButton hold={holdStrobe} />
        {/if}
      </div>

      <div class="cluster">
        <BeatIndicator
          quantum={store.quantum}
          position={store.positionInBar}
          tick={store.beatTick}
          phrase={stagePhrase(store.source, store.diagnostics)}
        />
        <AlignmentBadge aligned={store.alignment.aligned} method={store.alignment.method} />
      </div>
    </div>

    <button
      type="button"
      class="cluster right look-toggle"
      bind:this={lookToggle}
      aria-expanded={lookOpen}
      aria-controls="look"
      onclick={() => (lookOpen = !lookOpen)}
    >
      <!-- In two unbreakable halves, so a narrow corner wraps between the palette and
           the interval rather than inside "every 4 bars". -->
      <span class="reading">
        {#if showsRooms(store.hue) && engine !== null}
          <span class="whole">{engine.palette} &middot;</span>
          <span class="whole">{colorChangeLabel(engine.colorChangeBars)}</span>
        {:else}
          <span class="whole">beat: {store.source}</span>
        {/if}
      </span>

      <!-- `light` is the one field that says whether Flux is streaming, so the absence
           of a frame rate is the reading rather than a gap in one. -->
      <span class="reading dim numeric fps">
        {store.light === null ? 'lights idle' : `${store.light.fps.toFixed(1)} fps`}
      </span>
    </button>
  </div>

  <!-- Always rendered and hidden while closed, so the toggle's `aria-controls` always
       names something. A group rather than a dialog: it takes no focus and traps none. -->
  <div class="look" id="look" role="group" aria-label="Beat source and look" hidden={!lookOpen} bind:this={lookPanel}>
    <section class="look-part">
      <h2>beat</h2>
      <SourceSelector
        sources={store.sources}
        active={store.source}
        pending={store.pendingSource}
        select={selectSource}
      />
    </section>

    {#if showsRooms(store.hue) && engine !== null}
      <section class="look-part">
        <h2>look</h2>
        <LookChoices {engine} setEngine={setLightSettings} />
      </section>
    {/if}
  </div>
</div>

<Drawer open={drawerOpen} close={() => (drawerOpen = false)} scrollKey={drawerTab}>
  {#snippet header()}
    <div class="drawer-head">
      <h2 class="heading">settings</h2>
      <button type="button" class="settings" onclick={() => (drawerOpen = false)}>close</button>
    </div>

    <div class="tabs" role="tablist" aria-label="Settings" tabindex="-1" onkeydown={tabKeydown}>
      {#each DRAWER_TABS as tab (tab)}
        <button
          type="button"
          role="tab"
          id={`tab-${tab}`}
          class="tab"
          class:active={drawerTab === tab}
          class:wants={tab === 'setup' && needsSetup}
          aria-selected={drawerTab === tab}
          aria-controls={`panel-${tab}`}
          tabindex={drawerTab === tab ? 0 : -1}
          onclick={() => showTab(tab)}
        >
          {tab}
        </button>
      {/each}
    </div>
  {/snippet}

  <!--
    Every tab stays mounted and the ones not showing are hidden, for the reason the
    drawer itself is never unmounted: the bridge panel keeps a discovery that is halfway
    through, and the pickers keep a list that was already right.
  -->
  <div class="tab-panel" role="tabpanel" id="panel-lights" aria-labelledby="tab-lights" hidden={drawerTab !== 'lights'}>
    <!--
      The lights first, because they are what gets touched with music playing: start and
      stop, then colour, pulse and strobe in the order they are reached for.
    -->
    {#if showsRooms(store.hue) && store.hue !== null}
      <section class="panel">
        <h2>lights</h2>
        <LightsPanel
          hue={store.hue}
          engine={{ ...store.hue.engine, ...store.pendingEngine }}
          light={store.light}
          phrases={store.source === 'rekordbox'}
          setEngine={setLightSettings}
          {startStream}
          {stopStream}
        />
      </section>
    {/if}

    <!-- Said rather than left as a gap: without a room the lights panel is not
         offered, and a lights tab holding only the beat reads as one that lost half. -->
    {#if store.hue !== null && !showsRooms(store.hue)}
      <button type="button" class="hint" onclick={() => showTab('setup')}>
        the lights appear here once a bridge is set up &rarr;
      </button>
    {/if}

    <!--
      The beat after the lights, and without the source buttons: those are on the stage's
      look panel, one tap from the music, and what is left here is the correction to the
      grid the source chose.

      rekordbox reads bar one from the track, so a tap there is a second opinion it
      overrules at the next seek; what it offers instead is a correction to its grid. It
      reads bar one under another source's tempo too, and there the correction is offered
      beside the tap rather than in its place: the tap is still the only fix for a bar
      that nothing else set, and rekordbox going quiet leaves its last downbeat labelled
      as its own.
    -->
    <section class="panel">
      <h2>beat</h2>
      {#if store.source === 'rekordbox'}
        <BarShift shift={shiftBar} captions status={rekordboxStatus(store.diagnostics)} />
      {:else}
        <AnchorPad manual={store.source === 'manual'} bpm={store.grid?.bpm ?? null} {tap} {anchor} {setBpm} />
        {#if store.alignment.method === 'rekordbox'}
          <BarShift shift={shiftBar} captions />
        {/if}
      {/if}
    </section>
  </div>

  <div class="tab-panel" role="tabpanel" id="panel-screen" aria-labelledby="tab-screen" hidden={drawerTab !== 'screen'}>
    <!--
      The settings on this page that are about the device rather than about the show,
      which is why they are remembered here and never sent to the server.
    -->
    <section class="panel">
      <StagePanel
        on={stageOn}
        {stage}
        lit={store.light?.color !== undefined}
        previewing={store.light === null && store.preview !== null}
        setOn={setStageOn}
        {setStage}
      />
    </section>
  </div>

  <div class="tab-panel" role="tabpanel" id="panel-setup" aria-labelledby="tab-setup" hidden={drawerTab !== 'setup'}>
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

    <!-- Here rather than with the lights: it is set once per room and bridge, by ear,
         and between those visits it is the one slider nobody should brush mid-set. -->
    {#if showsRooms(store.hue) && store.hue !== null}
      <section class="panel">
        <h2>latency</h2>
        <LatencySlider offsetMs={store.hue.latencyOffsetMs} bpm={store.grid?.bpm ?? null} setOffset={setLatencyOffset} />
      </section>
    {/if}

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

    <UpdateNotice update={store.update} check={checkUpdate} restart={applyUpdate} />
    <p class="version">Flux {store.app ?? '--'} &middot; protocol {store.protocol ?? '--'}</p>
  </div>
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

    /* At least zero rather than the implicit auto, which grows to fit min-content: a
       badge that cannot shrink then made the whole HUD wider than a phone, and the
       settings button - the drawer's only standing opener - was past the right edge. */
    grid-template-columns: minmax(0, 1fr);
    padding: var(--gap);
    padding-bottom: max(var(--gap), env(safe-area-inset-bottom));

    /* The layer is a sheet of glass. Without this it is an invisible rectangle over the
       whole window, and everything under it - the stage, and whatever an overlay puts
       there - stops receiving a pointer. The two things in here that are controls take
       it back individually. */
    pointer-events: none;

    /* The drawer's own timing, so the readings move aside with it rather than after. */
    transition: right 180ms ease;
  }

  /*
   * Moved aside for the open drawer on a screen wide enough to show both.
   *
   * The drawer is not modal so that anchor and strobe can be pressed while something is
   * being adjusted, and that only works if they are not underneath it: centred on the
   * whole window, they slid under the drawer at about 1100px and the corner readings
   * were always covered. Inset by the drawer's width, the centre column is centred in
   * the stage that is left. The picture stays full width behind the drawer, since
   * resizing the canvas on every open would redraw the spectrum for nothing.
   *
   * Not on a phone, where the drawer is the full width and there is nothing beside it.
   */
  @media (min-width: 60rem) {
    .hud.beside {
      right: var(--drawer-width);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .hud {
      transition: none;
    }
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
    gap: var(--space-2);
    padding: 0.6rem 0.9rem;
    border-radius: var(--radius);
    background: rgb(7 9 13 / 88%);
    border: 1px solid var(--edge);
    backdrop-filter: blur(8px);
  }

  /*
   * Badge, light sync and settings across the top. The outer columns size to their
   * contents and the middle takes what is left, rather than three equal columns: equal
   * columns centre the pill exactly but squeeze the badge into an ellipsis the moment
   * the drawer narrows the stage, and the badge is the more important of the two.
   */
  .top {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr) auto;
    align-items: start;

    /* Measured for the pill's short form below: the drawer narrows this row without
       the window changing, so a media query cannot see it. */
    container-type: inline-size;
  }

  /* Short enough that the long form would wrap beside the badge and the settings
     button: at 1100px with the drawer open it broke across two lines with its own
     "start" stranded on the right. */
  @container (max-width: 50rem) {
    .sync .long {
      display: none;
    }

    .sync .short {
      display: inline;
    }
  }

  .top .sync {
    justify-self: center;
  }

  .top .settings {
    grid-column: 3;
  }

  .top .cluster {
    flex-direction: row;
    align-items: center;
    gap: var(--space-3);

    /* Lets the badge inside give way before the settings button does. */
    min-width: 0;
  }

  /* Not a cluster itself: it stacks a control above one, and only the readings want a
     surface. */
  .centre {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: var(--space-2);

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
    gap: var(--space-3);
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
    font-size: var(--text-md);
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
    font-size: var(--text-sm);
    letter-spacing: 0.06em;
    color: var(--ink);
  }

  .reading.dim {
    font-size: var(--text-sm);
    color: var(--ink-dim);
  }

  .settings {
    /* Taken back from the sheet of glass above. */
    pointer-events: auto;

    /* Never the thing that shrinks: the badge beside it ellipsizes instead. */
    flex: none;
    padding: 0 var(--space-4);

    /* Opaque, unlike the readings around it. A control needs a findable edge, and the
       scrim bands are a gradient - at the top of a tall window this button sits in the
       strong end of one and at the bottom of a short one it does not. */
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: 999px;
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    cursor: pointer;
    min-height: var(--control);
  }

  /* Nothing works until a bridge is chosen, and the drawer only prompts once. After
     that this is the only thing on screen saying so - by colour rather than by a
     second label, so the one button that opens the drawer is always called the same. */
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

  /* The drawer's title out-ranks the section headings inside it, which share its
     element and so would otherwise share its size. */
  .drawer-head .heading {
    font-size: var(--text-lg);
  }

  .drawer-head .settings {
    background: none;
  }

  /*
   * The tab strip. Pills like the settings button, so the drawer's controls read as one
   * family, and the active one filled rather than underlined: an underline two pixels
   * high is the first thing lost on a phone held at arm's length.
   */
  .tabs {
    display: flex;
    gap: var(--space-2);
  }

  .tab {
    flex: 1;
    padding: 0.45rem 0.6rem;
    background: none;
    border: 1px solid var(--edge);
    border-radius: 999px;
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    cursor: pointer;
    transition:
      background 120ms ease,
      color 120ms ease;
    min-height: var(--control);
  }

  .tab:hover {
    color: var(--ink);
  }

  .tab.active {
    background: var(--panel);
    color: var(--ink);
  }

  /* The same call to attention the settings button makes, on the tab it leads to. */
  .tab.wants {
    border-color: var(--accent);
    color: var(--accent);
  }

  .tab:focus-visible {
    outline: 2px solid var(--accent);
    outline-offset: 2px;
  }

  .tab-panel {
    display: flex;
    flex-direction: column;
    gap: var(--gap);
  }

  /* The attribute has to win over the display above, which it does not by default. */
  .tab-panel[hidden] {
    display: none;
  }

  .hint {
    padding: 0.75rem var(--gap);
    background: none;
    border: 1px dashed var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-sm);
    text-align: left;
    cursor: pointer;
  }

  .hint:hover {
    color: var(--ink);
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
    font-size: var(--text-sm);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink);
    font-weight: 600;
  }

  .disclosure {
    width: 100%;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--gap);
    padding: 0;
    background: none;
    border: none;
    font: inherit;
    cursor: pointer;
    min-height: var(--control);
  }

  .toggle {
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
  }

  /* Quiet rather than alarmed, because lights being off or on is a state somebody
     chose, not a fault. */
  .sync {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.4em;
    margin: 0;
    padding: 0.35rem 0.9rem;
    border: 1px solid var(--edge);
    border-radius: 999px;
    background: rgb(11 13 18 / 70%);
    color: var(--ink-dim);
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;

    /* Wraps inside the window rather than running off it on a phone. */
    max-width: 100%;
    text-align: center;
    min-height: var(--control);
  }

  .switch {
    /* Taken back from the sheet of glass, like the other controls on it. */
    pointer-events: auto;
    font-family: inherit;
    cursor: pointer;
  }

  .switch:hover,
  .switch:focus-visible {
    border-color: var(--accent);
    color: var(--ink);
  }

  .go {
    color: var(--accent);
  }

  .sync .dot {
    flex: none;
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--dead);
  }

  .sync .dot.quiet {
    background: var(--quiet);
  }

  .sync .dot.live {
    background: var(--live);
  }

  .short {
    display: none;
  }

  /* Taken back from the sheet of glass, with a control's edge on hover like the pill. */
  .look-toggle {
    pointer-events: auto;
    font: inherit;
    color: inherit;
    cursor: pointer;
  }

  .look-toggle:hover,
  .look-toggle:focus-visible,
  .look-toggle[aria-expanded='true'] {
    border-color: var(--accent);
  }

  /*
   * Just above the bottom row, in the space between it and the top: the HUD's middle
   * row, placed explicitly so it can share that cell with the mismatch warning. Sized
   * to that row and scrolling inside it, so on a short window it never covers the
   * buttons it was opened beside.
   */
  .look {
    grid-row: 2;
    grid-column: 1;
    align-self: end;
    justify-self: end;
    width: min(24rem, 100%);
    max-height: calc(100% - 2 * var(--space-2));
    overflow-y: auto;
    overscroll-behavior: contain;
    margin-bottom: var(--space-2);
    display: flex;
    flex-direction: column;
    gap: var(--space-4);
    padding: var(--space-4);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    background: var(--surface);
    pointer-events: auto;
  }

  .whole {
    white-space: nowrap;
  }

  /* The panel sets its own display, which would otherwise outrank `hidden`. */
  .look[hidden] {
    display: none;
  }

  .look-part {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
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
    font-size: var(--text-sm);
    max-width: 40rem;
  }

  .version {
    margin: 0;
    font-size: var(--text-xs);
    color: var(--ink-dim);
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
    /* The window's title bar already says Flux, and on a phone the width it takes is
       the width the connection badge needs. */
    h1 {
      display: none;
    }

    .bottom {
      grid-template-columns: 1fr;
    }

    /* Centred with everything else once it is the only thing in its row, unit
       included - the left edge it was dragged to no longer exists. */
    .left {
      justify-self: center;
      --bpm-align: center;
    }

    /* The palette summary stays, since it is the way into the look panel; the frame
       rate goes, being a diagnostic the drawer has. */
    .right {
      justify-self: center;
      align-items: center;
      text-align: center;
    }

    .fps {
      display: none;
    }

    /* The pill under the badge and settings rather than squeezed between them. */
    .top {
      grid-template-columns: minmax(0, 1fr) auto;
      row-gap: var(--space-2);
    }

    .top .sync {
      grid-column: 1 / -1;
      grid-row: 2;
    }

    .top .settings {
      grid-column: 2;
      grid-row: 1;
    }

    .look {
      justify-self: stretch;
      width: auto;
    }
  }
</style>
