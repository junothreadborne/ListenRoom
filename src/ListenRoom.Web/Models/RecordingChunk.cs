namespace ListenRoom.Web.Models;

public class RecordingChunk
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public string ParticipantId { get; set; } = "";
    public string ParticipantName { get; set; } = "";
    public int ChunkIndex { get; set; }
    public string FilePath { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public bool Assembled { get; set; }
    public Session Session { get; set; } = null!;
}
