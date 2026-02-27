using Microsoft.AspNetCore.SignalR;
using ListenRoom.Web.Models;
using ListenRoom.Web.Services;

namespace ListenRoom.Web.Hubs;

public class SessionHub : Hub
{
    private readonly SessionService _sessionService;
    private readonly SessionStateService _stateService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(SessionService sessionService, SessionStateService stateService,
        IServiceScopeFactory scopeFactory, ILogger<SessionHub> logger)
    {
        _sessionService = sessionService;
        _stateService = stateService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task JoinSession(string sessionId, string displayName)
    {
        var session = await _sessionService.GetSessionAsync(sessionId);
        if (session == null || session.Status != SessionStatus.Active)
            throw new HubException("Session not found or not active.");

        var connectionId = Context.ConnectionId;
        var state = _stateService.GetState(sessionId);

        if (state == null)
        {
            // First connection — find host from DB
            var host = session.Participants.FirstOrDefault(p => p.IsHost);
            var hostName = host?.DisplayName ?? displayName;
            var hostColor = host?.Color ?? "#E57373";

            state = _stateService.CreateState(sessionId, connectionId, hostName, hostColor);
        }
        else
        {
            // Add as new participant
            var dbParticipant = await _sessionService.AddParticipantAsync(sessionId, displayName, connectionId);

            var connected = new ConnectedParticipant
            {
                ConnectionId = connectionId,
                DisplayName = displayName,
                IsHost = false,
                HasToken = false,
                Color = dbParticipant.Color
            };

            _stateService.AddParticipant(sessionId, connected);
        }

        await Groups.AddToGroupAsync(connectionId, $"session-{sessionId}");

        // Resolve token holder name
        var tokenHolderName = "";
        if (state.Participants.TryGetValue(state.TokenHolderConnectionId, out var tokenHolder))
            tokenHolderName = tokenHolder.DisplayName;

        // Send full state to the joining client
        await Clients.Caller.SendAsync("SessionJoined", new
        {
            sessionId,
            audioUrl = $"/audio/{session.AudioFileName}",
            playback = new
            {
                state.Playback.Position,
                state.Playback.Playing,
                state.Playback.SentAt
            },
            tokenHolderConnectionId = state.TokenHolderConnectionId,
            tokenHolderName,
            participants = state.Participants.Values.Select(p => new
            {
                p.ConnectionId,
                p.DisplayName,
                p.Color,
                p.IsHost,
                p.HasToken,
                p.IsRecording
            }),
            session.ScratchpadContent
        });

        // Notify others
        var callerParticipant = state.Participants.GetValueOrDefault(connectionId);
        await Clients.GroupExcept($"session-{sessionId}", connectionId).SendAsync("ParticipantJoined", new
        {
            connectionId,
            displayName = callerParticipant?.DisplayName ?? displayName,
            color = callerParticipant?.Color ?? "",
            isHost = callerParticipant?.IsHost ?? false
        });
    }

    public async Task UpdatePlayback(double position, bool playing)
    {
        var (state, participant) = GetCallerContext();
        RequireTokenHolder(state);

        _stateService.UpdatePlayback(state.SessionId, position, playing);

        await Clients.GroupExcept($"session-{state.SessionId}", Context.ConnectionId)
            .SendAsync("PlaybackSync", new
            {
                position,
                playing,
                sentAt = state.Playback.SentAt
            });
    }

    public async Task PassToken(string toConnectionId)
    {
        var (state, _) = GetCallerContext();
        RequireTokenHolder(state);

        if (!state.Participants.ContainsKey(toConnectionId))
            throw new HubException("Target participant not found in session.");

        _stateService.TransferToken(state.SessionId, toConnectionId);

        var newHolder = state.Participants[toConnectionId];
        await Clients.Group($"session-{state.SessionId}").SendAsync("TokenUpdated", new
        {
            holderConnectionId = toConnectionId,
            holderName = newHolder.DisplayName
        });
    }

    public async Task ReclaimToken()
    {
        var (state, _) = GetCallerContext();
        RequireHost(state);

        _stateService.TransferToken(state.SessionId, state.HostConnectionId);

        var host = state.Participants[state.HostConnectionId];
        await Clients.Group($"session-{state.SessionId}").SendAsync("TokenUpdated", new
        {
            holderConnectionId = state.HostConnectionId,
            holderName = host.DisplayName
        });
    }

    public async Task UpdateScratchpad(string content)
    {
        var (state, participant) = GetCallerContext();

        // Broadcast to all others immediately
        await Clients.GroupExcept($"session-{state.SessionId}", Context.ConnectionId)
            .SendAsync("ScratchpadUpdated", new
            {
                content,
                authorConnectionId = Context.ConnectionId
            });

        await Clients.GroupExcept($"session-{state.SessionId}", Context.ConnectionId)
            .SendAsync("ParticipantTyping", new
            {
                connectionId = Context.ConnectionId,
                displayName = participant.DisplayName
            });

        // Persist to DB (fire-and-forget with its own scope)
        var sessionId = state.SessionId;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
                await svc.UpdateScratchpadAsync(sessionId, content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist scratchpad for session {SessionId}", sessionId);
            }
        });
    }

    public async Task RecordingReady()
    {
        var (state, participant) = GetCallerContext();
        // Mark ready but not yet recording
        await Clients.Group($"session-{state.SessionId}").SendAsync("RecordingStateChanged", new
        {
            connectionId = Context.ConnectionId,
            isRecording = false
        });
    }

    public async Task RecordingStarted()
    {
        var (state, participant) = GetCallerContext();
        participant.IsRecording = true;

        await Clients.Group($"session-{state.SessionId}").SendAsync("RecordingStateChanged", new
        {
            connectionId = Context.ConnectionId,
            isRecording = true
        });
    }

    public async Task RecordingStopped()
    {
        var (state, participant) = GetCallerContext();
        participant.IsRecording = false;

        await Clients.Group($"session-{state.SessionId}").SendAsync("RecordingStateChanged", new
        {
            connectionId = Context.ConnectionId,
            isRecording = false
        });
    }

    public async Task RequestTimestampSeek(double positionSeconds)
    {
        var (state, participant) = GetCallerContext();

        if (Context.ConnectionId == state.TokenHolderConnectionId)
            throw new HubException("Token holders can seek directly.");

        await Clients.Client(state.TokenHolderConnectionId).SendAsync("SeekRequested", new
        {
            fromName = participant.DisplayName,
            positionSeconds
        });
    }

    public async Task LeaveSession()
    {
        await HandleLeave(Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await HandleLeave(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task HandleLeave(string connectionId)
    {
        var result = _stateService.GetParticipantByConnectionId(connectionId);
        if (result == null) return;

        var (sessionId, participant) = result.Value;
        var state = _stateService.GetState(sessionId);
        if (state == null) return;

        _stateService.RemoveParticipant(sessionId, connectionId);
        await Groups.RemoveFromGroupAsync(connectionId, $"session-{sessionId}");

        // Update DB (use own scope since this may fire during disconnect)
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
            await svc.MarkParticipantLeftAsync(sessionId, connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark participant left in DB");
        }

        // If token holder left, auto-transfer to host
        if (connectionId == state.TokenHolderConnectionId && state.Participants.Count > 0)
        {
            var newHolder = state.HostConnectionId;
            // If host is gone too, pick anyone
            if (!state.Participants.ContainsKey(newHolder))
                newHolder = state.Participants.Keys.First();

            _stateService.TransferToken(sessionId, newHolder);

            var holderParticipant = state.Participants[newHolder];
            await Clients.Group($"session-{sessionId}").SendAsync("TokenUpdated", new
            {
                holderConnectionId = newHolder,
                holderName = holderParticipant.DisplayName
            });
        }

        await Clients.Group($"session-{sessionId}").SendAsync("ParticipantLeft", new
        {
            connectionId,
            displayName = participant.DisplayName
        });

        // If last participant, destroy state
        if (state.Participants.Count == 0)
        {
            _stateService.DestroyState(sessionId);
        }
    }

    private (LiveSessionState state, ConnectedParticipant participant) GetCallerContext()
    {
        var result = _stateService.GetParticipantByConnectionId(Context.ConnectionId);
        if (result == null)
            throw new HubException("You are not in a session.");

        var (sessionId, participant) = result.Value;
        var state = _stateService.GetState(sessionId)
            ?? throw new HubException("Session state not found.");

        return (state, participant);
    }

    private void RequireTokenHolder(LiveSessionState state)
    {
        if (Context.ConnectionId != state.TokenHolderConnectionId)
            throw new HubException("Only the token holder can perform this action.");
    }

    private void RequireHost(LiveSessionState state)
    {
        if (Context.ConnectionId != state.HostConnectionId)
            throw new HubException("Only the host can perform this action.");
    }
}
