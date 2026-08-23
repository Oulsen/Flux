using Flux.App;
using Flux.Beat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;

// The host process, from Phase 1 to release. It is a console today and grows a
// WinForms + WebView2 window in Phase 5 - later phases change what this process
// hosts, never which executable runs.
//
// The content root is pinned to the executable's own folder rather than left to
// the working directory. An installed app is launched from a shortcut or the
// tray, where the working directory is whatever Windows felt like - and a Flux
// that silently ignores its own appsettings.json depending on how it was started
// is a bug that only shows up after packaging.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings {
  Args = args,
  ContentRootPath = AppContext.BaseDirectory,
});

Console.WriteLine($"Flux {Version()} - music in, light out.");
Console.WriteLine();

// Registration order is start order, and the timer resolution has to be up
// before the beat scheduler starts sleeping against it.
builder.Services.AddHostedService<TimerResolutionService>();
builder.Services.AddAbletonLink(builder.Configuration);
builder.Services.AddHostedService<ConsoleBeatMonitor>();

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

static string Version() {
  string? informational = Assembly.GetEntryAssembly()?
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion;

  if(informational is null) {
    return "unknown version";
  }

  // Source Link appends "+<commit sha>", which is noise on a console banner.
  int build = informational.IndexOf('+', StringComparison.Ordinal);
  return build < 0 ? informational : informational[..build];
}
