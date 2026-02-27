# Phase 2: SignalR Hub & In-Memory Session State

> The real-time backbone. By the end of this phase the hub is fully implemented, the token model works, and you can validate the entire hub contract using a minimal browser test page — before any production frontend exists.

---

## Goals

- `SessionStateService` running as a singleton with `ConcurrentDictionary`-backed live state
- `SessionHub` fully implemented with all client→server and server→client methods
- Token model enforced: only token holder can update playback; only host can reclaim
- Latency-compensated playback sync broadcast
- Participant join/leave lifecycle connected to both SignalR and the DB
- Minimal HTML test page for manual verification of all hub events

---

## In-Memory State Service

`Services/SessionStateService.cs` — registered as a **singleton**.

```csharp
public class LiveSessionState
{
    public string SessionId { get; set; }
    public string TokenHolderConnectionId { get; set; }
    public string HostConnectionId { get; set; }
    public PlaybackState Playback { get; set; } = new();
    public Dictionary<string, ConnectedParticipant> Participants { get; set; } = new();
}

public class PlaybackState
{
    public double Position { get; set; }
    public bool Playing { get; set; }
    public long SentAt { get; set; }    // Unix ms
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

`SessionStateService` wraps a `ConcurrentDictionary<string, LiveSessionState>` keyed by session ID.

Methods to implement:

| Method | Description |
|---|---|
| `CreateState(sessionId, hostConnectionId, hostName, hostColor)` | Initialize state for a new session |
| `GetState(sessionId)` | Returns `LiveSessionState` or null |
| `AddParticipant(sessionId, participant)` | Add to in-memory participants dict |
| `RemoveParticipant(sessionId, connectionId)` | Remove; return the removed participant |
| `UpdatePlayback(sessionId, position, playing)` | Update position + playing; set `SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` |
| `TransferToken(sessionId, toConnectionId)` | Update `HasToken` on both old and new holder; update `TokenHolderConnectionId` |
| `GetParticipantByConnectionId(connectionId)` | Reverse-lookup across all sessions; needed for `OnDisconnectedAsync` |
| `DestroyState(sessionId)` | Remove session from dictionary on end |

---

## SignalR Hub

`Hubs/SessionHub.cs` — mapped to `/hubs/session`.

### Hub Registration (`Program.cs`)

```csharp
builder.Services.AddSignalR();
// ...
app.MapHub<SessionHub>("/hubs/session");
```

### Client → Server Methods

#### `JoinSession(string sessionId, string displayName)`

1. Validate session exists in DB and is `Active`.
2. Check if this is the first connection to this session (no live state yet → this is the host reconnecting or a fresh first join).
   - If `LiveSessionState` doesn't exist for this session, retrieve from DB to determine host.
3. Add participant to `LiveSessionState`. Assign color from DB participant record (created via `SessionService` if this is a new connection).
4. Add caller to SignalR group `session-{sessionId}`.
5. Send `SessionJoined` to caller with full state (see payload below).
6. Broadcast `ParticipantJoined` to all others in the group.

`SessionJoined` payload:

```json
{
  "sessionId": "room-4f9a",
  "audioUrl": "/audio/lesson10.mp3",
  "playback": { "position": 734.2, "playing": false, "sentAt": 1700000000000 },
  "tokenHolderConnectionId": "abc123",
  "tokenHolderName": "Juno",
  "participants": [
    { "connectionId": "abc123", "displayName": "Juno", "color": "#e74c3c", "isHost": true, "hasToken": true, "isRecording": false }
  ],
  "scratchpadContent": "## Notes\n..."
}
```

#### `UpdatePlayback(double position, bool playing)`

1. Verify caller is the token holder for their session. If not, do nothing (log a warning).
2. Update `PlaybackState` in `SessionStateService` (sets `SentAt` to now).
3. Broadcast `PlaybackSync` to all others in the group.

`PlaybackSync` payload: `{ position, playing, sentAt }`

#### `PassToken(string toConnectionId)`

1. Verify caller is current token holder.
2. Verify `toConnectionId` is a connected participant in the same session.
3. Call `SessionStateService.TransferToken(...)`.
4. Broadcast `TokenUpdated` to all in group: `{ holderConnectionId, holderName }`.

#### `ReclaimToken()`

1. Verify caller is the host of their session.
2. Transfer token from current holder to host.
3. Broadcast `TokenUpdated` to all in group.

#### `UpdateScratchpad(string content)`

1. Verify caller is a participant in an active session.
2. Persist to DB via `SessionService.UpdateScratchpadAsync(...)` (debounce: only write to DB if content has changed, but always broadcast).
3. Broadcast `ScratchpadUpdated` to all others in group: `{ content, authorConnectionId }`.
4. Also broadcast `ParticipantTyping` to all others: `{ connectionId, displayName }`.

> Note: once the Yjs sidecar is added in Phase 4, `UpdateScratchpad` becomes a fallback/persistence path rather than the primary sync mechanism.

#### `RecordingReady()`

Mark participant as recording-ready in `LiveSessionState`. Broadcast `RecordingStateChanged` to all: `{ connectionId, isRecording: false }` (ready but not yet recording).

#### `RecordingStarted()`

Set `IsRecording = true` in `LiveSessionState`. Broadcast `RecordingStateChanged`: `{ connectionId, isRecording: true }`.

#### `RecordingStopped()`

Set `IsRecording = false`. Broadcast `RecordingStateChanged`: `{ connectionId, isRecording: false }`.

#### `RequestTimestampSeek(double positionSeconds)`

1. Verify caller is a non-token-holder (token holders can seek directly).
2. Look up the token holder's `ConnectionId`.
3. Send `SeekRequested` **only** to the token holder: `{ fromName, positionSeconds }`.

#### `LeaveSession()`

Call the shared leave logic (same as `OnDisconnectedAsync`).

### `OnDisconnectedAsync`

1. Look up which session this connection belongs to (via `SessionStateService.GetParticipantByConnectionId`).
2. Remove from `LiveSessionState`.
3. Remove from SignalR group.
4. Update `LeftAt` on `Participant` record in DB.
5. If this was the token holder, auto-transfer token to host. Broadcast `TokenUpdated`.
6. Broadcast `ParticipantLeft` to remaining participants: `{ connectionId, displayName }`.
7. If this was the last participant, optionally mark session ended.

### Authorization Helpers

Add private methods to the hub:

```csharp
private (LiveSessionState state, ConnectedParticipant participant) GetCallerContext()
// throws HubException if caller isn't in a known session

private void RequireTokenHolder(LiveSessionState state)
// throws HubException if caller != state.TokenHolderConnectionId

private void RequireHost(LiveSessionState state)
// throws HubException if caller != state.HostConnectionId
```

Use `HubException` (which surfaces as a rejected promise on the client) rather than silently ignoring unauthorized calls.

---

## Scratchpad DB Debounce

`UpdateScratchpad` can fire rapidly. Don't hit SQLite on every keystroke. Options:
- **Simple:** fire-and-forget `Task.Run` with no debounce for now; revisit if perf is an issue.
- **Better:** use a `Timer` per session in `SessionStateService` that resets on each update and writes after 2s of inactivity.

Start with the simple approach; note it as a known optimization point.

---

## Minimal HTML Test Page

Create `wwwroot/test.html` — not linked from anywhere, just for manual testing. Should:

1. Connect to the hub via the SignalR JS client (CDN link is fine).
2. Have input fields for `sessionId` and `displayName` + a "Join" button.
3. Show a log of all received server→client events.
4. Have buttons to trigger each client→server method manually.

This is a dev-only artifact. It can be deleted before any release.

---

## SignalR Client JS (CDN, test page only)

```html
<script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"></script>
```

Production JS in Phase 3 will include SignalR from npm or a local copy.

---

## Verification Checklist

Before moving to Phase 3, confirm using two browser windows with the test page:

- [ ] Both clients can join the same session and see each other via `ParticipantJoined`
- [ ] First joiner holds the token; second joiner does not
- [ ] Token holder's `UpdatePlayback` broadcasts `PlaybackSync` to the other client
- [ ] Non-token-holder's `UpdatePlayback` is silently rejected (hub throws, client receives error)
- [ ] `PassToken` correctly moves the token; `TokenUpdated` received by both clients
- [ ] Host `ReclaimToken` works; non-host `ReclaimToken` is rejected
- [ ] `UpdateScratchpad` broadcasts to all but sender; DB record updated
- [ ] `RequestTimestampSeek` only reaches the token holder
- [ ] Disconnecting a client triggers `ParticipantLeft` on the other; token auto-transfers if token holder disconnects
- [ ] `SessionJoined` payload contains accurate current state (position, participants, scratchpad)
