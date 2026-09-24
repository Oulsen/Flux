<script lang="ts">
  /*
   * The strobe, for as long as a finger is on it.
   *
   * A hold rather than a toggle, which is what makes it the one to reach for mid-set: it
   * is pressed through a build-up and let go on the drop, and it cannot be left on by
   * accident. The switched-on strobe lives in the drawer for the rarer case of wanting it
   * to run hands-free.
   *
   * Every way a press can end is wired to the same release - the finger lifting, the
   * browser cancelling the pointer, capture being lost, the page losing focus or being
   * hidden, the component going away. `StrobeHold` makes a second release a no-op, so
   * nothing here has to know which of them fired first, and the server's lease covers
   * the case where none of them does.
   */
  import { onDestroy } from 'svelte';
  import { StrobeHold } from '../lib/strobe.ts';

  interface Props {
    /** Says whether the finger is down. Called again every renewal while it stays down. */
    hold: (held: boolean) => void;
  }

  const { hold }: Props = $props();

  const strobe = new StrobeHold((held) => hold(held));

  let down = $state(false);

  function start(): void {
    down = true;
    strobe.press();
  }

  function stop(): void {
    down = false;
    strobe.release();
  }

  /*
   * Captured, so the release arrives here even when the finger slides off the circle -
   * which on a phone in a dark booth is most presses. Without capture a slide-off is a
   * pointerleave and never a pointerup, and the strobe would run until the lease ran out.
   */
  function pressed(event: PointerEvent): void {
    event.preventDefault();
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    start();
  }

  // Key repeat is ignored rather than treated as a renewal: the renewal has its own
  // clock, and the key's repeat rate is the operating system's, not the lease's.
  function keyed(event: KeyboardEvent): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();

      if (!event.repeat) {
        start();
      }
    }
  }

  function unkeyed(event: KeyboardEvent): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      stop();
    }
  }

  function visibility(): void {
    if (document.visibilityState === 'hidden') {
      stop();
    }
  }

  onDestroy(stop);
</script>

<svelte:document onvisibilitychange={visibility} />

<button
  type="button"
  class="strobe"
  class:held={down}
  aria-pressed={down}
  onpointerdown={pressed}
  onpointerup={stop}
  onpointercancel={stop}
  onlostpointercapture={stop}
  onkeydown={keyed}
  onkeyup={unkeyed}
  onblur={stop}
  oncontextmenu={(event) => event.preventDefault()}
>
  strobe
</button>

<style>
  .strobe {
    /* The anchor's size, read from the same variable, so the two circles on the stage
       are one family rather than a button and its smaller cousin. */
    width: var(--anchor-size, 5rem);
    height: var(--anchor-size, 5rem);
    flex: none;
    border: 1px solid var(--edge);
    border-radius: 50%;
    background: var(--panel);
    color: var(--ink-dim);
    font: inherit;
    font-size: calc(var(--anchor-size, 5rem) * 0.19);
    padding: 0;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    cursor: pointer;

    /* None rather than manipulation, unlike the anchor: this is held, not tapped, and a
       held finger that drifts must not be taken by the browser as the start of a scroll
       - that fires pointercancel and ends the strobe mid-build. The context menu a long
       press would open is refused on the element for the same reason. */
    touch-action: none;
    -webkit-user-select: none;
    user-select: none;
    -webkit-touch-callout: none;
  }

  /* White while held, because that is what the room is doing. */
  .held {
    background: #fff;
    border-color: #fff;
    color: var(--surface);
  }
</style>
