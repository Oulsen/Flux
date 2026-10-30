<script lang="ts">
  /*
   * The downbeat correction under the rekordbox source, in place of the tap.
   *
   * Under rekordbox bar one is the track's own, read from its beatgrid, so a tap would
   * be a second opinion that rekordbox overrules at the next seek. What can be wrong is
   * the grid itself, a beat early or late, and the fix for that is relative: one beat
   * later than rekordbox says, for as long as this track plays. That is what these
   * send, and the server keeps it through seeks until the track changes.
   *
   * Plain clicks rather than the anchor's press-on-pointerdown: the anchor says "now"
   * and every millisecond of a held finger is phase error, while these say "one
   * beat" and mean the same thing whenever they land.
   */

  interface Props {
    shift: (beats: number) => void;
    /** Whether to say what the buttons do. The drawer does; the stage has no room. */
    captions?: boolean;
    /**
     * What rekordbox is doing, in the source's own words, shown with the captions. Without
     * it a rekordbox that is connected and stopped reads as "waiting" in the selector,
     * which looks exactly like one that is not connected at all.
     */
    status?: string | null;
  }

  const { shift, captions = false, status = null }: Props = $props();
</script>

<div class="bar-shift">
  <!-- The label sits between the arrows rather than on them. "beat" on a button read as
       a seek or a tempo nudge; what moves is the downbeat, and the arrows say which way. -->
  <div class="buttons" role="group" aria-label="Downbeat">
    <button
      type="button"
      aria-label="Move the downbeat a beat earlier"
      title="Move the downbeat a beat earlier"
      onclick={() => shift(-1)}>&lsaquo;</button
    >
    <span class="label">downbeat</span>
    <button
      type="button"
      aria-label="Move the downbeat a beat later"
      title="Move the downbeat a beat later"
      onclick={() => shift(1)}>&rsaquo;</button
    >
  </div>

  {#if captions}
    {#if status}
      <p class="status">rekordbox: {status}</p>
    {/if}
    <p class="caption">bar one comes from the rekordbox beatgrid</p>
    <p class="caption">if the grid's downbeat is off, move it a beat; it holds until the next track</p>
  {/if}
</div>

<style>
  .bar-shift {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    align-items: start;
  }

  .buttons {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  .label {
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink);
  }

  button {
    /* Square and thumb-sized: the arrow is the whole label, and the word between the
       two buttons says what they move. */
    width: var(--control-lg);
    height: var(--control-lg);
    padding: 0;
    border: 1px solid var(--accent);
    border-radius: var(--radius);

    /* Opaque for the reason the anchor is: on the stage it sits over the canvas. */
    background: var(--panel);
    color: var(--accent);
    font: inherit;
    font-size: var(--text-lg);
    cursor: pointer;
    touch-action: manipulation;
    -webkit-user-select: none;
    user-select: none;
  }

  button:active {
    background: var(--accent);
    color: var(--surface);
  }

  .status {
    margin: 0;
    font-size: var(--text-sm);
    color: var(--ink-dim);
  }

  .caption {
    margin: 0;
    font-size: var(--text-sm);
    letter-spacing: 0.06em;
    color: var(--ink-dim);
  }
</style>
