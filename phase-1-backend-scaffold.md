# Phase 1: Backend Scaffold

> Project setup, data model, EF Core + SQLite, configuration, and basic session CRUD endpoints. At the end of this phase you have a running ASP.NET Core server with a database, working REST endpoints, and no frontend beyond what's needed to verify them.

---

## Goals

- Solution and project structure created and compiling
- EF Core with SQLite, all models, initial migration applied
- Configuration system wired (`appsettings.json`)
- REST endpoints for session and audio management working and testable via curl or a REST client
- Hangfire registered (no jobs yet)
- Static file serving configured (empty `wwwroot` stubs)

---

## Project Setup

Create the solution and web project:

```
ListenRoom/
├── ListenRoom.sln
└── src/
    └── ListenRoom.Web/
```

Target framework: `.NET 8`. Project type: `ASP.NET Core Web Application` (minimal API or controller-based — use controllers per the spec structure).

NuGet packages to add:

| Package | Purpose |
|---|---|
| `Microsoft.EntityFrameworkCore.Sqlite` | SQLite provider |
| `Microsoft.EntityFrameworkCore.Design` | EF migrations tooling |
| `Hangfire.AspNetCore` | Background job framework |
| `Hangfire.SQLite` | Hangfire storage on SQLite |

---

## Configuration

`appsettings.json` — wire the `ListenRoom` section:

```json
{
  "ListenRoom": {
    "AudioDirectory": "./audio",
    "RecordingsDirectory": "./recordings",
    "MaxSessionAgeDays": 30,
    "AllowAnonymousJoin": true,
    "RequireHostApproval": false,
    "FfmpegPath": "/usr/bin/ffmpeg",
    "EnablePostProcessing": true,
    "RecordingChunkIntervalSeconds": 5,
    "ScratchpadSyncStrategy": "Sidecar"
  },
  "ConnectionStrings": {
    "Default": "Data Source=listenroom.db"
  },
  "Hangfire": {
    "WorkerCount": 2
  }
}
```

Create a strongly-typed `ListenRoomOptions` class and bind it in `Program.cs` via `services.Configure<ListenRoomOptions>(...)`.

---

## Data Models

Create in `Models/`:

### `Session.cs`

```csharp
public class Session
{
    public string Id { get; set; }              // e.g. "room-4f9a"
    public string AudioFileName { get; set; }
    public string AudioFilePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; }
    public string ScratchpadContent { get; set; } = "";
    public List<Participant> Participants { get; set; } = new();
    public List<RecordingChunk> RecordingChunks { get; set; } = new();
}

public enum SessionStatus { Active, Ended }
```

### `Participant.cs`

```csharp
public class Participant
{
    public string Id { get; set; }
    public string SessionId { get; set; }
    public string DisplayName { get; set; }
    public bool IsHost { get; set; }
    public bool HasToken { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public string Color { get; set; }
    public Session Session { get; set; }
}
```

### `RecordingChunk.cs`

```csharp
public class RecordingChunk
{
    public int Id { get; set; }
    public string SessionId { get; set; }
    public string ParticipantId { get; set; }
    public string ParticipantName { get; set; }
    public int ChunkIndex { get; set; }
    public string FilePath { get; set; }
    public DateTime ReceivedAt { get; set; }
    public bool Assembled { get; set; }
    public Session Session { get; set; }
}
```

### `AssembledRecording.cs`

```csharp
public class AssembledRecording
{
    public int Id { get; set; }
    public string SessionId { get; set; }
    public string ParticipantId { get; set; }
    public string ParticipantName { get; set; }
    public string FilePath { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime AssembledAt { get; set; }
    public Session Session { get; set; }
}
```

---

## Database Context

`Data/AppDbContext.cs`:

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Session> Sessions { get; set; }
    public DbSet<Participant> Participants { get; set; }
    public DbSet<RecordingChunk> RecordingChunks { get; set; }
    public DbSet<AssembledRecording> AssembledRecordings { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Session>().HasKey(s => s.Id);
        modelBuilder.Entity<Participant>().HasKey(p => p.Id);
        // configure navigation properties and cascade behavior
    }
}
```

Generate and apply initial migration:

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

---

## Services

### `SessionService.cs`

Responsible for all SQLite-backed session operations. No in-memory state here — that comes in Phase 2.

Methods to implement:

| Method | Description |
|---|---|
| `CreateSessionAsync(audioFileName, hostDisplayName)` | Generates session ID (`room-{4 random hex chars}`), creates `Session` and host `Participant`, saves to DB, creates recording directory on disk |
| `GetSessionAsync(sessionId)` | Fetch session with participants and chunks |
| `EndSessionAsync(sessionId)` | Sets `Status = Ended`, `EndedAt = now`, saves |
| `AddParticipantAsync(sessionId, displayName, connectionId, isHost)` | Creates participant, assigns color from palette |
| `UpdateScratchpadAsync(sessionId, content)` | Persists scratchpad text |
| `MarkSessionsInterruptedAsync()` | On startup: mark any `Active` sessions as interrupted (add an `Interrupted` status value, or simply `Ended` with a note) |

**Color palette** — assign colors in round-robin from a predefined accessible set. Store 8–10 hex values as a static array in the service.

**Session ID generation** — `"room-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLower()`

---

## Controllers

### `SessionController.cs`

| Method | Route | Action |
|---|---|---|
| `POST` | `/api/sessions` | Create session. Body: `{ audioFileName, displayName }`. Returns `{ sessionId, sessionUrl }` |
| `GET` | `/api/sessions` | List active sessions. Returns array of session summaries |
| `GET` | `/api/sessions/{id}` | Get session detail. Returns session + participants |
| `POST` | `/api/sessions/{id}/end` | End session. No body needed for now (host enforcement comes in Phase 2) |

### `AudioController.cs`

| Method | Route | Action |
|---|---|---|
| `GET` | `/api/audio` | List `.mp3`, `.m4a`, `.webm`, `.ogg` files from the configured `AudioDirectory`. Returns `[{ fileName, fileSizeBytes }]` |
| `GET` | `/audio/{filename}` | Serve audio file. Validate path stays within `AudioDirectory` — reject traversal attempts with 400. Stream file with appropriate `Content-Type` and `Accept-Ranges` support |

**Path traversal protection:** resolve the requested filename against `AudioDirectory` using `Path.GetFullPath` and verify the result starts with the canonical `AudioDirectory` path.

---

## Program.cs Wiring

```csharp
builder.Services.AddDbContext<AppDbContext>(...);
builder.Services.AddScoped<SessionService>();
builder.Services.AddControllers();
builder.Services.Configure<ListenRoomOptions>(...);
builder.Services.AddHangfire(...);
builder.Services.AddHangfireServer(options => { options.WorkerCount = ...; });

// Static files for wwwroot
app.UseStaticFiles();
app.UseHangfireDashboard("/hangfire");
app.MapControllers();

// On startup: mark interrupted sessions
using (var scope = app.Services.CreateScope())
{
    var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
    await sessionService.MarkSessionsInterruptedAsync();
}
```

---

## Directory Setup

On startup (or in `SessionService`), ensure the configured directories exist:

```csharp
Directory.CreateDirectory(options.AudioDirectory);
Directory.CreateDirectory(options.RecordingsDirectory);
```

---

## Verification Checklist

Before moving to Phase 2, confirm:

- [ ] `dotnet run` starts without errors
- [ ] `POST /api/sessions` creates a session row in SQLite and returns a valid session URL
- [ ] `GET /api/sessions` lists the created session
- [ ] `GET /api/sessions/{id}` returns full detail
- [ ] `POST /api/sessions/{id}/end` marks it ended
- [ ] `GET /api/audio` lists files from the audio directory (put at least one test file in `./audio`)
- [ ] `GET /audio/{filename}` streams the file; requesting `../outside` returns 400
- [ ] Hangfire dashboard accessible at `/hangfire`
- [ ] `dotnet ef database update` produces a valid `listenroom.db`
- [ ] Server restart correctly marks any stale `Active` sessions
