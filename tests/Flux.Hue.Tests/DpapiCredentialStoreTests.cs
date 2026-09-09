using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// Exercises the store against real DPAPI in a temporary folder, rather than against
/// a fake. The encryption is the part with something to get wrong - a blob that
/// round-trips in memory and cannot be read back from disk would pass every test
/// written against an abstraction over it.
/// </summary>
public sealed class DpapiCredentialStoreTests: IDisposable {
  private readonly string _directory = Path.Combine(Path.GetTempPath(), "flux-hue-tests-" + Guid.NewGuid().ToString("n"));

  public void Dispose() {
    if(Directory.Exists(_directory)) {
      Directory.Delete(_directory, recursive: true);
    }
  }

  [Fact]
  public void LoadsNothingBeforeAnythingIsPaired() {
    Assert.Null(Store().Load());
  }

  [Fact]
  public void RoundTripsWhatPairingProduced() {
    DpapiCredentialStore store = Store();
    HueCredentials saved = new HueCredentials("ecb5fafffe0d1234", "9pJ4XkQ2mN8vB1cD", "A1B2C3D4E5F60718");

    store.Save(saved);

    Assert.Equal(saved, store.Load());
  }

  [Fact]
  public void ReadsBackWhatAnotherInstanceWrote() {
    // The case that matters: the store that saves is a different object from the one
    // that reads, one process run later.
    Store().Save(new HueCredentials("bridge", "app-key", "client-key"));

    Assert.Equal("client-key", Store().Load()?.ClientKey);
  }

  [Fact]
  public void ReplacesEarlierCredentialsRatherThanKeepingBoth() {
    DpapiCredentialStore store = Store();
    store.Save(new HueCredentials("first", "app-1", "client-1"));

    store.Save(new HueCredentials("second", "app-2", "client-2"));

    Assert.Equal("second", store.Load()?.BridgeId);
  }

  [Fact]
  public void ClearForgetsTheBridge() {
    DpapiCredentialStore store = Store();
    store.Save(new HueCredentials("bridge", "app-key", "client-key"));

    store.Clear();

    Assert.Null(store.Load());
  }

  [Fact]
  public void ClearIsSafeWithNothingStored() {
    // Offered in the UI whether or not anything is paired, so refusing here would
    // turn a button into a crash report.
    Store().Clear();
  }

  [Fact]
  public void TreatsAnUnreadableBlobAsNothingStored() {
    // What a profile copied from another machine or another Windows account looks
    // like from here. The caller's answer to "nothing stored" is to offer pairing,
    // which is also the only cure for this, so it must not throw.
    Directory.CreateDirectory(_directory);
    File.WriteAllBytes(Path.Combine(_directory, "hue-credentials.dat"), [0x00, 0x01, 0x02, 0x03, 0x04]);

    Assert.Null(Store().Load());
  }

  [Fact]
  public void WritesNoKeyAnyoneCanReadOffTheDisk() {
    // The whole point of the store, asserted rather than assumed: the DTLS pre-shared
    // key must not appear in the file that holds it.
    Store().Save(new HueCredentials("bridge", "application-key-plaintext", "client-key-plaintext"));

    byte[] onDisk = File.ReadAllBytes(Path.Combine(_directory, "hue-credentials.dat"));

    // Compared as hex so the search is for the exact byte sequence rather than for
    // any encoding of it that a text conversion might have mangled.
    Assert.DoesNotContain(
      Convert.ToHexString(Encoding.UTF8.GetBytes("client-key-plaintext")),
      Convert.ToHexString(onDisk),
      StringComparison.Ordinal);
  }

  private DpapiCredentialStore Store() {
    return new DpapiCredentialStore(_directory, NullLogger<DpapiCredentialStore>.Instance);
  }
}
