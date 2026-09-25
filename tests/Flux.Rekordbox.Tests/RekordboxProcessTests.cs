using System.Diagnostics;
using Xunit;

namespace Flux.Rekordbox.Tests;

public class RekordboxProcessTests {
  [Fact]
  public void FindsTheModuleBaseWindowsReportsForARunningRekordbox() {
    // The attach reads the base from the process environment block so that it can do
    // without PROCESS_QUERY_INFORMATION. That route is only right if it agrees with
    // the one that uses the wider right, and the test may use it: it is not Flux.
    // Skips without a rekordbox, which is every CI run.
    RekordboxAttachResult attach = RekordboxProcess.Attach();
    if(attach.State == RekordboxAttachState.NotRunning) {
      Assert.Skip("No rekordbox running.");
    }

    if(attach.State == RekordboxAttachState.Elevated) {
      Assert.Skip("rekordbox runs as administrator and this test does not, so it cannot be read.");
    }

    Assert.Equal(RekordboxAttachState.Attached, attach.State);
    using RekordboxProcess rekordbox = attach.Process!;
    using Process windows = Process.GetProcessById(rekordbox.ProcessId);

    Assert.Equal((long)windows.MainModule!.BaseAddress, rekordbox.ModuleBase);
    Assert.Equal(windows.MainModule.FileName, rekordbox.ExecutablePath, ignoreCase: true);
    Assert.False(rekordbox.HasExited);
  }

  [Fact]
  public void ReadsARunningRekordboxThroughItsTableEntry() {
    // The table entry against the real process rather than against memory laid out
    // from the same entry. What a deck holds depends on the desk, so this asserts
    // only what holds with any tracks: every chain resolves, and the master names a
    // deck. Skips without a rekordbox, and for a version the table has no entry for.
    RekordboxAttachResult attach = RekordboxProcess.Attach();
    if(attach.State != RekordboxAttachState.Attached) {
      Assert.Skip($"No readable rekordbox ({attach.State}).");
    }

    using RekordboxProcess rekordbox = attach.Process!;
    if(!RekordboxChains.TryGet(rekordbox.Version, out RekordboxChains chains)) {
      Assert.Skip($"rekordbox {rekordbox.Version} has no chain table entry.");
    }

    RekordboxReading reading = chains.Read(rekordbox, rekordbox.ModuleBase);

    Assert.All(reading.Decks, deck => Assert.NotNull(deck.Bpm));
    Assert.All(reading.Decks, deck => Assert.NotNull(deck.SamplePosition));
    Assert.NotNull(reading.MasterDeck);
  }
}
