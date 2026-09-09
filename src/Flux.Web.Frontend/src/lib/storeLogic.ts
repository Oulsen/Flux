/**
 * The store's decisions, kept apart from the store's state.
 *
 * `FluxStore` is `$state` fields and a dispatch switch, which is what a store
 * should be and is also unreachable without a browser. The rules that are worth
 * being sure about live here as plain functions over plain values. They earned
 * the separation: the retirement rule below is the fix for a defect that only
 * ever appeared on a phone on bad Wi-Fi, where one dropped frame left the lit dot
 * a whole beat behind the music until the next beat arrived.
 */

/** The part of a held beat frame that decides whether it is still current. */
export interface HeldBeat {
  index: number;
}

/** The part of a state snapshot that answers the same question. */
export interface GridPosition {
  beatIndex: number;
}

/**
 * Whether a held beat frame has stopped describing where the grid is.
 *
 * Beats and snapshots arrive on separate pumps, so the two disagree constantly
 * and most of that disagreement is ordinary. Exactly one beat behind is the plain
 * race between the pumps, and there the beat frame is the fresher fact and stays.
 * A snapshot that has moved *past* the held frame means its successor was dropped
 * or is stuck behind a bad connection; more than one behind means a tempo change
 * renumbered the grid underneath it. Both retire the frame, and the readout falls
 * back to the snapshot within one state frame rather than waiting for a beat that
 * may never come.
 *
 * A source switch retires it immediately and without comparing anything: the
 * numbering belongs to the source that issued it, so the arithmetic above is not
 * merely wrong across a switch, it is meaningless.
 *
 * Retiring never fakes a flash. A snapshot places a position, not an instant.
 */
export function retiresHeldBeat(
  held: HeldBeat | null,
  grid: GridPosition | null,
  sourceChanged: boolean,
): boolean {
  if (held === null) {
    return false;
  }

  // A null grid is the source having never spoken, so there is no position to
  // hold at all - leaving one lit under a "waiting" label is the one thing a
  // beat readout must not do.
  if (sourceChanged || grid === null) {
    return true;
  }

  return grid.beatIndex > held.index || grid.beatIndex < held.index - 1;
}

/**
 * What a control still shows as pending once the server has stated the truth.
 *
 * A selection is a request, not a fact: the server owns which source is active
 * and which device is open, and it may answer with something other than what was
 * asked. So the control shows the pending value until a frame confirms it, and
 * then shows the server's value forever after. Resolving on equality rather than
 * on a reply is what makes it survive the case where somebody else, or a config
 * default, moved it in the meantime.
 */
export function resolvePending<T>(pending: T | null, confirmed: T): T | null {
  if (pending === null || pending === confirmed) {
    return null;
  }

  return pending;
}

/**
 * The same question for a record whose fields are asked for one at a time.
 *
 * The light engine's five settings travel as one complete statement, because that is
 * what the message is, but they are changed by five separate controls and by more than
 * one phone. So the outstanding set is per field: a page dragging brightness has that
 * field unconfirmed while a palette somebody else chose is already the truth, and
 * resolving the whole record at once would either discard the drag or keep overwriting
 * their palette with the one this page last heard.
 *
 * Resolving on equality rather than on a reply, exactly as {@link resolvePending} does,
 * so a field that arrives at the requested value by any route stops being pending.
 */
export function resolvePendingFields<T extends object>(pending: Partial<T>, confirmed: T): Partial<T> {
  const outstanding: Partial<T> = {};

  for (const key of Object.keys(pending) as (keyof T)[]) {
    if (pending[key] !== confirmed[key]) {
      outstanding[key] = pending[key];
    }
  }

  return outstanding;
}

/**
 * Whether an error frame ends a pending selection.
 *
 * `rejected` means the server understood the request and declined it, which is
 * the one outcome no later state frame will ever confirm, so nothing else would
 * clear the control. The protocol carries no correlation between a request and
 * its error, so this clears whatever was pending rather than the request that
 * caused it. With one pair of hands on one control that is the same thing, and
 * the alternative is a button left lit for a request that is already dead.
 */
export function clearsPendingSelection(code: string): boolean {
  return code === 'rejected';
}
