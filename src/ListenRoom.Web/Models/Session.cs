namespace ListenRoom.Web.Models;

public class Session
{
    public string Id { get; set; } = "";
    public string AudioFileName { get; set; } = "";
    public string AudioFilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SessionStatus Status { get; set; }
    public string ScratchpadContent { get; set; } = "";
    public List<Participant> Participants { get; set; } = new();
    public List<RecordingChunk> RecordingChunks { get; set; } = new();
    public List<AssembledRecording> AssembledRecordings { get; set; } = new();
}

public enum SessionStatus { Active, Ended }
