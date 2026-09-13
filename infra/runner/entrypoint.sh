#!/usr/bin/env bash
set -euo pipefail
if [[ ! -x ./config.sh ]]; then
  cp -a /opt/runner/. /runner/
fi
if [[ ${1:-} == register ]]; then
  if [[ -f .runner ]]; then
    echo 'Runner is already registered.' >&2
    exit 1
  fi
  read -r -s token
  ./config.sh --unattended --url https://github.com/ElliottHitch/Rip \
    --token "$token" --name rip-vps-docker --labels rip-release \
    --work _work
  unset token
  exit 0
fi
if [[ ! -f .runner ]]; then
  echo 'Register this runner first; see README.md.' >&2
  exit 1
fi
exec ./run.sh
