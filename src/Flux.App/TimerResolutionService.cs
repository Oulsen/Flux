using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// Raises the process timer resolution for as long as the app runs, and keeps
/// Windows from quietly taking it back.
///
/// Both halves are load-bearing, and Phase 0 measured why:
///
/// <list type="bullet">
/// <item><description><c>timeBeginPeriod(1)</c> takes the mean cost of
/// <c>Thread.Sleep(1)</c> from <b>15.46 ms to 1.48 ms</b>. Without it every beat
/// the scheduler aims at is quantised to roughly a sixteenth of a second, which
/// is audible as wrong. Per-process since Windows 10 2004, so this does not
/// degrade the rest of the machine.</description></item>
/// <item><description>Windows 11 <b>ignores that request once the process is
/// neither visible nor playing audio</b> - which is exactly Flux minimised to
/// tray, its normal running state. Opting out of timer throttling is what keeps
/// the resolution honest in the background. OpenRGB adopted the same call after
/// hitting this.</description></item>
/// </list>
///
/// Registered first among the hosted services so the resolution is up before the
/// beat scheduler starts sleeping against it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TimerResolutionService: IHostedService {
  /// <summary>1 ms: the finest resolution the multimedia timer API offers.</summary>
  private const uint TargetPeriodMillis = 1;

  private const uint TimerNoError = 0;

  /// <summary>ProcessPowerThrottling, from PROCESS_INFORMATION_CLASS.</summary>
  private const int ProcessPowerThrottling = 4;

  private const uint PowerThrottlingCurrentVersion = 1;

  /// <summary>PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION.</summary>
  private const uint IgnoreTimerResolution = 0x4;

  private readonly ILogger<TimerResolutionService> log;

  private bool raised;

  public TimerResolutionService(ILogger<TimerResolutionService> log) {
    this.log = log ?? throw new ArgumentNullException(nameof(log));
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    uint result = TimeBeginPeriod(TargetPeriodMillis);
    this.raised = result == TimerNoError;

    if(!this.raised) {
      this.log.LogWarning(
        "timeBeginPeriod({Period}) was refused. Beat timing will be quantised to the default ~15.6 ms tick.",
        TargetPeriodMillis);
    }

    this.IgnoreBackgroundTimerThrottling();
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    if(this.raised) {
      TimeEndPeriod(TargetPeriodMillis);
      this.raised = false;
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Tells Windows not to throttle this process's timers while it is in the
  /// background. Best effort: on a build without the mechanism the call simply
  /// fails, and the app is no worse off than if it had never been made.
  /// </summary>
  private void IgnoreBackgroundTimerThrottling() {
    PowerThrottlingState state = new PowerThrottlingState {
      Version = PowerThrottlingCurrentVersion,
      ControlMask = IgnoreTimerResolution,

      // A zero state bit against a set control bit reads as "control this
      // mechanism, and turn it off" - i.e. do not throttle.
      StateMask = 0,
    };

    if(!SetProcessInformation(
      GetCurrentProcess(),
      ProcessPowerThrottling,
      ref state,
      (uint)Marshal.SizeOf<PowerThrottlingState>())) {
      this.log.LogWarning(
        "Could not opt out of background timer throttling (error {Error}). Beat timing may degrade once the window is hidden.",
        Marshal.GetLastPInvokeError());
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct PowerThrottlingState {
    public uint Version;
    public uint ControlMask;
    public uint StateMask;
  }

  [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
  private static extern uint TimeBeginPeriod(uint milliseconds);

  [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
  private static extern uint TimeEndPeriod(uint milliseconds);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetProcessInformation(
    IntPtr process,
    int informationClass,
    ref PowerThrottlingState information,
    uint informationSize);

  [DllImport("kernel32.dll")]
  private static extern IntPtr GetCurrentProcess();
}
