
## /newchat and chat lifecycle (follow-up)
- /newchat slash command: same workspace → clears .agent/session.json for a fresh chat.
- /workspace <different-path> → validates git + clears session for the new workspace.
- Browser.MaxTurnsPerChat (default 20) → auto new chat when reached.
- ChatLimitNotice selector key (3 fallbacks) reported by keyless doctor.
- SwitchWorkspace: same workspace clears session only; different workspace validates git + clears session.
