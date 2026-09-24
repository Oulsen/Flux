/**
 * How often a held strobe button repeats its press, in milliseconds.
 *
 * Mirrored from `StrobeHolds.RenewEveryMs`, and only meaningful beside the lease it
 * renews: the server drops a hold that goes a second and a half without one, so a page
 * that went to sleep with a finger on the button stops strobing the room on its own
 * rather than waiting for a release that is never coming.
 */
export const STROBE_RENEW_MS = 500;

/** The two timer calls a hold needs, so a test can drive them without waiting. */
export interface HoldTimers {
  setInterval(callback: () => void, ms: number): unknown;
  clearInterval(handle: unknown): void;
}

const browserTimers: HoldTimers = {
  setInterval: (callback, ms) => globalThis.setInterval(callback, ms),
  clearInterval: (handle) => globalThis.clearInterval(handle as ReturnType<typeof globalThis.setInterval>),
};

/**
 * One finger on the strobe button, as the server needs to hear about it.
 *
 * A press is said once and then repeated for as long as it lasts, because the server
 * treats a hold as a lease rather than a switch - see `STROBE_RENEW_MS`. Kept out of
 * the component so the part that matters for safety, that a release always stops the
 * repeating and always says so, is under test rather than inside an event handler.
 *
 * Pressing while held and releasing while not are both nothing, which is what lets the
 * component wire every way a press can end - pointer up, pointer cancelled, capture
 * lost, focus lost, tab hidden - to one release without counting which fired first.
 */
export class StrobeHold {
  readonly #send: (held: boolean) => void;
  readonly #timers: HoldTimers;
  #renewing: unknown = null;

  constructor(send: (held: boolean) => void, timers: HoldTimers = browserTimers) {
    this.#send = send;
    this.#timers = timers;
  }

  get held(): boolean {
    return this.#renewing !== null;
  }

  press(): void {
    if (this.held) {
      return;
    }

    this.#send(true);
    this.#renewing = this.#timers.setInterval(() => this.#send(true), STROBE_RENEW_MS);
  }

  release(): void {
    if (!this.held) {
      return;
    }

    this.#timers.clearInterval(this.#renewing);
    this.#renewing = null;
    this.#send(false);
  }
}
