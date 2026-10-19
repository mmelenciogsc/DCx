# ADR 0092: Expose the signed global replica through an offline-first accessible surface

- Status: Accepted for local/native validation; production registry provisioning remains blocked
- Date: 2026-10-19

ADR 0093 later adds the separately protected, explicitly reviewed per-user enrollment workflow anticipated here. An actual production root/origin/key/TLS ceremony and server deployment remain blocked.

## Context

P09 already provided a signed, checkpointed, separately stored global CKB/CCB replica with offline search. It was usable only through Core APIs and automated tests. The Windows product offered no direct way for a keyboard or screen-reader user to distinguish an unconfigured registry from an empty verified replica, inspect the active signed state, search cached resources, or explicitly request synchronization.

Silently discovering a trust root or endpoint from mutable user storage would create an unsafe bootstrap. Automatically synchronizing when a window opens would also turn a read-only local action into an unexpected network action.

## Decision

DCx exposes global knowledge through a small provider-neutral view-source contract and a modeless WPF window. The normal product source is unavailable until a production trust root and registry transport are explicitly provisioned. Unconfigured state never implies that the authoritative catalog is empty or current.

When a configured source is supplied, status and search use only the separate signature-verified global replica. Search never receives a private-store handle, is capped at 512 characters and 20 displayed results, and rejects non-whitespace control characters before storage work. The window displays the active release sequence and SHA-256, resource count, trust-policy revision/key count where present, and whether synchronization is available.

Synchronization is never triggered by window creation, refresh, or search. It requires the user to invoke a separate access-keyed action. The existing client then verifies the complete trust-policy/release chain and atomically activates only accepted generations. Cancellation leaves the last activated generation usable. This surface provides no contribution, publication, artifact execution, account, or payment action.

The window exposes a heading-first focus target, assertive status, unique access keys for refresh/synchronize/cancel/search, bounded query input, human-readable CKB/CCB list-item automation names and read-only signed-catalog details. A sealed startup fixture requires both a command-line flag and a data-root-contained environment gate. It generates only synthetic keys/resources, uses an in-memory transport and opens no socket.

This milestone intentionally included no production bootstrap. ADR 0093 subsequently binds the exact trust root and HTTPS registry origin through a separately protected, explicitly reviewed enrollment workflow before normal product composition.

## Validation

Nineteen focused GlobalKnowledge tests pass. New cases prove empty/ready status, signed synchronization, offline search, no-transport denial, unavailable-state non-inference, and rejection of oversized/control-bearing queries.

Native UI Automation proves heading focus; distinct access keys; cancel availability while a deliberately delayed sync is active; explicit activation of one signed release containing one CKB and one CCB; offline selection and readable metadata for the CCB; zero fixture sockets; zero private collections searched; the main-shell entry; and disabled search/sync in the normal unconfigured state. The first run exposed a CLR type name as the list-item automation name; the item model was corrected and the unchanged gate passed.

The P09 aggregate passes 19/19 plus native UIA. The full Windows suite passes 589/589 with zero build warnings or errors.

## Consequences and residual risk

- Users can inspect and search an already configured verified replica without network access or private-store crossover.
- Configuration absence is truthful and fail closed. ADR 0093 adds explicit enrollment, but no actual production registry authority has been provisioned.
- The synthetic fixture proves product/UI composition, not production trust bootstrap, TLS identity, server availability or content quality.
- Automated UIA is not human JAWS, NVDA or Narrator acceptance.
- Docker/Compose execution, production key custody, malicious same-user isolation and hardware-backed monotonicity remain P09 blockers.

## Alternatives rejected

- Treat no local generation as an empty authoritative catalog: confuses missing configuration/connectivity with verified state.
- Synchronize automatically on open: creates an undisclosed network action and makes offline inspection unreliable.
- Reuse private search: violates the required physical and logical private/global boundary.
- Read a trust root and endpoint from an arbitrary mutable file: permits silent trust substitution before a protected provisioning design exists.
- Expose raw client exceptions in the live region: risks leaking implementation details and produces unstable screen-reader output.
