namespace ListenRoom.Web.Models;

public class Participant
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsHost { get; set; }
    public bool HasToken { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public string Color { get; set; } = "";
    public Session Session { get; set; } = null!;
}
