using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MediaArchive.Core;

public sealed record MediaAttributes(int? Width, int? Height, double? DurationSeconds, double? FramesPerSecond)
{
    public IReadOnlyList<string> Unknowns(string kind)
    {
        var unknowns = new List<string>();
        if (Width is null) unknowns.Add("width");
        if (Height is null) unknowns.Add("height");
        if (kind == "video" && DurationSeconds is null) unknowns.Add("duration");
        if (kind == "video" && FramesPerSecond is null) unknowns.Add("framesPerSecond");
        return unknowns;
    }
}

public sealed record MediaProbeResult(MediaAttributes Attributes, IReadOnlyList<string> Diagnostics);

public sealed class MediaProbe
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _prefixArguments;
    private readonly Func<string, string>? _outputProvider;
    public MediaProbe(string executable = "ffprobe") : this(executable, []) { }
    public MediaProbe(string executable, IEnumerable<string> prefixArguments)
    {
        _executable = executable;
        _prefixArguments = prefixArguments.ToArray();
    }
    public MediaProbe(Func<string, string> outputProvider)
    {
        _executable = "";
        _prefixArguments = [];
        _outputProvider = outputProvider ?? throw new ArgumentNullException(nameof(outputProvider));
    }

    public MediaProbeResult Probe(string path, string kind)
    {
        try
        {
            string output;
            if (_outputProvider is not null) output = _outputProvider(path);
            else
            {
                var start = new ProcessStartInfo(_executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in _prefixArguments) start.ArgumentList.Add(arg);
                foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate,duration:format=duration", "-of", "json", path }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Could not start ffprobe.");
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(entireProcessTree: true);
                    throw new IOException("ffprobe timed out after 30 seconds.");
                }
                Task.WaitAll(outputTask, errorTask);
                output = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult();
                if (process.ExitCode != 0) return Unknown($"ffprobe failed: {error.Trim()}");
            }
            using var json = JsonDocument.Parse(output);
            var stream = json.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array && streams.GetArrayLength() > 0 ? streams[0] : default;
            var width = PositiveInt(stream, "width");
            var height = PositiveInt(stream, "height");
            var duration = PositiveDouble(stream, "duration") ?? (json.RootElement.TryGetProperty("format", out var format) ? PositiveDouble(format, "duration") : null);
            double? fps = null;
            if (TryString(stream, "avg_frame_rate", out var rate))
            {
                var parts = rate.Split('/');
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0)
                    fps = numerator / denominator;
            }
            var attributes = new MediaAttributes(width, height, kind == "video" ? duration : null, kind == "video" ? fps : null);
            var unknowns = attributes.Unknowns(kind);
            return new(attributes, unknowns.Count == 0 ? [] : [$"ffprobe did not provide: {string.Join(", ", unknowns)}."]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            return Unknown($"ffprobe unavailable or invalid: {ex.Message}");
        }
    }

    private static MediaProbeResult Unknown(string diagnostic) => new(new(null, null, null, null), [diagnostic]);
    private static int? PositiveInt(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) && i > 0 ? i : null;
    private static double? PositiveDouble(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n > 0 ? n : null;
    private static bool TryString(JsonElement obj, string key, out string value) { value = ""; if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) { value = v.GetString()!; return true; } return false; }
}
