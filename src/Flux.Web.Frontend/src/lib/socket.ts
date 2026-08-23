import { reconnectDelay } from './backoff.ts';
import { parseMessage, type ClientMessage, type RawMessage } from './protocol.ts';

/**
 * How long the feed may fall silent before the connection is declared dead.
 *
 * The state stream arrives twenty times a second, so five seconds of nothing is
 * a hundred missing frames - the feed is its own heartbeat. This exists because
 * the browser's close event cannot be relied on: when Flux goes away, a phone
 * with its Wi-Fi radio in power save routinely never receives the closing
 * packet, and without it the socket looks open forever over a frozen page.
 */
const DEAD_AFTER_MS = 5_000;

export type ConnectionState = 'connecting' | 'open' | 'closed';

export interface SocketEvents {
  message(message: RawMessage): void;
  connection(state: ConnectionState): void;
}

/**
 * The page's one connection to Flux, reconnecting on its own.
 *
 * Reconnection is not a nicety here. The desktop shell, the phone and a laptop
 * may all have this page open across a set, through Wi-Fi roaming and a Flux
 * restart or two, and none of them should need a manual refresh. The server
 * holds no per-client state, so a reconnect needs nothing but a new socket.
 */
export class FluxSocket {
  readonly #url: string;
  readonly #events: SocketEvents;

  #socket: WebSocket | null = null;
  #attempt = 0;
  #retry: ReturnType<typeof setTimeout> | null = null;
  #watchdog: ReturnType<typeof setTimeout> | null = null;
  #stopped = false;

  constructor(url: string, events: SocketEvents) {
    this.#url = url;
    this.#events = events;
  }

  start(): void {
    this.#stopped = false;
    this.#open();
  }

  /** Sends a message, or drops it if there is nowhere to send it. */
  send(message: ClientMessage): void {
    if (this.#socket?.readyState === WebSocket.OPEN) {
      this.#socket.send(JSON.stringify(message));
    }
  }

  stop(): void {
    this.#stopped = true;

    if (this.#retry !== null) {
      clearTimeout(this.#retry);
      this.#retry = null;
    }

    this.#clearWatchdog();
    this.#socket?.close();
    this.#socket = null;
  }

  #open(): void {
    this.#events.connection('connecting');

    const socket = new WebSocket(this.#url);
    this.#socket = socket;

    // Armed from the attempt itself, not from 'open': the server greets every
    // connection immediately, so an attempt that produces no frame within the
    // window is just as dead as an established socket that fell silent.
    this.#armWatchdog(socket);

    socket.addEventListener('open', () => {
      this.#attempt = 0;
      this.#events.connection('open');
    });

    socket.addEventListener('message', (event: MessageEvent<string>) => {
      this.#armWatchdog(socket);

      const message = parseMessage(event.data);
      if (message !== null) {
        this.#events.message(message);
      }
    });

    // Only 'close' schedules the retry: an error is always followed by a close,
    // and handling both would reconnect twice and halve the backoff.
    socket.addEventListener('close', () => this.#dead(socket));
  }

  /**
   * Ends one connection, however its death was noticed - the browser's close
   * event or the watchdog. Guarded by identity so a zombie's late close event
   * cannot tear down the replacement connection.
   */
  #dead(socket: WebSocket): void {
    if (this.#socket !== socket) {
      return;
    }

    this.#clearWatchdog();
    this.#socket = null;
    this.#events.connection('closed');
    this.#scheduleRetry();
  }

  #armWatchdog(socket: WebSocket): void {
    this.#clearWatchdog();

    this.#watchdog = setTimeout(() => {
      this.#watchdog = null;
      this.#dead(socket);

      // Told to go away as well: the browser may keep a half-dead TCP
      // connection in CLOSING for a long time, and it is no longer ours.
      socket.close();
    }, DEAD_AFTER_MS);
  }

  #clearWatchdog(): void {
    if (this.#watchdog !== null) {
      clearTimeout(this.#watchdog);
      this.#watchdog = null;
    }
  }

  #scheduleRetry(): void {
    if (this.#stopped || this.#retry !== null) {
      return;
    }

    const delay = reconnectDelay(this.#attempt);
    this.#attempt += 1;

    this.#retry = setTimeout(() => {
      this.#retry = null;
      if (!this.#stopped) {
        this.#open();
      }
    }, delay);
  }
}

/** The socket URL for wherever this page happens to be served from. */
export function socketUrl(): string {
  const scheme = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${scheme}//${window.location.host}/ws`;
}
