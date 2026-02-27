namespace ListenRoom.Web.Models;

public class ListenRoomOptions
{
    public const string SectionName = "ListenRoom";

    public string AudioDirectory { get; set; } = "./audio";
    public string RecordingsDirectory { get; set; } = "./recordings";
    public int MaxSessionAgeDays { get; set; } = 30;
    public bool AllowAnonymousJoin { get; set; } = true;
    public bool RequireHostApproval { get; set; }
    public string FfmpegPath { get; set; } = "/usr/bin/ffmpeg";
    public bool EnablePostProcessing { get; set; } = true;
    public int RecordingChunkIntervalSeconds { get; set; } = 5;
    public string ScratchpadSyncStrategy { get; set; } = "Sidecar";
}
