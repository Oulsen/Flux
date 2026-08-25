using Flux.Audio;
using Microsoft.Extensions.Hosting;
using System.Globalization;

namespace Flux.App;

/// <summary>
/// Prints the capture level four times a second, so a device can be identified
/// without a browser.
///
/// The counterpart of <see cref="ConsoleBeatMonitor"/>, registered only when
/// Console:AudioMeter is set, and it exists for one reason that a device list
/// cannot serve: a name cannot identify an audio endpoint. The one carrying the
/// master mix on this rig is called <c>Microfoon (2- DDJ-FLX10)</c>, and thirteen
/// other inputs are permanently silent. What identifies a device is a level that
/// responds when a physical control moves - pull the channel fader and watch it
/// fall to digital silence - and a static reading cannot show that.
/// </summary>
public sealed class ConsoleAudioMonitor: BackgroundService {
  /// <summary>
  /// One line per meter window, so the console shows every measurement rather than
  /// a sample of them.
  /// </summary>
  private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

  /// <summary>Quietest level the bar shows, in dBFS. Below this it reads as empty.</summary>
  private const double FloorDb = -60;

  private const int BarWidth = 24;

  private readonly AudioCaptureService capture;

  public ConsoleAudioMonitor(AudioCaptureService capture) {
    this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    using PeriodicTimer timer = new PeriodicTimer(Interval);
    string? announced = null;

    try {
      while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
        AudioCaptureStatus status = this.capture.Status();

        if(status.Device is null) {
          // Said once rather than four times a second: with no device there is no
          // measurement to follow, so a repeated line would only bury the beat
          // log underneath it.
          if(announced is not null) {
            announced = null;
            Console.WriteLine("audio  no device");
          }

          continue;
        }

        announced = status.Device.Id;
        Console.WriteLine(Format(status.Device, status));
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }

  /// <summary>
  /// One line: what is open, how it is behaving, and how loud it is.
  ///
  /// Levels are printed in dBFS beside the bar because the interesting readings
  /// live in the bottom fifth of a linear scale - the Phase 0 measurement of this
  /// controller's mix was -23 dBFS RMS, which on a 0-to-1 bar is a barely visible
  /// stub.
  /// </summary>
  private static string Format(AudioDevice device, AudioCaptureStatus status) {
    string state = status.State switch {
      AudioDeviceState.Signal => "signal",
      AudioDeviceState.DigitalSilence => "digital silence",
      AudioDeviceState.NoData => "no data",
      _ => "failed",
    };

    double rmsDb = Decibels(status.Levels.Rms);
    int filled = (int)Math.Clamp((rmsDb - FloorDb) / -FloorDb * BarWidth, 0, BarWidth);

    return string.Create(
      CultureInfo.InvariantCulture,
      $"audio  {Shorten(device.Name),-30}  {state,-15}  rms {rmsDb,7:F1} dBFS  "
        + $"peak {Decibels(status.Levels.Peak),7:F1} dBFS  |{new string('#', filled).PadRight(BarWidth)}|");
  }

  /// <summary>
  /// A level as dBFS, with silence reported as a floor rather than as negative
  /// infinity: an unformattable number in the middle of a column of numbers
  /// destroys the alignment that makes the column readable.
  /// </summary>
  private static double Decibels(double level) =>
    level > 0 ? Math.Max(20 * Math.Log10(level), FloorDb) : FloorDb;

  private static string Shorten(string name) =>
    name.Length <= 30 ? name : name[..29] + "…";
}
