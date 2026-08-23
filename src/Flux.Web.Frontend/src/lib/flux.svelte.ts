import {
  PROTOCOL_VERSION,
  type BeatData,
  type DiagnosticsData,
  type ErrorData,
  type GridData,
  type HelloData,
  type RawMessage,
  type SourceState,
  type StateData,
} from './protocol.ts';
import type { ConnectionState } from './socket.ts';

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
  grid = $state<GridData | null>(null);
  diagnostics = $state<DiagnosticsData>({ packets: 0, reanchorMaxBeats: 0 });

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
        this.source = state.source;
        this.sourceState = state.sourceState;
        this.grid = state.grid;
        this.diagnostics = state.diagnostics;

        // The beat readout belongs to the source, so it goes when the source
        // has never spoken - otherwise a stale position sits lit under a
        // "waiting" label.
        if (state.grid === null) {
          this.beat = null;
        }

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
        this.lastError = message.data as ErrorData;
        console.warn('Flux rejected a message:', this.lastError);
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
  }
}
