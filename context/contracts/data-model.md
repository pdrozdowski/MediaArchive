# Data Model

## Batch staging

A batch contains one or more operator-supplied ZIP files and/or extracted folders. Each input is copied or extracted into a distinct numbered staging root; source-relative hierarchy is retained. Staging returns an overall completeness flag and one result per input with its source path, staged root, success state, and failure diagnostic. Unsafe paths, links/reparse points, root overlap, destination collisions, malformed ZIPs, and I/O failures produce incomplete results. Partial staging output may remain for inspection; sources are never removed or overwritten.

## Inspected media item

Each recognized photo or video record contains:

- Original input path and path relative to that input's staged root.
- Media kind (`photo` or `video`).
- The complete parsed JSON sidecar when one unambiguous association is established; otherwise the sidecar value is unknown.
- Explicit unknown field names, including `sidecar` when no unambiguous sidecar is available and `year` when no trustworthy year can be selected.
- Selected year, intended archive path, UTC preparation timestamp, and a flag indicating whether the intended path collides with another record.

Only common photo extensions (`jpg`, `jpeg`, `png`, `gif`, `webp`, `heic`, `heif`, `bmp`, `tif`, `tiff`) and video extensions (`mp4`, `mov`, `m4v`, `avi`, `mpg`, `mpeg`, `3gp`, `webm`) are recognized, without regard to extension case. Other files are listed as unrecognized.

## Sidecar association and dates

A JSON sidecar is associated only when its string `title` exactly equals a media basename including extension, in the same staged input directory, and the title has exactly one sidecar and one media claim. The full JSON object is preserved. Missing, malformed, competing, and cross-input-part candidates remain unresolved and produce diagnostics; no association is inferred from dates or other filename similarities.

The archive year comes first from `photoTakenTime.timestamp` when it is a valid, nonzero integer Unix-seconds value; conversion uses UTC. If that value is absent, malformed, zero, or out of range, the media filename is checked for a unique valid `YYYYMMDD` or `YYYY-MM-DD` token bounded by non-digits. Invalid or multiple distinct date tokens produce `Unknown`. Filesystem timestamps are not used. The intended path is prospective archive root / selected year (or `Unknown`) / original filename; it is a preview only and is never written during inspection.

## Diagnostics and preservation

Staging failures and inspection diagnostics identify the input and, where available, the relative path. Diagnostics cover unresolved or malformed metadata, unusable dates, unsupported files, and intended-path collisions. A batch with any failed input is incomplete even when other inputs staged successfully. Preparation is read-only with respect to source media and the prospective archive.
