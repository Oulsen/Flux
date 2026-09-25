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
