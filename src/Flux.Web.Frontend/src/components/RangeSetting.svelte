<script lang="ts">
  interface Props {
    /** Unique, because the label points at the input by id. */
    id: string;
    /** What the control is called, shown small and upper case beside its value. */
    name: string;
    min: number;
    max: number;
    step: number;
    /** The value in force, including a change this client has made and sent. */
    value: number;
    /** The value in words. Every setting here means something a bare number does not say. */
    format: (value: number) => string;
    /** What the setting does, in one line. Omitted where the label says enough. */
    hint?: string;
    /**
     * The longer explanation: why the setting exists and how to judge it. In a popup
     * behind a "?" rather than on screen, because a drawer of always-on paragraphs is a
     * drawer that scrolls past the control somebody opened it for - and a popup rather
     * than a paragraph that unfolds, because unfolding moves every control below it.
     */
    detail?: string;
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
    detail,
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
   * the thumb back. The drag owns the position instead, and hands it back as soon as
   * `value` agrees with it - which is what keeps a value set from a second phone able
   * to move this slider at all.
   *
   * That handover relies on the caller passing back what this control committed rather
   * than only what the server has confirmed. A `value` that can disagree indefinitely
   * is a thumb that never reconciles, parked on a number nothing else in the system
   * holds.
   */
  let dragged = $state<number | null>(null);

  /** Not reactive: nothing renders from it, it only paces the sending. */
  let lastSent = 0;

  let more: HTMLButtonElement | undefined = $state();
  let tip: HTMLElement | undefined = $state();

  /*
   * A mouse gets the explanation by pointing, which is what a tooltip is: clicking a
   * "?" to read a sentence and clicking again to put it away was two errands for a
   * glance. Pointing at the name or the "?" opens it and leaving closes it. A finger
   * has no hover, so on touch the "?" stays a tap, and the keyboard keeps Enter.
   */
  let pointer = '';

  function hoverIn(event: PointerEvent): void {
    pointer = event.pointerType;

    if (event.pointerType === 'mouse' && tip !== undefined && !tip.matches(':popover-open')) {
      tip.showPopover();
    }
  }

  function hoverOut(event: PointerEvent): void {
    if (event.pointerType === 'mouse' && tip?.matches(':popover-open')) {
      tip.hidePopover();
    }
  }

  // A mouse click on the "?" would toggle shut what pointing at it just opened.
  function clicked(event: MouseEvent): void {
    if (pointer === 'mouse') {
      event.preventDefault();
    }
  }

  /*
   * Puts the popup beside its "?", which the popover itself does not do: it opens in the
   * top layer, outside the drawer, with no idea where the button is. Below the button
   * and right-aligned to it, flipped above when the bottom of the window is closer than
   * its height, and kept inside the window either way.
   */
  function place(event: ToggleEvent): void {
    if (event.newState !== 'open' || more === undefined) {
      return;
    }

    const tip = event.currentTarget as HTMLElement;
    const button = more.getBoundingClientRect();
    const margin = 8;
    const width = tip.offsetWidth;
    const height = tip.offsetHeight;

    const left = Math.min(Math.max(margin, button.right - width), window.innerWidth - width - margin);
    const below = button.bottom + margin;
    const top = below + height > window.innerHeight - margin ? Math.max(margin, button.top - margin - height) : below;

    tip.style.left = `${left}px`;
    tip.style.top = `${top}px`;

    // Placed once, so a scroll would leave it floating beside nothing. A tap already
    // dismisses it; a mouse wheel does not, so the scroll does.
    window.addEventListener('scroll', () => tip.hidePopover(), { capture: true, once: true });
  }

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
  <!-- The "?" sits beside the label rather than in it: a button inside a label would
       also hand the slider focus every time somebody asked what it is for. -->
  <div class="head" role="group" aria-label={name} onpointerenter={hoverIn} onpointerleave={hoverOut}>
    <label for={id}>
      <span class="name">{name}</span>
      <span class="value">{format(position)}</span>
    </label>

    {#if detail !== undefined}
      <!-- The browser's own popover: a tap anywhere else or Escape closes it, and the
           button reports itself expanded, with none of that written here. -->
      <button
        type="button"
        class="more"
        popovertarget={`${id}-detail`}
        aria-label={`About ${name}`}
        bind:this={more}
        onpointerdown={(event) => (pointer = event.pointerType)}
        onclick={clicked}>?</button
      >
      <p popover id={`${id}-detail`} class="tip" ontoggle={place} bind:this={tip}>{detail}</p>
    {/if}
  </div>

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
    gap: var(--space-1);
  }

  .head {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  label {
    flex: 1;
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: var(--gap);
    font-size: var(--text-sm);
  }

  .name {
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
    font-size: var(--text-xs);
  }

  .value {
    color: var(--ink);
    font-variant-numeric: tabular-nums;
  }

  input {
    width: 100%;
    /* Thumb-sized on touch, because this gets dragged on a phone in a dark booth. A
       mouse gets less, which `--chip` explains. */
    min-height: var(--chip);
    accent-color: var(--accent);
  }

  .note {
    margin: 0;
    font-size: var(--text-sm);
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
    font-size: var(--text-sm);
    line-height: 1.5;
    color: var(--ink-dim);
  }

  /* Fixed and placed by `place`; the browser's own popover styling centres it in the
     window, which is nowhere near the "?" it answers. */
  .tip {
    position: fixed;
    inset: auto;
    margin: 0;
    width: max-content;
    max-width: min(18rem, calc(100vw - 1rem));
    padding: var(--space-3);
    background: var(--panel);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    box-shadow: 0 8px 24px rgb(0 0 0 / 50%);
    color: var(--ink);
    font-size: var(--text-sm);
    line-height: 1.5;
  }

  .more {
    flex: none;
    width: var(--chip);
    height: var(--chip);
    padding: 0;
    background: none;
    border: 1px solid var(--edge);
    border-radius: 50%;
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-sm);
    cursor: pointer;
  }

  .more:hover,
  .more:focus-visible,
  .more:has(+ .tip:popover-open) {
    border-color: var(--accent);
    color: var(--ink);
  }
</style>
