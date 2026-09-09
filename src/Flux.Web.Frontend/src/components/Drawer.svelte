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
   */

  import type { Snippet } from 'svelte';

  interface Props {
    open: boolean;
    close: () => void;
    children: Snippet;
  }

  const { open, close, children }: Props = $props();

  function onKeydown(event: KeyboardEvent): void {
    if (open && event.key === 'Escape') {
      close();
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

{#if open}
  <!-- A button rather than a bare element with a click handler, so that dismissing the
       drawer is reachable without a pointer and announces itself as an action. -->
  <button type="button" class="backdrop" aria-label="Close settings" onclick={close}></button>
{/if}

<aside class="drawer" class:open aria-label="Settings" inert={!open}>
  <div class="scroller">
    {@render children()}
  </div>
</aside>

<style>
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 2;
    padding: 0;
    border: none;
    cursor: pointer;

    /* Barely there. The stage is the reason the drawer is an overlay rather than a
       column, so dimming it to unreadability would give back exactly what the layout
       was for: a colour being tuned has to stay visible while it is tuned. */
    background: rgb(0 0 0 / 35%);
  }

  .drawer {
    position: fixed;
    top: 0;
    right: 0;
    z-index: 3;
    display: flex;
    flex-direction: column;
    width: min(26rem, 100%);
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

  .scroller {
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
