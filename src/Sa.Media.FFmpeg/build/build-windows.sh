#!/usr/bin/env bash

set -eu

cd $(dirname $0)
BASE_DIR=$(pwd)

echo "starting in $BASE_DIR"

source common.sh || { echo "common.sh не найден или поврежден"; exit 1; }

if [ ! -e $FFMPEG_TARBALL ]
then
    echo "Downloading FFmpeg $FFMPEG_TARBALL_URL..."
	curl -s -L -O $FFMPEG_TARBALL_URL
fi

: ${ARCH:=x86_64}  # Если ARCH не задан, используем x86_64

OUTPUT_DIR=artifacts/ffmpeg-$FFMPEG_VERSION-audio-$ARCH-w64-mingw32

BUILD_DIR=$(mktemp -d -p $(pwd) build.XXXXXXXX)
trap 'rm -rf $BUILD_DIR' EXIT

cd $BUILD_DIR
echo "Extracting FFmpeg... $BUILD_DIR"
tar --strip-components=1 -xf $BASE_DIR/$FFMPEG_TARBALL

FFMPEG_CONFIGURE_FLAGS+=(
    --prefix=$BASE_DIR/$OUTPUT_DIR
    --extra-cflags='-static -static-libgcc -static-libstdc++'
    --extra-ldflags='-static -static-libgcc -static-libstdc++'
    --target-os=mingw32
    --arch=$ARCH
    --cross-prefix=$ARCH-w64-mingw32-

    # configure prepends $cross_prefix to the pkg-config default, which would look for a
    # nonexistent x86_64-w64-mingw32-pkg-config and silently disable all pkg-config-based
    # lib detection (в т.ч. --enable-libmp3lame/libvorbis/libopus из common.sh).
    # Force the host pkg-config; .pc files come from PKG_CONFIG_PATH
    # (на этой машине — vcpkg, triplet x64-mingw-static).
    --pkg-config=pkg-config
    --pkg-config-flags=--static

    --disable-libdrm
)

echo "Configuring FFmpeg with flags:"
printf '%s\n' "${FFMPEG_CONFIGURE_FLAGS[@]}"

./configure "${FFMPEG_CONFIGURE_FLAGS[@]}" || {
    echo "Configuration failed!"
    cat ffbuild/config.log
    exit 1
}


echo "Building FFmpeg..."
make -j"$(nproc)"

echo "Installing FFmpeg..."
make install

chown $(stat -c '%u:%g' $BASE_DIR) -R $BASE_DIR/$OUTPUT_DIR
echo "Build completed successfully!"
