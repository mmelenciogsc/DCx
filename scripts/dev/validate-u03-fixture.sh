#!/usr/bin/env bash
# Run manually from any directory. This fixture is never started by normal Compose workflows.
set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly COMPOSE_FILE="$REPOSITORY_ROOT/server/docker-compose.u03-fixture.yml"
readonly FIXTURE_FILE="$REPOSITORY_ROOT/server/u03-fixture/index.html"
readonly PROJECT_NAME="dcx-u03-fixture-validation"
readonly SERVICE_NAME="u03-fixture"
readonly IMAGE_NAME="${PROJECT_NAME}-${SERVICE_NAME}:latest"
readonly FIXTURE_URL="http://127.0.0.1:43189/"

compose=(docker compose --project-name "$PROJECT_NAME" --file "$COMPOSE_FILE" --profile manual)
rendered_config="$(mktemp)"
response_body="$(mktemp)"
started=0

die() {
  printf 'U03 fixture validation: %s\n' "$*" >&2
  exit 1
}

project_resources() {
  docker ps -aq --filter "label=com.docker.compose.project=$PROJECT_NAME"
  docker network ls -q --filter "label=com.docker.compose.project=$PROJECT_NAME"
  docker volume ls -q --filter "label=com.docker.compose.project=$PROJECT_NAME"
  if docker image inspect "$IMAGE_NAME" >/dev/null 2>&1; then
    printf '%s\n' "$IMAGE_NAME"
  fi
}

cleanup() {
  local exit_code=$?
  if (( started )); then
    "${compose[@]}" down --remove-orphans --rmi local >/dev/null 2>&1 || true
  fi
  rm -f "$rendered_config" "$response_body"
  exit "$exit_code"
}
trap cleanup EXIT HUP INT TERM

for command in docker curl sha256sum cmp grep tee seq sleep; do
  command -v "$command" >/dev/null 2>&1 || die "required command is unavailable: $command"
done
docker info >/dev/null 2>&1 || die "Docker daemon is unavailable"

printf '%s\n' '--- rendered Compose security configuration ---'
"${compose[@]}" config | tee "$rendered_config"

grep -Fq 'read_only: true' "$rendered_config" || die 'rendered configuration is not read-only'
grep -Fq 'user: 65532:65532' "$rendered_config" || die 'rendered configuration does not use the non-root UID/GID'
grep -Fq 'no-new-privileges:true' "$rendered_config" || die 'missing no-new-privileges security option'
grep -Eq '^[[:space:]]+- ALL$' "$rendered_config" || die 'missing cap_drop: ALL'
grep -Fq 'host_ip: 127.0.0.1' "$rendered_config" || die 'port is not loopback-only'
grep -Fq 'published: "43189"' "$rendered_config" || die 'unexpected published port'
grep -Fq 'target: 8080' "$rendered_config" || die 'unexpected container port'
if grep -Eq '^[[:space:]]+(environment|env_file|volumes|devices|secrets|network_mode|restart):' "$rendered_config"; then
  die 'rendered configuration contains a prohibited service capability'
fi

if [[ -n "$(project_resources)" ]]; then
  die "refusing to run because project resources already exist for $PROJECT_NAME"
fi

grep -Fq 'type="password"' "$FIXTURE_FILE" || die 'fixture lacks a password field'
grep -Fq 'for="fixture-text"' "$FIXTURE_FILE" || die 'fixture lacks a labelled normal field'
grep -Fq 'for="fixture-password"' "$FIXTURE_FILE" || die 'fixture lacks a labelled password field'
grep -Fq 'fixture-ready' "$FIXTURE_FILE" || die 'fixture lacks fixture-ready notification'
grep -Fq 'take-control-requested' "$FIXTURE_FILE" || die 'fixture lacks takeover notification'
grep -Fq 'hand-control-back' "$FIXTURE_FILE" || die 'fixture lacks handback notification'
grep -Fq 'sessionId: bridgeBinding.sessionId' "$FIXTURE_FILE" || die 'fixture lacks an opaque session-bound bridge envelope'
grep -Fq 'navigationGeneration: bridgeBinding.navigationGeneration' "$FIXTURE_FILE" || die 'fixture lacks a navigation-bound bridge envelope'
if grep -Eq '<form|\.value|localStorage|sessionStorage|fetch\(|XMLHttpRequest' "$FIXTURE_FILE"; then
  die 'fixture contains a forbidden retention, submission, or network primitive'
fi

printf '%s\n' '--- starting isolated manual fixture ---'
started=1
"${compose[@]}" up --build --detach --wait

printf '%s\n' '--- probing loopback fixture ---'
for _attempt in $(seq 1 20); do
  if curl --fail --silent --show-error --noproxy '*' "$FIXTURE_URL" --output "$response_body"; then
    break
  fi
  sleep 1
done
[[ -s "$response_body" ]] || die "fixture did not serve $FIXTURE_URL"
cmp --silent "$FIXTURE_FILE" "$response_body" || die 'served fixture does not exactly match baked HTML'

printf '%s\n' '--- fixture and response SHA-256 ---'
sha256sum "$FIXTURE_FILE" "$response_body"

container_id="$("${compose[@]}" ps --quiet "$SERVICE_NAME")"
[[ -n "$container_id" ]] || die 'fixture container ID was unavailable'
image_digest="$(docker inspect --format '{{.Image}}' "$container_id")"
[[ "$image_digest" == sha256:* ]] || die 'container image digest was unavailable'
printf 'image-digest=%s\n' "$image_digest"

printf '%s\n' '--- stopping and removing isolated fixture resources ---'
"${compose[@]}" down --remove-orphans --rmi local
started=0

[[ -z "$(project_resources)" ]] || die "cleanup left project resources for $PROJECT_NAME"
printf '%s\n' 'U03 fixture validation passed.'
