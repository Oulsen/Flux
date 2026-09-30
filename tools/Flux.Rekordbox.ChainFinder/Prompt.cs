using System.Globalization;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// Console prompts that keep asking until the answer is one the tool can use. Input that
/// ends - a closed terminal, a piped script run out - stops the tool rather than being
/// taken as an answer: the desk is not there to have done what the prompt asked, and a
/// prompt that asks again would ask forever.
/// </summary>
internal static class Prompt {
  internal static float Bpm(string question) => Read<float>(question, text =>
    float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float bpm) && bpm is > 40 and < 300
      ? bpm : null);

  internal static bool YesNo(string question) {
    Console.Write(question);
    return Line().StartsWith("y", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// What a step does and why, in grey under its heading: the run is long, and the desk
  /// is asked to do things whose point is not visible from the prompt alone.
  /// </summary>
  internal static void Why(string text) {
    ConsoleColor was = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(text);
    Console.ForegroundColor = was;
  }

  internal static void Enter() {
    Console.Write("Press Enter when ready. ");
    Line();
  }

  private static T Read<T>(string question, Func<string, T?> parse) where T : struct {
    while(true) {
      Console.Write(question);
      if(parse(Line()) is { } value) {
        return value;
      }

      Console.WriteLine("  Not a valid answer; try again.");
    }
  }

  private static string Line() {
    string? line = Console.ReadLine();
    if(line is null) {
      Console.WriteLine("\nInput ended before the run did. Nothing was written.");
      Environment.Exit(1);
    }

    return line!.Trim();
  }
}
