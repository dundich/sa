#!/usr/bin/env bash
#
# Fetch the container images the integration test suites start (PostgreSQL, SeaweedFS, Ryuk), so a
# machine without registry access can still run them.
#
# The suites start one container per test class, but the image is pulled once and then reused from
# the local daemon — after `pull` (or `load`) they need no registry at all.
#
# Usage:
#   ./build-sh/do-docker-images.sh status            # what is present locally
#   ./build-sh/do-docker-images.sh pull              # pull everything (needs registry access)
#   ./build-sh/do-docker-images.sh save [dir]        # write *.tar into dir (default dist/docker-images)
#   ./build-sh/do-docker-images.sh load [dir]        # docker load *.tar from dir
#
# Offline transfer: `save` on a machine with registry access, copy the directory over, `load` here.
#
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/tasks.sh"

case "${1:-status}" in
  status) docker_images_status ;;
  pull)   docker_images_pull ;;
  save)   docker_images_save "${2:-}" ;;
  load)   docker_images_load "${2:-}" ;;
  *)
    echo "usage: $0 {status|pull|save [dir]|load [dir]}" >&2
    exit 2
    ;;
esac
