# Phase 6: Export & Session Summary

> The finishing pass. Export endpoints, the full summary page, ZIP archive generation, and any UI polish that didn't make it into earlier phases. By the end of this phase, ListenRoom is feature-complete for V1.

---

## Goals

- `ExportController` with all export endpoints implemented
- `ExportService` generating markdown exports and ZIP archives
- `summary.html` fully implemented (metadata, scratchpad view, recording status, all export buttons)
- "Rejoin" workflow (continuation session)
- Recording assembly status polling on the summary page
- Miscellaneous UI polish and edge cases from earlier phases

---

## `ExportService.cs`

### Notes Export

```csharp
public async Task<byte[]> GenerateNotesExportAsync(string sessionId)
{
    var session = await _db.Sessions
        .Include(s => s.Participants)
        .FirstOrDefaultAsync(s => s.Id == sessionId);

    var duration = session.EndedAt.HasValue
        ? session.EndedAt.Value - session.CreatedAt
        : TimeSpan.Zero;

    var header = $"""
        # ListenRoom Session Notes
        Audio: {session.AudioFileName}
        Date: {session.CreatedAt:yyyy-MM-dd}
        Duration: {duration:hh\\:mm\\:ss}
        Participants: {string.Join(", ", session.Participants.Select(p => p.DisplayName))}
        ---

        """;

    var content = header + (session.ScratchpadContent ?? "");
    return Encoding.UTF8.GetBytes(content);
}
```

### ZIP Archive

```csharp
public async Task<byte[]> GenerateArchiveAsync(string sessionId)
{
    using var ms = new MemoryStream();
    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
    {
        // notes.md
        var notes = await GenerateNotesExportAsync(sessionId);
        var notesEntry = archive.CreateEntry("notes.md");
        using (var entryStream = notesEntry.Open())
            await entryStream.WriteAsync(notes);

        // session.json
        var meta = await GenerateSessionMetadataAsync(sessionId);
        var metaEntry = archive.CreateEntry("session.json");
        using (var entryStream = metaEntry.Open())
            await JsonSerializer.SerializeAsync(entryStream, meta);

        // recordings/
        var recordings = await _db.AssembledRecordings
            .Where(r => r.SessionId == sessionId)
            .ToListAsync();

        foreach (var recording in recordings)
        {
            if (!File.Exists(recording.FilePath)) continue;
            var ext = Path.GetExtension(recording.FilePath);
            var entryName = $"recordings/{recording.ParticipantName}{ext}";
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(recording.FilePath);
            await fileStream.CopyToAsync(entryStream);
        }

        // combined.mp3 (if exists)
        var combinedPath = Path.Combine(_options.RecordingsDirectory, sessionId, "combined.mp3");
        if (File.Exists(combinedPath))
        {
            var combinedEntry = archive.CreateEntry("recordings/combined.mp3");
            using var entryStream = combinedEntry.Open();
            using var fileStream = File.OpenRead(combinedPath);
            await fileStream.CopyToAsync(entryStream);
        }
    }
    return ms.ToArray();
}
```

### `session.json` Metadata

```csharp
public async Task<object> GenerateSessionMetadataAsync(string sessionId)
{
    var session = await _db.Sessions
        .Include(s => s.Participants)
        .FirstOrDefaultAsync(s => s.Id == sessionId);

    return new
    {
        sessionId = session.Id,
        audioFile = session.AudioFileName,
        createdAt = session.CreatedAt,
        endedAt = session.EndedAt,
        duration = session.EndedAt.HasValue
            ? (session.EndedAt.Value - session.CreatedAt).TotalSeconds
            : 0,
        participants = session.Participants.Select(p => new
        {
            name = p.DisplayName,
            isHost = p.IsHost,
            joinedAt = p.JoinedAt,
            leftAt = p.LeftAt
        })
    };
}
```

---

## `ExportController.cs`

```csharp
[ApiController]
[Route("api/export")]
public class ExportController : ControllerBase
{
    [HttpGet("{sessionId}/notes")]
    public async Task<IActionResult> ExportNotes(string sessionId)
    {
        var bytes = await _exportService.GenerateNotesExportAsync(sessionId);
        return File(bytes, "text/markdown", $"notes-{sessionId}.md");
    }

    [HttpGet("{sessionId}/archive")]
    public async Task<IActionResult> ExportArchive(string sessionId)
    {
        var bytes = await _exportService.GenerateArchiveAsync(sessionId);
        return File(bytes, "application/zip", $"listenroom-{sessionId}.zip");
    }
}
```

---

## `summary.html`

### URL Pattern

`/session/{id}/summary` — handled by the fallback router from Phase 3.

### Layout

```
┌──────────────────────────────────────────────────────────────────┐
│  ListenRoom — Session Summary                                     │
├──────────────────────────────────────────────────────────────────┤
│  room-4f9a                                                        │
│  Audio: Lesson 10 — Language Transfer Arabic                      │
│  Date: 2024-11-15   Duration: 00:24:18                           │
│  Participants: Juno, Sarah, Ahmed                                  │
├──────────────────────────────────────────────────────────────────┤
│  Session Notes                                                    │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  ## Lesson 10 Notes                                        │  │
│  │  [02:14] بدّي = "I want"                                   │  │
│  │  [04:55] root pattern ك-ت-ب                               │  │
│  └────────────────────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────────────────────┤
│  Recordings                                                       │
│  Juno    [assembling…]                                           │
│  Sarah   ✓ [Download .webm] [Download .mp3]                      │
│  Combined  [not available]                                        │
├──────────────────────────────────────────────────────────────────┤
│  [Export Notes]  [Download All (.zip)]                           │
│  [Continue This Session →]                                       │
└──────────────────────────────────────────────────────────────────┘
```

### Behavior

On load:
1. Extract session ID from URL.
2. `GET /api/sessions/{id}` — populate metadata and scratchpad.
3. Render scratchpad as read-only markdown (use `marked.js`; timestamps are rendered but don't need to be clickable — no audio player here).
4. `GET /api/recording/{sessionId}/status` — populate recording status table.
5. If any recording is still `pending` assembly, start polling every 3 seconds. Stop when all are resolved.

### Recording Status Table

| Participant | Status | Actions |
|---|---|---|
| Juno | assembling… | spinner |
| Sarah | ready | [Download .webm] [Download .mp3 (if available)] |

Each download button links directly to `GET /api/recording/{sessionId}/{participantId}`.

### Export Buttons

- "Export Notes" → `GET /api/export/{sessionId}/notes` (triggers browser download)
- "Download All (.zip)" → `GET /api/export/{sessionId}/archive`

Both are standard `<a href="...">` links with `download` attribute, or triggered via `fetch` + `URL.createObjectURL` for better UX.

### Continuation Session ("Continue This Session")

Clicking this button:
1. Prompts for a display name (same modal as session join).
2. `POST /api/sessions` with `{ audioFileName: session.audioFileName, displayName, continuationOf: sessionId }`.
3. Server creates a new session and pre-populates its `ScratchpadContent` with the previous session's notes (appending a `---` separator and a new session header).
4. Redirect to the new session URL.

**Server-side:** `SessionService.CreateSessionAsync` accepts an optional `continuationOf` parameter. If provided, it copies `ScratchpadContent` from the referenced session.

---

## UI Polish Pass

Items deferred from earlier phases:

### General
- [ ] Loading states for all async operations (spinners or skeleton placeholders)
- [ ] Error states: session not found, session ended, server unreachable
- [ ] `<title>` tags updated per page (`ListenRoom — room-4f9a`, `ListenRoom — Session Summary`)
- [ ] Favicon (simple SVG, no PNG needed)

### `index.html`
- [ ] Empty state: "No audio files found. Add .mp3 or .webm files to the `./audio` directory."
- [ ] Empty state: "No active sessions."
- [ ] Sort active sessions by creation time, newest first

### `session.html`
- [ ] Seek-requested notification auto-dismisses after 15 seconds if neither Go nor Ignore is clicked
- [ ] Token holder's "Pass Control" button lists only participants who are connected (handle join/leave updates)
- [ ] Disconnection banner: "Reconnecting…" with a spinner; auto-clears on reconnect
- [ ] Host-only controls (End Session, Take Control) are hidden (not just disabled) for non-hosts
- [ ] Audio loading state: show "Loading audio…" until `canplay` event fires
- [ ] Seek bar disabled during `waiting` / `stalled` audio events

### Participant List
- [ ] Participant count badge in the header: "3 in room"
- [ ] Visual distinction between "recording ready" (mic icon, grey) and "recording active" (mic icon, red + pulse)
- [ ] Participant color dot as a CSS border on the row, not just a symbol

### Keyboard Shortcuts

Document and implement:

| Shortcut | Action |
|---|---|
| `Space` | Play / Pause (token holder only) |
| `Ctrl+T` / `Cmd+T` | Insert timestamp |
| `Ctrl+E` / `Cmd+E` | Toggle scratchpad edit/view |
| `Left` / `Right` | Seek ±5s (token holder only) |

Intercept `Space` only when the scratchpad textarea is not focused.

---

## Security Hardening Pass

Review the following before considering V1 complete:

- [ ] `AudioController`: path traversal check uses `Path.GetFullPath` comparison (already specced in Phase 1 — verify implementation)
- [ ] `RecordingController`: chunk upload validates `sessionId` is active and `participantId` exists in the session
- [ ] ZIP archive generation: participant names used as filenames are sanitized (`Path.GetInvalidFileNameChars` removal)
- [ ] Session ID generation: uses `RandomNumberGenerator`, not `Random`
- [ ] No `ScratchpadContent` or participant names are reflected into HTML without escaping (use `marked` for rendering, which escapes by default; all DOM writes use `.textContent` not `.innerHTML` except for the markdown render output)
- [ ] Hangfire dashboard: accessible at `/hangfire` with no auth in V1 (document this as a known exposure for public deployments)

---

## Final Verification Checklist

Full end-to-end test:

- [ ] Create a session on `index.html`, join from a second browser window with a different display name
- [ ] Token passes correctly; sync works; scratchpad edits reflect on both sides
- [ ] Both participants enable mic and record for at least 30 seconds
- [ ] Host ends the session; both clients redirect to summary page
- [ ] Summary page shows metadata and scratchpad content correctly
- [ ] Recording assembly completes (watch Hangfire dashboard); status updates via polling
- [ ] "Export Notes" produces a valid `.md` file with the header and scratchpad content
- [ ] "Download All" produces a `.zip` containing `notes.md`, `session.json`, and participant `.webm` files
- [ ] "Continue This Session" creates a new session with the previous scratchpad content pre-loaded
- [ ] Keyboard shortcuts all work as documented
- [ ] Server restart: stale Active session marked as ended; `listenroom.db` intact; recording chunks on disk survive
- [ ] Path traversal attempt (`GET /audio/../appsettings.json`) returns 400

---

## Post-V1 Notes

The following are explicitly out of scope but worth noting for continuity:

- **Yjs C# native (Option A):** The Node sidecar is the current implementation. If the sidecar operational overhead becomes a pain point, the migration path is to implement `y-protocols` directly in C# and remove the sidecar and proxy middleware. The Yjs wire protocol is documented and stable.
- **Waveform visualization:** Replace the `<input type="range">` seek bar with `wavesurfer.js`. The FFmpeg waveform data generated in `PostProcessRecordingJob` is already the right format to feed into it.
- **WebRTC voice:** Add a signaling layer via SignalR and a `RTCPeerConnection` mesh for live voice. The recording pipeline is independent and continues to work alongside it.
- **Whisper transcription:** Add a `TranscribeRecordingJob` that runs Whisper on assembled tracks and appends a transcript block to the scratchpad export.
