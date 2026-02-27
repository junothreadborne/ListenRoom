using Hangfire;
using Hangfire.Storage.SQLite;
using Microsoft.EntityFrameworkCore;
using ListenRoom.Web.Data;
using ListenRoom.Web.Models;
using ListenRoom.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<ListenRoomOptions>(
    builder.Configuration.GetSection(ListenRoomOptions.SectionName));

// EF Core + SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Services
builder.Services.AddScoped<SessionService>();

// Controllers
builder.Services.AddControllers();

// Hangfire
builder.Services.AddHangfire(config =>
    config.UseSQLiteStorage());
builder.Services.AddHangfireServer(options =>
{
    var workerCount = builder.Configuration.GetValue<int>("Hangfire:WorkerCount", 2);
    options.WorkerCount = workerCount;
});

var app = builder.Build();

// Ensure directories exist
var listenRoomOptions = builder.Configuration.GetSection(ListenRoomOptions.SectionName).Get<ListenRoomOptions>()
    ?? new ListenRoomOptions();
Directory.CreateDirectory(Path.GetFullPath(listenRoomOptions.AudioDirectory));
Directory.CreateDirectory(Path.GetFullPath(listenRoomOptions.RecordingsDirectory));

// Apply migrations automatically
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // Mark any stale active sessions from a previous run
    var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
    await sessionService.MarkSessionsInterruptedAsync();
}

app.UseStaticFiles();
app.UseHangfireDashboard("/hangfire");
app.MapControllers();

// Fallback: serve index.html for the root
app.MapFallbackToFile("index.html");

app.Run();
