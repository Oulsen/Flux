namespace Flux.Rekordbox;

/// <summary>
/// Read access to another process's address space, and nothing more.
///
/// The seam exists for the tests: a pointer chain is only worth trusting once it has
/// been walked against memory laid out on purpose, and the real process is rekordbox
/// on somebody's desk. There is deliberately no write here, and no way to ask for one.
/// </summary>
internal interface IProcessMemory {
  /// <summary>
  /// Fills <paramref name="destination"/> from <paramref name="address"/>. False when
  /// any of it cannot be read, which for another process's memory is ordinary: a page
  /// freed since the pointer to it was read, or a pointer that was never a pointer.
  /// </summary>
  bool TryRead(long address, Span<byte> destination);
}

/// <summary>
/// Read access plus the two things a memory sweep needs that a single chain does not:
/// where the module is, and which regions are worth reading. The chain finder sweeps;
/// Flux itself only walks chains, so it needs no more than <see cref="IProcessMemory"/>.
/// The seam lets the finder's search run against memory a test lays out.
/// </summary>
internal interface IScannableMemory: IProcessMemory {
  /// <summary>Where <c>rekordbox.exe</c> is mapped; the origin of every chain.</summary>
  long ModuleBase { get; }

  /// <summary>The committed, readable regions in address order.</summary>
  IEnumerable<(long Base, long Size)> Regions(bool writableOnly = false);
}
