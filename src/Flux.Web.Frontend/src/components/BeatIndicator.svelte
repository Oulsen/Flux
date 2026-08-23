<script lang="ts">
  interface Props {
    /** Beats per bar, as the sync source reports it. Never assumed to be four. */
    quantum: number;
    /** 1-based, or 0 when nothing is playing. */
    position: number;
    /** Increments once per beat, which is what retriggers the flash. */
    tick: number;
    /** Whether the bar position has been anchored to the music. False for all of Phase 1. */
    aligned: boolean;
  }

  const { quantum, position, tick, aligned }: Props = $props();

  const slots = $derived(Array.from({ length: Math.max(1, quantum) }, (_, index) => index + 1));
</script>

<div class="indicator">
  <!--
    Keyed on the beat counter rather than on the position: at a quantum of one,
    or through a repeated position, the position alone never changes and the
    flash would fire once and then stop. Re-creating the row restarts the
    animation, which is the whole point.
  -->
  {#key tick}
    <div class="slots">
      {#each slots as slot (slot)}
        <span class="slot" class:lit={slot === position}></span>
      {/each}
    </div>
  {/key}

  <p class="caption">
    {#if aligned}
      bar position anchored
    {:else}
      counting beats &middot; bar position not anchored yet
    {/if}
  </p>
</div>

<style>
  .indicator {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.9rem;
  }

  .slots {
    display: flex;
    gap: 0.9rem;
  }

  .slot {
    width: 1.1rem;
    height: 1.1rem;
    border: 1px solid var(--edge);
    border-radius: 50%;
    background: transparent;
  }

  .lit {
    background: var(--accent);
    border-color: var(--accent);
    animation: strike 320ms ease-out;
  }

  @keyframes strike {
    from {
      transform: scale(1.6);
      box-shadow: 0 0 1.6rem var(--accent);
    }

    to {
      transform: scale(1);
      box-shadow: 0 0 0 transparent;
    }
  }

  /* A pulsing dot is decoration for anyone who asked the browser to stop
     animating; the lit position still says everything it needs to. */
  @media (prefers-reduced-motion: reduce) {
    .lit {
      animation: none;
    }
  }

  .caption {
    margin: 0;
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    color: var(--ink-faint);
    text-align: center;
  }
</style>
