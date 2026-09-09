<script lang="ts">
  import AnchorButton from './AnchorButton.svelte';

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
</script>

<!--
  The pad and the two captions that are always there form one row, and the
  manual-only tempo form sits under it rather than inside it. Centring the pad
  against a column whose height changes with the mode moved the button
  vertically every time the source was switched, which is the one thing a
  control aimed at in a dark booth must not do.
-->
<div class="anchor-pad">
  <div class="row">
    <AnchorButton {manual} {tap} />

    <div class="detail">
      <p class="caption">
        every tap sets the downbeat{#if manual}, and the tempo{/if}
      </p>

      <!--
        Said out loud because an anchor placed by drop detection looks identical
        to a right one, and nothing else on the page suggests a human can
        overrule it.
      -->
      <p class="caption">a tap overrules whatever anchored the bar</p>
    </div>
  </div>

  <!--
    Kept in the layout on every source rather than added on the manual one. The
    stage above shares this column and is the element that takes the slack, so a
    form appearing here shrinks the BPM readout - the one number the page exists
    to show - every time the source is switched. Hidden rather than removed
    holds the height; visibility also takes it out of the tab order and off a
    screen reader, so what is reserved is space and nothing else.
  -->
  <div class="tempo-controls" class:reserved={!manual}>
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
  </div>
</div>

<style>
  .anchor-pad {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    align-items: start;
  }

  .row {
    display: flex;
    align-items: center;
    gap: var(--gap);
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

  .tempo-controls {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .reserved {
    visibility: hidden;
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
