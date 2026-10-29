# DOCS-TODO

- Document `Snapshot.Filter.SensitiveFilePatterns` and `Snapshot.Filter.MaskSensitiveLines` (attachment-only sensitive-file filtering and value redaction, marker line on the tree attachment) in docs/configuration.md.

## Exit codes (`keyless run` / `keyless resume`)

| Code | Meaning |
| --- | --- |
| 0 | The turn completed (or completed by @@done). |
| 1 | The turn failed before or during the run (`result.ErrorMessage`). |
| 2 | The turn stopped early (`result.StoppedReason` is printed, e.g. no reply, chat exhausted). |
| 130 | Cancelled by the user (Ctrl+C during the turn). |
