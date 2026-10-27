
## Follow-up fixes (post-docs)
- SendButton fallbacks: paperclip (iconLabelPrimary) removed and explicitly skipped in code — clicking it opened the OS "Open" file dialog instead of sending; retry is now Enter-only.
- Unborn-branch fix: git branch --show-current preferred (rev-parse --abbrev-ref returns "HEAD" with no commits).
- Port-probe timeout no longer surfaces as "cancelled by the user" (TaskCanceledException distinguished from caller cancellation).
- Snapshots generated natively in C# (WorkspaceAnalyzer) — no workspace bat files needed.
