# DOCS-TODO

- Composer safety net: `PlaywrightChatSession._composerSuspect` is set when a send with attachments ends without a confirmed send (offline, upload failed, upload-wait timeout, send not confirmed, cancelled, exception) and cleared only by a confirmed send; the next send reloads the chat page (waiting out any generation first) before the composer reset — document the safety net in docs/browser-automation.md.
- Dirty redelivery: when the send control stays disabled for the whole upload window with no visible chips (independent of the chip selectors), the session reloads once, redoes the delivery once, and only then fails with KA_COMPOSER_NOT_CLEAN — document the reload-and-redo behavior in docs/browser-automation.md.
- keyless doctor: AttachmentChips/AttachmentRemove/AttachmentFailed now print either the matched fallback index or the trimmed HTML of the closest candidate (chip, else composer area) — document the doctor output format in docs/browser-automation.md.
