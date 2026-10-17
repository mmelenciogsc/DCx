# CCB hostile-code sandbox

Status: local rootless policy plus in-process authoritative-service orchestration. Not approved or exposed for production CCB publication.

## Trust boundary

The authoritative Linux contribution service owns sandbox validation. The Windows client may prepare an approved item but never executes community code and cannot manufacture authoritative evidence. WSL is used only to reproduce the local hostile gate.

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

Signed publication and its digest-bound operation record commit atomically in the registry. If the process stops before the separate governance row advances, an identical retry returns the original release sequence; a changed request with the same operation ID is rejected. There is not yet an automatic stalled-operation recovery worker.

This code is not reachable from the simulation HTTP service: it registers no upload, authentication, review or approval endpoint and has no production executor configuration.

## Remaining production work

Required work includes authenticated upload/reviewer APIs, a durable outbox and queued execution leases, general language-specific dependency-lock/build profiles, seccomp, measured cgroup-v2 resource enforcement, immutable builder/base-image digests, Ubuntu 24.04 service deployment, monitoring and independent penetration testing.

Official upstream policy guidance: <https://github.com/containers/bubblewrap/blob/main/README.md>.
