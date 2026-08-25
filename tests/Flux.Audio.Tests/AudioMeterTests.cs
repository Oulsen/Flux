using Flux.Beat.Tests;
using Xunit;

namespace Flux.Audio.Tests;

public class AudioMeterTests {
  private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

  [Fact]
  public void ReportsNoDataBeforeAnythingArrives() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    Assert.Equal(AudioDeviceState.NoData, meter.Read().State);

    // Still nothing after a full window: a device that delivers no callbacks at
    // all is ordinary for a loopback endpoint with nothing playing to it, and
    // saying so is the point of this state.
    clock.Advance(Window);
    AudioMeterReading reading = meter.Read();

    Assert.Equal(AudioDeviceState.NoData, reading.State);
    Assert.Equal(AudioLevels.None, reading.Levels);
  }

  [Fact]
  public void PublishesRmsAndPeakWhenTheWindowCloses() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    meter.Add(new PcmBlock(Frames: 2, Samples: 4, NonZeroSamples: 4, SumSquares: 1.0, Peak: 0.5));

    // Nothing is published mid-window: a callback is about ten milliseconds long
    // and a meter drawn from one flickers far too fast to read.
    Assert.Equal(AudioDeviceState.NoData, meter.Read().State);

    clock.Advance(Window);
    AudioMeterReading reading = meter.Read();

    Assert.Equal(AudioDeviceState.Signal, reading.State);
    Assert.Equal(0.5, reading.Levels.Rms, precision: 6);
    Assert.Equal(0.5, reading.Levels.Peak, precision: 6);
  }

  [Fact]
  public void CallsEverySampleBeingZeroDigitalSilence() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    meter.Add(new PcmBlock(2, 4, NonZeroSamples: 0, SumSquares: 0, Peak: 0));
    clock.Advance(Window);

    AudioMeterReading reading = meter.Read();

    // Not "quiet": music at -60 dBFS still has non-zero samples, so bit-exact
    // zeros mean a stopped deck or a closed fader. That distinction is how Phase 0
    // proved the FLX-10's input carries a digital mix rather than room sound.
    Assert.Equal(AudioDeviceState.DigitalSilence, reading.State);
    Assert.Equal(AudioLevels.None, reading.Levels);
  }

  [Fact]
  public void ClampsLevelsToFullScale() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    // The shared mixer can hand over floats past full scale, and the protocol
    // promises 0 to 1.
    meter.Add(new PcmBlock(1, 1, 1, SumSquares: 4, Peak: 1.5));
    clock.Advance(Window);

    AudioMeterReading reading = meter.Read();

    Assert.Equal(1, reading.Levels.Rms, precision: 6);
    Assert.Equal(1, reading.Levels.Peak, precision: 6);
  }

  [Fact]
  public void ClosesAnOverdueWindowFromAnArrivingBlockToo() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    meter.Add(new PcmBlock(1, 1, 1, SumSquares: 0.25, Peak: 0.5));
    clock.Advance(Window);

    // The second block belongs to the next window, and adding it publishes the
    // first. Without this, a device delivering steadily would only ever publish on
    // whatever schedule its readers happened to keep.
    meter.Add(new PcmBlock(1, 1, 1, SumSquares: 1, Peak: 1));

    Assert.Equal(0.5, meter.Read().Levels.Peak, precision: 6);
  }

  [Fact]
  public void FallsBackToNoDataWhenADeviceGoesQuiet() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    meter.Add(new PcmBlock(1, 1, 1, 0.25, 0.5));
    clock.Advance(Window);
    Assert.Equal(AudioDeviceState.Signal, meter.Read().State);

    clock.Advance(Window);

    // Reading is what closes the empty window. A device that stops calling back
    // would otherwise leave its last level on screen for the rest of the night.
    Assert.Equal(AudioDeviceState.NoData, meter.Read().State);
  }

  [Fact]
  public void StartsOverForADifferentDevice() {
    FakeClock clock = new FakeClock();
    AudioMeter meter = new AudioMeter(clock);

    meter.Add(new PcmBlock(1, 1, 1, 0.25, 0.5));
    clock.Advance(Window);
    Assert.Equal(AudioDeviceState.Signal, meter.Read().State);

    meter.Reset();

    Assert.Equal(AudioDeviceState.NoData, meter.Read().State);
    Assert.Equal(AudioLevels.None, meter.Read().Levels);
  }
}
