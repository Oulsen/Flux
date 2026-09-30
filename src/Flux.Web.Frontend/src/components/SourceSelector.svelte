<script lang="ts">
  import type { SourceReportData } from '../lib/protocol.ts';

  interface Props {
    /** Every source the running build has. Empty until the first snapshot. */
    sources: SourceReportData[];
    /** The one the server says is driving the grid. */
    active: string;
    /** The one the user asked for, while the server has yet to confirm it. */
    pending: string | null;
    select: (source: string) => void;
  }

  const { sources, active, pending, select }: Props = $props();

  /*
   * A source's wire name is displayed rather than looked up in a table of
   * friendly names. A build newer than this page can carry a source the page has
   * never heard of, and a lookup with no entry for it would either hide it or
   * label it blank - both worse than showing the name the server used.
   */
  function label(name: string): string {
    return name.charAt(0).toUpperCase() + name.slice(1);
  }

  function bar(confidence: number): string {
    if (!Number.isFinite(confidence)) {
      return '0%';
    }

    return `${Math.min(1, Math.max(0, confidence)) * 100}%`;
  }
</script>

<div class="selector" role="group" aria-label="Sync source">
  {#if sources.length === 0}
    <p class="empty">no sources yet</p>
  {:else}
    {#each sources as source (source.name)}
      <button
        type="button"
        class="option {source.state}"
        class:active={source.name === active}
        class:pending={source.name === pending}
        aria-pressed={source.name === active}
        onclick={() => select(source.name)}
      >
        <span class="head">
          <span class="dot"></span>
          <span class="name">{label(source.name)}</span>
        </span>

        <!-- Confidence is the source's own opinion of itself, which is the whole
             point of showing every source and not only the active one: it is how
             you tell that audio has locked before switching to it. -->
        <span class="confidence"><span class="fill" style="width: {bar(source.confidence)}"></span></span>
      </button>
    {/each}
  {/if}
</div>

<style>
  /* A grid that wraps rather than a row that cannot: four names side by side need
     about 380px, the drawer leaves about 350, and the last button - rekordbox, the one
     usually active - was cut off at the drawer's edge. Two by two in the drawer, four
     across wherever there is room for them. */
  .selector {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(7.5rem, 1fr));
    gap: 0.5rem;
  }

  .empty {
    margin: 0;
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    color: var(--ink-faint);
  }

  .option {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    /* Thumb-sized: this gets pressed on a phone in the dark. */
    min-height: 3rem;
    padding: 0.6rem 0.7rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: 0.85rem;
    cursor: pointer;
  }

  .head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }

  .dot {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--dead);
    flex: none;
  }

  .live .dot {
    background: var(--live);
  }

  .quiet .dot {
    background: var(--quiet);
  }

  .name {
    letter-spacing: 0.06em;
  }

  .active {
    border-color: var(--accent);
    color: var(--ink);
  }

  /* The pressed button does not move to the active style: the server decides
     what is active, and a control that jumped on the press would claim a switch
     that can still be declined. */
  .pending {
    border-style: dashed;
    border-color: var(--ink-faint);
    opacity: 0.7;
  }

  .confidence {
    display: block;
    height: 2px;
    background: var(--edge);
    border-radius: 1px;
    overflow: hidden;
  }

  .fill {
    display: block;
    height: 100%;
    background: var(--ink-faint);
  }

  .active .fill {
    background: var(--accent);
  }
</style>
