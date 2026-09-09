/*
 * What this device wants the page to look like, remembered between visits.
 *
 * Deliberately per device and never on the server, and deliberately down to one thing.
 *
 * How the stage *looks* started out here and moved to the server, because the look is
 * part of the show: it is set by whoever is standing at the page, and it should be the
 * same on the laptop and on the phone in somebody's hand. What is left is the switch
 * that argument never covered - whether this device draws a canvas at all is a fact
 * about this device, and syncing it would let the booth laptop blank the visualizer on
 * somebody's handset.
 */

/** Whether the stage is drawn. Namespaced, because a browser origin is shared. */
const STAGE_KEY = 'flux.stage';

/**
 * The part of `Storage` this module uses, so a test can supply one.
 *
 * Narrower than `Storage` on purpose: it names what is actually touched, and it lets a
 * test hand over an object literal rather than emulating the whole interface.
 */
export interface PreferenceStore {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

/**
 * Whether the visualizer should be drawn at all.
 *
 * **Defaults to on, which reverses the policy this page shipped with.** While the
 * spectrum was a panel somebody opened, defaulting it off kept the booth phone from
 * subscribing to a 30 Hz feed it had no room to draw. That argument dies once the
 * visualizer is the page: a phone that opens Flux and sees an empty rectangle has been
 * given a broken app to save it some bytes. The switch survives for the booth phone
 * that genuinely wants numbers only.
 */
export function readStageOn(storage: PreferenceStore | null): boolean {
  return read(storage, STAGE_KEY) !== 'off';
}

/** Remembers the choice, or silently does not. */
export function writeStageOn(storage: PreferenceStore | null, on: boolean): void {
  write(storage, STAGE_KEY, on ? 'on' : 'off');
}

/*
 * Both halves are wrapped, and not out of habit. `localStorage` throws on access - not
 * on use, on the property read itself - when storage is blocked, which a browser does
 * for third-party contexts and for Safari in private browsing. An unhandled throw here
 * happens during startup and takes the whole page with it, so a remembered preference
 * would be able to stop Flux from rendering a beat.
 */

function read(storage: PreferenceStore | null, key: string): string | null {
  if (storage === null) {
    return null;
  }

  try {
    return storage.getItem(key);
  } catch {
    return null;
  }
}

function write(storage: PreferenceStore | null, key: string, value: string): void {
  if (storage === null) {
    return;
  }

  try {
    storage.setItem(key, value);
  } catch {
    // A preference that cannot be saved is a preference that does not persist, which
    // is a smaller problem than the one throwing here would create.
  }
}

/**
 * The browser's own storage, or null where there is none to be had.
 *
 * Resolved through a function rather than read at module scope so that the throw
 * described above happens somewhere a caller can survive it.
 */
export function browserStorage(): PreferenceStore | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}
