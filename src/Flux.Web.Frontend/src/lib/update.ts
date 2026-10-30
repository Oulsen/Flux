import type { UpdateData } from './protocol.ts';

/**
 * What the drawer says about updates, or null for nothing at all.
 *
 * Nothing when the server sent no update frame or cannot update itself. A development
 * build was never installed, and a control there would only ever say so.
 */
export function updateLabel(update: UpdateData | null): string | null {
  switch (update?.status) {
    case 'checking':
      return 'checking for a newer flux';
    case 'current':
      return 'flux is up to date';
    case 'ready':
      return update.version === null ? 'a newer flux is ready' : `flux ${update.version} is ready`;
    case 'failed':
      return 'could not check for updates';
    default:
      return null;
  }
}

/** Whether the button applies a downloaded version rather than asking for one. */
export function canRestartToUpdate(update: UpdateData | null): boolean {
  return update?.status === 'ready';
}

/** Whether a check is worth offering now: not while one runs, and not once one is ready. */
export function canCheckForUpdate(update: UpdateData | null): boolean {
  return update?.status === 'current' || update?.status === 'failed';
}
