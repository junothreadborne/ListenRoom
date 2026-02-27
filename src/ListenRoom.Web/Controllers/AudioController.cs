using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ListenRoom.Web.Models;

namespace ListenRoom.Web.Controllers;

[ApiController]
public class AudioController : ControllerBase
{
    private readonly ListenRoomOptions _options;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".webm", ".ogg", ".wav", ".flac"
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".webm"] = "audio/webm",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac"
    };

    public AudioController(IOptions<ListenRoomOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet("api/audio")]
    public IActionResult ListAudioFiles()
    {
        var audioDir = Path.GetFullPath(_options.AudioDirectory);
        if (!Directory.Exists(audioDir))
            return Ok(Array.Empty<object>());

        var files = Directory.GetFiles(audioDir)
            .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)))
            .Select(f => new
            {
                fileName = Path.GetFileName(f),
                fileSizeBytes = new FileInfo(f).Length
            })
            .OrderBy(f => f.fileName);

        return Ok(files);
    }

    [HttpGet("audio/{filename}")]
    public IActionResult ServeAudio(string filename)
    {
        var audioDir = Path.GetFullPath(_options.AudioDirectory);
        var filePath = Path.GetFullPath(Path.Combine(audioDir, filename));

        // Path traversal protection
        if (!filePath.StartsWith(audioDir, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Invalid file path" });

        if (!System.IO.File.Exists(filePath))
            return NotFound();

        var ext = Path.GetExtension(filePath);
        if (!ContentTypes.TryGetValue(ext, out var contentType))
            return BadRequest(new { error = "Unsupported audio format" });

        var stream = System.IO.File.OpenRead(filePath);
        return File(stream, contentType, enableRangeProcessing: true);
    }
}
