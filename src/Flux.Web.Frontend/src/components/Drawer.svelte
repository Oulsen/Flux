<script lang="ts">
  /*
   * Everything that is adjusted rather than watched, kept off the stage.
   *
   * Mounted whether or not it is open, and slid out of the way when it is not. The
   * alternative - rendering it only while open - would tear down and rebuild every
   * panel inside on each visit, which for the bridge panel means losing a discovery
   * that is halfway through and for the pickers means a flash of "waiting" on a list
   * that was already correct. `inert` is what keeps a closed drawer out of the tab
   * order and away from a screen reader, since off-screen alone does neither.
   *
   * Not modal, corrected on 2026-09-11 after using it. There was a full-screen backdrop
   * here that dismissed the drawer when tapped, and being full-screen it also covered
   * the stage - so pressing anchor with the settings open closed the drawer and threw
   * the press away, which is the one control that gets pressed while something else is
   * being adjusted. The comment justifying the backdrop argued it kept dismissal
   * reachable on a phone, and that argument does not survive the drawer being full-width
   * on a phone: there is no backdrop to reach there. What dismisses this is the close
   * button, Escape, the toggle that opened it, and on the desk a press on the stage
   * beside it.
   *
   * That last one is still not a backdrop, and the difference is the whole point: a
   * backdrop is an element over the stage that receives every press, while the page
   * listens for presses that reach the stage on their own. The stage controls take the
   * pointer back from the layer above the picture, so a press on anchor lands on anchor
   * and never on the stage - it acts, and the drawer stays open beside it.
   *
   * Focus follows the drawer in both directions. Opening leaves focus on whatever
   * opened it, which is behind the drawer, so it moves to the selected tab. Closing
   * makes the drawer `inert`, which would drop focus held inside it onto the page body,
   * so it goes back to where it came from.
   */

  import type { Snippet } from 'svelte';

  interface Props {
    open: boolean;
    close: () => void;

    /**
     * What stays put above the scrolling part: the heading and the tabs. Outside the
     * scroller so that switching tabs never means scrolling back up to find them.
     */
    header?: Snippet;

    /**
     * Changes whenever the content is a different page of the drawer, and puts the
     * scroller back at the top when it does. A tab opened halfway down, at the offset
     * the previous tab was left at, looks like a tab with its first panels missing.
     */
    scrollKey?: unknown;

    children: Snippet;
  }

  const { open, close, header, scrollKey, children }: Props = $props();

  let scroller: HTMLDivElement | undefined = $state();
  let aside: HTMLElement | undefined = $state();

  /* Where focus was when the drawer opened, to hand it back to on close. */
  let opener: HTMLElement | null = null;
  let returnFocus = false;

  // Before the DOM changes, because once `inert` is set there is no focus left inside
  // the drawer to ask about.
  $effect.pre(() => {
    if (open) {
      opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    } else {
      returnFocus = aside?.contains(document.activeElement) ?? false;
    }
  });

  $effect(() => {
    if (open) {
      const target =
        aside?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]') ?? aside?.querySelector<HTMLElement>('button');
      // Without scrolling: the drawer is still sliding in, and a fixed element pulled
      // into view mid-transition can nudge the page under it.
      target?.focus({ preventScroll: true });
    } else if (returnFocus) {
      returnFocus = false;
      opener?.focus();
    }
  });

  $effect(() => {
    // Read for the dependency: the value itself says nothing, only that it moved.
    void scrollKey;
    scroller?.scrollTo({ top: 0 });
  });

  function onKeydown(event: KeyboardEvent): void {
    // An open popup inside the drawer takes the Escape for itself, and the browser closes
    // it after this has run: closing the drawer as well would take two steps back for one.
    // The stage's look panel takes it the same way, by marking the event handled.
    if (open && event.key === 'Escape' && !event.defaultPrevented && document.querySelector(':popover-open') === null) {
      close();
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

<aside class="drawer" class:open aria-label="Settings" inert={!open} bind:this={aside}>
  {#if header}
    <div class="header">
      {@render header()}
    </div>
  {/if}

  <div class="scroller" bind:this={scroller}>
    {@render children()}
  </div>
</aside>

<style>
  .drawer {
    position: fixed;
    top: 0;
    right: 0;
    z-index: 3;
    display: flex;
    flex-direction: column;
    width: min(var(--drawer-width), 100%);
    height: 100%;
    background: var(--surface);
    border-left: 1px solid var(--edge);
    transform: translateX(100%);
    transition: transform 180ms ease;
  }

  .drawer.open {
    transform: translateX(0);
  }

  /* The stage's own motion is the page working; this is furniture moving, and the two
     deserve different answers. `BeatIndicator` argues at length for ignoring the
     preference, and every word of it is about the beat flash rather than about a panel
     sliding in from the side. */
  @media (prefers-reduced-motion: reduce) {
    .drawer {
      transition: none;
    }
  }

  .header {
    display: flex;
    flex-direction: column;
    gap: var(--space-3);
    padding: var(--gap) var(--gap) 0.75rem;
    border-bottom: 1px solid var(--edge);
  }

  .scroller {
    /* The rest of the drawer's height, and no more: without the zero basis a flex
       child sizes to its content and the drawer, not the scroller, overflows. */
    flex: 1 1 0;
    min-height: 0;
    display: flex;
    flex-direction: column;
    gap: var(--gap);
    padding: var(--gap);
    overflow-y: auto;

    /* Kept inside the drawer rather than let loose on the page behind it, which is
       what a phone does by default once the panel runs past the fold. */
    overscroll-behavior: contain;

    /* The notch and the home indicator both live in this rectangle on a phone held in
       landscape, which is how the booth holds one. */
    padding-bottom: max(var(--gap), env(safe-area-inset-bottom));
  }
</style>
