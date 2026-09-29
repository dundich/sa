#!/usr/bin/env bash
#
# Enable long path support on Linux (symlink-based workaround).
#
# On Linux there is no native 260-char limit like on Windows, but some
# legacy tools or WSL interop can still trip over deep paths. This script
# creates a short symlink at /sa pointing to the repository root so that
# deeply nested temp/build paths stay under 260 chars even in constrained
# environments.
#
# Usage: sudo ./do-fix-max-path-linux.sh
#

set -euo pipefail

LINK="/sa"
TARGET="$(pwd)"

echo "Creating symlink $LINK -> $TARGET"

if [[ -L "$LINK" ]]; then
  echo "$LINK already exists, updating..."
  rm "$LINK"
elif [[ -e "$LINK" ]]; then
  echo "Error: $LINK exists and is not a symlink"
  exit 1
fi

ln -s "$TARGET" "$LINK"

echo "Symlink created successfully."
echo "You can now use $LINK as a shortcut for deep build operations."
echo ""
echo "Example:"
echo "  cd $LINK/src && dotnet test"
echo "  # instead of:"
echo "  cd ../sa/src && dotnet test"
