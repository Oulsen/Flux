using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace Flux.Web.Server;

/// <summary>
/// Checks the web settings before Kestrel is handed them.
///
/// Timing is the whole value here. The listening URL is built from these values
/// during startup, and Kestrel's own complaint about a malformed one names
/// neither the setting nor the file it came from - so a typo surfaces as an
/// address-parsing stack trace from deep inside the host rather than as a
/// sentence saying which key to fix.
/// </summary>
internal sealed class WebOptionsValidator: IValidateOptions<WebOptions> {
  public ValidateOptionsResult Validate(string? name, WebOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    List<string> failures = new List<string>();

    if(!IPAddress.TryParse(options.BindAddress, out IPAddress? bindTo)
      || bindTo.AddressFamily != AddressFamily.InterNetwork) {
      failures.Add($"Web:BindAddress must be an IPv4 address, not '{options.BindAddress}'.");
    }

    if(options.Port is < 1 or > 65535) {
      failures.Add($"Web:Port must be between 1 and 65535, not {options.Port}.");
    }

    // The upper bound is not a performance limit but a sanity one: anything past
    // a display refresh is sent to nobody who can see it, and a mistyped 2000
    // would otherwise spin a timer thread for no visible reason.
    if(options.StateHz is < 1 or > 120) {
      failures.Add($"Web:StateHz must be between 1 and 120, not {options.StateHz}.");
    }

    if(options.ClientQueueCapacity < 1) {
      failures.Add($"Web:ClientQueueCapacity must be at least 1, not {options.ClientQueueCapacity}.");
    }

    return failures.Count == 0
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(failures);
  }
}
