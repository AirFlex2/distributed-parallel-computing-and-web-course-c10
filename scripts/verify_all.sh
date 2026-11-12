#!/usr/bin/env bash
set -euo pipefail
# Єдиний канонічний тестовий сценарій реалізовано у scripts/verify_all.ps1 (кросплатформенно через pwsh)
exec pwsh "$(dirname "$0")/verify_all.ps1" "$@"
