using System.Globalization;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>Console prompts that keep asking until the answer is one the tool can use.</summary>
internal static class Prompt {
  internal static float Bpm(string question) => Read<float>(question, text =>
    float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float bpm) && bpm is > 40 and < 300
      ? bpm : null);

  internal static int Master(string question) => Read<int>(question, text =>
    int.TryParse(text, out int deck) && deck is >= 1 and <= 4 ? deck : null);

  internal static bool YesNo(string question) {
    Console.Write(question);
    return Console.ReadLine()?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
  }

  internal static void Enter() {
    Console.Write("Press Enter when ready. ");
    Console.ReadLine();
  }

  private static T Read<T>(string question, Func<string, T?> parse) where T : struct {
    while(true) {
      Console.Write(question);
      if(parse(Console.ReadLine()?.Trim() ?? "") is { } value) {
        return value;
      }

      Console.WriteLine("  Not a valid answer; try again.");
    }
  }
}
