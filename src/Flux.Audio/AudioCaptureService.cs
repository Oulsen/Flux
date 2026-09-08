using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;

namespace Flux.Audio;

/// <summary>
/// Keeps one audio device open and its samples flowing, for as long as the host
/// runs.
///
/// Everything about this class is a consequence of devices being unreliable in
/// ordinary ways. A controller is plugged in after the app starts, or unplugged
/// mid-set. Another application holds a device exclusively for a while. A loopback
/// endpoint delivers nothing at all for minutes because nothing is playing to it,
/// which is not a fault and must not be treated as one. So it re-checks, reopens
/// with a backoff, and never lets any of that reach the host: a machine with no
/// usable input still has Link and still has a DJ who can tap.
/// </summary>
public sealed class AudioCaptureService: BackgroundService, IAudioSamples, IAudioDevices {
  /// <summary>
  /// Mono samples kept for the analyzer.
  ///
  /// Sized in samples rather than in seconds because what it has to guarantee is a
  /// whole FFT window: 32768 is sixteen 2048-sample windows, which at 48 kHz is
  /// about two thirds of a second, and at any rate a device might open at it is
  /// still several windows of slack for an analyzer frame that ran late. Fixed at
  /// construction, so switching to a device with a different sample rate cannot
  /// reallocate the buffer the analyzer is reading from.
  /// </summary>
  private const int RingCapacity = 1 << 15;

  /// <summary>
  /// How often the device list is re-checked.
  ///
  /// This is what notices a controller being plugged in, so it is a
  /// responsiveness budget rather than a housekeeping interval. A second is fast
  /// enough that plugging in the FLX-10 feels immediate and slow enough that
  /// enumerating COM endpoints is invisible. The device list a browser sees is
  /// this stale at worst; the levels on it are not, because those come from the
  /// device that is open.
  /// </summary>
  private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

  /// <summary>First wait after a device refused to open.</summary>
  private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Longest wait between attempts on the same device. A capped backoff and not an
  /// escalating one: the usual cause is another application holding the device
  /// exclusively, which ends without warning, and half a minute is as long as Flux
  /// should take to notice.
  /// </summary>
  private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

  private readonly IAudioDeviceCatalog catalog;
  private readonly IMonotonicClock clock;
  private readonly AudioOptions options;
  private readonly IMonoTap tap;
  private readonly ILogger<AudioCaptureService> log;
  private readonly MonoRingBuffer ring = new MonoRingBuffer(RingCapacity);
  private readonly AudioMeter meter;

  /// <summary>
  /// Guards what the capture loop publishes and the web layer reads. Two threads,
  /// one of them at the state rate, so it is never contended.
  /// </summary>
  private readonly object statusGate = new object();

  /// <summary>
  /// Released by <see cref="TrySelect"/> so a device change takes effect at once
  /// rather than at the end of the current poll. A wait with a timeout rather than
  /// a timer beside an event, because then the loop has exactly one place it can
  /// be woken up.
  /// </summary>
  private readonly SemaphoreSlim wake = new SemaphoreSlim(0);

  private IReadOnlyList<AudioDevice> devices = [];

  /// <summary>The device selection resolved to, whether or not it could be opened.</summary>
  private AudioDevice? wanted;

  /// <summary>The device actually delivering audio, or null.</summary>
  private AudioDevice? open;

  private string? requestedDeviceId;
  private int sampleRate;

  /// <summary>
  /// Set when the wanted device could not be opened, or stopped with an error.
  /// Separate from "nothing arrived", which is what a silent device looks like.
  /// </summary>
  private bool failed;

  /// <summary>Guards <see cref="TrySelect"/> against waking a disposed semaphore.</summary>
  private bool disposed;

  // The capture loop's state, touched only by the loop's own thread and by tests
  // driving PollOnce directly. Fields rather than locals so one poll is a callable
  // step: the resolve, backoff and forget rules below are policy, not plumbing,
  // and policy that can only run against real hardware can only regress there.
  private AudioCaptureSession? session;
  private AudioDevice? capturing;
  private TimeSpan retryDelay = MinRetryDelay;
  private double retryNotBeforeMicros;

  /// <summary>
  /// The device the backoff belongs to. A wait earned by one device must not be
  /// served by another: a selection is acted on at once however long the previous
  /// device had been refusing to open.
  /// </summary>
  private string? retryDeviceId;

  private bool saidThereIsNothing;

  /// <summary>
  /// Internal because the catalog is: it hands out NAudio endpoints, and that is
  /// not a type this assembly wants in its public surface. Built by
  /// <see cref="ServiceCollectionExtensions.AddAudioCapture"/> rather than by the
  /// container's constructor discovery.
  /// </summary>
  internal AudioCaptureService(
    IAudioDeviceCatalog catalog,
    IMonotonicClock clock,
    IOptions<AudioOptions> options,
    IMonoTap tap,
    ILogger<AudioCaptureService> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.options = options.Value;
    this.tap = tap ?? throw new ArgumentNullException(nameof(tap));
    this.log = log ?? throw new ArgumentNullException(nameof(log));
    this.meter = new AudioMeter(clock);
  }

  /// <summary>
  /// The rate the open device is running at, or zero while nothing is open.
  ///
  /// The device's choice rather than a setting, so the analyzer reads it every frame
  /// and derives its band edges from it: a kick band computed for 48 kHz sits an
  /// octave out on a device that opened at 96.
  /// </summary>
  public int SampleRate {
    get {
      lock(this.statusGate) {
        return this.open is null ? 0 : this.sampleRate;
      }
    }
  }

  /// <inheritdoc/>
  public IReadOnlyList<AudioDevice> Devices() {
    lock(this.statusGate) {
      return this.devices;
    }
  }

  /// <inheritdoc/>
  public AudioCaptureStatus Status() {
    AudioMeterReading reading = this.meter.Read();
    long captured = this.ring.Written;

    lock(this.statusGate) {
      if(this.wanted is null) {
        return new AudioCaptureStatus(null, AudioDeviceState.NoData, AudioLevels.None, 0, 0);
      }

      if(this.open is null) {
        return new AudioCaptureStatus(
          this.wanted,
          this.failed ? AudioDeviceState.Failed : AudioDeviceState.NoData,
          AudioLevels.None,
          0,
          0);
      }

      return new AudioCaptureStatus(this.open, reading.State, reading.Levels, this.sampleRate, captured);
    }
  }

  /// <inheritdoc/>
  ///
  /// <remarks>
  /// The id is matched against the list this service last saw, so an id that was
  /// never in it is refused rather than remembered: a selection is only ever made
  /// from a list somebody was looking at.
  /// </remarks>
  public bool TrySelect(string deviceId) {
    if(deviceId is not { Length: > 0 }) {
      return false;
    }

    lock(this.statusGate) {
      if(this.disposed || this.devices.All(device => device.Id != deviceId)) {
        return false;
      }

      this.requestedDeviceId = deviceId;

      // Released inside the lock, because Dispose takes the same lock before
      // disposing the semaphore: a release racing a dispose would otherwise throw
      // into whichever socket receive loop carried the selection. Release never
      // blocks, so holding the lock across it costs nothing.
      this.wake.Release();
    }

    return true;
  }

  /// <summary>
  /// Copies the newest captured samples for analysis.
  /// </summary>
  /// <returns>
  /// False when that much audio has not been captured yet, in which case nothing
  /// is written. The analyzer then skips a frame rather than transforming a
  /// half-filled window, whose spectrum shows a step change that looks exactly
  /// like a hit.
  /// </returns>
  public bool TryReadLatest(Span<float> destination) => this.ring.CopyLatest(destination);

  public override void Dispose() {
    // Under the status gate so a TrySelect in flight either completes its release
    // first or sees the flag and declines. Shutdown ordering happens to protect
    // this today, because the web host stops before this service is disposed, but
    // a registration reorder would reopen the race without anything failing loudly.
    lock(this.statusGate) {
      if(!this.disposed) {
        this.disposed = true;
        this.wake.Dispose();
      }
    }

    base.Dispose();
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Nothing may escape this method. BackgroundService hands the task straight
    // back to StartAsync, so an exception here faults host startup - and a
    // controller that will not open is a bad reason for Flux not to launch at all.
    try {
      await this.CaptureUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
    } catch(OperationCanceledException) {
      // The host is stopping.
    } catch(Exception ex) {
      this.log.LogError(ex, "The audio capture loop stopped. There is no audio source for the rest of this run.");
    }
  }

  /// <summary>
  /// Re-checks the device list, and keeps the right device open, until the host
  /// stops.
  /// </summary>
  private async Task CaptureUntilStoppedAsync(CancellationToken stoppingToken) {
    try {
      while(!stoppingToken.IsCancellationRequested) {
        this.PollOnce();

        // A timed wait, so a selection arriving mid-interval is acted on at once.
        await this.wake.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
      }
    } finally {
      this.session?.Dispose();
      this.session = null;
      this.capturing = null;
      this.Forget(failedToOpen: false);
    }
  }

  /// <summary>
  /// One pass of the capture loop: resolve the device the selection asks for,
  /// close a session that no longer matches it, and open or retry as the backoff
  /// allows.
  ///
  /// Internal so the tests can run the loop's policy one step at a time against a
  /// fake catalog and a fake clock, without the timed wait between steps. The same
  /// seam <see cref="IAudioSamples"/> opens for the analyzer thread, for the same
  /// Phase 1 reason: arithmetic that only runs against real hardware only fails
  /// there.
  /// </summary>
  internal void PollOnce() {
    AudioDevice? target = this.Resolve(this.catalog.Devices());

    // A session is closed before anything else when its device is gone, has
    // been superseded, or has reported an error. Keeping it would leave a dead
    // device's last levels sitting on the meter.
    if(this.session is not null && (target is null || target.Id != this.capturing?.Id || this.session.Failed)) {
      bool broke = this.session.Failed;
      this.log.LogInformation("Closing the audio capture of {Device}.", this.capturing?.Name);

      this.session.Dispose();
      this.session = null;
      this.Forget(failedToOpen: broke);

      this.retryDelay = MinRetryDelay;
      this.retryDeviceId = broke ? this.capturing?.Id : null;
      this.retryNotBeforeMicros = broke ? this.clock.NowMicros + (MinRetryDelay.TotalMilliseconds * 1000.0) : 0;
      this.capturing = null;
    }

    if(target is null) {
      if(!this.saidThereIsNothing) {
        this.saidThereIsNothing = true;
        this.log.LogInformation(
          "No audio device matches {Preference}. Audio analysis is idle until one is selected.{Candidates}",
          this.Describe(),
          Candidates(this.Devices()));
      }

      return;
    }

    this.saidThereIsNothing = false;

    if(this.session is not null) {
      return;
    }

    // The backoff belongs to the device that earned it. A different device
    // arriving, or being selected, starts fresh at once: making a new choice
    // wait out an old device's half-minute would read as dead hardware.
    if(this.retryDeviceId is not null && this.retryDeviceId != target.Id) {
      this.retryDelay = MinRetryDelay;
      this.retryDeviceId = null;
      this.retryNotBeforeMicros = 0;
    }

    if(this.clock.NowMicros < this.retryNotBeforeMicros) {
      return;
    }

    this.session = this.TryOpen(target);
    this.capturing = this.session is null ? null : target;

    if(this.session is null) {
      this.retryDeviceId = target.Id;
      this.retryNotBeforeMicros = this.clock.NowMicros + (this.retryDelay.TotalMilliseconds * 1000.0);
      this.retryDelay = Slower(this.retryDelay);
    } else {
      this.retryDelay = MinRetryDelay;
      this.retryDeviceId = null;
    }
  }

  /// <summary>
  /// Works out which device should be open, and publishes both that and the list
  /// it came from.
  ///
  /// A device chosen at runtime is matched strictly, while a configured one falls
  /// back to the name preference, and the difference is deliberate. A settings file
  /// belongs to one machine, so a copied one names an endpoint that does not exist
  /// on the second - falling back to the FLX-10 that is plugged into it is the
  /// useful answer. A live selection came from a list the user was looking at, and
  /// quietly capturing something else instead would be overruling a choice
  /// somebody just made.
  /// </summary>
  private AudioDevice? Resolve(IReadOnlyList<AudioDevice> present) {
    lock(this.statusGate) {
      this.devices = present;

      this.wanted = this.requestedDeviceId is { Length: > 0 } requested
        ? present.FirstOrDefault(device => device.Id == requested)
        : AudioDeviceCatalog.Select(present, this.options.DeviceId, this.options.PreferDeviceNamed);

      return this.wanted;
    }
  }

  private AudioCaptureSession? TryOpen(AudioDevice device) {
    MMDevice? endpoint = this.catalog.Open(device.Id);
    AudioCaptureSession? session = endpoint is null
      ? null
      : AudioCaptureSession.Open(device, endpoint, this.ring, this.meter, this.tap, this.log);

    if(session is null) {
      endpoint?.Dispose();

      lock(this.statusGate) {
        this.failed = true;
      }

      return null;
    }

    lock(this.statusGate) {
      this.open = device;
      this.sampleRate = session.SampleRate;
      this.failed = false;
    }

    return session;
  }

  /// <summary>
  /// Drops everything about the device that was open, so nothing about it can be
  /// reported as current.
  ///
  /// The ring buffer is emptied with it, and that is the load-bearing part:
  /// samples from two devices spliced together contain a discontinuity, and a
  /// discontinuity is exactly what a spectral-flux detector calls a beat.
  /// </summary>
  private void Forget(bool failedToOpen) {
    lock(this.statusGate) {
      this.open = null;
      this.sampleRate = 0;
      this.failed = failedToOpen;
    }

    this.ring.Reset();
    this.meter.Reset();
  }

  /// <summary>
  /// Says what was being looked for, so the log names the setting to change rather
  /// than only reporting that nothing was found.
  /// </summary>
  private string Describe() {
    string? requested;
    lock(this.statusGate) {
      requested = this.requestedDeviceId;
    }

    if(requested is { Length: > 0 }) {
      return $"the selected device ({requested})";
    }

    if(this.options.DeviceId is { Length: > 0 } configured) {
      return $"Audio:DeviceId '{configured}' or Audio:PreferDeviceNamed '{this.options.PreferDeviceNamed}'";
    }

    return this.options.PreferDeviceNamed is { Length: > 0 } preferred
      ? $"Audio:PreferDeviceNamed '{preferred}'"
      : "any device (Audio:PreferDeviceNamed is empty)";
  }

  /// <summary>
  /// Names the inputs that were not chosen.
  ///
  /// Inputs only, never the render endpoints: on this rig the endpoint carrying
  /// the master mix is an input called <c>Microfoon (2- DDJ-FLX10)</c>, so the
  /// useful hint is the short list a human can recognise the controller in, not
  /// thirteen render endpoints Phase 0 already proved are silent while ASIO owns
  /// playback.
  /// </summary>
  private static string Candidates(IReadOnlyList<AudioDevice> devices) {
    string[] inputs = [.. devices
      .Where(device => device.Kind == AudioDeviceKind.Capture)
      .Select(device => device.Name)];

    return inputs.Length == 0
      ? string.Empty
      : $" Inputs seen: {string.Join(", ", inputs)}.";
  }

  private static TimeSpan Slower(TimeSpan delay) {
    TimeSpan doubled = delay * 2;
    return doubled > MaxRetryDelay ? MaxRetryDelay : doubled;
  }
}
