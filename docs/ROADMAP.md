# WXDataStudio Roadmap

## Phase 0 — Locked test baseline

Status: in progress

Baseline:
- Xiaomi MIX 2S / polaris
- MIUI V10.3.5.0.PDGCNXM
- Android 9 / SDK 28
- arm64-v8a
- Magisk root
- WeChat 8.0.76 / versionCode 3141
- APK SHA-256 `04792eb19a566395a9fabee9a50be80245826cb774ec64637a22f7f28acd4054`

Deliverables:
- Adapter manifest
- Environment report
- APK hash verification
- No APK binary committed to repository

## Phase 1 — Windows shell and device page

Deliverables:
- .NET 8 WPF app
- Four-pane main UI
- ADB device discovery
- Device/MIUI/Android/Root/WeChat version display
- Compatibility badge for Adapter 8.0.76
- Activity log panel

Exit criteria:
- App builds on GitHub Actions windows-latest
- MIX 2S is shown as compatible when version/hash match

## Phase 2 — Snapshot engine

Deliverables:
- Immutable snapshot model
- Hash manifest
- Workspace clone model
- Snapshot browser
- Restore-point metadata
- No edits to source snapshot

Exit criteria:
- Snapshot can be created twice with deterministic manifest format
- Hash validation reports pass/fail clearly

## Phase 3 — Read-only database adapter

Deliverables:
- Adapter contract
- EnMicroMsg.db/WAL/SHM location detection
- Database-open provider interface
- Conversation mapping
- Contact mapping
- Message mapping
- Unknown-type capture

Exit criteria:
- Real test workspace can be parsed without modifying phone data
- Conversation list and text messages are rendered

## Phase 4 — Media and search

Deliverables:
- Image preview
- Video/voice/file metadata preview
- Attachment existence validator
- Conversation/message/date/type search
- HTML/JSON export

Exit criteria:
- Text + image history can be browsed and exported

## Phase 5 — Workspace editor

Deliverables:
- Text editing in workspace
- Non-transaction message date editing
- Media replacement in workspace
- Undo/redo
- Batch edit support
- Diff viewer
- Audit log

Restrictions:
- Payment, transfer, red-packet, bill and payment-status records remain read-only
- Any demo rendering of transaction-like content is labeled as local/non-original output

## Phase 6 — Integrity engine

Deliverables:
- DB open/integrity checks
- Message ordering checks
- Conversation summary/time checks
- Attachment/reference checks
- WAL/SHM checks
- UID/GID/mode/SELinux target checks
- Blocking rules before any restore/write-back experiment

Exit criteria:
- A failed critical check blocks downstream write operations

## Phase 7 — Controlled Android restore experiment

Prerequisite:
- Phases 1–6 pass on disposable/validated snapshots

Deliverables:
- Restore plan generator
- Automatic rollback point
- File ownership/permission/context preservation
- Start-up verification report
- Manual confirmation gate

Exit criteria:
- One validated non-financial test conversation survives restart and consistency checks
- Rollback restores prior validated state

## Phase 8 — Android to iPhone migration verification

Deliverables:
- Pre-migration readiness report
- Android-side validation checklist
- Post-migration comparison report

Final principle: iPhone transfer is performed by WeChat's own supported chat migration mechanism; WXDataStudio does not directly modify iOS application containers.
