using Flux.App;
using Flux.Beat;
using Flux.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// The host process, from Phase 1 to release. It is a console today and grows a
// WinForms + WebView2 window in Phase 5 - later phases change what this process
// hosts, never which executable runs.
//
// The content root is pinned to the executable's own folder rather than left to
// the working directory. An installed app is launched from a shortcut or the
// tray, where the working directory is whatever Windows felt like - and a Flux
// that silently ignores its own appsettings.json depending on how it was started
// is a bug that only shows up after packaging. It decides where wwwroot is
// found, too, which turns the same trap into a blank page.
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
  Args = args,
  ContentRootPath = AppContext.BaseDirectory,
});

Console.WriteLine($"Flux {FluxVersion.Current} - music in, light out.");
Console.WriteLine();

// Registration order is start order, and the timer resolution has to be up
// before the beat scheduler starts sleeping against it.
builder.Services.AddHostedService<TimerResolutionService>();
builder.Services.AddAbletonLink(builder.Configuration);
builder.Services.AddHostedService<ConsoleBeatMonitor>();
builder.AddFluxWeb();

WebApplication app = builder.Build();
app.MapFluxWeb();

return await app.RunFluxAsync().ConfigureAwait(false);
