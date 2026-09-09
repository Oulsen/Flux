/**
 * The client half of `docs/protocol.md`.
 *
 * This file is a mirror, not a source: the document is authoritative and the
 * backend's `Flux.Web.Server.Protocol` is the other reflection of it. When one
 * of the three changes, all three change.
 */

/** The protocol version this page was built against. */
export const PROTOCOL_VERSION = 1;

export type SourceState = 'waiting' | 'live' | 'quiet';

/** What put the bar where it is, or null while the bar is only a count. */
export type AlignmentMethod = 'cue' | 'tap' | 'drop';

/** What the detector made of one analyzer frame. */
export type SpectrumBeat = 'none' | 'beat' | 'strong';

export type DeviceKind = 'capture' | 'loopback';

export type DeviceState = 'signal' | 'digitalSilence' | 'noData' | 'failed';

/**
 * Where Flux has got to with a bridge.
 *
 * Seven rather than a paired boolean, because each one is a different screen.
 * `unpaired` carrying a `message` is the ordinary end of a pairing attempt nobody
 * answered and must not be painted as a failure - nothing broke, the button is on a
 * physical object in another room and it was not pressed in time.
 */
export type HueState =
  | 'disabled'
  | 'unpaired'
  | 'discovering'
  | 'pairing'
  | 'connecting'
  | 'ready'
  | 'failed';

/**
 * Which colours the light engine draws from.
 *
 * `random` is any hue on the wheel; the rest are curated sets of eight. `ocean` and
 * `warm` are tight on purpose, so they change by less than the others and that is not
 * a fault.
 */
export type HuePalette =
  | 'random'
  | 'warm'
  | 'cool'
  | 'neon'
  | 'sunset'
  | 'ocean'
  | 'forest'
  | 'party';

/**
 * How the light engine turns beats into colour.
 *
 * Named for the engine and not for the lights, because `state.light` already means
 * something else - what the stream is achieving, measured. These are settings.
 *
 * Both zeroes read backwards and both are deliberate: no interval means the colour
 * changes as often as it can, and no pulse means a steady colour rather than an
 * instantaneous flash.
 */
export interface HueEngineData {
  palette: HuePalette;
  /** Bars between colour changes, 0 to 16, where 0 means every beat. */
  colorChangeBars: number;
  /**
   * How long a beat's pulse takes to fade, 0 to 2000 ms, where 0 means no pulse.
   *
   * Under about 80 ms is honoured and may not be visible: the bridge forwards to a bulb
   * at roughly 25 updates a second, so a pulse that short is one or two Zigbee frames.
   */
  pulseMs: number;
  /** How bright a beat drives the lights, 0 to 100 per cent. The ceiling for everything sent, so 0 is a blackout. */
  brightness: number;
  /**
   * How far the light drops between beats, 0 to 100 per cent of `brightness`.
   *
   * A fraction of the ceiling rather than a floor of its own, which is what makes a
   * short pulse usable: at a low depth a quick flash sits on a bright room instead of
   * leaving a dark one. 0 never dips; 100 fades to nothing.
   */
  pulseDepth: number;
}

export interface HelloData {
  protocol: number;
  app: string;
  quantum: number;
  stateHz: number;
  spectrumHz: number;
  devicesHz: number;
}

export interface GridData {
  bpm: number;
  beat: number;
  beatIndex: number;
  positionInBar: number;
  barAligned: boolean;
}

/** One source's own view of itself, whether or not it is the active one. */
export interface SourceReportData {
  name: string;
  state: SourceState;
  confidence: number;
}

export interface AlignmentData {
  aligned: boolean;
  method: AlignmentMethod | null;
}

/**
 * Health counters for the active source.
 *
 * Every field but `reanchorMaxBeats` is optional because the server omits what
 * does not apply to the current source, rather than sending a zero. A missing
 * field means "not a thing this source has", so render a blank and never a 0.
 */
export interface DiagnosticsData {
  reanchorMaxBeats: number;
  packets?: number;
  fps?: number;
  beatsDetected?: number;
  bpmLocked?: boolean;
  lastBeatAgoMs?: number;
  taps?: number;
  lastTapAgoMs?: number;
}

/**
 * What the light stream is achieving.
 *
 * `fps` is measured and is not the configured rate echoed back. It is also not the
 * rate a bulb renders: the bridge forwards over Zigbee at roughly 25 updates a second
 * and drops the excess, so a gap between the two is the design and not a fault.
 */
export interface LightData {
  fps: number;
  /**
   * The colour the last frame carried, or absent until one has gone out.
   *
   * What the bridge was sent, so the page can draw the room rather than model it.
   * Anything a bulb also does is decided on the server and read from here; a second
   * engine in the browser would drift from this one and neither could be called wrong.
   */
  color?: LightColorData;
}

/** One colour, linear 0 to 1 per channel, exactly as the engine produced it. */
export interface LightColorData {
  r: number;
  g: number;
  b: number;
}

export interface StateData {
  source: string;
  sourceState: SourceState;
  sources: SourceReportData[];
  /** Null exactly when nothing has been received yet. Never render it as zero. */
  grid: GridData | null;
  alignment: AlignmentData;
  /** Null exactly when Flux is not streaming. The only field that says whether it is. */
  light: LightData | null;
  diagnostics: DiagnosticsData;
}

export interface SpectrumData {
  /** 64 display columns, low frequency first, each 0 to 255. */
  bins: number[];
  kick: number;
  mids: number;
  highs: number;
  flux: number;
  beat: SpectrumBeat;
}

/**
 * One audio endpoint, and only what the server actually measured of it.
 *
 * `state`, `rms` and `peak` are optional because only the selected device is
 * measured: metering every endpoint would mean holding all of them open for a
 * whole set. A missing field means "not measured", so render a blank and never a
 * zero - a row of zeroed meters reads as twenty dead devices rather than as
 * nineteen unmeasured ones.
 *
 * A device that was selected and would not open keeps its `state` (`failed`, the
 * answer to why there is no audio) and still has no levels, because it delivered
 * no samples. `digitalSilence` does arrive with levels: zeros are a real reading
 * of a stopped deck.
 */
export interface DeviceData {
  id: string;
  name: string;
  kind: DeviceKind;
  selected: boolean;
  state?: DeviceState;
  rms?: number;
  peak?: number;
}

export interface DevicesData {
  devices: DeviceData[];
}

export interface BeatData {
  index: number;
  positionInBar: number;
  barAligned: boolean;
  bpm: number;
}

export interface HueBridgeData {
  id: string;
  /** Where it answered from this time. Display only: DHCP moves it, so never store it. */
  address: string;
}

export interface HueAreaData {
  id: string;
  name: string;
  channelCount: number;
  /**
   * Whether something already holds this area's one streaming session, which is the
   * pre-flight for a failure that otherwise arrives as an opaque DTLS timeout. It does
   * not say who: the bridge offers no name to blame, and it does not distinguish Flux
   * from another application.
   */
  streaming: boolean;
}

/**
 * Everything the page knows about the bridge, pushed on change rather than at the
 * state rate.
 *
 * Nothing here is optional. Every null is a documented value - no bridge paired, no
 * window running - rather than an absence, which is the opposite of how
 * `DiagnosticsData` and `DeviceData` treat theirs.
 */
export interface HueData {
  state: HueState;
  bridge: HueBridgeData | null;
  discovered: HueBridgeData[];
  areas: HueAreaData[];
  selectedAreaId: string | null;
  /** Non-null exactly when `state` is `pairing`. */
  pairingSecondsRemaining: number | null;
  /** Signed milliseconds; positive sends light early. Always a real number. */
  latencyOffsetMs: number;
  engine: HueEngineData;
  message: string | null;
}

export interface EchoData {
  t: number;
}

export interface ErrorData {
  code: string;
  message: string;
}

/**
 * A complete statement of what this client wants, never a delta.
 *
 * Both fields go out every time on purpose: there is no unsubscribe message, so
 * a client and a server can never end up disagreeing about what is switched on.
 */
export interface SubscribeData {
  spectrum: boolean;
  deviceLevels: boolean;
}

export interface SelectSourceData {
  source: string;
}

export interface SelectAudioDeviceData {
  deviceId: string;
}

export interface SetBpmData {
  /** Null clears the manual tempo, which is not the same as setting it to zero. */
  bpm: number | null;
}

export interface PairBridgeData {
  bridgeId: string;
}

export interface SelectAreaData {
  areaId: string;
}

export interface SetLatencyOffsetData {
  /** -500 to 500. Outside that is refused rather than clamped. */
  offsetMs: number;
}

/**
 * One complete statement of how the lights should look, never a patch.
 *
 * Every field goes out every time for the reason `subscribe` sends both of its flags:
 * a message carrying a palette alone would rely on the server to supply the rest, and
 * two phones doing that at once produce a look neither of them chose.
 */
export type SetLightSettingsData = HueEngineData;

/**
 * None of these is answered with a reply.
 *
 * Each is confirmed by the broadcast that follows - a `hue` frame for the bridge
 * messages - because pairing is normally watched on two screens at once, and a reply
 * addressed to the phone would leave the laptop showing a state that had already
 * moved. So a button stays pending until the feed agrees with it.
 */
export type ClientMessage =
  | { type: 'ping'; data: EchoData }
  | { type: 'subscribe'; data: SubscribeData }
  | { type: 'selectSource'; data: SelectSourceData }
  | { type: 'selectAudioDevice'; data: SelectAudioDeviceData }
  // A tap carries no payload: the server times it on arrival, because the two
  // clocks are unrelated and a timestamp from this one would place the beat
  // somewhere arbitrary.
  | { type: 'tap'; data: Record<string, never> }
  | { type: 'setBpm'; data: SetBpmData }
  | { type: 'discoverBridges'; data: Record<string, never> }
  | { type: 'pairBridge'; data: PairBridgeData }
  | { type: 'selectArea'; data: SelectAreaData }
  // Confirmed by state.light rather than by a hue frame: it is the only field that
  // says whether Flux is streaming, and it is the one that carries the rate.
  | { type: 'startStream'; data: Record<string, never> }
  | { type: 'stopStream'; data: Record<string, never> }
  | { type: 'forgetBridge'; data: Record<string, never> }
  | { type: 'setLatencyOffset'; data: SetLatencyOffsetData }
  | { type: 'setLightSettings'; data: SetLightSettingsData };

/**
 * An inbound message decoded only as far as its type tag.
 *
 * The payload stays `unknown` because it came off a socket: the protocol says a
 * client ignores types it does not recognise, and a union that assumed every
 * message were known would make that rule unexpressible.
 */
export interface RawMessage {
  type: string;
  data: unknown;
}

/** Reads a frame, or returns null if it is not a message at all. */
export function parseMessage(text: string): RawMessage | null {
  let parsed: unknown;

  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) {
    return null;
  }

  const candidate = parsed as { type?: unknown; data?: unknown };
  if (typeof candidate.type !== 'string') {
    return null;
  }

  return { type: candidate.type, data: candidate.data };
}
