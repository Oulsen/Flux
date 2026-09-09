<script lang="ts">
  import { LATENCY_OFFSET_LIMIT_MS, offsetLabel } from '../lib/hue.ts';

  interface Props {
    /** The offset the server says is in force, signed, positive being early. */
    offsetMs: number;
    setOffset: (offsetMs: number) => void;
  }

  const { offsetMs, setOffset }: Props = $props();

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

  const position = $derived(dragged ?? offsetMs);

  $effect(() => {
    if (dragged !== null && dragged === offsetMs) {
      dragged = null;
    }
  });

  function drag(event: Event): void {
    const value = Number((event.currentTarget as HTMLInputElement).value);
    dragged = value;

    const now = Date.now();
    if (now - lastSent >= SEND_INTERVAL_MS) {
      lastSent = now;
      setOffset(value);
    }
  }

  /** The release, which has to land whatever the throttle skipped on the way. */
  function release(event: Event): void {
    const value = Number((event.currentTarget as HTMLInputElement).value);

    dragged = value;
    lastSent = Date.now();
    setOffset(value);
  }
</script>

<!--
  The one number that decides whether the light looks like it lands on the music, and
  the reason it is a slider rather than a setting: the only instrument that can measure
  it is somebody standing in front of the lights while the music plays.
-->
<div class="offset">
  <label for="latency-offset">
    <span class="name">latency offset</span>
    <span class="value">{offsetLabel(position)}</span>
  </label>

  <input
    id="latency-offset"
    type="range"
    min={-LATENCY_OFFSET_LIMIT_MS}
    max={LATENCY_OFFSET_LIMIT_MS}
    step="5"
    value={position}
    oninput={drag}
    onchange={release}
  />

  <p class="hint">
    The light path runs behind the music, so positive sends the change early. Tune it by eye, on the
    lights rather than on this number.
  </p>
</div>

<style>
  .offset {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
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
    min-height: 2.5rem;
    accent-color: var(--accent);
  }

  .hint {
    margin: 0;
    font-size: 0.7rem;
    line-height: 1.5;
    color: var(--ink-faint);
  }
</style>
