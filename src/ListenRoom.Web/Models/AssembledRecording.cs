namespace ListenRoom.Web.Models;

public class AssembledRecording
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public string ParticipantId { get; set; } = "";
    public string ParticipantName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public DateTime AssembledAt { get; set; }
    public Session Session { get; set; } = null!;
}
