#!/usr/bin/env bash

set -eu

cd $(dirname $0)
BASE_DIR=$(pwd)

echo "starting in $BASE_DIR"

source common.sh || { echo "common.sh не найден или поврежден"; exit 1; }


# # Установка зависимостей (только для Ubuntu/Debian)
# sudo apt update
# sudo apt install -y build-essential yasm nasm git pkg-config libmp3lame-dev libopus-dev

if [ ! -e $FFMPEG_TARBALL ]
then
    echo "Downloading FFmpeg $FFMPEG_TARBALL_URL"
	curl -s -L -O $FFMPEG_TARBALL_URL
fi

: ${ARCH:=x86_64}  # Если ARCH не задан, используем x86_64

OUTPUT_DIR=artifacts/ffmpeg-$FFMPEG_VERSION-audio-$ARCH-linux-gnu

case $ARCH in
    x86_64)
        ;;
    i686)
        FFMPEG_CONFIGURE_FLAGS+=(--cc="gcc -m32")
        ;;
    arm64)
        FFMPEG_CONFIGURE_FLAGS+=(
            --enable-cross-compile
            --cross-prefix=aarch64-linux-gnu-
            --target-os=linux
            --arch=aarch64
        )
        ;;
    arm*)
        FFMPEG_CONFIGURE_FLAGS+=(
            --enable-cross-compile
            --cross-prefix=arm-linux-gnueabihf-
            --target-os=linux
            --arch=arm
        )
        case $ARCH in
            armv7-a)
                FFMPEG_CONFIGURE_FLAGS+=(
                    --cpu=armv7-a
                )
                ;;
            armv8-a)
                FFMPEG_CONFIGURE_FLAGS+=(
                    --cpu=armv8-a
                )
                ;;
            armhf-rpi2)
                FFMPEG_CONFIGURE_FLAGS+=(
                    --cpu=cortex-a7
                    --extra-cflags='-fPIC -mcpu=cortex-a7 -mfloat-abi=hard -mfpu=neon-vfpv4 -mvectorize-with-neon-quad'
                )
                ;;
            armhf-rpi3)
                FFMPEG_CONFIGURE_FLAGS+=(
                    --cpu=cortex-a53
                    --extra-cflags='-fPIC -mcpu=cortex-a53 -mfloat-abi=hard -mfpu=neon-fp-armv8 -mvectorize-with-neon-quad'
                )
                ;;
        esac
        ;;
    *)
        echo "Unknown architecture: $ARCH"
        exit 1
        ;;
esac



FFMPEG_CONFIGURE_FLAGS+=(

    # --extra-cflags="-I/usr/local/include"
    # --extra-ldflags="-L/usr/lib/x86_64-linux-gnu"
    # --extra-libs="-logg -lm"

    --enable-libmp3lame
    --enable-libvorbis
    --enable-libopus

    --enable-encoder=libmp3lame
    --enable-encoder=libopus

    --enable-parser=vorbis

    --enable-encoder=vorbis
    --enable-decoder=vorbis
    --enable-encoder=libvorbis
    --enable-decoder=libvorbis

    --enable-muxer=ogg
    --enable-demuxer=ogg
)


# Убедиться, что pkg-config найдет нужные .pc файлы
export PKG_CONFIG_PATH="/usr/lib/pkgconfig"
echo "[+] PKG_CONFIG_PATH = $PKG_CONFIG_PATH"


BUILD_DIR=$(mktemp -d -p $(pwd) build.XXXXXXXX)
# trap 'rm -rf $BUILD_DIR' EXIT

cd $BUILD_DIR
echo "Extracting FFmpeg... $BUILD_DIR"
tar --strip-components=1 -xf $BASE_DIR/$FFMPEG_TARBALL

FFMPEG_CONFIGURE_FLAGS+=(--prefix=$BASE_DIR/$OUTPUT_DIR)

echo "Configuring FFmpeg with flags:"
printf '%s\n' "${FFMPEG_CONFIGURE_FLAGS[@]}"

./configure "${FFMPEG_CONFIGURE_FLAGS[@]}" || (cat ffbuild/config.log && exit 1)

echo "Building FFmpeg..."
make

echo "Installing FFmpeg..."
make install

chown $(stat -c '%u:%g' $BASE_DIR) -R $BASE_DIR/$OUTPUT_DIR
echo "Build completed successfully!"
