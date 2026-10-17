# ADR 0013: Rootless authoritative-server CCB sandbox

- Status: Provisional; local hostile gate only
- Date: 2026-10-17

## Context

Community code must never execute in-process or with ordinary child-process authority. The product architecture assigns contribution validation to the authoritative Ubuntu service, not the Windows client. Windows Sandbox is unavailable on this Windows Home development host, native Docker is absent, and WSL Docker daemon access would require privilege. Rootless Bubblewrap 0.6.1 and unprivileged user namespaces are already available for a non-privileged local policy test.

Bubblewrap's official documentation states that it constructs namespaces but is not by itself a complete sandbox; the caller's arguments define the security model. It documents mount, user, PID, network, IPC and UTS namespaces, `PR_SET_NO_NEW_PRIVS`, read-only mounts and the need for `--new-session`. Source: <https://github.com/containers/bubblewrap/blob/main/README.md>.

## Decision

Use a versioned, hash-pinned Bubblewrap policy as the first authoritative-server execution boundary. It receives only an admitted read-only package workspace and read-only test request, a pinned static runtime, empty read-only root, minimal process/dev mounts and private tmpfs. It creates all supported namespaces, shares no network, drops all capabilities, clears the environment, uses UID/GID 65534 and enforces bounded resources and output.

Canonical evidence binds artifact, extracted workspace, test suite, launcher, backend, runtime and output digests. The Windows app never runs contributed code; WSL is only a local validation host. CCB approval remains disabled unless the authoritative service itself produces and persists passing evidence.

The first contribution profile is an exact five-file BusyBox-shell package. The server admits and quarantines a digest-verified ZIP, validates its closed two-component CycloneDX/runtime/license declaration, safely extracts it, rescans every textual component and test input, executes every declared test, persists body-free evidence and allows signed artifact propagation only after all expected output digests match. Existing private schema-v1 packages remain compatible but are not authoritative contribution packages.

## Consequences

- The prototype demonstrates concrete host-filesystem, system-filesystem, network, environment, privilege, traversal, symlink, timeout and output-flood denials without installation or elevation.
- A narrow BusyBox-shell runtime keeps the initial attack surface and fixture behavior deterministic; additional language adapters require separately pinned reproducible builder/runtime images.
- The in-process service fixture validates the orchestration, but the simulation host intentionally exposes no contribution/authentication/reviewer API and no production executor configuration.
- The remaining production gate includes authenticated APIs, durable outbox/queued reviewer orchestration, general dependency-lock/build profiles, seccomp, measured cgroup-v2 enforcement, immutable base-image/toolchain digests, Ubuntu 24.04 deployment hardening, monitoring and independent penetration testing.
- A missing executor still fails with `ccb_sandbox_review_required`; production publication remains unavailable until the remaining gates pass.
