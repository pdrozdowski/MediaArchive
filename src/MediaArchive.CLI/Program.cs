using MediaArchive.Core;

return InspectionCommand.Run(args);

internal static class InspectionCommand
{
    private const string Usage = "Usage: MediaArchive.CLI inspect --input <zip-or-folder> [--input <zip-or-folder> ...] --staging-parent <path> --staging-name <name> --archive-root <path>";

    public static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (!TryParse(args, out var inputs, out var stagingParent, out var stagingName, out var archiveRoot, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var staging = new BatchStager().Stage(inputs, stagingParent!, archiveRoot!, stagingName!);
        var inspection = new BatchInspector().Inspect(staging, archiveRoot!);
        Console.WriteLine($"Batch: {(staging.IsComplete ? "complete" : "incomplete")}");
        Console.WriteLine($"Staging root: {staging.StagingRoot}");
        foreach (var input in staging.Inputs)
            Console.WriteLine($"Input: {(input.Succeeded ? "staged" : "failed")} | {input.InputPath} | {input.StagedRoot}");

        foreach (var item in inspection.Items)
        {
            var sidecar = item.Sidecar is { } json ? json.GetRawText() : "Unknown";
            var unknowns = item.Unknowns.Count == 0 ? "none" : string.Join(",", item.Unknowns);
            Console.WriteLine($"Item: {item.MediaKind} | input={item.InputPath} | source={item.RelativePath} | year={item.Year} | unknown={unknowns} | archive={item.IntendedArchivePath} | preparedUtc={item.PreparedAtUtc:O} | collision={item.IntendedPathCollision} | metadata={sidecar}");
        }

        foreach (var file in inspection.UnrecognizedFiles)
            Console.WriteLine($"Unrecognized: {file}");

        foreach (var diagnostic in inspection.Diagnostics)
            Console.Error.WriteLine($"Diagnostic: {diagnostic.Code} | input={diagnostic.InputPath ?? "Unknown"} | path={diagnostic.RelativePath ?? "Unknown"} | {diagnostic.Message}");

        return staging.IsComplete ? 0 : 1;
    }

    private static bool TryParse(string[] args, out List<string> inputs, out string? stagingParent, out string? stagingName, out string? archiveRoot, out string error)
    {
        inputs = [];
        stagingParent = null;
        stagingName = null;
        archiveRoot = null;
        error = "Invalid arguments.";
        if (args.Length == 0 || args[0] != "inspect") return false;

        for (var index = 1; index < args.Length; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option '{option}' requires a value.";
                return false;
            }

            var value = args[++index];
            switch (option)
            {
                case "--input": inputs.Add(value); break;
                case "--staging-parent": if (stagingParent is not null) return Duplicate(option, out error); stagingParent = value; break;
                case "--staging-name": if (stagingName is not null) return Duplicate(option, out error); stagingName = value; break;
                case "--archive-root": if (archiveRoot is not null) return Duplicate(option, out error); archiveRoot = value; break;
                default: error = $"Unknown option '{option}'."; return false;
            }
        }

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(stagingParent) || string.IsNullOrWhiteSpace(stagingName) || string.IsNullOrWhiteSpace(archiveRoot))
        {
            error = "At least one --input and all three roots/names are required.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool Duplicate(string option, out string error)
    {
        error = $"Option '{option}' may be supplied only once.";
        return false;
    }
}
