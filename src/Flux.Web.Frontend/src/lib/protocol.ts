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

export interface HelloData {
  protocol: number;
  app: string;
  quantum: number;
  stateHz: number;
}

export interface GridData {
  bpm: number;
  beat: number;
  beatIndex: number;
  positionInBar: number;
  barAligned: boolean;
}

export interface DiagnosticsData {
  packets: number;
  reanchorMaxBeats: number;
}

export interface StateData {
  source: string;
  sourceState: SourceState;
  /** Null exactly when nothing has been received yet. Never render it as zero. */
  grid: GridData | null;
  diagnostics: DiagnosticsData;
}

export interface BeatData {
  index: number;
  positionInBar: number;
  barAligned: boolean;
  bpm: number;
}

export interface EchoData {
  t: number;
}

export interface ErrorData {
  code: string;
  message: string;
}

export type ClientMessage = { type: 'ping'; data: EchoData };

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
