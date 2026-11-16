<script lang="ts">
  import AnchorButton from './AnchorButton.svelte';

  interface Props {
    /** The tempo on the grid right now, shown as the box's placeholder. */
    bpm: number | null;
    /** Sets the downbeat and nothing else - see the button below for why a tap cannot. */
    anchor: () => void;
    setBpm: (bpm: number | null) => void;
  }

  const { bpm, anchor, setBpm }: Props = $props();

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
</script>

<!--
  What the manual source needs beyond the stage's own button: a typed tempo, and a press
  that moves the downbeat without retuning it. Only shown under manual, because nothing
  here means anything under another source - and the big tap button is not repeated:
  it lives on the stage, where it is pressed on the downbeat with the music playing.
-->
<div class="manual-tempo">
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

  <!--
    The downbeat without the tempo, which under manual the stage's button cannot say:
    a tap there is also a tempo press, so two quick presses retune the grid to however
    fast somebody pressed. This is the press for when the tempo is already right -
    typed, usually - and only the bar is wrong.

    The same component as the stage's button and not a copy, because the rule worth
    not duplicating is that the press fires on pointerdown rather than on click.
    `manual` is false on purpose: presses here never feed the tempo, and false is the
    prop's way of saying so - it also keeps the label reading "anchor".
  -->
  <div class="anchor-only">
    <AnchorButton manual={false} tap={anchor} />
    <p class="caption">anchor sets the downbeat and leaves the tempo alone</p>
  </div>
</div>

<style>
  .manual-tempo {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    align-items: start;
  }

  .caption {
    margin: 0;
    font-size: var(--text-sm);
    letter-spacing: 0.06em;
    color: var(--ink-dim);
  }

  .tempo {
    display: flex;
    gap: var(--space-2);
  }

  .anchor-only {
    /* A third of the pad above it: still a comfortable thumb target, visibly
       subordinate to the pad that does more. The size variable is the contract
       AnchorButton documents for exactly this kind of caller. */
    --anchor-size: 3rem;
    display: flex;
    align-items: center;
    gap: var(--gap);
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
    font-size: var(--text-md);
    min-height: var(--control);
  }

  .set {
    padding: 0.5rem 0.9rem;
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    cursor: pointer;
    min-height: var(--control);
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
