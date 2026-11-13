/*
 * What this device wants the page to look like, remembered between visits.
 *
 * Deliberately per device and never on the server, and deliberately kept to what is
 * about the device rather than about the show.
 *
 * How the stage *looks* started out here and moved to the server, because the look is
 * part of the show: it is set by whoever is standing at the page, and it should be the
 * same on the laptop and on the phone in somebody's hand. What is left is what that
 * argument never covered. Whether this device draws a canvas at all is a fact about
 * this device, and syncing it would let the booth laptop blank the visualizer on
 * somebody's handset. Which settings tab it last had open is the same kind of fact:
 * the laptop living on setup says nothing about the phone that only ever wants lights.
 */

/** Whether the stage is drawn. Namespaced, because a browser origin is shared. */
const STAGE_KEY = 'flux.stage';

/** Whether this device draws the deck strip. Local for the reason the stage switch is. */
const WAVEFORM_KEY = 'flux.waveform';

/** Which of rekordbox's waveforms the deck strip draws, and in whose colours. */
const WAVEFORM_STYLE_KEY = 'flux.waveformStyle';

/** Which settings tab was open last. */
const DRAWER_TAB_KEY = 'flux.drawerTab';

/**
 * The settings drawer's tabs, grouped by how often they are touched: lights is reached
 * for with music playing, screen now and then, setup once and then rarely. Named for
 * what each one changes, because the earlier show and stage both sounded like the
 * picture and only one of them was.
 */
export const DRAWER_TABS = ['lights', 'screen', 'setup'] as const;

export type DrawerTab = (typeof DRAWER_TABS)[number];

/** The names the first two tabs were stored under before they were renamed. */
const FORMER_TABS: Readonly<Record<string, DrawerTab>> = { show: 'lights', stage: 'screen' };

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

/**
 * Whether the deck strip is drawn, when there is a deck to draw. On unless turned off:
 * it only appears while a DJ program is being followed, which is when it is wanted, and
 * a screen with no room for it can say so once.
 */
export function readWaveformOn(storage: PreferenceStore | null): boolean {
  return read(storage, WAVEFORM_KEY) !== 'off';
}

/** Remembers the choice, or silently does not. */
export function writeWaveformOn(storage: PreferenceStore | null, on: boolean): void {
  write(storage, WAVEFORM_KEY, on ? 'on' : 'off');
}

/**
 * The deck strip's style, after rekordbox's own waveform views: `rgb`, its colour
 * waveform; `3-band`, its 3-band waveform in its blue, amber and white; and `flux`, the
 * 3-band waveform in Flux's blues. RGB is the default because it is rekordbox's, so the
 * strip looks like the deck on the screen beside it unless somebody chose otherwise.
 */
export const WAVEFORM_STYLES = ['rgb', '3-band', 'flux'] as const;

export type WaveformStyle = (typeof WAVEFORM_STYLES)[number];

/** RGB unless another style was chosen; anything else stored reads as the default. */
export function readWaveformStyle(storage: PreferenceStore | null): WaveformStyle {
  const saved = read(storage, WAVEFORM_STYLE_KEY);
  return WAVEFORM_STYLES.find((style) => style === saved) ?? 'rgb';
}

/** Remembers the choice, or silently does not. */
export function writeWaveformStyle(storage: PreferenceStore | null, style: WaveformStyle): void {
  write(storage, WAVEFORM_STYLE_KEY, style);
}

/** Remembers the choice, or silently does not. */
export function writeStageOn(storage: PreferenceStore | null, on: boolean): void {
  write(storage, STAGE_KEY, on ? 'on' : 'off');
}

/**
 * The tab the drawer opens on. Lights when nothing was remembered, or when what was
 * remembered is not a tab this page has - a key from another version, most likely -
 * because lights is the tab the page is open for. A tab remembered under its former
 * name opens as the tab it became.
 */
export function readDrawerTab(storage: PreferenceStore | null): DrawerTab {
  const saved = read(storage, DRAWER_TAB_KEY);

  if (saved !== null && Object.hasOwn(FORMER_TABS, saved)) {
    return FORMER_TABS[saved];
  }

  return DRAWER_TABS.find((tab) => tab === saved) ?? 'lights';
}

/** Remembers the tab, or silently does not. */
export function writeDrawerTab(storage: PreferenceStore | null, tab: DrawerTab): void {
  write(storage, DRAWER_TAB_KEY, tab);
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
