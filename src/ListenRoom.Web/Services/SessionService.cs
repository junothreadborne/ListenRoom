using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ListenRoom.Web.Data;
using ListenRoom.Web.Models;

namespace ListenRoom.Web.Services;

public class SessionService
{
    private readonly AppDbContext _db;
    private readonly ListenRoomOptions _options;

    private static readonly string[] ColorPalette =
    [
        "#E57373", "#64B5F6", "#81C784", "#FFD54F",
        "#BA68C8", "#4DD0E1", "#FF8A65", "#A1887F",
        "#90A4AE", "#F06292"
    ];

    public SessionService(AppDbContext db, IOptions<ListenRoomOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<Session> CreateSessionAsync(string audioFileName, string hostDisplayName)
    {
        var sessionId = "room-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLower();
        var audioPath = Path.GetFullPath(Path.Combine(_options.AudioDirectory, audioFileName));

        var session = new Session
        {
            Id = sessionId,
            AudioFileName = audioFileName,
            AudioFilePath = audioPath,
            CreatedAt = DateTime.UtcNow,
            Status = SessionStatus.Active
        };

        var host = new Participant
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionId,
            DisplayName = hostDisplayName,
            IsHost = true,
            HasToken = true,
            JoinedAt = DateTime.UtcNow,
            Color = ColorPalette[0]
        };

        session.Participants.Add(host);

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        // Ensure recording directory exists
        var recordingDir = Path.Combine(_options.RecordingsDirectory, sessionId);
        Directory.CreateDirectory(recordingDir);

        return session;
    }

    public async Task<Session?> GetSessionAsync(string sessionId)
    {
        return await _db.Sessions
            .Include(s => s.Participants)
            .Include(s => s.RecordingChunks)
            .FirstOrDefaultAsync(s => s.Id == sessionId);
    }

    public async Task<List<Session>> GetActiveSessionsAsync()
    {
        return await _db.Sessions
            .Include(s => s.Participants)
            .Where(s => s.Status == SessionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<Session?> EndSessionAsync(string sessionId)
    {
        var session = await _db.Sessions.FindAsync(sessionId);
        if (session == null) return null;

        session.Status = SessionStatus.Ended;
        session.EndedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return session;
    }

    public async Task<Participant> AddParticipantAsync(string sessionId, string displayName, string connectionId, bool isHost = false)
    {
        var participantCount = await _db.Participants.CountAsync(p => p.SessionId == sessionId);
        var color = ColorPalette[participantCount % ColorPalette.Length];

        var participant = new Participant
        {
            Id = connectionId,
            SessionId = sessionId,
            DisplayName = displayName,
            IsHost = isHost,
            HasToken = isHost,
            JoinedAt = DateTime.UtcNow,
            Color = color
        };

        _db.Participants.Add(participant);
        await _db.SaveChangesAsync();
        return participant;
    }

    public async Task UpdateScratchpadAsync(string sessionId, string content)
    {
        var session = await _db.Sessions.FindAsync(sessionId);
        if (session == null) return;

        session.ScratchpadContent = content;
        await _db.SaveChangesAsync();
    }

    public async Task MarkParticipantLeftAsync(string sessionId, string connectionId)
    {
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.SessionId == sessionId && p.Id == connectionId);
        if (participant != null)
        {
            participant.LeftAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task MarkSessionsInterruptedAsync()
    {
        var activeSessions = await _db.Sessions
            .Where(s => s.Status == SessionStatus.Active)
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            session.Status = SessionStatus.Ended;
            session.EndedAt = DateTime.UtcNow;
        }

        if (activeSessions.Count > 0)
            await _db.SaveChangesAsync();
    }
}
