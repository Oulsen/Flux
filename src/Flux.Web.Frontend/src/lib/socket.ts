import { reconnectDelay } from './backoff.ts';
import { parseMessage, type ClientMessage, type RawMessage } from './protocol.ts';

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

    this.#socket?.close();
    this.#socket = null;
  }

  #open(): void {
    this.#events.connection('connecting');

    const socket = new WebSocket(this.#url);
    this.#socket = socket;

    socket.addEventListener('open', () => {
      this.#attempt = 0;
      this.#events.connection('open');
    });

    socket.addEventListener('message', (event: MessageEvent<string>) => {
      const message = parseMessage(event.data);
      if (message !== null) {
        this.#events.message(message);
      }
    });

    // Only 'close' schedules the retry: an error is always followed by a close,
    // and handling both would reconnect twice and halve the backoff.
    socket.addEventListener('close', () => {
      this.#socket = null;
      this.#events.connection('closed');
      this.#scheduleRetry();
    });
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
