# ADR 0056: Inspect pinned Whisper runtime archives before any extraction

- Status: Accepted for pre-extraction admission
- Date: 2026-10-18

## Context

An exact size and SHA-256 bind a staged runtime archive to the reviewed publisher artifact, but they do not prove that a future Windows extraction policy safely handles its ZIP structure. The P06 install gate must not discover traversal, case collisions, links, encrypted content, decompression bombs, corrupt entries or an ambiguous executable only after files are written.

## Decision

After exact candidate identity is verified under the existing no-write/no-delete lease, parse and fully stream every ZIP entry without extracting it. Admit at most 1,024 entries, 1.5 GiB per file and 3 GiB expanded in total, with a maximum 500:1 entry compression ratio. Require normalized bounded forward-slash paths, case-insensitive uniqueness for the Windows target, safe segments, no drive/ADS syntax, no Windows device names, and only regular files or directories. Reject links, reparse/special entries and encryption.

Compute and compare CRC-32 while streaming each entry. Require exactly one regular file whose final component is `whisper-cli.exe`. Return only aggregate file/directory/expanded-byte counts, that CLI path, and at most 32 license/notice paths. Do not retain entry contents or extract, execute, install, approve or select anything.

The accessible staged-artifact result will expose this bounded inventory. A matching archive remains subject to complete contained-license/provenance review and a separately approved extraction design before it can become an installed backend.

## Consequences

- Traversal, case collisions, reserved device paths, links, encryption, missing/duplicate CLI payloads, decompression bombs and CRC corruption fail before any extraction.
- Review performs an additional full decompression pass and can take materially longer than hashing; cancellation remains supported and memory is bounded to a reusable 1 MiB buffer.
- The structural review is not an extractor, installer, publisher-authenticity claim or redistribution approval.
- The actual pinned runtime still requires owner-authorized staging and validation against this policy before P06 can advance.
