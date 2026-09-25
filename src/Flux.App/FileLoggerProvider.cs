using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Flux.App;

/// <summary>
/// The log, in a file, now that a window application has no console to print it on.
///
/// Written by one background task from a bounded queue, never by the caller. The beat
/// scheduler, the stream loop and the audio thread all log, and a disk that stalls for
/// a moment must cost them a dropped line rather than a late beat. The queue is large
/// against anything Flux logs at its configured levels, so a line is only lost in the
/// case where the alternative was blocking.
///
/// One file per day, named by the day the process started rather than rolled at
/// midnight: a set that runs past twelve is one night and reads better as one file.
/// Files older than <see cref="RetainedDays"/> are deleted at startup.
///
/// A copy that finds the day's file held by another writes its own, named with its
/// process id. That is the copy launched while the last one is still stopping, and the
/// alternative - both appending to one file - has each overwrite the other's lines,
/// which would be the old copy's shutdown and the new one's reason for failing to
/// start: the two things anyone opening the log then is looking for.
/// </summary>
internal sealed class FileLoggerProvider: ILoggerProvider {
  /// <summary>
  /// How many days of logs are kept. Two weeks, reasoned rather than measured: long
  /// enough to still hold last weekend's set when somebody looks into it midweek,
  /// short enough that the folder never becomes a thing to clean up.
  /// </summary>
  internal const int RetainedDays = 14;

  /// <summary>Lines held for the writer before new ones are dropped.</summary>
  private const int QueueCapacity = 4096;

  private const string Prefix = "flux-";
  private const string Extension = ".log";
  private const string DateFormat = "yyyyMMdd";

  private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

  private readonly Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity) {
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleReader = true,
  });

  private readonly Task writing;

  /// <param name="directory">The log folder, created if missing.</param>
  /// <param name="now">The local time at startup, which names the file and dates the retention.</param>
  internal FileLoggerProvider(string directory, DateTime now) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);

    DateOnly today = DateOnly.FromDateTime(now);

    // Opened here rather than by the writer, so the path is settled before anything
    // can ask for it: a startup failure names it, and it has to name the file this run
    // actually wrote.
    (this.Path, StreamWriter? file) = Open(directory, today);
    this.writing = Task.Run(() => this.WriteAsync(file, directory, today));
  }

  /// <summary>The file this run writes to, for pointing somebody at it.</summary>
  internal string Path { get; }

  public ILogger CreateLogger(string categoryName) {
    return new FileLogger(this, categoryName);
  }

  /// <summary>
  /// Lets the writer finish what is queued, for a bounded time. Safe to call more than
  /// once, which a crash does: the crash handler flushes, then the normal way out does.
  ///
  /// Never throws. This runs on the way out of the process, and a writer that failed on
  /// something unexpected has already lost its lines; rethrowing that here would only
  /// replace a clean exit with a crash about the log.
  /// </summary>
  public void Dispose() {
    this.lines.Writer.TryComplete();

    try {
      this.writing.Wait(FlushTimeout);
    } catch(AggregateException) {
      // The writer's own failure, already past saving.
    }
  }

  /// <summary>The name of the file for a day.</summary>
  internal static string FileName(DateOnly day) {
    return Prefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension;
  }

  /// <summary>The name of the file for a day, for a copy that found that day's file taken.</summary>
  internal static string FileName(DateOnly day, int processId) {
    return Prefix
      + day.ToString(DateFormat, CultureInfo.InvariantCulture)
      + "-" + processId.ToString(CultureInfo.InvariantCulture)
      + Extension;
  }

  /// <summary>
  /// Which of these file names are logs past their retention.
  ///
  /// Decided from the date in the name and not from the file's timestamps, so nothing
  /// that is not one of Flux's own logs can match: a file somebody dropped into the
  /// folder is left alone, whatever its age.
  /// </summary>
  internal static IEnumerable<string> Expired(IEnumerable<string> fileNames, DateOnly today) {
    DateOnly oldestKept = today.AddDays(-RetainedDays);

    foreach(string name in fileNames) {
      if(!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      string stem = name[Prefix.Length..^Extension.Length];

      if(stem.Length < DateFormat.Length) {
        continue;
      }

      string date = stem[..DateFormat.Length];
      string processId = stem[DateFormat.Length..];

      // Nothing after the date, or a dash and a process id: the two names Flux writes.
      if(processId.Length > 0
        && (processId.Length < 2 || processId[0] != '-' || !processId[1..].All(char.IsAsciiDigit))) {
        continue;
      }

      if(DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
        && day < oldestKept) {
        yield return name;
      }
    }
  }

  internal void Enqueue(string line) {
    this.lines.Writer.TryWrite(line);
  }

  /// <summary>
  /// Opens the day's file, or this copy's own when another copy holds it.
  ///
  /// Nowhere to write means no file, and the lines are drained and dropped. There is no
  /// second place to report that to, and a log that cannot be written is not a reason
  /// for the lights to stop.
  /// </summary>
  private static (string Path, StreamWriter? File) Open(string directory, DateOnly today) {
    string shared = System.IO.Path.Combine(directory, FileName(today));

    try {
      Directory.CreateDirectory(directory);
      return (shared, OpenForAppend(shared));
    } catch(UnauthorizedAccessException) {
      return (shared, null);
    } catch(IOException) {
      // Most likely the copy that is still stopping, which has it open for writing.
    }

    string own = System.IO.Path.Combine(directory, FileName(today, Environment.ProcessId));

    try {
      return (own, OpenForAppend(own));
    } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
      return (shared, null);
    }
  }

  /// <summary>
  /// Shared for reading only, so the log can be opened while Flux is running, which is
  /// when somebody wants to read it, and a second writer is refused rather than let in
  /// to write over this one's lines.
  /// </summary>
  private static StreamWriter OpenForAppend(string path) {
    return new StreamWriter(
      new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
      new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
  }

  private async Task WriteAsync(StreamWriter? file, string directory, DateOnly today) {
    try {
      DeleteExpired(directory, today);
    } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
      // A folder that cannot be listed keeps its old logs until a later start.
    }

    try {
      while(await this.lines.Reader.WaitToReadAsync().ConfigureAwait(false)) {
        while(this.lines.Reader.TryRead(out string? line)) {
          file?.Write(line);
        }

        // Flushed per batch rather than per line: whatever arrived together is written
        // together, and a crash loses at most the batch in hand.
        file?.Flush();
      }
    } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
      // A disk that fails partway is the same answer as one that was never there.
    } finally {
      if(file is not null) {
        await file.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  private static void DeleteExpired(string directory, DateOnly today) {
    IEnumerable<string> names = Directory
      .EnumerateFiles(directory, Prefix + "*" + Extension)
      .Select(System.IO.Path.GetFileName)
      .OfType<string>();

    foreach(string name in Expired(names, today)) {
      try {
        File.Delete(System.IO.Path.Combine(directory, name));
      } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
        // Open in an editor, most likely. It goes on a later start.
      }
    }
  }

  private sealed class FileLogger(FileLoggerProvider provider, string category): ILogger {
    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull {
      return null;
    }

    // Filtering is the logger factory's, from the Logging section of appsettings, so a
    // level reaching this far is one somebody asked for.
    public bool IsEnabled(LogLevel logLevel) {
      return logLevel != LogLevel.None;
    }

    public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      if(!this.IsEnabled(logLevel)) {
        return;
      }

      StringBuilder line = new StringBuilder()
        .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
        .Append(' ')
        .Append(Label(logLevel))
        .Append(' ')
        .Append(category)
        .Append(": ")
        .AppendLine(formatter(state, exception));

      if(exception is not null) {
        line.AppendLine(exception.ToString());
      }

      provider.Enqueue(line.ToString());
    }

    /// <summary>The console logger's four-letter labels, so the two read alike.</summary>
    private static string Label(LogLevel level) {
      return level switch {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
      };
    }
  }
}
