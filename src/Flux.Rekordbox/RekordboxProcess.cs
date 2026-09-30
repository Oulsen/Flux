using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flux.Rekordbox;

/// <summary>Why <see cref="RekordboxProcess.Attach"/> did or did not get a process to read.</summary>
internal enum RekordboxAttachState {
  Attached,

  /// <summary>No <c>rekordbox.exe</c> in this session.</summary>
  NotRunning,

  /// <summary>
  /// rekordbox runs as administrator and Flux does not. Windows refuses a process read
  /// access to one above it, and that is right, so this is not something to work
  /// around: the fix is starting rekordbox normally, and the log has to say that
  /// rather than a bare "access denied".
  /// </summary>
  Elevated,

  /// <summary>Opened, but refused for a reason other than elevation.</summary>
  Denied,

  /// <summary>Not a 64-bit Flux, so the process layout below does not apply.</summary>
  Unsupported,
}

/// <summary>The outcome of one attach attempt. <see cref="Process"/> is set only when attached.</summary>
internal readonly record struct RekordboxAttachResult(RekordboxAttachState State, RekordboxProcess? Process);

/// <summary>
/// A running rekordbox, opened to read and for nothing else.
///
/// The handle carries <c>PROCESS_VM_READ</c> and <c>PROCESS_QUERY_LIMITED_INFORMATION</c>
/// and no other right. Everything this class needs fits inside those two, which is why
/// it does not use the obvious tools: <see cref="Process.MainModule"/> and a toolhelp
/// module snapshot both open the process again with <c>PROCESS_QUERY_INFORMATION</c>,
/// so the module base comes from the process environment block instead, read through
/// the same handle, and the executable's path from <c>QueryFullProcessImageName</c>,
/// which the limited right allows.
///
/// The version is read from the executable on disk, not from memory. The chains are
/// per rekordbox version, and a chain from one version walked in another can land on a
/// plausible value in the wrong place, so the version has to come from somewhere the
/// chains are not.
/// </summary>
internal sealed class RekordboxProcess: IScannableMemory, IDisposable {
  private const string ProcessName = "rekordbox";

  private const uint VmRead = 0x0010;
  private const uint QueryLimitedInformation = 0x1000;
  private const int AccessDenied = 5;
  private const uint TokenQuery = 0x0008;
  private const int TokenElevation = 20;
  private const int ProcessBasicInformationClass = 0;
  private const uint StillActive = 259;

  /// <summary><c>PEB.ImageBaseAddress</c> on x64: the base the executable was mapped at.</summary>
  private const int PebImageBaseOffset = 0x10;

  private readonly SafeProcessHandle handle;

  private RekordboxProcess(SafeProcessHandle handle, int processId, long moduleBase, string executablePath, Version version) {
    this.handle = handle;
    this.ProcessId = processId;
    this.ModuleBase = moduleBase;
    this.ExecutablePath = executablePath;
    this.Version = version;
  }

  internal int ProcessId { get; }

  /// <summary>Where <c>rekordbox.exe</c> is mapped. Every chain starts here.</summary>
  public long ModuleBase { get; }

  internal string ExecutablePath { get; }

  /// <summary>The file version of the executable, <c>7.2.19.0</c> and the like.</summary>
  internal Version Version { get; }

  /// <summary>
  /// Whether the process is still running. Reads from one that has exited only fail,
  /// so this is what tells "rekordbox quit" apart from "this chain broke".
  /// </summary>
  internal bool HasExited => !GetExitCodeProcess(this.handle, out uint code) || code != StillActive;

  /// <summary>
  /// Opens the first <c>rekordbox.exe</c> in this session that can be read. When none
  /// can, the reason given is the most useful one found: elevation over plain refusal,
  /// because elevation is the one the user can do something about.
  /// </summary>
  internal static RekordboxAttachResult Attach() {
    if(!Environment.Is64BitProcess) {
      return new(RekordboxAttachState.Unsupported, null);
    }

    Process[] found = Process.GetProcessesByName(ProcessName);
    try {
      if(found.Length == 0) {
        return new(RekordboxAttachState.NotRunning, null);
      }

      RekordboxAttachState best = RekordboxAttachState.NotRunning;
      foreach(Process candidate in found) {
        RekordboxAttachResult result = TryOpen(candidate.Id);
        if(result.State == RekordboxAttachState.Attached) {
          return result;
        }

        if(result.State == RekordboxAttachState.Elevated || best == RekordboxAttachState.NotRunning) {
          best = result.State;
        }
      }

      return new(best, null);
    } finally {
      foreach(Process process in found) {
        process.Dispose();
      }
    }
  }

  public bool TryRead(long address, Span<byte> destination) {
    if(destination.IsEmpty) {
      return true;
    }

    return ReadProcessMemory(this.handle, (nint)address, ref MemoryMarshal.GetReference(destination), destination.Length, out nint read)
      && read == destination.Length;
  }

  /// <summary>
  /// The committed, readable regions of the address space, in address order. For the
  /// chain finder, which has to sweep all of rekordbox's memory to find a value; Flux
  /// itself never sweeps, because it only ever walks chains it was given.
  /// </summary>
  public IEnumerable<(long Base, long Size)> Regions(bool writableOnly = false) {
    const uint Committed = 0x1000;
    const uint Guard = 0x100, NoAccess = 0x01;
    const uint Writable = 0x04 | 0x08 | 0x40 | 0x80;
    long address = 0;
    while(VirtualQueryEx(this.handle, (nint)address, out MemoryInfo info, Marshal.SizeOf<MemoryInfo>()) != 0) {
      bool readable = info.State == Committed && info.Protect != 0 && (info.Protect & (Guard | NoAccess)) == 0;
      if(readable && (!writableOnly || (info.Protect & Writable) != 0)) {
        yield return (info.BaseAddress, info.RegionSize);
      }

      // Regions come back in address order and do not overlap, so the next query
      // starts past this one. A region of no size would loop forever.
      if(info.RegionSize <= 0) {
        yield break;
      }

      address = info.BaseAddress + info.RegionSize;
    }
  }

  public void Dispose() => this.handle.Dispose();

  private static RekordboxAttachResult TryOpen(int processId) {
    SafeProcessHandle handle = OpenProcess(VmRead | QueryLimitedInformation, false, (uint)processId);
    if(handle.IsInvalid) {
      int error = Marshal.GetLastPInvokeError();
      handle.Dispose();
      if(error != AccessDenied) {
        // Gone between the listing and the open, most likely.
        return new(RekordboxAttachState.NotRunning, null);
      }

      return new(IsElevatedAboveUs(processId) ? RekordboxAttachState.Elevated : RekordboxAttachState.Denied, null);
    }

    if(!TryReadModuleBase(handle, out long moduleBase) || !TryGetImagePath(handle, out string path)) {
      handle.Dispose();
      return new(RekordboxAttachState.Denied, null);
    }

    // The handle is ours until the process object owns it, and the version read can throw
    // - an update replacing the file, say - on an attach retried every two seconds.
    try {
      FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
      var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
      return new(RekordboxAttachState.Attached, new RekordboxProcess(handle, processId, moduleBase, path, version));
    } catch {
      handle.Dispose();
      throw;
    }
  }

  /// <summary>
  /// Whether the process runs elevated while Flux does not. Asked with the limited
  /// right alone, which Windows grants even across elevation, so the answer is
  /// available exactly when the read was refused.
  /// </summary>
  private static bool IsElevatedAboveUs(int processId) {
    if(Environment.IsPrivilegedProcess) {
      return false;
    }

    using SafeProcessHandle limited = OpenProcess(QueryLimitedInformation, false, (uint)processId);
    if(limited.IsInvalid || !OpenProcessToken(limited, TokenQuery, out SafeAccessTokenHandle token)) {
      return false;
    }

    using(token) {
      return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;
    }
  }

  private static bool TryReadModuleBase(SafeProcessHandle handle, out long moduleBase) {
    moduleBase = 0;
    int status = NtQueryInformationProcess(
      handle, ProcessBasicInformationClass, out ProcessBasicInformation info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
    if(status != 0 || info.PebBaseAddress == 0) {
      return false;
    }

    Span<byte> pointer = stackalloc byte[8];
    if(!ReadProcessMemory(handle, info.PebBaseAddress + PebImageBaseOffset, ref MemoryMarshal.GetReference(pointer), 8, out nint read)
      || read != 8) {
      return false;
    }

    moduleBase = BinaryPrimitives.ReadInt64LittleEndian(pointer);
    return moduleBase != 0;
  }

  private static bool TryGetImagePath(SafeProcessHandle handle, out string path) {
    char[] buffer = new char[1024];
    int length = buffer.Length;
    if(!QueryFullProcessImageName(handle, 0, buffer, ref length)) {
      path = "";
      return false;
    }

    path = new string(buffer, 0, length);
    return true;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessBasicInformation {
    public nint ExitStatus;
    public nint PebBaseAddress;
    public nint AffinityMask;
    public nint BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, ref byte buffer, nint size, out nint read);

  [StructLayout(LayoutKind.Sequential)]
  private struct MemoryInfo {
    public long BaseAddress;
    public long AllocationBase;
    public uint AllocationProtect;
    public uint Alignment1;
    public long RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
    public uint Alignment2;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern nint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryInfo info, int length);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] path, ref int size);

  [DllImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

  [DllImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

  [DllImport("ntdll.dll")]
  private static extern int NtQueryInformationProcess(
    SafeProcessHandle process, int infoClass, out ProcessBasicInformation info, int length, out int returned);
}
