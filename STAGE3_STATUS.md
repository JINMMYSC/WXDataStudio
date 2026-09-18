# WXDataStudio v0.3 Stage 3 status

Stage 3 is implemented as a safe workspace/read-only feature set. Real phone database write-back remains disabled.

## Implemented

- Offline snapshot catalog: automatically ignores incomplete snapshots and can select a known snapshot manually.
- Database diagnostics: main DB size/status, WAL/SHM presence, and SHA-256 manifest integrity.
- Conversation search and message-type filtering.
- Group-chat sender extraction for incoming chatroom messages.
- Message parsing for text, image, voice, video, emoji, location, file, links, mini programs, contact cards, quotes, system/call records, transfer, red packet, and payment classes.
- Extended quote / mini-program / contact-card metadata fields in the UI.
- Payment, transfer, and red-packet classes are enforced read-only.
- Local workspace additions for text, images, video, voice, files, emoji, location/card, contact card, link, and quote messages.
- Workspace media import copies selected files into stable workspace-owned storage.
- Workspace edit/revert/audit/diff support, including newly created local messages.
- Timeline validation.
- Migration-readiness checks with JSON and text report export.
- CI smoke coverage for parsers, SQLCipher profiles, workspace editing/media, snapshot catalog, timeline/readiness, and report export.

## Safety / stability gates

- Original snapshots stay read-only.
- Phone write-back is not enabled.
- Transaction-class records cannot be created or edited in a workspace.
- Database session keys are not written to repository, snapshot, or application logs.

## Device-dependent acceptance still required

These cannot be truthfully marked complete until the locked MIX 2S / WeChat 8.0.76 test phone is connected again:

1. Resolve the actual WCDB key/cipher parameters for the real 8.0.76 database.
2. Load real conversations and message rows from the captured snapshot.
3. Verify real image/video/voice/file path mapping.
4. Validate representative group, quote, mini-program, system, transfer, red-packet, and payment records against the real database.
5. Validate rollback before enabling any phone write-back.
6. Validate Android WeChat launch/readback after a controlled non-sensitive test edit.
7. Run WeChat's supported Android-to-iPhone migration and produce the final migration report.
