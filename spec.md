# ListenRoom — Full Product Specification

> A shared listening room for collaborative audio annotation and synchronized learning sessions. Built for language learners. Designed around the workflow of listening, pausing, discussing, and building a durable shared record of understanding.

---

## Vision

ListenRoom is a self-hosted, real-time web application where participants listen to a shared audio file in sync, annotate it collaboratively, and optionally record their own voices locally — producing a per-track recording archive alongside timestamped notes as the session artifact. The canonical use case is language learning: a student and tutor, or two learners at the same level, working through a lesson or podcast together with the tool as their shared workspace.

The architecture is intentionally local-first. No cloud dependency, no accounts required, no third-party infrastructure. Run it on a local machine or a cheap VPS and share a URL.

---

## Architecture Overview

### Stack

| Layer | Technology |
|---|---|
| Server runtime | .NET 8 (ASP.NET Core) |
| Real-time transport | ASP.NET Core SignalR (WebSocket with fallback) |
| Scratchpad sync | Yjs CRDT (via direct C# protocol implementation or Node sidecar) |
| Audio recording | WebRTC `getUserMedia` + `MediaRecorder` → chunked upload |
| Audio playback | HTML5 `<audio>` element, served as static file |
| Persistence | SQLite via EF Core |
| Background jobs | Hangfire (chunk reassembly, post-processing) |
| Frontend | Vanilla JavaScript, single-page, no framework |
| Styling | Plain CSS, no utility framework required |

### Deployment

For local development: `dotnet run`. The frontend is served as static files from `wwwroot`. A single process handles everything. SQLite lives in the app data directory. Audio files and recording chunks live on the local filesystem.

For VPS deployment: reverse proxy (Caddy or nginx) in front of Kestrel, HTTPS termination at the proxy layer (required for WebRTC), systemd service for process management.

---

## Data Model

### Session

```csharp
public class Session
{
    public string Id { get; set; }              // short human-readable: "room-4f9a"
    public string AudioFileName { get; set; }   // filename on disk
    public string AudioFilePath { get; set; }   // full path
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; }   // Active, Ended
    public string ScratchpadContent { get; set; } // persisted on update
    public List<Participant> Participants { get; set; }
    public List<RecordingChunk> RecordingChunks { get; set; }
}

public enum SessionStatus { Active, Ended }
```

### Participant

```csharp
public class Participant
{
    public string Id { get; set; }              // connection ID
    public string SessionId { get; set; }
    public string DisplayName { get; set; }
    public bool IsHost { get; set; }
    public bool HasToken { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public string Color { get; set; }           // assigned on join for scratchpad attribution
}
```

### RecordingChunk

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
}
```

### AssembledRecording

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
}
```

---

## In-Memory Session State

Beyond the persisted model, the server holds live session state in a singleton `SessionStateService` backed by `ConcurrentDictionary`:

```csharp
public class LiveSessionState
{
    public string SessionId { get; set; }
    public string TokenHolderConnectionId { get; set; }
    public string HostConnectionId { get; set; }
    public PlaybackState Playback { get; set; }
    public Dictionary<string, ConnectedParticipant> Participants { get; set; }
}

public class PlaybackState
{
    public double Position { get; set; }    // seconds
    public bool Playing { get; set; }
    public long SentAt { get; set; }        // Unix ms at time of last sync
}

public class ConnectedParticipant
{
    public string ConnectionId { get; set; }
    public string DisplayName { get; set; }
    public bool IsHost { get; set; }
    public bool HasToken { get; set; }
    public bool IsRecording { get; set; }
    public string Color { get; set; }
}
```

---

## SignalR Hub Contract

### Hub: `SessionHub` — `/hubs/session`

#### Client → Server (Hub Methods)

| Method | Parameters | Authorization |
|---|---|---|
| `JoinSession` | `sessionId, displayName` | Any |
| `UpdatePlayback` | `position, playing` | Token holder only |
| `PassToken` | `toConnectionId` | Token holder only |
| `ReclaimToken` | — | Host only |
| `UpdateScratchpad` | `content` | Any participant |
| `RecordingReady` | — | Any participant |
| `RecordingStarted` | — | Any participant |
| `RecordingStopped` | — | Any participant |
| `RequestTimestampSeek` | `positionSeconds` | Non-token-holders |
| `LeaveSession` | — | Any participant |

#### Server → Client (Client Methods)

| Method | Payload | Recipients |
|---|---|---|
| `SessionJoined` | Full `LiveSessionState` + audio URL | Joining client |
| `ParticipantJoined` | `{ connectionId, displayName, color, isHost }` | All others |
| `ParticipantLeft` | `{ connectionId, displayName }` | All others |
| `PlaybackSync` | `{ position, playing, sentAt }` | All except sender |
| `TokenUpdated` | `{ holderConnectionId, holderName }` | All |
| `ScratchpadUpdated` | `{ content, authorConnectionId }` | All except sender |
| `ParticipantTyping` | `{ connectionId, displayName }` | All except sender |
| `RecordingStateChanged` | `{ connectionId, isRecording }` | All |
| `SeekRequested` | `{ fromName, positionSeconds }` | Token holder only |
| `SessionEnded` | — | All |

---

## Features

### 1. Session Management

**Creation**
- Host navigates to `/` and sees a list of available audio files from the server's configured audio directory.
- Selects a file, enters a display name, clicks "Create Session."
- Server creates a `Session` record in SQLite, initializes `LiveSessionState`, returns session URL.
- Session URL format: `/session/room-4f9a`

**Joining**
- Participant navigates to session URL, enters display name.
- Server assigns a color from a predefined accessible palette.
- Server pushes full current state on connect: playback position (latency-compensated), playing state, token holder, participant list, current scratchpad content, recording state of each participant.
- Client's audio element seeks to synced position immediately on join.

**Ending**
- Host can click "End Session" which broadcasts `SessionEnded` to all clients.
- On session end, server marks session as `Ended` in SQLite, triggers Hangfire job for recording assembly.
- Clients are redirected to a session summary page showing export options.

**Session persistence**
- Sessions are stored in SQLite and survive server restarts.
- Active sessions at restart are marked as interrupted; their scratchpad content is preserved.
- Recording chunks on disk are preserved across restarts and can be reassembled manually.

---

### 2. Playback Controls & Sync

**Token model**
- Only the token holder can play, pause, or seek.
- Controls are visually disabled for non-holders.
- Token holder is shown in a persistent "Now controlling:" indicator near the player.
- "Pass Control" button opens a participant picker.
- "Take Control" button always visible to host; bypasses token holder consent.

**Sync protocol**
- On any playback event (play, pause, seek), token holder's client sends `UpdatePlayback` to the hub.
- Hub broadcasts `PlaybackSync` to all other clients with `sentAt` timestamp.
- Receiving clients calculate adjusted position: `position + (Date.now() - sentAt) / 1000`
- **Tolerance threshold:** ±2 seconds. If local position is within tolerance, no seek is applied. This prevents micro-correction jitter during normal playback.
- Outside tolerance: seek immediately and silently.
- On reconnect: client receives fresh state via `SessionJoined` and resyncs.

**Timestamp seek requests**
- Non-token-holders can click a timestamp in the scratchpad to send `RequestTimestampSeek` to the hub.
- Hub forwards as `SeekRequested` only to the token holder's client.
- Token holder sees a small notification: "Juno wants to jump to 04:55 — [Go] [Ignore]"
- This preserves token authority while enabling collaborative navigation.

**Playback speed**
- Speed control (0.75x, 1.0x, 1.25x, 1.5x) is local to each client, not synced.
- Position sync is still accurate regardless of local speed setting because it's time-based.

---

### 3. Scratchpad

**Sync strategy**
- Yjs CRDT for conflict-free concurrent editing. No last-write-wins, no collision risk.
- Implementation options (choose one at build time):
  - **Option A:** Direct C# Yjs protocol implementation. Keeps everything in one process. More work upfront.
  - **Option B:** Thin Node.js sidecar running `y-websocket`. C# server proxies Yjs WebSocket connections to it. Lower implementation risk, minor operational complexity.
- Recommendation: start with Option B, migrate to Option A if the sidecar becomes a pain point.

**Timestamp insertion**
- "Insert Timestamp" button (also `Ctrl+T` / `Cmd+T`) inserts `[MM:SS]` at cursor position.
- Timestamp reflects current playback position at moment of press.
- In the rendered view, timestamps are clickable links that trigger `RequestTimestampSeek`.

**Authorship attribution**
- Each participant has an assigned color.
- Characters typed by each participant carry Yjs awareness metadata.
- In the rendered view, a subtle left-border color on lines indicates who wrote them.
- This is display-only; the underlying document is plain text/markdown.

**Markdown rendering**
- Scratchpad renders as formatted markdown in "view" mode.
- Toggle between "edit" and "view" modes with a button or keyboard shortcut.
- In edit mode, raw markdown is shown. In view mode, rendered HTML with clickable timestamps.

**Typing indicator**
- When a participant is actively typing, their name appears as "DisplayName editing…" in the scratchpad header.
- Based on Yjs awareness protocol — no separate SignalR message needed.

---

### 4. Recording (Riverside Model)

This is the architectural centrepiece of the recording feature. The key principle: **record locally, upload in the background, assemble post-session.** The recording quality never depends on network reliability.

#### Client-Side Recording

**Consent and readiness**
- On join, participants are asked whether they want to enable their microphone.
- Consenting participants click "Enable Recording" which triggers `getUserMedia({ audio: true })`.
- Browser permission prompt is shown. On grant, participant's `RecordingReady` is sent to hub.
- Other participants see a microphone icon appear next to that participant's name.
- Recording does not start automatically — it starts when the host starts playback (or manually).

**Recording start/stop**
- When the session host clicks "Start Recording" (or it can be tied to playback start — configurable):
  - Hub broadcasts a `RecordingStateChanged` event.
  - All consenting clients start their `MediaRecorder` simultaneously.
  - Clients send `RecordingStarted` to hub on actual start.
- Recording produces WebM/Opus chunks via `MediaRecorder` with a `timeslice` of 5000ms (5-second chunks).

**Chunk upload**
- Each chunk is uploaded via a `POST /api/recording/chunk` HTTP endpoint (not WebSocket) as `multipart/form-data`.
- Payload: `{ sessionId, participantId, chunkIndex, data: Blob }`
- Upload happens in the background immediately as each chunk is produced — non-blocking.
- Failed uploads are retried with exponential backoff (3 retries, max 30s delay).
- Chunk index ensures ordering even if uploads arrive out of order.

**Local backup**
- Chunks are also written to IndexedDB locally as they're produced.
- On session end, if any chunks failed to upload, the client offers a "Upload missing chunks" option.
- This ensures recordings survive intermittent connectivity.

**Recording stop**
- On "Stop Recording" or session end, `MediaRecorder` is stopped, final chunk is flushed and uploaded.
- Client sends `RecordingStopped` to hub.
- Client shows upload progress for any remaining queued chunks.

#### Server-Side Chunk Handling

**Chunk receipt**
- `POST /api/recording/chunk` receives multipart upload.
- Validates session exists and is active.
- Writes chunk to disk: `recordings/{sessionId}/{participantId}/chunk-{index}.webm`
- Inserts `RecordingChunk` record into SQLite.
- Returns 200 immediately — no synchronous processing.

**Assembly job**
- Triggered by Hangfire when session ends.
- For each participant with chunks: concatenate chunks in order → single `.webm` file.
- WebM/Opus concatenation is valid without re-encoding (chunks share the same codec context).
- Output: `recordings/{sessionId}/{participantId}/recording.webm`
- Inserts `AssembledRecording` record into SQLite.
- Optionally: transcode to MP3 via FFmpeg if available on the host system (Hangfire background job, not blocking).

**Post-processing (optional, if FFmpeg present)**
- Normalize audio levels per track.
- Generate waveform data (JSON array of amplitude values for scrubbing visualization).
- Mix all tracks to a single combined recording with per-track level control.

---

### 5. Session Export

**Notes export**
- "Export Notes" button downloads scratchpad as `.md` file.
- Auto-prepended header:
  ```markdown
  # ListenRoom Session Notes
  Audio: [filename]
  Date: [ISO date]
  Duration: [HH:MM:SS]
  Participants: [comma-separated names]
  ---
  ```
- Timestamps remain as plain `[MM:SS]` text in the export — readable outside the tool.

**Recording export**
- Available on the session summary page after session end and assembly completion.
- Per-track downloads: each participant's recording as `.webm` or `.mp3`.
- Combined mix download if FFmpeg post-processing ran.
- Waveform data bundled as JSON for use in external players.

**Full session archive**
- A "Download Everything" option produces a `.zip` containing:
  - `notes.md`
  - `recordings/[name].webm` (one per participant)
  - `recordings/combined.mp3` (if available)
  - `session.json` (metadata: participants, timestamps, duration)

---

### 6. Session Summary Page

After a session ends, all participants are redirected to `/session/room-4f9a/summary`.

This page shows:
- Session metadata (audio file, date, duration, participants)
- The scratchpad in read-only rendered markdown view
- Recording assembly status (with a progress indicator if Hangfire job is still running)
- Export buttons for notes, individual tracks, combined mix, full archive
- A "Rejoin" option that opens a new session with the same audio file and pre-populates the scratchpad with the previous session's notes (continuation workflow)

---

## UI Layout

```
┌──────────────────────────────────────────────────────────────────┐
│  ListenRoom                              room-4f9a  [End Session] │
├───────────────────────────────────────┬──────────────────────────┤
│                                       │  Participants             │
│  Lesson 10 — Language Transfer Arabic │                           │
│                                       │  ● Juno (host) 🎮         │
│  ▶  ━━━━━━━━━●━━━━━━━━━━━━  12:34    │  ○ Sarah 🎙               │
│     0:00                    24:18     │  ○ Ahmed 🎙               │
│                                       │                           │
│  [⏸ Pause] [🔇 1.0x ▾]               │  [Pass Control ▾]         │
│  [📎 Timestamp]  [📤 Export]          │  [Take Control]           │
│                                       │                           │
│  🔴 Recording  00:12:34               │  ──────────────────────   │
│                                       │  🎙 Sarah  ████████░░     │
│                                       │  🎙 Ahmed  ██████████     │
├───────────────────────────────────────┴──────────────────────────┤
│  Scratchpad                              [Edit] [View]  Sarah…    │
│                                                                   │
│  ## Lesson 10 Notes                                               │
│                                                                   │
│  [02:14] بدّي = "I want" — Levantine, not فصحى                   │
│  [04:55] root pattern ك-ت-ب → كتاب، كاتب، مكتبة               │
│  [08:30] negation: ما + verb + ش  (Egyptian specific)            │
│                                                                   │
│                                                                   │
└──────────────────────────────────────────────────────────────────┘
```

---

## API Endpoints

| Method | Route | Description |
|---|---|---|
| `GET` | `/` | Home page — audio file list, create session |
| `GET` | `/session/{id}` | Session room page |
| `GET` | `/session/{id}/summary` | Post-session summary |
| `GET` | `/api/sessions` | List active sessions |
| `POST` | `/api/sessions` | Create new session |
| `GET` | `/api/sessions/{id}` | Get session state |
| `POST` | `/api/sessions/{id}/end` | End session (host only) |
| `GET` | `/api/audio` | List available audio files |
| `GET` | `/audio/{filename}` | Serve audio file (static) |
| `POST` | `/api/recording/chunk` | Upload recording chunk |
| `GET` | `/api/recording/{sessionId}/status` | Assembly job status |
| `GET` | `/api/recording/{sessionId}/{participantId}` | Download assembled track |
| `GET` | `/api/recording/{sessionId}/combined` | Download combined mix |
| `GET` | `/api/export/{sessionId}/notes` | Download notes as .md |
| `GET` | `/api/export/{sessionId}/archive` | Download full .zip archive |

---

## Configuration (`appsettings.json`)

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

---

## Project Structure

```
ListenRoom/
├── ListenRoom.sln
├── src/
│   └── ListenRoom.Web/
│       ├── Hubs/
│       │   └── SessionHub.cs
│       ├── Services/
│       │   ├── SessionStateService.cs      // in-memory live state
│       │   ├── SessionService.cs           // SQLite operations
│       │   ├── RecordingService.cs         // chunk handling
│       │   └── ExportService.cs            // notes + archive generation
│       ├── Jobs/
│       │   ├── AssembleRecordingJob.cs
│       │   └── PostProcessRecordingJob.cs
│       ├── Controllers/
│       │   ├── SessionController.cs
│       │   ├── AudioController.cs
│       │   ├── RecordingController.cs
│       │   └── ExportController.cs
│       ├── Data/
│       │   ├── AppDbContext.cs
│       │   └── Migrations/
│       ├── Models/
│       │   ├── Session.cs
│       │   ├── Participant.cs
│       │   ├── RecordingChunk.cs
│       │   └── AssembledRecording.cs
│       ├── wwwroot/
│       │   ├── index.html
│       │   ├── session.html
│       │   ├── summary.html
│       │   ├── css/
│       │   │   └── main.css
│       │   └── js/
│       │       ├── session.js              // main session logic
│       │       ├── player.js               // audio sync
│       │       ├── scratchpad.js           // Yjs client
│       │       ├── recorder.js             // MediaRecorder + chunk upload
│       │       └── signalr-client.js       // hub connection wrapper
│       ├── Program.cs
│       └── appsettings.json
├── sidecar/                                // optional Yjs Node sidecar
│   ├── package.json
│   ├── server.js
│   └── Dockerfile
└── docker-compose.yml                      // optional: app + sidecar together
```

---

## Security Considerations

- Session IDs are short random strings — not guessable but not cryptographically secret. For V1, knowing the session ID is sufficient to join. This is intentional; sharing a URL is the auth model.
- `RequireHostApproval` config flag (future): host must approve each join request.
- Audio files are served only from the configured `AudioDirectory`. Path traversal is validated.
- Recording chunks are validated against active sessions — orphaned chunks are rejected.
- WebRTC audio never leaves the client except via the chunked upload endpoint. No peer-to-peer audio data flows through the server.
- HTTPS is required for WebRTC `getUserMedia` in all browsers. Enforce at reverse proxy layer.

---

## Out of Scope for V1

- User accounts or authentication beyond display names
- Video recording or video tracks
- Multiple audio files per session (playlist mode)
- Live audio streaming between participants (WebRTC peer-to-peer voice — see future enhancements)
- Mobile-optimized layout
- Public session discovery

---

## Future Enhancements

### Near-term (V1.5)

**Continuation sessions** — On the summary page, "Continue This Session" creates a new session with the same audio file and pre-populates the scratchpad with previous notes. Enables multi-day study workflows without losing context.

**Per-user private notes lane** — A second scratchpad column visible only to the individual. Exported alongside shared notes, clearly delineated. Useful when one participant is the teacher and doesn't want to pollute the shared pad with their own prep notes.

**Timestamp click-to-seek for non-holders via notification** — Already in the spec as `RequestTimestampSeek`. Implement the token holder notification UI.

**Yjs C# native implementation** — Replace the Node sidecar with a direct C# Yjs sync provider. Simplifies deployment to a single process.

**Waveform visualization** — Replace the plain seek bar with a waveform display generated from the audio file (via FFmpeg or a JS library like `wavesurfer.js`). Significantly improves the scrubbing experience, especially for annotated review sessions.

### Medium-term

**WebRTC peer-to-peer voice** — Add live voice communication between participants so ListenRoom becomes a self-contained tutoring room without needing a separate call. Architecture: mesh WebRTC for small sessions (≤4 participants), with the server as signaling relay via SignalR. Does not replace the Riverside-model recording — both can coexist. Voice is ephemeral; recordings are the archive.

**Multi-track timeline view** — On the summary page, show a visual timeline of all participant recordings as stacked waveform lanes, with scratchpad annotations as markers. Clicking a marker seeks all tracks to that timestamp. This is essentially a lightweight DAW-style review interface.

**Session replay** — Play back a session including all scratchpad edit events in sequence, synced to the audio. Watch the notes being built in real time as the audio plays. Requires storing Yjs document history, which Yjs supports natively.

**Playlist / course mode** — Load a series of audio files (e.g., all Language Transfer Arabic lessons) as a sequential playlist. Notes are per-track but browsable as a unified course document. Progress is tracked per lesson.

**Mobile layout** — Responsive design pass. The core interaction model (player + scratchpad) maps well to a two-panel mobile layout. Recording on mobile requires testing across iOS Safari's WebRTC constraints.

**Embeddable review player** — A read-only shareable view of a session export: audio player + timestamped notes rendered inline. Static HTML generation from the session archive. Share a link, someone can review your annotated lesson without needing ListenRoom running.

### Longer-term / Architectural

**Multi-room server** — Run a single ListenRoom instance that hosts multiple concurrent independent sessions, each with their own audio, participants, and recordings. Currently implied by the architecture but worth calling out as an explicit scaling target.

**Plugin system for post-processing** — A hook-based system where post-session jobs can be extended: automatic transcription via Whisper, translation of scratchpad content, vocabulary extraction for flashcard export (Anki format), AI-generated session summary. The Hangfire job pipeline is already the right foundation for this.

**Federated sessions** — Two ListenRoom instances on different networks establish a shared session. Participants connect to their local instance; the instances sync state over a server-to-server WebSocket. Eliminates the need for one party to expose their local instance publicly. Complex but architecturally elegant for a tool designed for distributed learners.
