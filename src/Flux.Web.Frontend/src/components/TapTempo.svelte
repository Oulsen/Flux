<script lang="ts">
  interface Props {
    /** Whether the manual source is active, which is what makes a typed tempo mean anything. */
    manual: boolean;
    /** The tempo on the grid right now, shown as the box's placeholder. */
    bpm: number | null;
    tap: () => void;
    setBpm: (bpm: number | null) => void;
  }

  const { manual, bpm, tap, setBpm }: Props = $props();

  /* The bounds ManualSource enforces. Repeated rather than derived because there
     is no shared definition to derive from: the server rejects out of range, and
     this only saves the round trip. */
  const MIN_BPM = 20;
  const MAX_BPM = 200;

  /*
   * The box is a command, not a display. Mirroring the live tempo into it would
   * mean fighting the state feed for the caret twenty times a second, so what is
   * typed stays local and the current tempo appears as the placeholder instead.
   */
  // Null rather than an empty string: a number input binds a number, and an
  // empty one binds null.
  let typed = $state<number | null>(null);

  function submit(): void {
    // An empty box withdraws the manual tempo, which is not the same as setting
    // it to zero: the grid goes back to having nothing rather than to claiming
    // a tempo of nothing.
    if (typed === null) {
      setBpm(null);
      return;
    }

    // The browser blocks a submit outside min and max and says why, so this is
    // the backstop rather than the check the user sees.
    if (Number.isFinite(typed) && typed >= MIN_BPM && typed <= MAX_BPM) {
      setBpm(typed);
      typed = null;
    }
  }

  /*
   * A tap fires on the press and not on the click, because a click lands on
   * release and hands the server however long the finger stayed down. The server
   * timestamps on arrival, so every millisecond before that is phase error in a
   * gesture whose only job is to say "now".
   */
  function pressed(event: PointerEvent): void {
    event.preventDefault();
    tap();
  }

  // Pointer events skip the keyboard entirely, and preventDefault here stops the
  // synthesised click that would otherwise tap a second time.
  function keyed(event: KeyboardEvent): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      tap();
    }
  }
</script>

<div class="tap-tempo">
  <button type="button" class="tap" onpointerdown={pressed} onkeydown={keyed}>tap</button>

  <div class="detail">
    <p class="caption">
      every tap sets the downbeat{#if manual}, and the tempo{/if}
    </p>

    {#if manual}
      <form
        class="tempo"
        onsubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <label class="field">
          <span class="visually-hidden">Tempo in BPM</span>
          <input
            class="numeric"
            type="number"
            inputmode="decimal"
            step="0.01"
            min={MIN_BPM}
            max={MAX_BPM}
            placeholder={bpm === null ? 'bpm' : bpm.toFixed(2)}
            bind:value={typed}
          />
        </label>
        <button type="submit" class="set">set</button>
      </form>

      <p class="caption">a typed tempo leaves the downbeat where it is; empty clears it</p>
    {/if}
  </div>
</div>

<style>
  .tap-tempo {
    display: flex;
    align-items: center;
    gap: var(--gap);
  }

  .tap {
    /* Sized for a thumb on a phone in a dark booth, which is the only place this
       button is ever pressed in a hurry. */
    width: 5rem;
    height: 5rem;
    flex: none;
    border: 1px solid var(--accent);
    border-radius: 50%;
    background: var(--panel);
    color: var(--accent);
    font: inherit;
    font-size: 0.9rem;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    cursor: pointer;

    /* Scoped here rather than left to the page: manipulation drops the
       double-tap zoom delay, and a fast second tap is the normal way this
       button is used. app.css already suppresses selection page-wide, and this
       repeats it because a press-and-hold on the one control meant to be
       hammered must never start a selection. */
    touch-action: manipulation;
    -webkit-user-select: none;
    user-select: none;
  }

  .tap:active {
    background: var(--accent);
    color: var(--surface);
  }

  .detail {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    min-width: 0;
  }

  .caption {
    margin: 0;
    font-size: 0.75rem;
    letter-spacing: 0.06em;
    color: var(--ink-faint);
  }

  .tempo {
    display: flex;
    gap: 0.5rem;
  }

  .field {
    min-width: 0;
  }

  input {
    width: 6rem;
    padding: 0.5rem 0.6rem;
    background: var(--surface);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink);
    font: inherit;
    font-size: 0.9rem;
  }

  .set {
    padding: 0.5rem 0.9rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: 0.8rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    cursor: pointer;
  }

  .visually-hidden {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }
</style>
