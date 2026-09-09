<script lang="ts">
  interface Props {
    /** Unique, because the label points at the input by id. */
    id: string;
    /** What the control is called, shown small and upper case beside its value. */
    name: string;
    min: number;
    max: number;
    step: number;
    /** The value the server says is in force. */
    value: number;
    /** The value in words. Every setting here means something a bare number does not say. */
    format: (value: number) => string;
    /** Why the setting exists and how to judge it. Omitted where the label says enough. */
    hint?: string;
    /** A reading derived from the value, shown under the slider when there is one. */
    note?: string | null;
    /** Whether that note is a warning rather than an observation. */
    warn?: boolean;
    /**
     * Resolves a dragged value to one worth having, where part of the range is not.
     *
     * Applied to the thumb as well as to what is sent, which is the part that matters:
     * the drag owns the thumb until the server agrees with it, so a value snapped on
     * the way out but not on the way in would never agree and the thumb would stick.
     */
    snap?: (value: number) => number;
    commit: (value: number) => void;
  }

  const {
    id,
    name,
    min,
    max,
    step,
    value,
    format,
    hint,
    note = null,
    warn = false,
    snap = (raw: number) => raw,
    commit,
  }: Props = $props();

  /**
   * How often a drag in progress is allowed to reach the server.
   *
   * A pointer move produces an input event, and without this each one would be a
   * socket message, a settings file written, and a `hue` frame carrying the whole
   * entertainment area list broadcast to every open page. Ten a second is far finer
   * than an eye judging light against a beat can resolve.
   */
  const SEND_INTERVAL_MS = 100;

  /*
   * Where the thumb is, which is not always where the server is.
   *
   * A slider bound straight to the server's value fights the finger dragging it: the
   * value only catches up on the throttle, so every frame arriving mid-drag would snap
   * the thumb back. The drag owns the position instead, and hands it back the moment
   * the server agrees - which is what keeps a value set from a second phone able to
   * move this slider at all.
   */
  let dragged = $state<number | null>(null);

  /** Not reactive: nothing renders from it, it only paces the sending. */
  let lastSent = 0;

  const position = $derived(dragged ?? value);

  $effect(() => {
    if (dragged !== null && dragged === value) {
      dragged = null;
    }
  });

  function drag(event: Event): void {
    const next = snap(Number((event.currentTarget as HTMLInputElement).value));
    dragged = next;

    const now = Date.now();
    if (now - lastSent >= SEND_INTERVAL_MS) {
      lastSent = now;
      commit(next);
    }
  }

  /** The release, which has to land whatever the throttle skipped on the way. */
  function release(event: Event): void {
    const next = snap(Number((event.currentTarget as HTMLInputElement).value));

    dragged = next;
    lastSent = Date.now();
    commit(next);
  }
</script>

<!--
  One control shared by every number here, because every one of them is the same thing:
  a range whose value lives on the server, tuned by somebody watching the lights rather
  than watching the number. The part worth not having two copies of is the reconciliation
  between a finger and a feed.
-->
<div class="setting">
  <label for={id}>
    <span class="name">{name}</span>
    <span class="value">{format(position)}</span>
  </label>

  <input {id} type="range" {min} {max} {step} value={position} oninput={drag} onchange={release} />

  {#if note !== null}
    <!-- Derived from the value rather than describing the control, which is what makes
         it worth the line: a number the page computed and the user could not. -->
    <p class="note" class:warn>{note}</p>
  {/if}

  {#if hint !== undefined}
    <p class="hint">{hint}</p>
  {/if}
</div>

<style>
  .setting {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  label {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: var(--gap);
    font-size: 0.75rem;
  }

  .name {
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--ink-faint);
  }

  .value {
    color: var(--ink);
    font-variant-numeric: tabular-nums;
  }

  input {
    width: 100%;
    /* Thumb-sized, because this gets dragged on a phone in a dark booth. */
    min-height: 2.25rem;
    accent-color: var(--accent);
  }

  .note {
    margin: 0;
    font-size: 0.7rem;
    color: var(--ink-dim);
    font-variant-numeric: tabular-nums;
  }

  /* Not alarming, because nothing is broken: the value is legal and is doing exactly
     what it says. It is only further than somebody probably meant to go. */
  .warn {
    color: var(--quiet);
  }

  .hint {
    margin: 0;
    font-size: 0.7rem;
    line-height: 1.5;
    color: var(--ink-faint);
  }
</style>
