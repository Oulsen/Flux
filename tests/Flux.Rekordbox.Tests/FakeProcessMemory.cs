using System.Buffers.Binary;
using System.Text;

namespace Flux.Rekordbox.Tests;

/// <summary>
/// A process address space made of the blocks a test puts there. A read succeeds only
/// when it falls entirely inside one block, which is how an unmapped neighbour behaves
/// in the real thing.
/// </summary>
internal sealed class FakeProcessMemory: IProcessMemory {
  private readonly List<(long Start, byte[] Bytes)> blocks = [];

  internal void Put(long address, byte[] bytes) => this.blocks.Add((address, bytes));

  internal void PutPointer(long address, long pointer) {
    byte[] bytes = new byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, pointer);
    this.Put(address, bytes);
  }

  internal void PutSingle(long address, float value) {
    byte[] bytes = new byte[4];
    BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
    this.Put(address, bytes);
  }

  internal void PutInt64(long address, long value) => this.PutPointer(address, value);

  internal void PutUtf8(long address, string text) => this.Put(address, [.. Encoding.UTF8.GetBytes(text), 0]);

  public bool TryRead(long address, Span<byte> destination) {
    foreach((long start, byte[] bytes) in this.blocks) {
      if(address >= start && address + destination.Length <= start + bytes.Length) {
        bytes.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
        return true;
      }
    }

    return false;
  }
}
