# Phase 4: Scratchpad

> Collaborative text editing with Yjs CRDT, markdown rendering, timestamp insertion, authorship attribution, and typing indicators. By the end of this phase the scratchpad is fully functional as a real-time collaborative workspace.

---

## Goals

- Yjs Node.js sidecar running and proxied through the ASP.NET Core server
- `scratchpad.js` implemented: Yjs client, awareness, edit/view toggle
- Timestamp insertion (`[MM:SS]`) keyed to current audio position
- Authorship coloring on lines in view mode
- Clickable timestamps triggering `RequestTimestampSeek`
- Typing indicator in scratchpad header
- Scratchpad content persisted to SQLite on change (debounced)

---

## Architecture Decision: Yjs Sidecar (Option B)

Use the Node.js `y-websocket` sidecar. The ASP.NET Core server proxies WebSocket connections from the browser to the sidecar.

**Why proxy rather than connect directly:**
- Keeps the browser-visible URL on a single origin (no CORS complications)
- The sidecar doesn't need to know about session auth
- Easier to swap out for Option A (native C#) later

**Sidecar location:** `sidecar/` at project root.

---

## Node.js Sidecar

### `sidecar/package.json`

```json
{
  "name": "listenroom-yjs-sidecar",
  "version": "1.0.0",
  "type": "module",
  "dependencies": {
    "y-websocket": "^2.0.0"
  },
  "scripts": {
    "start": "node server.js"
  }
}
```

### `sidecar/server.js`

```js
import { WebSocketServer } from 'ws'
import { setupWSConnection } from 'y-websocket/bin/utils'
import http from 'http'

const PORT = process.env.PORT || 1234

const server = http.createServer((req, res) => {
    res.writeHead(200)
    res.end('y-websocket sidecar')
})

const wss = new WebSocketServer({ server })

wss.on('connection', (ws, req) => {
    setupWSConnection(ws, req)
})

server.listen(PORT, () => {
    console.log(`Yjs sidecar listening on port ${PORT}`)
})
```

Room names in `y-websocket` map to session IDs. The client connects with a room name equal to the `sessionId`.

### `sidecar/Dockerfile`

```dockerfile
FROM node:20-alpine
WORKDIR /app
COPY package.json .
RUN npm install
COPY server.js .
EXPOSE 1234
CMD ["node", "server.js"]
```

---

## WebSocket Proxy in ASP.NET Core

The browser connects to `/hubs/yjs/{sessionId}` on the ASP.NET Core server. The server proxies this to `ws://localhost:1234/{sessionId}` (the sidecar).

Add a middleware or minimal endpoint in `Program.cs`:

```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hubs/yjs") && context.WebSockets.IsWebSocketRequest)
    {
        var sessionId = context.Request.Path.Value!.Split('/').Last();
        // validate session is active
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://localhost:1234/{sessionId}"), CancellationToken.None);
        await ProxyWebSocket(context, client);
        return;
    }
    await next();
});
```

`ProxyWebSocket` is a helper that bidirectionally pumps messages between the browser WebSocket and the sidecar WebSocket. Keep it simple: two background tasks, one reading from each end and writing to the other.

**Configuration:** the sidecar port should be read from `appsettings.json`:

```json
"ListenRoom": {
    "YjsSidecarUrl": "ws://localhost:1234"
}
```

---

## `scratchpad.js`

### Initialization

```js
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'

class Scratchpad {
    constructor({ sessionId, connectionId, displayName, color, audioPlayer, hubClient }) {
        this.doc = new Y.Doc()
        this.text = this.doc.getText('scratchpad')
        this.provider = new WebsocketProvider(
            `ws://${location.host}/hubs/yjs`,
            sessionId,
            this.doc
        )
        this.awareness = this.provider.awareness
        // set local awareness state
        this.awareness.setLocalStateField('user', { name: displayName, color })
        // ...
    }
}
```

### Edit / View Mode

Two rendering modes, toggled by a button (also keyboard shortcut `Ctrl+E` / `Cmd+E`):

**Edit mode:**
- `<textarea>` (or a `contenteditable` div) bound to the Yjs `Y.Text` via `y-codemirror` or manual binding.
- For V1, a plain `<textarea>` with manual sync to `Y.Text` is acceptable. More complex but zero dependencies.
- Simpler approach: use a `<textarea>` and on each `input` event, compute the diff and apply it to `Y.Text`. On `Y.Text` change, update the textarea if the change came from a remote peer.

**View mode:**
- Render `Y.Text.toString()` as markdown using a lightweight renderer (e.g., `marked.js` from CDN).
- Post-process rendered HTML to make timestamps clickable (see below).
- Apply authorship coloring (see below).

### Yjs ↔ Textarea Sync (Manual Binding)

```js
// On local input:
textarea.addEventListener('input', () => {
    const newContent = textarea.value
    // compute diff from last known value, apply to Y.Text
    // simplest: clear and reinsert (lossy for collaboration during edit)
    // better: use a proper diff algorithm
})

// On remote change:
this.text.observe(() => {
    const remote = this.text.toString()
    if (textarea.value !== remote) {
        const sel = { start: textarea.selectionStart, end: textarea.selectionEnd }
        textarea.value = remote
        // attempt to restore cursor (best-effort)
        textarea.setSelectionRange(sel.start, sel.end)
    }
})
```

> **Note:** A full cursor-preserving collaborative textarea binding is non-trivial. For V1, the acceptable compromise is: during active local editing, remote updates may jump the cursor. This is rare in the 2-person use case and tolerable. If it becomes painful, evaluate `y-codemirror.next` as a drop-in upgrade.

### Timestamp Insertion

"Insert Timestamp" button + `Ctrl+T` / `Cmd+T`:

```js
function insertTimestamp() {
    const seconds = Math.floor(audioPlayer.currentTime)
    const mm = String(Math.floor(seconds / 60)).padStart(2, '0')
    const ss = String(seconds % 60).padStart(2, '0')
    const stamp = `[${mm}:${ss}]`

    // Insert at cursor position in textarea
    const pos = textarea.selectionStart
    const before = textarea.value.slice(0, pos)
    const after = textarea.value.slice(pos)
    textarea.value = before + stamp + after
    // Apply to Y.Text
    this.text.insert(pos, stamp)
    textarea.setSelectionRange(pos + stamp.length, pos + stamp.length)
}
```

### Clickable Timestamps in View Mode

After markdown rendering, find all `[MM:SS]` patterns in the text and replace with `<a>` elements:

```js
function makeTimestampsClickable(html) {
    return html.replace(/\[(\d{2}):(\d{2})\]/g, (match, mm, ss) => {
        const seconds = parseInt(mm) * 60 + parseInt(ss)
        return `<a href="#" class="timestamp-link" data-seconds="${seconds}">${match}</a>`
    })
}

// Event delegation on the view container:
viewContainer.addEventListener('click', (e) => {
    if (e.target.classList.contains('timestamp-link')) {
        e.preventDefault()
        const pos = parseFloat(e.target.dataset.seconds)
        if (isTokenHolder) {
            audioPlayer.seek(pos)
        } else {
            hubClient.send('RequestTimestampSeek', pos)
        }
    }
})
```

### Authorship Attribution

Yjs awareness provides per-peer metadata. Use it to track which character ranges belong to which user.

However, tracking per-character authorship in a `Y.Text` without a dedicated extension (like Yjs's `Y.Attributes`) is complex. **V1 approach:** track authorship at the line level.

- When a participant edits, tag lines they've touched using a separate `Y.Map` keyed by line number → `{ authorConnectionId, color }`.
- On render in view mode, wrap each line in a `<div>` with a left-border `style="border-left: 3px solid {color}"`.
- This is approximate (last writer per line wins) but sufficient for the language learning use case.

### Typing Indicator

Yjs awareness handles this without a SignalR message. When a peer's awareness state includes an `isTyping: true` field, display their name in the scratchpad header.

```js
this.awareness.on('change', () => {
    const states = Array.from(this.awareness.getStates().values())
    const typing = states
        .filter(s => s.isTyping && s.user?.name !== displayName)
        .map(s => s.user.name)
    typingIndicator.textContent = typing.length
        ? `${typing.join(', ')} editing…`
        : ''
})

// On local input, set and clear isTyping:
let typingTimeout
textarea.addEventListener('input', () => {
    this.awareness.setLocalStateField('isTyping', true)
    clearTimeout(typingTimeout)
    typingTimeout = setTimeout(() => {
        this.awareness.setLocalStateField('isTyping', false)
    }, 2000)
})
```

---

## Scratchpad Persistence

Yjs syncs the document in-memory via the sidecar. The sidecar does not persist to disk by default (in-memory only). On session end the current scratchpad content must be saved to SQLite.

**Strategy:**
- On `Y.Text` change, debounce a `POST` or `PUT` to the server with the full text content.
- Server calls `SessionService.UpdateScratchpadAsync(sessionId, content)`.
- On session end (before redirect), send a final save with the current content.

**Debounce interval:** 3 seconds of inactivity before writing to DB.

The existing `UpdateScratchpad` hub method from Phase 2 is repurposed: instead of being the sync mechanism, it becomes the persistence endpoint. The client calls it on debounce with the full content.

> Alternatively, the server can call `SessionService.UpdateScratchpadAsync` directly from a background polling loop. The hub method approach is simpler and avoids a separate HTTP call.

---

## Scratchpad UI Layout

```
┌────────────────────────────────────────────────────────────────┐
│  Scratchpad      [Edit] [View]   Sarah editing…   [Ctrl+T]     │
├────────────────────────────────────────────────────────────────┤
│  (edit mode: textarea)                                         │
│  (view mode: rendered markdown with clickable timestamps)      │
└────────────────────────────────────────────────────────────────┘
```

The scratchpad panel occupies the bottom section of `session.html`, as shown in the spec wireframe.

---

## Dependencies (frontend)

Add to `wwwroot` (download or CDN):

| Library | Purpose |
|---|---|
| `yjs` | CRDT core |
| `y-websocket` | WebSocket provider for Yjs |
| `marked` | Markdown rendering in view mode |

For V1, CDN links are acceptable:

```html
<script type="module">
    import * as Y from 'https://cdn.jsdelivr.net/npm/yjs/+esm'
    import { WebsocketProvider } from 'https://cdn.jsdelivr.net/npm/y-websocket/+esm'
    import { marked } from 'https://cdn.jsdelivr.net/npm/marked/+esm'
</script>
```

---

## `docker-compose.yml`

For local dev with the sidecar running alongside the app:

```yaml
version: '3.8'
services:
  app:
    build: .
    ports:
      - "5000:5000"
    depends_on:
      - yjs-sidecar
    environment:
      - ListenRoom__YjsSidecarUrl=ws://yjs-sidecar:1234

  yjs-sidecar:
    build: ./sidecar
    ports:
      - "1234:1234"
```

For `dotnet run` local dev (no Docker): start the sidecar manually with `node sidecar/server.js` alongside `dotnet run`.

---

## Verification Checklist

Before moving to Phase 5, confirm with two browser windows:

- [ ] Both clients see the same scratchpad content after typing in either window
- [ ] No data loss during simultaneous edits (Yjs resolves conflicts)
- [ ] Edit/view toggle renders markdown correctly
- [ ] Timestamp insertion via button inserts `[MM:SS]` at cursor
- [ ] `Ctrl+T` / `Cmd+T` shortcut also inserts timestamp
- [ ] Timestamps in view mode are clickable links
- [ ] Token holder clicking a timestamp seeks audio directly
- [ ] Non-token-holder clicking a timestamp sends `RequestTimestampSeek`; token holder sees notification
- [ ] Typing indicator shows peer name while they're editing; clears after 2s idle
- [ ] Authorship colors appear on lines in view mode
- [ ] Scratchpad content survives a page refresh (persisted to DB, restored via `SessionJoined`)
- [ ] Sidecar connection failure degrades gracefully (edit still works, sync resumes on reconnect)
