<script lang="ts">
  interface Props {
    /** Beats per bar, as the sync source reports it. Never assumed to be four. */
    quantum: number;
    /** 1-based, or 0 when nothing is playing. */
    position: number;
    /** Increments once per beat, which is what retriggers the flash. */
    tick: number;
  }

  // Whether the bar is anchored, and by what, is `AlignmentBadge`'s to say. Two
  // components rendering the same fact is how they end up disagreeing about it.
  const { quantum, position, tick }: Props = $props();

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

  /* prefers-reduced-motion is deliberately not honoured here. The flash is the
     page's core function - it is the only element that can show the beat
     instant - and Windows raises the flag machine-wide from a generic
     "animation effects" toggle, muting a beat visualizer the user opened on
     purpose. Anyone who genuinely wants a still page has the moving dot row
     saying the same thing. */
</style>
