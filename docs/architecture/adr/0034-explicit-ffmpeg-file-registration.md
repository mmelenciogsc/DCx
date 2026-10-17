# ADR 0034: Require exact-file approval before expanding FFmpeg media processing

- Status: Accepted for registration; consumer migration remains pending
- Date: 2026-10-17

## Context

DCx needs FFmpeg for bounded frame/audio conversion, but trusting the first `ffmpeg.exe` on `PATH` does not identify executable bytes, license terms or publisher evidence. The development host contains a 148,583,424-byte Gyan build with GPLv3 configuration and no valid Windows publisher signature. Bundling, installing or silently selecting it would exceed current authority and could create distribution/licensing obligations.

## Decision

DCx exposes a separate keyboard/UIA-accessible registration for one existing local FFmpeg executable and its complete license file. Review holds both files read-only while hashing them, displays exact byte SHA-256 values, the full bounded UTF-8 license and real Windows Authenticode status, and warns that digest identity does not prove publisher authenticity or distribution clearance. Approval begins unchecked, is invalidated by path edits and requires a full two-file/signature recheck immediately before atomic persistence. Clear removes only DCx state.

Network/device paths, reparse files or ancestors, excessive sizes, malformed licenses, incomplete signer evidence and malformed/future persisted state fail closed. The registration UI cannot download, install, bundle, delete or upload FFmpeg.

This decision does not yet authorize a consumer. Existing frame extraction must be migrated away from `PATH` to the exact persisted binding before the registration closes that execution gap. MP3/MP4 transcription extraction remains unavailable until a bounded, leased, hash-reverified processor and composed transcription fixture pass.

## Consequences

- Users can inspect unsigned status and GPLv3 text before making a per-user exact-file choice.
- Native fixture UIA can prove consent/persistence without approving the real installed candidate.
- The installed candidate remains inventoried with `approved=false`; no legal or publisher claim is made.
- P06 remains blocked on consumer migration, legal/license review, real media extraction benchmarks and real Whisper/embedding backends.
