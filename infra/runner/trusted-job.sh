#!/usr/bin/env bash
set -euo pipefail
if [[ ${GITHUB_REPOSITORY:-} != ElliottHitch/Rip || ${GITHUB_REF:-} != refs/heads/main ]]; then
  echo 'This runner accepts only ElliottHitch/Rip on main.' >&2
  exit 1
fi
case ${GITHUB_EVENT_NAME:-} in
  push|workflow_dispatch) ;;
  *) echo 'This runner does not accept pull-request or other event jobs.' >&2; exit 1 ;;
esac
