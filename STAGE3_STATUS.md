# WXDataStudio v0.3 Stage 3 status

Stage 3 is implemented as a safe workspace/read-only feature set. Real phone database write-back remains disabled.

## Implemented

- Offline snapshot catalog: automatically ignores incomplete snapshots and can select a known snapshot manually.
- New snapshots also capture hashed local resolver-support files (WeChat auth/system prefs, CompatibleInfo.cfg, and bounded device-token probes) under `support/`; values are never printed to logs.
- Credential diagnostics/resolution can use that support context offline, so a later USB disconnect does not force re-reading the phone.
- Database diagnostics: main DB size/status, WAL/SHM presence, and SHA-256 manifest integrity.
- Privacy-safe database key diagnostics: reports only whether UIN/device-token sources are available and how many bounded candidates can be tested; actual UIN/IMEI/key values are not displayed or logged.
- Bounded UIN discovery from WeChat auth, system-config, and last-login preference files.
- Bounded device-token discovery from system properties plus printable candidates recovered from `CompatibleInfo.cfg`.
- Legacy WeChat passphrase candidates including `MD5(device token + UIN)[:7]`, signed/unsigned UIN variants, and an empty-device fallback.
- Read-only SQLCipher compatibility probing plus a deterministic SQLCipher1-style page decrypt fallback (1024-byte pages / PBKDF2-HMAC-SHA1 / 4000 iterations / no HMAC) that writes only a derived plaintext copy.
- Manual session passphrase and raw 32-byte AES-key fallback; key material stays in process memory and is never written to application logs.
- Conversation search and message-type filtering.
- Group-chat sender extraction for incoming chatroom messages.
- Message parsing for text, image, voice, video, emoji, location, file, links, mini programs, contact cards, quotes, system/call records, transfer, red packet, and payment classes.
- Extended quote / mini-program / contact-card metadata fields in the UI.
- Payment, transfer, and red-packet classes are enforced read-only.
- Local workspace additions for text, images, video, voice, files, emoji, location/card, contact card, link, and quote messages.
- Workspace media import copies selected files into stable workspace-owned storage.
- Workspace media replacement is restricted to media-capable message classes.
- Workspace edit/revert/audit/diff support, including newly created local messages.
- Timeline validation.
- Migration-readiness checks with JSON and text report export.
- Verified rollback ZIP generation from an intact snapshot.
- CI smoke coverage for parsers, SQLCipher profiles, SC1 passphrase/raw-key page decryption, workspace editing/media, snapshot catalog, timeline/readiness, rollback packaging, and report export.

## Safety / stability gates

- Original snapshots stay read-only.
- Derived plaintext databases are written under the snapshot's `derived` area; the original encrypted database is never overwritten.
- Phone write-back is not enabled.
- Transaction-class records cannot be created or edited in a workspace.
- Database session keys are not written to repository, snapshot manifest, or application logs.
- Resolver support stays inside the local snapshot and is excluded from rollback ZIP packages.
- SQLite connection pooling is disabled for immutable snapshot reads to avoid stale handles and file-lock surprises.

## Device-dependent acceptance still required

These cannot be truthfully marked complete until the locked MIX 2S / WeChat 8.0.76 test phone is connected again:

1. Create one fresh resolver-enabled snapshot on the locked MIX 2S, then run the bounded UIN + device-token + SC1 resolver against the real 8.0.76 `EnMicroMsg.db`.
2. If no bounded passphrase matches, obtain a raw key through a controlled user-owned-device diagnostic path and validate it without altering the source snapshot.
3. Load real conversations and message rows from the captured snapshot.
4. Verify real image/video/voice/file path mapping.
5. Validate representative group, quote, mini-program, system, transfer, red-packet, and payment records against the real database.
6. Validate rollback before enabling any phone write-back.
7. Validate Android WeChat launch/readback after a controlled non-sensitive test edit.
8. Run WeChat's supported Android-to-iPhone migration and produce the final migration report.
