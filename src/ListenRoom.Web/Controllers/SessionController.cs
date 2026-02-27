using Microsoft.AspNetCore.Mvc;
using ListenRoom.Web.Services;

namespace ListenRoom.Web.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionController : ControllerBase
{
    private readonly SessionService _sessionService;

    public SessionController(SessionService sessionService)
    {
        _sessionService = sessionService;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AudioFileName) || string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest(new { error = "audioFileName and displayName are required" });

        var session = await _sessionService.CreateSessionAsync(request.AudioFileName, request.DisplayName);
        return Ok(new { sessionId = session.Id, sessionUrl = $"/session/{session.Id}" });
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var sessions = await _sessionService.GetActiveSessionsAsync();
        var result = sessions.Select(s => new
        {
            s.Id,
            s.AudioFileName,
            s.CreatedAt,
            status = s.Status.ToString(),
            participantCount = s.Participants.Count
        });
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var session = await _sessionService.GetSessionAsync(id);
        if (session == null) return NotFound();

        return Ok(new
        {
            session.Id,
            session.AudioFileName,
            session.CreatedAt,
            session.EndedAt,
            status = session.Status.ToString(),
            session.ScratchpadContent,
            participants = session.Participants.Select(p => new
            {
                p.Id,
                p.DisplayName,
                p.IsHost,
                p.HasToken,
                p.Color,
                p.JoinedAt,
                p.LeftAt
            })
        });
    }

    [HttpPost("{id}/end")]
    public async Task<IActionResult> End(string id)
    {
        var session = await _sessionService.EndSessionAsync(id);
        if (session == null) return NotFound();
        return Ok(new { message = "Session ended", sessionId = id });
    }
}

public class CreateSessionRequest
{
    public string AudioFileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
