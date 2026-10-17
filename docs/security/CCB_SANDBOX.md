# CCB hostile-code sandbox

Status: local rootless prototype. Not approved for production or CCB publication.

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

## Remaining production work

The prototype is intentionally disconnected from publication. Required work includes authoritative artifact intake and extraction, dependency locks, CycloneDX/license validation, multiple test vectors, durable evidence and reviewer state, signed artifact propagation, seccomp, measured cgroup-v2 resource enforcement, immutable builder/base-image digests, Ubuntu 24.04 service deployment, monitoring and independent penetration testing.

Official upstream policy guidance: <https://github.com/containers/bubblewrap/blob/main/README.md>.
