# CLI Interface

## Inspect a Takeout batch

```text
MediaArchive.CLI inspect --input <zip-or-folder> [--input <zip-or-folder> ...] \
  --staging-parent <path> --staging-name <new-directory-name> --archive-root <path>
```

- `--input` is repeatable and accepts a ZIP file or an already extracted directory. Each input is staged beneath its own numbered root, preserving paths within that input.
- `--staging-parent` is the parent where a new, non-existing staging directory will be created. `--staging-name` is one simple directory name, not a path.
- `--archive-root` is the prospective archive root used to display intended locations. The command does not write to it.
- `--help` prints usage and exits 0.

The command prints a batch status and input staging results to stdout, followed by recognized media records and unrecognized file paths. Each item includes media kind, original input identity, source-relative path, selected year, unknown fields, intended archive path, UTC preparation timestamp, collision flag, and the complete associated sidecar JSON when available. Diagnostics, including staging failures, unresolved metadata, invalid dates, and intended-path collisions, are written to stderr.

Exit codes: 0 means every input staged successfully; 1 means one or more inputs failed staging (the command still displays successfully staged inputs and diagnostics); 2 means invalid command-line arguments. A failed or incomplete batch must not be treated as fully inspected. Staging and inspection never delete or modify source media, and inspection never writes to the prospective archive.
