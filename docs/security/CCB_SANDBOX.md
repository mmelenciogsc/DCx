# CCB hostile-code sandbox

Status: local rootless policy plus in-process authoritative-service orchestration. Not approved or exposed for production CCB publication.

## Trust boundary

The authoritative Linux contribution service owns sandbox validation. The Windows client may select one schema-v2 package, review its complete admitted text and submit the exact digest-bound snapshot into loopback quarantine, but it never executes community code or manufactures authoritative evidence. The source path is cleared locally and is not transported. WSL is used only to reproduce the local hostile gate.

Policy `dcx-ccb-bwrap-v1` exposes:

- one digest-bound package tree, read-only at `/package`;
- one bounded request, read-only at `/input/request`;
- one pinned static BusyBox runtime, read-only at `/runtime/busybox`;
- isolated `/proc`, minimal `/dev`, private tmpfs `/tmp` and `/work`.

It exposes no host root, home, repository, system tree, network route, socket or inherited environment. Bubblewrap creates all supported namespaces, drops all capabilities, starts a new session, uses UID/GID 65534 and remounts the empty root read-only.

## Bounds and evidence

Admission permits at most 256 regular entries, 64 MiB expanded workspace data, a safe relative `.sh` entrypoint and 1 MiB input. CPU, address space, process count, descriptors, file output, diagnostics, captured output and wall time are bounded. A timeout or overflow kills the namespace and produces only a stable failure code.

Canonical SHA-256 evidence binds the artifact identifier, extracted workspace, test suite, launcher, Bubblewrap backend, runtime and output. Child diagnostics are drained but never retained.

## Package and publication gate

The schema-v2 contribution profile admits exactly five declared files: an exact manifest, BusyBox-shell entrypoint, deterministic test suite, CycloneDX SBOM and license. It rejects unknown manifest/test fields, duplicate JSON properties, undeclared members, path collisions, unsafe/non-UTF-8 content and any dependency set other than the package plus the pinned runtime. Package identity, version and approved SPDX license must agree with contribution metadata.

Intake stores the exact SHA-256-addressed ZIP in quarantine. Approval rechecks the bytes, scans every textual component and test input, safely extracts to an ephemeral workspace, recomputes the canonical workspace digest, and requires passing bound evidence plus the declared output digest for every test. Evidence storage contains digests and stable failure codes, never test input, output or child diagnostics. Only then can the in-process server fixture add the exact artifact to a signed release; replica tests verify lazy digest-checked retrieval.

Signed publication and its digest-bound operation record commit atomically in the registry. Reviewer-authorized intent is durable first; startup validates and processes at most 100 pending intents, and the local state transition plus intent completion commit together. Recovery rehashes quarantine and authoritative artifacts, reuses the original operation/timestamp/reviewer and does not rerun CCB code. Changed request, candidate or artifact bytes fail closed.

The default simulation service remains read-only for contributions. When the API, worker and complete hash-pinned Linux executor configuration are all enabled, CCB approval first persists and claims a digest-bound review job before calling this executor. That combined HTTP-to-real-Bubblewrap path remains unrun without a Linux .NET service runtime or approved Compose environment; it is not production exposure.

## Remaining production work

Required work includes production identity/ingress, supported distributed queue/high-availability deployment beyond the same-SQLite simulation worker, lease heartbeat/alerting evidence, general language-specific dependency-lock/build profiles, seccomp, measured cgroup-v2 resource enforcement, immutable builder/base-image digests, Ubuntu 24.04 service deployment, monitoring and independent penetration testing.

Official upstream policy guidance: <https://github.com/containers/bubblewrap/blob/main/README.md>.
