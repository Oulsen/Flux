/**
 * Which health counter belongs to which source.
 *
 * Separate from the component because it is the part of that panel that can be
 * wrong rather than merely plain. The server omits a field that does not apply
 * instead of sending a zero, so a page asking for the wrong one renders a blank
 * under a confident label, which looks exactly like a counter sitting at
 * nothing. Reading `packets` while audio is driving is that mistake, and it is
 * the one this module exists to make testable.
 *
 * The diagnostics table in `docs/protocol.md` is the authority for the mapping
 * below. When a source gains a counter there, it gains a row here.
 */

import { formatAgo, formatCount, formatRate, UNKNOWN } from './format.ts';
import type { DiagnosticsData } from './protocol.ts';

/** One labelled counter, already formatted for the panel. */
export interface DiagnosticRow {
  label: string;
  value: string;
}

/**
 * Whether the tempo estimate has settled.
 *
 * Absent and false are deliberately not the same answer: a source with no
 * opinion on the matter has to read as a blank, while one that has an opinion
 * and says "not yet" is reporting a real state of the detector.
 */
function formatLock(locked: boolean | undefined): string {
  if (locked === undefined) {
    return UNKNOWN;
  }

  return locked ? 'locked' : 'settling';
}

/**
 * The counters the named source keeps, in reading order.
 *
 * Only the source's own: `reanchorMaxBeats` is the grid's metric and is asked of
 * every source, so it is rendered outside this list and never repeated in it.
 */
export function sourceRows(source: string, diagnostics: DiagnosticsData): DiagnosticRow[] {
  switch (source) {
    case 'link':
      return [{ label: 'packets', value: formatCount(diagnostics.packets) }];

    case 'audio':
      return [
        { label: 'fps', value: formatRate(diagnostics.fps) },
        { label: 'beats', value: formatCount(diagnostics.beatsDetected) },
        { label: 'bpm lock', value: formatLock(diagnostics.bpmLocked) },
        { label: 'last beat', value: formatAgo(diagnostics.lastBeatAgoMs) },
      ];

    case 'manual':
      return [
        { label: 'taps', value: formatCount(diagnostics.taps) },
        { label: 'last tap', value: formatAgo(diagnostics.lastTapAgoMs) },
      ];

    case 'rekordbox':
      // The reason is words rather than a counter, because each way of not reading
      // rekordbox has a different fix and a number would not say which. Absent means
      // it is following a playing deck, which is the one state that needs no advice.
      return [
        { label: 'deck', value: formatCount(diagnostics.deck) },
        { label: 'status', value: diagnostics.detail ?? 'following' },
      ];

    default:
      // A source this page has never heard of is the protocol's documented
      // degradation rather than an error. It contributes no rows, and
      // everything the grid owns still renders beside the gap.
      return [];
  }
}
