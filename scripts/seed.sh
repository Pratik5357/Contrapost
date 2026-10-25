#!/usr/bin/env bash
# Creates a demo client in the local docker compose stack and prints its API key.
# Each run creates a new client; keys can't be recovered later, only their hashes are stored.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
docker compose -f "$root/deploy/docker-compose.yml" run --rm migrate create-client "${1:-demo}"
