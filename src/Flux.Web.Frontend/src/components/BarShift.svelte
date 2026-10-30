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
  <div class="buttons">
    <button type="button" aria-label="Move bar one a beat earlier" onclick={() => shift(-1)}>&lsaquo; beat</button>
    <button type="button" aria-label="Move bar one a beat later" onclick={() => shift(1)}>beat &rsaquo;</button>
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
    gap: var(--space-2);
  }

  button {
    /* Sized as the anchor-only button beside the tempo box is: a thumb target that is
       visibly a correction rather than the page's main control. */
    min-width: var(--shift-size, 5rem);
    height: var(--control-lg);
    padding: 0 0.9rem;
    border: 1px solid var(--accent);
    border-radius: var(--radius);

    /* Opaque for the reason the anchor is: on the stage it sits over the canvas. */
    background: var(--panel);
    color: var(--accent);
    font: inherit;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
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
