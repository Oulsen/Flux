<script lang="ts">
  import type { DeviceData, DeviceState } from '../lib/protocol.ts';
  import LevelMeter from './LevelMeter.svelte';

  interface Props {
    devices: DeviceData[];
    /** The device the user asked for, while the server has yet to confirm it. */
    pending: string | null;
    select: (deviceId: string) => void;
  }

  const { devices, pending, select }: Props = $props();

  /*
   * Split by capture path, because the two answer different questions. An input
   * is a cable into the machine, which at the booth is the controller's master
   * return. A loopback is whatever this machine is playing through its own
   * speakers, which is what makes Flux useful with no controller attached at all.
   */
  const inputs = $derived(devices.filter((device) => device.kind === 'capture'));
  const outputs = $derived(devices.filter((device) => device.kind === 'loopback'));

  const stateLabel: Record<DeviceState, string> = {
    signal: 'signal',
    digitalSilence: 'silent',
    noData: 'no data',
    failed: 'failed',
  };
</script>

<div class="picker">
  {#if devices.length === 0}
    <p class="empty">no audio devices</p>
  {:else}
    {#each [{ title: 'inputs', list: inputs }, { title: 'outputs, captured as loopback', list: outputs }] as group (group.title)}
      {#if group.list.length > 0}
        <h3>{group.title}</h3>

        <ul>
          {#each group.list as device (device.id)}
            <li>
              <button
                type="button"
                class="device"
                class:selected={device.selected}
                class:pending={device.id === pending}
                aria-pressed={device.selected}
                onclick={() => select(device.id)}
              >
                <span class="row">
                  <span class="name">{device.name}</span>

                  <!-- Only the device being captured has a state, so the chip is
                       absent rather than blank for all the others. -->
                  {#if device.state !== undefined}
                    <span class="chip {device.state}">{stateLabel[device.state]}</span>
                  {/if}
                </span>

                {#if device.selected}
                  <LevelMeter rms={device.rms} peak={device.peak} />
                {/if}
              </button>
            </li>
          {/each}
        </ul>
      {/if}
    {/each}
  {/if}
</div>

<style>
  /* A machine with a multi-channel interface on it shows twenty-odd endpoints,
     so the list scrolls instead of pushing the rest of the page off screen. */
  .picker {
    max-height: 18rem;
    overflow-y: auto;
  }

  .empty {
    margin: 0;
    font-size: var(--text-sm);
    color: var(--ink-dim);
  }

  h3 {
    margin: 0.75rem 0 0.4rem;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
    font-weight: 600;
  }

  h3:first-child {
    margin-top: 0;
  }

  ul {
    margin: 0;
    padding: 0;
    list-style: none;
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
  }

  .device {
    width: 100%;
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    /* The whole row is the target: this gets tapped on a phone in the dark. */
    min-height: var(--control);
    padding: 0.5rem 0.7rem;
    background: var(--surface);
    border: 1px solid var(--edge);
    border-radius: var(--radius);
    color: var(--ink-dim);
    font: inherit;
    font-size: var(--text-sm);
    text-align: left;
    cursor: pointer;
  }

  .row {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  .name {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .selected {
    border-color: var(--accent);
    color: var(--ink);
  }

  /* As with the source selector, a pressed row does not claim to be selected:
     an unknown or unavailable device is answered with a rejection. */
  .pending {
    border-style: dashed;
    border-color: var(--ink-faint);
    opacity: 0.7;
  }

  .chip {
    flex: none;
    padding: 0.1rem 0.45rem;
    border: 1px solid currentcolor;
    border-radius: 999px;
    font-size: var(--text-xs);
    letter-spacing: var(--tracking-label);
    text-transform: uppercase;
    color: var(--ink-dim);
  }

  .signal {
    color: var(--live);
  }

  .digitalSilence {
    color: var(--quiet);
  }

  .failed {
    color: var(--alarm);
  }
</style>
