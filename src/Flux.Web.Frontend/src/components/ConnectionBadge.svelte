<script lang="ts">
  import type { ConnectionState } from '../lib/socket.ts';
  import type { SourceState } from '../lib/protocol.ts';

  interface Props {
    connection: ConnectionState;
    sourceState: SourceState;
    /** The active source's name, because the badge reports on whichever one it is. */
    source: string;
  }

  const { connection, sourceState, source }: Props = $props();

  /*
   * The connection to Flux outranks the state of the sync source, because a
   * dropped socket makes every other reading on the page a memory. Only once
   * the page is genuinely connected does what the source is doing become the
   * headline.
   *
   * The source is named from the wire rather than written into the text. Saying
   * "waiting for link" while audio is selected is not a wording problem, it is
   * the badge reporting on the wrong machine.
   */
  const label = $derived.by(() => {
    if (connection === 'connecting') {
      return { text: 'connecting', tone: 'dead' };
    }

    if (connection === 'closed') {
      return { text: 'reconnecting', tone: 'alarm' };
    }

    switch (sourceState) {
      case 'live':
        return { text: 'live', tone: 'live' };
      case 'quiet':
        return { text: `${source} quiet`, tone: 'quiet' };
      default:
        return { text: `waiting for ${source}`, tone: 'dead' };
    }
  });
</script>

<span class="badge {label.tone}">
  <span class="dot"></span>
  {label.text}
</span>

<style>
  .badge {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.25rem 0.7rem;
    border: 1px solid var(--edge);
    border-radius: 999px;
    font-size: 0.8rem;
    letter-spacing: 0.06em;
    text-transform: uppercase;
    color: var(--ink-dim);
    white-space: nowrap;
  }

  .dot {
    width: 0.5rem;
    height: 0.5rem;
    border-radius: 50%;
    background: var(--dead);
  }

  .live .dot {
    background: var(--live);
  }

  .quiet .dot {
    background: var(--quiet);
  }

  .alarm .dot {
    background: var(--alarm);
  }

  .live {
    color: var(--live);
  }

  .quiet {
    color: var(--quiet);
  }

  .alarm {
    color: var(--alarm);
  }
</style>
