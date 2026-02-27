# Phase 5: Recording Pipeline

> The Riverside-model recording system. Local capture, background chunk upload, IndexedDB backup, server-side reassembly via Hangfire, and optional FFmpeg post-processing. By the end of this phase, participants can record their microphone audio locally and retrieve assembled per-track files after the session ends.

---

## Goals

- `recorder.js`: `getUserMedia`, `MediaRecorder`, 5-second chunk production, chunked upload with retry, IndexedDB backup
- `POST /api/recording/chunk` endpoint receiving and storing chunks
- `RecordingController` and `RecordingService` implemented
- `AssembleRecordingJob` (Hangfire) correctly concatenates WebM/Opus chunks
- `PostProcessRecordingJob` (Hangfire, optional) runs if FFmpeg is present
- Recording state (ready/recording/stopped) visible in the participants panel
- "Upload missing chunks" recovery flow on session end

---

## Client-Side Recording

### Consent Flow

On join (after `SessionJoined` is received):

1. Show a mic consent dialog: "Enable your microphone for session recording? [Enable] [Skip]"
2. On "Enable": call `getUserMedia({ audio: true })`.
3. On browser permission grant: send `RecordingReady` to hub. Show mic icon in local participant UI.
4. On browser permission denial or "Skip": no recording. Mic icon absent.

The dialog should only appear once per join (not on reconnect). Track consent state in `sessionStorage`.

### `recorder.js`

```js
class SessionRecorder {
    constructor({ sessionId, participantId, hubClient }) {
        this.sessionId = sessionId
        this.participantId = participantId
        this.hubClient = hubClient
        this.mediaRecorder = null
        this.chunkIndex = 0
        this.uploadQueue = []
        this.db = null  // IndexedDB handle
    }

    async requestMic() {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
        this.stream = stream
        return true
    }

    async startRecording() {
        const options = { mimeType: 'audio/webm;codecs=opus' }
        this.mediaRecorder = new MediaRecorder(this.stream, options)
        this.mediaRecorder.ondataavailable = (e) => this.handleChunk(e.data)
        this.mediaRecorder.start(5000)  // 5-second timeslice
        await this.hubClient.send('RecordingStarted')
    }

    async stopRecording() {
        return new Promise((resolve) => {
            this.mediaRecorder.onstop = async () => {
                await this.flushRemainingUploads()
                await this.hubClient.send('RecordingStopped')
                resolve()
            }
            this.mediaRecorder.stop()
        })
    }

    async handleChunk(blob) {
        const index = this.chunkIndex++
        await this.saveChunkToIndexedDB(index, blob)
        this.enqueueUpload(index, blob)
    }
}
```

### Chunk Upload

```js
async uploadChunk(index, blob, retries = 3) {
    const formData = new FormData()
    formData.append('sessionId', this.sessionId)
    formData.append('participantId', this.participantId)
    formData.append('chunkIndex', index)
    formData.append('data', blob, `chunk-${index}.webm`)

    for (let attempt = 0; attempt < retries; attempt++) {
        try {
            const res = await fetch('/api/recording/chunk', { method: 'POST', body: formData })
            if (res.ok) {
                await this.markChunkUploaded(index)  // update IndexedDB
                return
            }
        } catch (err) {
            if (attempt < retries - 1) {
                await delay(Math.min(1000 * Math.pow(2, attempt), 30000))  // exp backoff
            }
        }
    }
    console.error(`Chunk ${index} failed after ${retries} attempts — saved to IndexedDB`)
}
```

### IndexedDB Backup

Use a single IndexedDB database named `listenroom-recording` with an object store `chunks`.

Schema:
```js
{ key: `${sessionId}-${participantId}-${index}`, blob: Blob, uploaded: boolean }
```

On session end: query IndexedDB for any chunks with `uploaded: false`. If any exist, show a banner:

> "Some audio chunks failed to upload. [Upload missing chunks] [Skip]"

"Upload missing chunks" iterates the failed chunks and retries uploads sequentially.

### Browser Compatibility Notes

- `audio/webm;codecs=opus` is supported in Chrome and Firefox. Safari requires `audio/mp4;codecs=aac` — detect and use `MediaRecorder.isTypeSupported()` to pick the right MIME type. Store MIME type per chunk in IndexedDB for reassembly.
- iOS Safari has additional restrictions on `getUserMedia` that may limit functionality. Document this as a known limitation.

---

## Server-Side Chunk Handling

### `RecordingController.cs`

#### `POST /api/recording/chunk`

```csharp
[HttpPost("chunk")]
public async Task<IActionResult> UploadChunk([FromForm] ChunkUploadRequest request)
{
    // Validate session is active
    var session = await _sessionService.GetSessionAsync(request.SessionId);
    if (session == null || session.Status != SessionStatus.Active)
        return BadRequest("Session not found or not active");

    // Save chunk to disk
    await _recordingService.SaveChunkAsync(request);
    return Ok();
}

public class ChunkUploadRequest
{
    public string SessionId { get; set; }
    public string ParticipantId { get; set; }
    public int ChunkIndex { get; set; }
    public IFormFile Data { get; set; }
}
```

#### `GET /api/recording/{sessionId}/status`

Returns assembly job status for all participants in the session:

```json
{
  "participants": [
    { "participantId": "abc", "participantName": "Juno", "status": "assembled", "filePath": "..." },
    { "participantId": "xyz", "participantName": "Sarah", "status": "pending" }
  ]
}
```

#### `GET /api/recording/{sessionId}/{participantId}`

Streams the assembled `.webm` (or `.mp3` if available) for download.

#### `GET /api/recording/{sessionId}/combined`

Streams the combined mix if FFmpeg post-processing ran.

---

### `RecordingService.cs`

```csharp
public class RecordingService
{
    public async Task SaveChunkAsync(ChunkUploadRequest request)
    {
        var dir = Path.Combine(_options.RecordingsDirectory, request.SessionId, request.ParticipantId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"chunk-{request.ChunkIndex}.webm");

        using var stream = new FileStream(path, FileMode.Create);
        await request.Data.CopyToAsync(stream);

        // Insert RecordingChunk record into DB
        await _db.RecordingChunks.AddAsync(new RecordingChunk
        {
            SessionId = request.SessionId,
            ParticipantId = request.ParticipantId,
            ChunkIndex = request.ChunkIndex,
            FilePath = path,
            ReceivedAt = DateTime.UtcNow,
            Assembled = false
        });
        await _db.SaveChangesAsync();
    }

    public async Task<List<string>> GetOrderedChunkPathsAsync(string sessionId, string participantId)
    {
        return await _db.RecordingChunks
            .Where(c => c.SessionId == sessionId && c.ParticipantId == participantId)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.FilePath)
            .ToListAsync();
    }
}
```

---

## Hangfire Jobs

### `AssembleRecordingJob.cs`

Triggered on session end (from `SessionController.End` or the hub's end-session logic):

```csharp
public class AssembleRecordingJob
{
    public async Task ExecuteAsync(string sessionId)
    {
        var participants = await _db.RecordingChunks
            .Where(c => c.SessionId == sessionId)
            .Select(c => new { c.ParticipantId, c.ParticipantName })
            .Distinct()
            .ToListAsync();

        foreach (var participant in participants)
        {
            var chunkPaths = await _recordingService.GetOrderedChunkPathsAsync(
                sessionId, participant.ParticipantId);

            if (!chunkPaths.Any()) continue;

            var outputPath = Path.Combine(
                _options.RecordingsDirectory, sessionId,
                participant.ParticipantId, "recording.webm");

            await ConcatenateChunksAsync(chunkPaths, outputPath);

            // Insert AssembledRecording record
            await _db.AssembledRecordings.AddAsync(new AssembledRecording
            {
                SessionId = sessionId,
                ParticipantId = participant.ParticipantId,
                ParticipantName = participant.ParticipantName,
                FilePath = outputPath,
                AssembledAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        // Enqueue post-processing if FFmpeg available
        if (_options.EnablePostProcessing && File.Exists(_options.FfmpegPath))
            _backgroundJobs.Enqueue<PostProcessRecordingJob>(j => j.ExecuteAsync(sessionId));
    }

    private async Task ConcatenateChunksAsync(List<string> chunkPaths, string outputPath)
    {
        // WebM/Opus chunks from the same MediaRecorder session share codec context.
        // Binary concatenation is valid for Opus audio in WebM.
        using var output = new FileStream(outputPath, FileMode.Create);
        foreach (var path in chunkPaths)
        {
            using var chunk = new FileStream(path, FileMode.Open);
            await chunk.CopyToAsync(output);
        }
    }
}
```

**Important caveat:** Raw binary concatenation of WebM files works for Opus audio chunks produced by the same `MediaRecorder` session because they share the EBML header from the first chunk. If the first chunk's header is incomplete (some browsers don't produce a full header on the first `ondataavailable`), concatenation may produce a file with a malformed header. Test this across Chrome and Firefox. If issues arise, use FFmpeg's `concat` demuxer as a fallback.

### `PostProcessRecordingJob.cs`

Optional — only runs if `EnablePostProcessing = true` and FFmpeg is present.

Steps:
1. For each `AssembledRecording` in the session:
   - Normalize audio: `ffmpeg -i input.webm -filter:a loudnorm output-normalized.webm`
   - Transcode to MP3: `ffmpeg -i input-normalized.webm -codec:a libmp3lame -q:a 2 output.mp3`
   - Generate waveform data: `ffmpeg -i input.webm -filter:a aformat=channel_layouts=mono,asetnsamples=200 -vn -f null /dev/null` or use `ffprobe` + a waveform filter
2. If all participants have assembled recordings: mix to combined file using `ffmpeg`'s `amix` filter.

Run each FFmpeg call via `Process.Start` with appropriate argument escaping. Capture stderr for error logging.

---

## Enqueueing Jobs on Session End

In `SessionController.End` (or wherever session end is triggered):

```csharp
await _sessionService.EndSessionAsync(sessionId);
_backgroundJobs.Enqueue<AssembleRecordingJob>(j => j.ExecuteAsync(sessionId));
```

This fires immediately and runs in the Hangfire worker pool (2 workers per config).

---

## Recording UI

### Participants Panel

Add to each participant row:

- 🎙 icon: visible when participant is recording-ready (sent `RecordingReady`)
- Red pulsing dot + 🔴: when actively recording (sent `RecordingStarted`)
- Greyed mic: when stopped

Handle `RecordingStateChanged` hub event to update these indicators.

### Session Room Controls

Add to the player panel:

- "Enable Mic" button (pre-session): triggers consent flow
- "Start Recording" / "Stop Recording" button (host only, or tied to playback)
- Recording timer display: `🔴 00:12:34` (elapsed time since recording started)
- Upload progress bar: shown during active recording, showing queued vs uploaded chunks

### Upload Progress

Track `uploadedChunks` and `totalChunks` in `recorder.js`. Emit a custom event `uploadprogress` that `session.js` listens to and updates the progress bar.

---

## Verification Checklist

Before moving to Phase 6, confirm:

- [ ] "Enable Mic" shows browser permission prompt
- [ ] On grant, mic icon appears in participants panel for that user (via `RecordingStateChanged`)
- [ ] Starting recording causes `MediaRecorder` to produce chunks every 5 seconds
- [ ] Each chunk is uploaded to `POST /api/recording/chunk` within a second of being produced
- [ ] Chunks are written to disk in the correct directory structure
- [ ] `RecordingChunk` rows are inserted in SQLite
- [ ] Deliberately fail network (throttle/block in devtools) → chunks queue and retry; IndexedDB backup confirmed
- [ ] Stopping recording sends the final chunk and calls `RecordingStopped`
- [ ] `POST /api/sessions/{id}/end` triggers the Hangfire assembly job
- [ ] Hangfire dashboard shows the job running and completing
- [ ] Assembled `recording.webm` is playable in a browser or media player
- [ ] `GET /api/recording/{sessionId}/status` returns correct assembly status
- [ ] `GET /api/recording/{sessionId}/{participantId}` streams the assembled file
- [ ] FFmpeg post-processing (if configured) produces `.mp3` output
- [ ] Missing chunk recovery UI appears if any `uploaded: false` chunks exist in IndexedDB
