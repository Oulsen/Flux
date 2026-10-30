<script lang="ts">
  import type { UpdateData } from '../lib/protocol.ts';
  import { canCheckForUpdate, canRestartToUpdate, updateLabel } from '../lib/update.ts';

  interface Props {
    /** The last update frame, or null when the server sent none. */
    update: UpdateData | null;
    check: () => void;
    restart: () => void;
  }

  const { update, check, restart }: Props = $props();

  const label = $derived(updateLabel(update));
  const ready = $derived(canRestartToUpdate(update));
</script>

<!--
  Beside the version line, because that is where somebody looks to ask which Flux this
  is. Nothing at all for a server that cannot update itself.

  The restart says what it costs: every page drops and the lights stop until the new
  version is up, which is a choice for between sets and not for the middle of one.
-->
{#if label !== null}
  <div class="update">
    <p class="status" class:ready>{label}</p>
    {#if ready}
      <button type="button" class="action" onclick={restart}>restart to update</button>
      <p class="note">The lights stop for a few seconds while Flux restarts.</p>
    {:else}
      <button type="button" class="action quiet" disabled={!canCheckForUpdate(update)} onclick={check}>
        check now
      </button>
    {/if}
  </div>
{/if}

<style>
  .update {
    display: grid;
    justify-items: center;
    gap: 0.4rem;
  }

  .status,
  .note {
    margin: 0;
    font-size: var(--text-xs);
    color: var(--ink-dim);
    text-align: center;
  }

  .status {
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
  }

  .ready {
    color: var(--ink);
  }

  .action {
    min-height: var(--control);
    padding: 0.5rem 0.7rem;
    background: none;
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink);
    font: inherit;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    cursor: pointer;
  }

  .action:disabled {
    cursor: default;
    opacity: 0.4;
  }

  .quiet {
    color: var(--ink-dim);
  }
</style>
