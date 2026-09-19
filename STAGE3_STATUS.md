# WXDataStudio v0.3 Stage 3 status

Stage 3 is implemented as a workspace feature set. Snapshots stay immutable; editing happens in a local workspace copy, and the phone write-back channel is under construction (page-level re-encryption already implemented and round-trip verified).

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
- Conversation search, current-conversation message keyword search, and message-type filtering.
- Dynamic schema detection: exact WeChat table names are preferred, with column-based message/contact detection and a conversation-list fallback built from message rows when rconversation is unavailable.
- Conversation/message timestamp normalization and rendering guards prevent malformed or millisecond timestamps from crashing the UI.
- Group-chat sender extraction for incoming chatroom messages.
- Message parsing for text, image, voice, video, emoji, location, file, links, mini programs, contact cards, quotes, system/call records, transfer, red packet, and payment classes.
- Extended quote / mini-program / contact-card metadata fields in the UI.
- Payment, transfer and red-packet classes are labelled transaction-class and are editable for recovering deleted records; every change is audited.
- Legacy SQLCipher1 page re-encryption with the original salt, used by the upcoming restore channel.
- Snapshot write-ahead log folding with generation-salt filtering and a SQLite integrity gate: the fold is only accepted when SQLite validates the resulting image.
- Local workspace additions for text, images, video, voice, files, emoji, location/card, contact card, link, and quote messages.
- Workspace media import copies selected files into stable workspace-owned storage.
- Workspace media replacement is restricted to media-capable message classes.
- Workspace edit/revert/audit/diff support, including newly created local messages.
- Timeline validation.
- Migration-readiness checks with JSON and text report export.
- Privacy-safe message census (`MessageCensusService`): per-class counts with direction split, group-sender resolution, sensitive-class counts, unclassified raw types, and an appmsg sub-type histogram. It records counts and numeric type values only, never content, chat names or device identifiers.
- Conversation export (`ChatExportService`): one conversation becomes a readable HTML transcript plus a full JSON payload, with attachment accounting (local copy, device-only reference, missing) and HTML escaping of all message text. Exports are written only to the directory the user picks.
- A one-click Stage 3 read-only acceptance action creates a fresh resolver-enabled snapshot, verifies integrity/database opening/schema mapping, requires real conversations plus sample text messages, censuses every conversation, and writes a privacy-safe acceptance report plus `.census.json`.
- Snapshot read session: every SQLite/SQLCipher read runs against a verified working copy under `<snapshot>/derived/read-session`, and the acceptance flow re-checks the manifest after parsing.
- Verified rollback ZIP generation from an intact snapshot.
- CI smoke coverage for parsers, SQLCipher profiles, SC1 passphrase/raw-key page decryption, workspace editing/media, snapshot catalog, timeline/readiness, rollback packaging, and report export.

## Safety / stability gates

- Original snapshots stay read-only.
- SQLite rewrites `-shm` bookkeeping even for read-only WAL opens, so the pristine snapshot database set is never opened directly; reads use a copy whose SHA-256 is verified against the source first.
- Derived plaintext databases are written under the snapshot's `derived` area; the original encrypted database is never overwritten.
- Phone write-back is not enabled.
- Transaction-class records cannot be created or edited in a workspace.
- Database session keys are not written to repository, snapshot manifest, or application logs.
- Resolver support stays inside the local snapshot and is excluded from rollback ZIP packages.
- SQLite connection pooling is disabled for immutable snapshot reads to avoid stale handles and file-lock surprises.

## Real-device verification (MIX 2S, 2026-09-20, read-only)

Performed on the locked MIX 2S / Android 9 / WeChat 8.0.76 (3141) against snapshot `20260920-011627`.

Verified:

1. Bounded UIN + device-token + SC1 resolver opened the real 8.0.76 `EnMicroMsg.db` read-only through a derived plaintext copy. Credential source: snapshot-derived device token + UIN with SC1 page fallback. No credential values were printed or written outside the local snapshot area.
2. Snapshot capture: main DB 8,198,144 bytes, WAL 401,416 bytes, SHM 32,768 bytes, each compared as source → phone staging copy → local copy with matching size and SHA-256. Manifest integrity reported 0 errors both before and after parsing.
3. Schema: message, conversation and contact tables all detected.
4. Real data: 37 conversations (5 groups), 558 messages read across every conversation with 0 conversation read failures.
5. Message-class census: link 242 (appmsg 5 ×229, appmsg 51 ×12, appmsg 62 ×1), image 136, text 111, voice 22, file 17 (appmsg 6), video 15, unknown 4, quote 4 (appmsg 57), emoji 2, system 2, location 1, contact card 1, red packet 1. Group-sender resolution: 158 of 198 incoming group messages.
6. Transaction labelling: the red-packet record is labelled transaction-class and is editable for recovery. No transfer or payment rows exist in this snapshot.
7. Media mapping sample: image 12/12, voice 12/12, video 10/10 resolved to real files.
8. Rollback: a rollback package was generated from the snapshot and its 4 entries matched the snapshot files by length and SHA-256.
9. Immutability: manifest, sizes and SHA-256 values were unchanged after the full parse.

Not verified:

1. File and emoji payloads are absent from this phone: emoji 0/2 and file 0/8 in the resolver, and a bounded device-wide search (`/data/data/com.tencent.mm`, `/sdcard` depth 6, 25 s timeout per sample) found 0/3 file and 0/2 emoji copies. Identifiers are extracted and searchable; the cached payloads simply do not exist on the device.
2. Mini program, transfer, payment and call classes do not occur in this snapshot, so only synthetic coverage exists for them.
3. Two appmsg sub-type 1 messages and two messages without an appmsg sub-type remain `Unknown`.
4. No Android write-back, and no Android-to-iPhone migration run.

Still required before any write-back or migration claim:

1. Validate Android WeChat launch/readback after a controlled non-sensitive test edit.
2. Run WeChat's supported Android-to-iPhone migration and produce the final migration report.
