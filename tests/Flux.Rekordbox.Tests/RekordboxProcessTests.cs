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
}
