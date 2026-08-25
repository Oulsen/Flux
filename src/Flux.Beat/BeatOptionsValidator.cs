using Microsoft.Extensions.Options;

namespace Flux.Beat;

/// <summary>
/// Checks the beat settings before anything starts, so a bad one names itself.
///
/// Both values reach the grid and the scheduler on background threads, where a
/// nonsense quantum surfaces as an indicator that renders wrongly and a
/// non-positive staleness window as a source that is somehow always quiet.
/// Neither reads as a settings problem at that point, which is the entire reason
/// this runs at startup instead.
/// </summary>
internal sealed class BeatOptionsValidator: IValidateOptions<BeatOptions> {
  public ValidateOptionsResult Validate(string? name, BeatOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    List<string> failures = new List<string>();

    if(options.Quantum < 1) {
      failures.Add($"Beat:Quantum must be at least 1 beat per bar, not {options.Quantum}.");
    }

    if(options.StaleAfter <= TimeSpan.Zero) {
      failures.Add($"Beat:StaleAfter must be greater than zero, not {options.StaleAfter}.");
    }

    // Only emptiness is checkable here. Whether the name matches a source depends
    // on which sources this build registered, which nothing knows yet at options
    // time - and an unknown name is survivable by design, where a blank one just
    // means the setting was half-edited.
    if(string.IsNullOrWhiteSpace(options.DefaultSource)) {
      failures.Add("Beat:DefaultSource must name a sync source, such as 'link'.");
    }

    return failures.Count == 0
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(failures);
  }
}
