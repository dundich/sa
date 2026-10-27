#!/usr/bin/env bash
#
# Route Docker Hub (registry, auth and its blob CDN) around a VPN tunnel, through the physical
# interface. Only those hosts bypass the tunnel; everything else keeps using it.
#
# Why this exists: when a tunnel owns the default route, a `docker pull` is pushed through it too.
# On a throttled tunnel a 150 MB image can take hours, while the same pull over the physical link
# finishes in seconds. The images are pulled once (./do-docker-images.sh pull), so this is a one-off.
#
# Requires root (it edits the routing tables).
#
# Usage:
#   sudo ./build-sh/do-docker-bypass.sh status   # which path a pull would take right now
#   sudo ./build-sh/do-docker-bypass.sh on       # add the bypass routes
#   sudo ./build-sh/do-docker-bypass.sh off      # remove them
#
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/tasks.sh"

case "${1:-status}" in
  status) docker_bypass_status ;;
  on)     docker_bypass_on ;;
  off)    docker_bypass_off ;;
  *)
    echo "usage: $0 {status|on|off}" >&2
    exit 2
    ;;
esac
