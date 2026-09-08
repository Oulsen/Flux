using Flux.Audio;
using Flux.Beat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Flux.Web.Server;

/// <summary>Wires the web layer into the host.</summary>
public static class WebHostExtensions {
  /// <summary>
  /// Registers the hub and the broadcaster, and points Kestrel at the
  /// configured address.
  ///
  /// The settings are read and validated here rather than only on start,
  /// because the listening URL is built from them a few lines later. Left to
  /// Kestrel, a mistyped port surfaces as an address-parsing failure that names
  /// neither the key nor the file it came from.
  /// </summary>
  public static WebApplicationBuilder AddFluxWeb(this WebApplicationBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);

    IConfigurationSection section = builder.Configuration.GetSection(WebOptions.SectionName);

    WebOptions options = new WebOptions();
    section.Bind(options);

    ValidateOptionsResult validation = new WebOptionsValidator().Validate(Options.DefaultName, options);
    if(validation.Failed) {
      throw new OptionsValidationException(Options.DefaultName, typeof(WebOptions), validation.Failures);
    }

    builder.Services.AddSingleton<IValidateOptions<WebOptions>, WebOptionsValidator>();
    builder.Services.AddOptions<WebOptions>().Bind(section).ValidateOnStart();

    // Built by a factory rather than by constructor discovery, because the audio
    // seam is genuinely optional: this container may hold no IAudioDevices at all,
    // and GetService is the only resolution that answers null instead of throwing.
    // The alternative - an empty interface implementation registered as a
    // placeholder - would make "there is no audio in this build" indistinguishable
    // from "there are no devices on this machine".
    builder.Services.AddSingleton<SocketHub>(provider => new SocketHub(
      provider.GetRequiredService<IOptions<WebOptions>>(),
      provider.GetRequiredService<IOptions<BeatOptions>>(),
      provider.GetRequiredService<SyncSourceManager>(),
      provider.GetRequiredService<ManualSource>(),
      provider.GetService<IAudioDevices>(),
      provider.GetRequiredService<ILogger<SocketHub>>()));

    builder.Services.AddHostedService<BeatBroadcaster>();

    // Both audio feeds are registered whether or not this build has audio. Each one
    // starts nothing when its seam is absent, which keeps the decision in one place
    // rather than making every host that wants a web UI know whether it has a sound
    // card.
    builder.Services.AddHostedService(provider => new SpectrumBroadcaster(
      provider.GetRequiredService<SocketHub>(),
      provider.GetService<IAudioSpectrum>(),
      provider.GetRequiredService<IOptions<WebOptions>>()));

    builder.Services.AddHostedService(provider => new DevicesBroadcaster(
      provider.GetRequiredService<SocketHub>(),
      provider.GetService<IAudioDevices>(),
      provider.GetRequiredService<IOptions<WebOptions>>()));

    // Listening is configured rather than described in a URL string: the address
    // has just been parsed and checked, and handing Kestrel the parsed value
    // keeps it from being re-parsed under different rules.
    builder.WebHost.ConfigureKestrel(kestrel => {
      // Bounded because the socket is unauthenticated on the LAN and Kestrel's
      // default is unlimited: each upgraded connection pins a receive buffer and
      // a queue, and far fewer than this many screens is already every phone in
      // the room.
      kestrel.Limits.MaxConcurrentUpgradedConnections = 64;
      kestrel.Listen(IPAddress.Parse(options.BindAddress), options.Port);
    });

    return builder;
  }

  /// <summary>
  /// Serves the single-page app and the WebSocket it talks over.
  /// </summary>
  public static WebApplication MapFluxWeb(this WebApplication app) {
    ArgumentNullException.ThrowIfNull(app);

    app.UseWebSockets();
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.Map("/ws", async (HttpContext context, SocketHub hub, IHostApplicationLifetime lifetime) => {
      if(!context.WebSockets.IsWebSocketRequest) {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("This endpoint expects a WebSocket upgrade.").ConfigureAwait(false);
        return;
      }

      if(!IsOwnOrigin(context.Request)) {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
      }

      using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

      // Linked to ApplicationStopping as well as the request, so an open browser
      // tab cannot hold Ctrl+C waiting for the shutdown timeout: a socket with
      // nothing to say is exactly the connection Kestrel would otherwise sit and
      // wait on.
      using CancellationTokenSource closing = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted,
        lifetime.ApplicationStopping);

      await hub.ServeAsync(socket, closing.Token).ConfigureAwait(false);
    });

    ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Flux.Web.Server");
    WarnIfFrontendMissing(app, log);
    app.Lifetime.ApplicationStarted.Register(() => AnnounceUrls(app, log));

    return app;
  }

  /// <summary>
  /// Runs the host, and turns the one startup failure that is genuinely likely
  /// into a sentence.
  ///
  /// A port already in use is not an edge case here: launching Flux twice is the
  /// obvious way to meet it, and a stock Windows 11 already listens on some of
  /// the plausible ports. Unhandled, it arrives as forty lines of Kestrel stack
  /// trace whose actual content is "something else has the port".
  /// </summary>
  /// <returns>A process exit code.</returns>
  public static async Task<int> RunFluxAsync(this WebApplication app) {
    ArgumentNullException.ThrowIfNull(app);

    // Read before the run, not inside the catch. A failed start disposes the
    // service provider on its way out, so a lookup afterwards throws
    // ObjectDisposedException and buries the very cause this exists to report.
    int port = app.Services.GetRequiredService<IOptions<WebOptions>>().Value.Port;

    try {
      await app.RunAsync().ConfigureAwait(false);
      return 0;
    } catch(IOException ex) when(ex.InnerException is AddressInUseException) {
      // Written to the console rather than logged, for the same reason: the
      // logging providers are being torn down alongside everything else, and a
      // fatal startup message that might not be delivered is worth nothing.
      Console.Error.WriteLine();
      Console.Error.WriteLine(
        $"Port {port} is already in use, so Flux has no web interface and has stopped.");
      Console.Error.WriteLine(
        "Another copy of Flux may already be running. Otherwise set Web:Port in appsettings.json to a free port.");

      return 1;
    }
  }

  /// <summary>
  /// Rejects a WebSocket upgrade requested by a page Flux did not serve.
  ///
  /// Worth doing even though version 1's only client message is a ping, because
  /// the same-origin policy does not cover WebSockets: any page the DJ happens to
  /// have open can open a socket to a known local port, and the browser will
  /// send it. Phase 3 puts bridge pairing on this socket, so the check wants to
  /// exist before the thing worth protecting arrives rather than after.
  ///
  /// A missing Origin is allowed: that is a native client, not a browser, and it
  /// could forge the header regardless - the header is a defence against pages,
  /// and only pages are obliged to send it honestly.
  /// </summary>
  internal static bool IsOwnOrigin(HttpRequest request) {
    string? origin = request.Headers.Origin;
    if(string.IsNullOrEmpty(origin)) {
      return true;
    }

    if(!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed)) {
      return false;
    }

    // The dev server is a second origin by definition - Vite serves the page on
    // its own port and proxies this socket - so loopback is allowed outright
    // rather than by matching a port that changes. That trusts pages served by
    // any local process, which is proportionate while the socket only carries
    // beats: tighten this before Phase 3 puts bridge pairing on it.
    if(IPAddress.TryParse(parsed.Host, out IPAddress? address) && IPAddress.IsLoopback(address)) {
      return true;
    }

    return string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
      || string.Equals(parsed.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Says so when the SPA has not been built.
  ///
  /// The build is two steps by design - Vite writes into wwwroot, then dotnet
  /// builds - so a fresh clone that skips the first one produces a backend that
  /// starts perfectly and serves a blank 404. One line at startup is the
  /// difference between that and a puzzle.
  /// </summary>
  private static void WarnIfFrontendMissing(WebApplication app, ILogger log) {
    string root = app.Environment.WebRootPath
      ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");

    if(!File.Exists(Path.Combine(root, "index.html"))) {
      log.LogWarning(
        "No web app found in {Root}. Run `npm install && npm run build` in src/Flux.Web.Frontend, then build again.",
        root);
    }
  }

  /// <summary>
  /// Prints the addresses the page can actually be opened on, including from the
  /// phone.
  ///
  /// Kestrel's own "Now listening on http://0.0.0.0:5040" is true and useless
  /// from another device. These are the URLs to type in.
  /// </summary>
  private static void AnnounceUrls(WebApplication app, ILogger log) {
    WebOptions options = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;

    log.LogInformation("Flux is on http://localhost:{Port}", options.Port);

    // A specific bind address is already the whole answer; only the wildcard
    // needs the interface list expanded.
    if(!string.Equals(options.BindAddress, IPAddress.Any.ToString(), StringComparison.Ordinal)) {
      return;
    }

    foreach(string address in LocalAddresses()) {
      log.LogInformation("  and on http://{Address}:{Port} from the phone", address, options.Port);
    }
  }

  private static IEnumerable<string> LocalAddresses() {
    return NetworkInterface.GetAllNetworkInterfaces()
      .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
      .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
      .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
      .Select(unicast => unicast.Address)
      .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
      .Select(address => address.ToString());
  }
}
