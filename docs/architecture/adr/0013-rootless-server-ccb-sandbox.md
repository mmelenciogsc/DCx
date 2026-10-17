# ADR 0013: Rootless authoritative-server CCB sandbox

- Status: Provisional; local hostile gate only
- Date: 2026-10-17

## Context

Community code must never execute in-process or with ordinary child-process authority. The product architecture assigns contribution validation to the authoritative Ubuntu service, not the Windows client. Windows Sandbox is unavailable on this Windows Home development host, native Docker is absent, and WSL Docker daemon access would require privilege. Rootless Bubblewrap 0.6.1 and unprivileged user namespaces are already available for a non-privileged local policy test.

Bubblewrap's official documentation states that it constructs namespaces but is not by itself a complete sandbox; the caller's arguments define the security model. It documents mount, user, PID, network, IPC and UTS namespaces, `PR_SET_NO_NEW_PRIVS`, read-only mounts and the need for `--new-session`. Source: <https://github.com/containers/bubblewrap/blob/main/README.md>.

## Decision

Use a versioned, hash-pinned Bubblewrap policy as the first authoritative-server execution boundary. It receives only an admitted read-only package workspace and read-only test request, a pinned static runtime, empty read-only root, minimal process/dev mounts and private tmpfs. It creates all supported namespaces, shares no network, drops all capabilities, clears the environment, uses UID/GID 65534 and enforces bounded resources and output.

Canonical evidence binds artifact, extracted workspace, test suite, launcher, backend, runtime and output digests. The Windows app never runs contributed code; WSL is only a local validation host. CCB approval remains disabled unless the authoritative service itself produces and persists passing evidence.

## Consequences

- The prototype demonstrates concrete host-filesystem, system-filesystem, network, environment, privilege, traversal, symlink, timeout and output-flood denials without installation or elevation.
- A narrow BusyBox-shell runtime keeps the initial attack surface and fixture behavior deterministic; additional language adapters require separately pinned reproducible builder/runtime images.
- The remaining production gate includes safe package extraction, dependency/lock/SBOM/license validation, durable evidence and reviewer orchestration, seccomp, measured cgroup-v2 enforcement, immutable base-image/toolchain digests, Ubuntu 24.04 deployment hardening and independent penetration testing.
- Until those gates pass, `ccb_sandbox_review_required` remains the only publication result for CCB submissions.
