using MediaArchive.Core;

return InspectionCommand.Run(args);

internal static class InspectionCommand
{
    private const string Usage = "Usage: MediaArchive.CLI <inspect|archive> --input <zip-or-folder> [--input <zip-or-folder> ...] --staging-parent <path> --staging-name <name> --archive-root <path>\n       MediaArchive.CLI browse --archive-root <path>";

    public static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args.Length == 3 && args[0] == "browse" && args[1] == "--archive-root")
        {
            try
            {
                var catalog = ArchiveCatalogStore.Read(args[2]);
                foreach (var item in catalog.Items)
                    Console.WriteLine($"Item: {item.MediaType} | input={item.InputPath} | source={item.RelativePath} | year={item.Year} | archive={item.ArchivePath} | file={item.FileName} | sha256={item.Sha256} | width={item.Attributes.Width?.ToString() ?? "Unknown"} | height={item.Attributes.Height?.ToString() ?? "Unknown"} | duration={item.Attributes.DurationSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"} | fps={item.Attributes.FramesPerSecond?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"} | unknown={string.Join(",", item.Unknowns)} | metadata={item.OriginalMetadata?.GetRawText() ?? "Unknown"}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Catalog: failed | {ex.Message}");
                return 1;
            }
        }

        if (!TryParse(args, out var command, out var inputs, out var stagingParent, out var stagingName, out var archiveRoot, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var staging = new BatchStager().Stage(inputs, stagingParent!, archiveRoot!, stagingName!);
        var inspection = new BatchInspector().Inspect(staging, archiveRoot!);
        if (command == "archive")
        {
            if (!staging.IsComplete)
            {
                PrintStagingFailures(staging);
                return 1;
            }
            var result = new ArchiveCopier().Copy(staging, inspection, archiveRoot!);
            foreach (var copy in result.VerifiedCopies) Console.WriteLine($"Verified: {copy.ArchivePath} | sha256={copy.Sha256}");
            foreach (var diagnostic in result.Diagnostics)
                Console.Error.WriteLine($"Diagnostic: {diagnostic.Code} | input={diagnostic.InputPath ?? "Unknown"} | path={diagnostic.RelativePath ?? "Unknown"} | {diagnostic.Message}");
            var catalog = new ArchiveCatalogIntegrator().Integrate(staging, inspection, result, archiveRoot!);
            foreach (var diagnostic in catalog.Diagnostics)
                Console.Error.WriteLine($"Diagnostic: {diagnostic.Code} | input={diagnostic.InputPath ?? "Unknown"} | path={diagnostic.RelativePath ?? "Unknown"} | {diagnostic.Message}");
            foreach (var warning in catalog.Warnings)
                Console.Error.WriteLine($"Warning: {warning.Code} | input={warning.InputPath ?? "Unknown"} | path={warning.RelativePath ?? "Unknown"} | {warning.Message}");
            Console.WriteLine($"Catalog: {Path.Combine(archiveRoot!, ArchiveCatalogStore.FileName)} | items={catalog.Catalog.Items.Count}");
            return result.Succeeded && catalog.Diagnostics.Count == 0 ? 0 : 1;
        }
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

    private static void PrintStagingFailures(BatchStagingResult staging)
    {
        Console.WriteLine("Batch: incomplete");
        foreach (var input in staging.Inputs)
            Console.WriteLine($"Input: {(input.Succeeded ? "staged" : "failed")} | {input.InputPath} | {input.StagedRoot}");
        foreach (var diagnostic in new BatchInspector().Inspect(staging, staging.StagingRoot).Diagnostics)
            Console.Error.WriteLine($"Diagnostic: {diagnostic.Code} | input={diagnostic.InputPath ?? "Unknown"} | path={diagnostic.RelativePath ?? "Unknown"} | {diagnostic.Message}");
    }

    private static bool TryParse(string[] args, out string command, out List<string> inputs, out string? stagingParent, out string? stagingName, out string? archiveRoot, out string error)
    {
        command = "";
        inputs = [];
        stagingParent = null;
        stagingName = null;
        archiveRoot = null;
        error = "Invalid arguments.";
        if (args.Length == 0 || args[0] is not ("inspect" or "archive")) return false;
        command = args[0];

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

