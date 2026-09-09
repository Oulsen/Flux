import {
  PROTOCOL_VERSION,
  type AlignmentData,
  type BeatData,
  type DeviceData,
  type DevicesData,
  type DiagnosticsData,
  type ErrorData,
  type GridData,
  type HelloData,
  type HueData,
  type LightData,
  type RawMessage,
  type SourceReportData,
  type SourceState,
  type SpectrumData,
  type StateData,
} from './protocol.ts';
import type { ConnectionState } from './socket.ts';
import { clearsPendingSelection, resolvePending, retiresHeldBeat } from './storeLogic.ts';

/**
 * Everything the page knows, which is exactly what the server has told it.
 *
 * There is no client-side derivation of tempo or beat position here, and there
 * should never be: the backend owns the timing, and a page that extrapolated on
 * its own would drift away from the lights it is supposed to be a window onto.
 */
export class FluxStore {
  connection = $state<ConnectionState>('connecting');

  app = $state<string | null>(null);
  protocol = $state<number | null>(null);
  quantum = $state(4);
  stateHz = $state(20);

  source = $state('link');
  sourceState = $state<SourceState>('waiting');

  /**
   * Every source the build has, whether or not it is the active one.
   *
   * Empty until the first snapshot, and never filled in from a guess: which
   * sources exist is a property of the running server, not of this page, and a
   * selector offering one that is not there would be offering a dead end.
   */
  sources = $state<SourceReportData[]>([]);

  alignment = $state<AlignmentData>({ aligned: false, method: null });

  /**
   * The source the user asked for, until a snapshot says it happened.
   *
   * The server decides what is active, so the control shows this rather than the
   * request itself: a selection can be declined, and a button that moved on the
   * press would then be lying about the state of the machine.
   */
  pendingSource = $state<string | null>(null);

  /**
   * Every audio endpoint Flux can see, and empty until something subscribes.
   *
   * Empty therefore means "not asked for" rather than "none exist", which is why
   * the picker is the thing that subscribes: a list nobody wanted is not a list
   * that came back short.
   */
  devices = $state<DeviceData[]>([]);

  /** The device the user asked for, until a frame shows it selected. */
  pendingDevice = $state<string | null>(null);

  /**
   * The newest analyzer frame, replaced rather than accumulated.
   *
   * Only the newest one is worth holding: the server samples its analyzer rather
   * than queueing from it, so a page that fell behind should catch up to what is
   * being heard now and never work through a backlog of what was.
   *
   * Null means no frame has arrived, which is not the same as silence - it is
   * what an unsubscribed page and a dropped socket both look like. Whatever
   * draws this has to say so rather than render an empty spectrum.
   */
  latestSpectrum = $state<SpectrumData | null>(null);

  grid = $state<GridData | null>(null);
  // Only the grid's own metric starts at a number. Every other counter belongs
  // to whichever source is active, and a zero before the first frame would
  // claim a measurement nobody has made.
  diagnostics = $state<DiagnosticsData>({ reanchorMaxBeats: 0 });

  /**
   * The bridge picture, or null before the first frame has arrived.
   *
   * Null is "not told yet" and never "no bridge": the server sends one of these the
   * moment a socket opens, so null lasts about as long as the handshake. A page that
   * rendered "unpaired" while it was null would flash a pairing screen at somebody
   * whose lights are already running.
   */
  hue = $state<HueData | null>(null);

  /** Non-null exactly while Flux is streaming to a bridge. */
  light = $state<LightData | null>(null);

  beat = $state<BeatData | null>(null);

  /** Bumped once per beat, so a flash can be retriggered even on a repeat position. */
  beatTick = $state(0);

  latencyMs = $state<number | null>(null);
  lastError = $state<ErrorData | null>(null);

  /**
   * Whether the server speaks a protocol this page was not built for.
   *
   * Worth surfacing rather than muddling through: the fields may have moved, so
   * every number on screen is suspect, and a stale page left open on a phone is
   * exactly how that goes unnoticed.
   */
  get protocolMismatch(): boolean {
    return this.protocol !== null && this.protocol !== PROTOCOL_VERSION;
  }

  /** The position to light up: the beat's own, falling back to the snapshot's. */
  get positionInBar(): number {
    return this.beat?.positionInBar ?? this.grid?.positionInBar ?? 0;
  }

  apply(message: RawMessage): void {
    // The protocol's escalation rule: on a version mismatch the fields may have
    // moved, so nothing is ingested and the stage freezes behind the banner. A
    // hello still passes - it is what clears the mismatch after Flux is rolled
    // back or the page reloads mid-session.
    if (this.protocolMismatch && message.type !== 'hello') {
      return;
    }

    switch (message.type) {
      case 'hello': {
        const hello = message.data as HelloData;
        this.protocol = hello.protocol;
        this.app = hello.app;
        this.quantum = hello.quantum;
        this.stateHz = hello.stateHz;
        break;
      }

      case 'state': {
        const state = message.data as StateData;
        // Read before the assignment below overwrites what it is compared to.
        const sourceChanged = state.source !== this.source;

        this.source = state.source;
        this.sourceState = state.sourceState;
        this.sources = state.sources;
        this.alignment = state.alignment;
        this.grid = state.grid;
        this.light = state.light;
        this.diagnostics = state.diagnostics;
        this.pendingSource = resolvePending(this.pendingSource, state.source);

        if (retiresHeldBeat(this.beat, state.grid, sourceChanged)) {
          this.beat = null;
        }

        break;
      }

      case 'devices': {
        const data = message.data as DevicesData;
        this.devices = data.devices;

        // Confirmed by the list itself rather than by a reply, because the
        // selection is a property of the server and not the answer to a
        // question. A device that was asked for and could not be opened still
        // comes back selected, labelled failed, which is the useful outcome and
        // must not leave the control spinning.
        const selected = data.devices.find((device) => device.selected)?.id;
        if (selected !== undefined) {
          this.pendingDevice = resolvePending(this.pendingDevice, selected);
        }

        break;
      }

      case 'hue': {
        // Replaced wholesale rather than merged. The frame is a complete snapshot
        // and the server compares it field by field before sending, so anything
        // that arrives is genuinely different from what is held.
        this.hue = message.data as HueData;
        break;
      }

      case 'spectrum': {
        // Held as the object it arrived as, because the visualizer tells one
        // frame from the next by identity: a frame that redraws several times
        // must trigger its beat flash exactly once.
        this.latestSpectrum = message.data as SpectrumData;
        break;
      }

      case 'beat': {
        this.beat = message.data as BeatData;
        this.beatTick += 1;
        break;
      }

      case 'pong': {
        const echo = message.data as { t: number };
        this.latencyMs = Date.now() - echo.t;
        break;
      }

      case 'error': {
        const error = message.data as ErrorData;
        this.lastError = error;

        if (clearsPendingSelection(error.code)) {
          this.pendingSource = null;
          this.pendingDevice = null;
        }

        console.warn('Flux rejected a message:', error);
        break;
      }

      default:
        // Unknown types are ignored on purpose: it is what lets a newer server
        // add a message without breaking a page left open on a phone.
        break;
    }
  }

  /** Called when the socket drops, so nothing on screen claims to be live. */
  disconnected(): void {
    this.latencyMs = null;

    // A request made over a socket that then died may never have arrived, and
    // nothing is coming back to resolve it either way. Leaving the control
    // pending would leave it pending until the page is reloaded.
    this.pendingSource = null;
    this.pendingDevice = null;

    // A subscription is a preference held by the connection that is now gone, so
    // the list it filled is gone with it. Keeping the rows would show meters
    // frozen at whatever they read when the socket died.
    this.devices = [];

    // Same argument, and it matters more here: a held frame leaves a spectrum
    // standing on screen that looks exactly like a live one, on a display whose
    // whole job is to say what is being heard right now.
    this.latestSpectrum = null;

    // A stream Flux may or may not still be running is not a measured frame rate,
    // and this field's whole contract is that a number in it was measured.
    this.light = null;

    // `hue` is deliberately kept, unlike everything above it. A reconnect brings a
    // fresh frame within a second, the connection badge already marks the whole page
    // as last-known, and the one moment this matters is a phone rejoining the Wi-Fi
    // on the way back from the cupboard - blanking the pairing screen exactly then
    // would be the worst possible time to be tidy.
  }
}
