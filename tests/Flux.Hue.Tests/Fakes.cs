namespace Flux.Hue.Tests;

/// <summary>
/// A bridge that does whatever a test needs, including the two things a real one
/// cannot be asked to do on demand: refuse to register because nobody has pressed a
/// button yet, and then stop refusing.
/// </summary>
internal sealed class FakeHueBridgeClient: IHueBridgeClient {
  /// <summary>What discovery answers with.</summary>
  public List<HueBridge> Bridges { get; set; } = [];

  /// <summary>What the bridge reports when asked for its areas.</summary>
  public List<HueArea> Areas { get; set; } = [];

  /// <summary>
  /// Answers to successive pairing attempts. A null is "the link button has not been
  /// pressed"; running out means it never is.
  /// </summary>
  public Queue<HueCredentials?> PairAnswers { get; } = new Queue<HueCredentials?>();

  public Exception? DiscoverThrows { get; set; }

  public Exception? AreasThrows { get; set; }

  public int PairAttempts { get; private set; }

  public int DiscoverCalls { get; private set; }

  public int AreaCalls { get; private set; }

  public Task<IReadOnlyList<HueBridge>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken) {
    DiscoverCalls++;
    return DiscoverThrows is not null
      ? Task.FromException<IReadOnlyList<HueBridge>>(DiscoverThrows)
      : Task.FromResult<IReadOnlyList<HueBridge>>([.. Bridges]);
  }

  public Task<HueCredentials?> TryPairAsync(HueBridge bridge, CancellationToken cancellationToken) {
    PairAttempts++;
    return Task.FromResult(PairAnswers.Count > 0 ? PairAnswers.Dequeue() : null);
  }

  /// <summary>What answers at an address, for the fallback discovery cannot cover.</summary>
  public Dictionary<string, HueBridge> AtAddress { get; } = [];

  public int DescribeCalls { get; private set; }

  public Task<HueBridge?> TryDescribeAsync(string address, CancellationToken cancellationToken) {
    DescribeCalls++;
    return Task.FromResult(AtAddress.GetValueOrDefault(address));
  }

  public Task<IReadOnlyList<HueArea>> GetAreasAsync(HueBridge bridge, HueCredentials credentials, CancellationToken cancellationToken) {
    AreaCalls++;

    return AreasThrows is not null
      ? Task.FromException<IReadOnlyList<HueArea>>(AreasThrows)
      : Task.FromResult<IReadOnlyList<HueArea>>([.. Areas]);
  }
}

/// <summary>The credential store without the encryption or the disk.</summary>
internal sealed class InMemoryCredentialStore: ICredentialStore {
  public HueCredentials? Stored { get; set; }

  public HueCredentials? Load() => Stored;

  public void Save(HueCredentials credentials) => Stored = credentials;

  public void Clear() => Stored = null;
}

/// <summary>The settings store without the disk.</summary>
internal sealed class InMemorySettingsStore: IHueSettingsStore {
  public HueSettings Stored { get; set; } = HueSettings.Empty;

  public HueSettings Load() => Stored;

  public void Save(HueSettings settings) => Stored = settings;
}
