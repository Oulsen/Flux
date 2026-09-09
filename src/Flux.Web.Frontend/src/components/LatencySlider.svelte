<script lang="ts">
  import { LATENCY_OFFSET_LIMIT_MS, offsetBeatsLabel, offsetIsNearABeat, offsetLabel } from '../lib/hue.ts';
  import RangeSetting from './RangeSetting.svelte';

  interface Props {
    /** The offset the server says is in force, signed, positive being early. */
    offsetMs: number;
    /** The tempo, so the offset can be said in beats. Null while there is no grid. */
    bpm: number | null;
    setOffset: (offsetMs: number) => void;
  }

  const { offsetMs, bpm, setOffset }: Props = $props();

  /*
   * The offset in beats, which is the reading that makes this control honest.
   *
   * Milliseconds alone are unjudgeable: 400 looks like a modest correction and at
   * 130 BPM it is most of a beat, which moves every colour change onto the beat before
   * the one it was aimed at. Saying so here is cheaper than the evening it costs
   * otherwise.
   */
  const beats = $derived(offsetBeatsLabel(offsetMs, bpm));
  const nearABeat = $derived(offsetIsNearABeat(offsetMs, bpm));
</script>

<!--
  The one number that decides whether the light looks like it lands on the music, and
  the reason it is a slider rather than a setting: the only instrument that can measure
  it is somebody standing in front of the lights while the music plays.
-->
<RangeSetting
  id="latency-offset"
  name="latency offset"
  min={-LATENCY_OFFSET_LIMIT_MS}
  max={LATENCY_OFFSET_LIMIT_MS}
  step={5}
  value={offsetMs}
  format={offsetLabel}
  note={beats}
  warn={nearABeat}
  hint="The light path runs behind the music, so positive sends the change early. Expect somewhere around 60 to 140 ms."
  commit={setOffset}
/>
