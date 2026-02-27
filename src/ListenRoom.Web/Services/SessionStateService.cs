using System.Collections.Concurrent;

namespace ListenRoom.Web.Services;

public class LiveSessionState
{
    public string SessionId { get; set; } = "";
    public string TokenHolderConnectionId { get; set; } = "";
    public string HostConnectionId { get; set; } = "";
    public PlaybackState Playback { get; set; } = new();
    public Dictionary<string, ConnectedParticipant> Participants { get; set; } = new();
    private readonly object _lock = new();

    public object Lock => _lock;
}

public class PlaybackState
{
    public double Position { get; set; }
    public bool Playing { get; set; }
    public long SentAt { get; set; }
}

public class ConnectedParticipant
{
    public string ConnectionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsHost { get; set; }
    public bool HasToken { get; set; }
    public bool IsRecording { get; set; }
    public string Color { get; set; } = "";
}

public class SessionStateService
{
    private readonly ConcurrentDictionary<string, LiveSessionState> _sessions = new();

    public LiveSessionState CreateState(string sessionId, string hostConnectionId, string hostName, string hostColor)
    {
        var state = new LiveSessionState
        {
            SessionId = sessionId,
            TokenHolderConnectionId = hostConnectionId,
            HostConnectionId = hostConnectionId,
            Participants =
            {
                [hostConnectionId] = new ConnectedParticipant
                {
                    ConnectionId = hostConnectionId,
                    DisplayName = hostName,
                    IsHost = true,
                    HasToken = true,
                    Color = hostColor
                }
            }
        };

        _sessions[sessionId] = state;
        return state;
    }

    public LiveSessionState? GetState(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var state);
        return state;
    }

    public void AddParticipant(string sessionId, ConnectedParticipant participant)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            lock (state.Lock)
            {
                state.Participants[participant.ConnectionId] = participant;
            }
        }
    }

    public ConnectedParticipant? RemoveParticipant(string sessionId, string connectionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return null;

        lock (state.Lock)
        {
            if (state.Participants.Remove(connectionId, out var participant))
                return participant;
        }

        return null;
    }

    public void UpdatePlayback(string sessionId, double position, bool playing)
    {
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            state.Playback.Position = position;
            state.Playback.Playing = playing;
            state.Playback.SentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    public void TransferToken(string sessionId, string toConnectionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return;

        lock (state.Lock)
        {
            // Remove token from current holder
            if (state.Participants.TryGetValue(state.TokenHolderConnectionId, out var oldHolder))
                oldHolder.HasToken = false;

            // Give token to new holder
            if (state.Participants.TryGetValue(toConnectionId, out var newHolder))
                newHolder.HasToken = true;

            state.TokenHolderConnectionId = toConnectionId;
        }
    }

    public (string sessionId, ConnectedParticipant participant)? GetParticipantByConnectionId(string connectionId)
    {
        foreach (var kvp in _sessions)
        {
            lock (kvp.Value.Lock)
            {
                if (kvp.Value.Participants.TryGetValue(connectionId, out var participant))
                    return (kvp.Key, participant);
            }
        }

        return null;
    }

    public void DestroyState(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }
}
