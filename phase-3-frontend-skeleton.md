# Phase 3: Frontend Skeleton

> Three HTML pages, the SignalR client wrapper, session creation and join UI, and audio playback sync. By the end of this phase you have a fully usable synchronized listening session — two people can join a room, hear the same audio in sync, and pass the token back and forth. No scratchpad, no recording yet.

---

## Goals

- `index.html`: list audio files, create session, display active sessions
- `session.html`: full session room UI (player, participants, token controls) minus scratchpad and recording panels
- `summary.html`: stub — exists and renders session metadata; export buttons are placeholders
- `signalr-client.js`: hub connection wrapper with reconnection logic
- `player.js`: audio element management, playback sync with tolerance threshold, latency compensation
- `session.js`: orchestrates everything — receives hub events, updates UI

---

## File Layout

```
wwwroot/
├── index.html
├── session.html
├── summary.html
├── css/
│   └── main.css
└── js/
    ├── signalr-client.js
    ├── player.js
    ├── session.js
    ├── scratchpad.js       ← stub only, implemented in Phase 4
    └── recorder.js         ← stub only, implemented in Phase 5
```

---

## `index.html`

### Layout

- Page title: "ListenRoom"
- Section: "Start a Session"
  - Dropdown or list of audio files (fetched from `GET /api/audio`)
  - Text input: Display Name
  - Button: "Create Session"
- Section: "Active Sessions" (fetched from `GET /api/sessions`)
  - Each entry: session ID, audio file name, participant count, "Join" link

### Behavior (`index.html` inline script or a small `index.js`)

On load:
1. `GET /api/audio` → populate the audio file selector
2. `GET /api/sessions` → populate the active sessions list

On "Create Session":
1. `POST /api/sessions` with `{ audioFileName, displayName }`
2. Redirect to the returned session URL

On "Join" link:
- Navigate to `/session/{id}` (session page handles join flow)

---

## `session.html`

### URL pattern

The page is a single HTML file. JavaScript reads `window.location.pathname` to extract the session ID:

```js
const sessionId = window.location.pathname.split('/').pop();
```

### Layout (per spec wireframe)

```
┌──────────────────────────────────────────────────────────────────┐
│  ListenRoom                              room-4f9a  [End Session] │
├───────────────────────────────────────┬──────────────────────────┤
│                                       │  Participants             │
│  [audio filename]                     │                           │
│                                       │  ● Juno (host) 🎮         │
│  ▶  ━━━━━━━━━●━━━━━━━━━━━━  12:34    │  ○ Sarah                  │
│     0:00                    24:18     │                           │
│                                       │  [Pass Control ▾]         │
│  [⏸ Pause] [1.0x ▾]                  │  [Take Control]           │
│  [📎 Timestamp]  [📤 Export]          │                           │
│                                       │  Now controlling: Juno    │
├───────────────────────────────────────┴──────────────────────────┤
│  Scratchpad  ← placeholder, Phase 4                              │
└──────────────────────────────────────────────────────────────────┘
```

### Join Flow

On page load:
1. Show a modal/overlay: "Enter your display name" + "Join" button.
2. On submit: connect to SignalR, call `JoinSession(sessionId, displayName)`.
3. On `SessionJoined`: dismiss modal, populate UI from state payload, seek audio to synced position.

If the session doesn't exist or is ended, show an error message.

### Player Controls

- HTML5 `<audio>` element (hidden or visible — controls are custom)
- Seek bar: custom `<input type="range">` updated by `timeupdate` event
- Time display: current position / total duration
- Play/Pause button
- Speed selector: `<select>` with options 0.75, 1.0, 1.25, 1.5 — local only, not synced

**Token enforcement in UI:**
- When the local client is NOT the token holder: play/pause/seek are `disabled` and visually greyed
- When the local client IS the token holder: controls are enabled
- "Pass Control" button: opens a participant picker (simple `<select>` with connected participants minus self)
- "Take Control" button: visible only to host; always active

### Participants Panel

List of connected participants, one per row:
- Colored dot (participant's assigned color)
- Display name
- `(host)` label if applicable
- 🎮 icon next to the token holder
- Microphone icon placeholder for Phase 5

Updated in real-time by `ParticipantJoined` / `ParticipantLeft` / `TokenUpdated` events.

### End Session

"End Session" button in header — visible only to host.
On click: `POST /api/sessions/{id}/end`, then redirect to `/session/{id}/summary`.

---

## `signalr-client.js`

Wraps the SignalR `HubConnection`. Handles:

- Connection creation and start
- Automatic reconnection using SignalR's built-in `withAutomaticReconnect()`
- Event registration via a simple `on(event, handler)` wrapper
- A `send(method, ...args)` wrapper that returns a Promise
- Exposes connection state and `connectionId`

```js
class SignalRClient {
    constructor(url) { ... }
    async connect() { ... }
    on(event, handler) { ... }
    async send(method, ...args) { ... }
    get connectionId() { ... }
    get isConnected() { ... }
}
```

The hub URL is `/hubs/session`.

On reconnect: call `JoinSession` again to re-sync state. The hub's `SessionJoined` response will re-populate the client fully.

---

## `player.js`

Manages the `<audio>` element and all sync logic.

```js
class AudioPlayer {
    constructor(audioElement) { ... }

    load(audioUrl) { ... }

    // Called by hub event handler
    applySync({ position, playing, sentAt }) {
        const adjustedPosition = position + (Date.now() - sentAt) / 1000;
        const delta = Math.abs(this.audio.currentTime - adjustedPosition);
        if (delta > 2.0) {
            this.audio.currentTime = adjustedPosition;
        }
        if (playing && this.audio.paused) this.audio.play();
        if (!playing && !this.audio.paused) this.audio.pause();
    }

    // Called when local client is token holder and acts on controls
    onUserPlay() { ... }    // → triggers UpdatePlayback via callback
    onUserPause() { ... }
    onUserSeek(position) { ... }

    // Sets whether local controls are interactive
    setControlsEnabled(enabled) { ... }

    // Returns current position in seconds
    get currentTime() { ... }
}
```

**Speed control** — `audio.playbackRate = selectedSpeed`. Local only; no hub interaction.

**Tolerance threshold** — `±2.0` seconds. Values within range: do not seek. Prevents jitter during normal playback while staying in sync.

**`timeupdate` handler** — updates seek bar and time display. At 60fps this is fine; the browser throttles it appropriately.

---

## `session.js`

Top-level orchestrator. Wires `SignalRClient`, `AudioPlayer`, and the DOM together.

Responsibilities:
- Initialize `SignalRClient` and `AudioPlayer`
- Handle the join modal and call `JoinSession`
- Register handlers for all hub events and update DOM/player accordingly
- Send hub messages when user interacts with controls (play, pause, seek, pass/take token)
- Manage local state: `myConnectionId`, `isTokenHolder`, `isHost`, `sessionId`

Key event handlers:

| Hub Event | Action |
|---|---|
| `SessionJoined` | Populate participant list, load audio, seek to synced position, set token holder UI |
| `ParticipantJoined` | Add participant to list |
| `ParticipantLeft` | Remove participant from list |
| `PlaybackSync` | Call `player.applySync(...)` |
| `TokenUpdated` | Update `isTokenHolder` flag, update "Now controlling" display, toggle controls enabled/disabled |
| `SessionEnded` | Redirect to summary page |
| `SeekRequested` | Show notification to token holder: "[Name] wants to jump to [MM:SS] — [Go] [Ignore]" |

**`SeekRequested` notification** — a dismissible banner or toast. "Go" seeks immediately and syncs. "Ignore" dismisses.

---

## `summary.html`

Stub for Phase 3. Should:
- Extract session ID from URL
- `GET /api/sessions/{id}` to fetch metadata
- Display: audio file, date, participant names
- Placeholder text: "Recording assembly in progress…"
- Export buttons (disabled/greyed): "Export Notes", "Download Recordings", "Download Archive"

Will be fully implemented in Phase 6.

---

## CSS (`main.css`)

No utility framework. Plain CSS only. Priorities for Phase 3:

- Two-column layout for the session room (player left, participants right)
- Seek bar styling: remove default range input appearance, add a custom track and thumb
- Participant list: color dots, token indicator
- Join modal overlay
- Responsive to viewport changes (not mobile-optimized, just don't break at standard desktop widths)
- Disabled controls: `opacity: 0.4; pointer-events: none;` or native `disabled` attribute

Color palette for participants (accessible, distinct):

```js
const PARTICIPANT_COLORS = [
    '#e74c3c', '#3498db', '#2ecc71', '#f39c12',
    '#9b59b6', '#1abc9c', '#e67e22', '#e91e63'
];
```

---

## Static File Routing

The server must serve `session.html` for `/session/{id}` and `summary.html` for `/session/{id}/summary`. Since these are SPA-style routes, configure fallback routing:

```csharp
// In Program.cs, after MapControllers():
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/session/") && path.EndsWith("/summary"))
        await context.Response.SendFileAsync("wwwroot/summary.html");
    else if (path.StartsWith("/session/"))
        await context.Response.SendFileAsync("wwwroot/session.html");
    else
        await context.Response.SendFileAsync("wwwroot/index.html");
});
```

---

## Verification Checklist

Before moving to Phase 4, confirm with two browser windows:

- [ ] `index.html` loads audio files and active sessions on load
- [ ] Creating a session redirects to the correct session URL
- [ ] Both clients can join the same session room
- [ ] Participant list updates live when someone joins or leaves
- [ ] Token holder's play/pause/seek syncs to the other client with latency compensation
- [ ] Within-tolerance positional drift does not cause seek jitter
- [ ] Outside-tolerance seek triggers an immediate correction on the other client
- [ ] "Pass Control" correctly transfers the token; controls flip enabled/disabled accordingly
- [ ] Host "Take Control" works; non-host button is absent
- [ ] "End Session" (host) broadcasts session end and both clients redirect to summary
- [ ] Reconnecting client re-syncs to current state on `SessionJoined`
- [ ] Speed control changes local playback rate without broadcasting
- [ ] `SeekRequested` notification appears only for the token holder; "Go" and "Ignore" work
