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


## Archive a batch

```text
MediaArchive.CLI archive --input <zip-or-folder> [--input <zip-or-folder> ...] \
  --staging-parent <path> --staging-name <new-directory-name> --archive-root <path>
```

The command stages and inspects the batch, hashes recognized staged sources with SHA-256, and checks their size and hash against catalog candidates and earlier verified items in the batch. A duplicate is associated with an existing archive file only after its current bytes are freshly hashed and verified; no second media file is created. Otherwise, the command copies new media into `<archive-root>/<year-or-Unknown>/<filename>` and independently hashes the source and destination. It prints `Verified` lines only for newly copied or duplicate-associated content whose archived bytes match. Each source occurrence is retained as its own catalog entry with its metadata and provenance, even when entries share one archive path. It does not overwrite existing media files or remove source or staging files. Incomplete staging or malformed/unreadable catalog prevents archive integration. Copy, candidate, collision, verification, and catalog errors are written to stderr and return 1; invalid arguments return 2. Unavailable media probing is reported as a warning and fields remain explicitly unknown.

## Browse the local catalog

```text
MediaArchive.CLI browse --archive-root <path>
```

Reads the persisted catalog without staging a batch and prints each source association, including input identity, source-relative path, year, archive path, hash, unknown fields, and available original sidecar metadata. Duplicate sources appear as separate entries and may share an archive path. Returns 0 when the catalog is readable and 1 when it is malformed or inaccessible. An absent catalog is an empty catalog.
