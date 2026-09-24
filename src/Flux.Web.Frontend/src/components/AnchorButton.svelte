<script lang="ts">
  /*
   * The press that says where the bar starts.
   *
   * Extracted from `AnchorPad` when the same button was wanted on the stage, because
   * the part worth not duplicating is not the circle - it is the rule that a tap fires
   * on the press and never on the click. Two copies of that would drift, and the
   * symptom of the wrong one is phase error nobody can see: the button would still
   * work, and the downbeat would land wherever the finger happened to lift.
   */

  interface Props {
    /** Whether the manual source is active, which is what makes repeated presses a tempo. */
    manual: boolean;
    tap: () => void;
  }

  const { manual, tap }: Props = $props();

  /*
   * The label follows the mode because the gesture does. Under manual the button
   * is hit repeatedly to give a tempo, and "anchor" invites exactly one press -
   * a label that hides the feature the mode exists for. Everywhere else the
   * tempo comes from the source and the only thing a press can say is where the
   * bar starts. The pad's size, shape and position never move with it.
   */
  const label = $derived(manual ? 'tap' : 'anchor');

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

<!--
  Labelled for what it does rather than for the gesture, and deliberately
  using the word the badge beside it uses: a press here reads back as "bar
  anchored by a tap", so the button and its own feedback name one thing.
  "Sync" was the other candidate and is wrong twice over - the selector picks a
  sync source, and on DJ gear the word means tempo, which this only touches on
  the manual source.
-->
<button type="button" class="tap" onpointerdown={pressed} onkeydown={keyed}>{label}</button>

<style>
  .tap {
    /* Sized for a thumb on a phone in a dark booth, which is the only place this
       button is ever pressed in a hurry. The caller may shrink it, and the stage
       does: there it sits under the beat dots rather than beside a caption. */
    width: var(--anchor-size, 5rem);
    height: var(--anchor-size, 5rem);
    flex: none;
    border: 1px solid var(--accent);
    border-radius: 50%;

    /* Opaque rather than tinted, because on the stage this sits over a canvas and a
       translucent target is one whose edge disappears against a bright frame. */
    background: var(--panel);
    color: var(--accent);
    font: inherit;

    /*
     * Scaled from the circle rather than fixed, because the caller may shrink the
     * circle and "anchor" is six letters. At a fixed 0.9rem the word ran edge to edge
     * inside the stage's smaller button and read as a mistake - the label has to be a
     * proportion of the thing it is inside, not a constant beside it.
     */
    font-size: calc(var(--anchor-size, 5rem) * 0.19);

    /* box-sizing is border-box page-wide, so the browser's own button padding
       comes out of the size rather than adding to it. Six letters and the
       default padding do not both fit, and the tracking is what makes a short
       word look deliberate, so the padding goes instead. */
    padding: 0;
    letter-spacing: 0.08em;
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
</style>
