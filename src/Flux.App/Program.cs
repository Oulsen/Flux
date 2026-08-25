using Flux.App;
using Flux.Audio;
using Flux.Beat;
using Flux.Web.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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
Console.WriteLine("Waiting for Ableton Link. Enable LINK in rekordbox and play a track. Ctrl+C to stop.");
Console.WriteLine();

// Registration order is start order, and the timer resolution has to be up
// before the beat scheduler starts sleeping against it. The grid and scheduler
// come next, then the sources that feed them.
builder.Services.AddHostedService<TimerResolutionService>();
builder.Services.AddBeatCore(builder.Configuration);
builder.Services.AddAbletonLink(builder.Configuration);

// Capture and analysis run whichever source is driving the beat, because the
// spectrum they feed is the instrument every later phase is checked against. A
// spectrum that went dark because the DJ selected Link would switch itself off
// exactly when it was being used to check the thing under test. The audio sync
// source, which is what turns this into a tempo, arrives with the rest of Phase 2.
builder.Services.AddAudioCapture(builder.Configuration);
builder.Services.AddAudioAnalysis();

// The per-beat console line was Phase 1's stand-in for the visualizer; the web
// app has taken that job, so it is opt-in diagnostics now - three lines a
// second, for nobody, on a default night.
if(builder.Configuration.GetValue("Console:BeatLog", defaultValue: false)) {
  builder.Services.AddHostedService<ConsoleBeatMonitor>();
}

// The same arrangement for the capture level, and for a sharper reason: a device
// list can only show names, and on this rig the endpoint carrying the master mix
// is called "Microfoon". Only a level that falls when the channel fader does can
// tell the right device from the thirteen silent ones beside it.
if(builder.Configuration.GetValue("Console:AudioMeter", defaultValue: false)) {
  builder.Services.AddHostedService<ConsoleAudioMonitor>();
}

builder.AddFluxWeb();

WebApplication app = builder.Build();
app.MapFluxWeb();

return await app.RunFluxAsync().ConfigureAwait(false);
