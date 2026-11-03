#!/usr/bin/env bash
set -eu

cd $(dirname $0)
BASE_DIR=$(pwd)

echo "starting in $BASE_DIR"

source common.sh || { echo "common.sh не найден или поврежден"; exit 1; }


# # Установка зависимостей (только для Ubuntu/Debian)
# sudo apt update
# sudo apt install -y build-essential yasm nasm git pkg-config libmp3lame-dev libopus-dev

# Проверяем наличие архива FFmpeg
if [ ! -f "$FFMPEG_TARBALL" ]
then
    echo "Downloading FFmpeg $FFMPEG_TARBALL_URL"
    curl -s -L -O "$FFMPEG_TARBALL_URL"

    # Проверяем что файл скачался корректно
    if [ ! -f "$FFMPEG_TARBALL" ]; then
        echo "ERROR: Failed to download $FFMPEG_TARBALL"
        exit 1
    fi
    echo "Download completed: $FFMPEG_TARBALL"
else
    echo "FFmpeg archive already exists: $FFMPEG_TARBALL"
    echo "Skipping download..."
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

    # Статическая линковка — как в build-windows.sh. Без этих флагов linux-пейлоад
    # линковался с системными libmp3lame.so.0/libopus.so.0/libvorbis*.so, и бинарник
    # не запускался на машине без соответствующих -dev/рантайм-пакетов
    # (exit 127, "error while loading shared libraries"). --pkg-config-flags=--static
    # заставляет pkg-config отдавать .a-библиотеки кодеков, -static — тянуть статически
    # и glibc/libm. Требуются *-dev пакеты, в которых лежат .a: libmp3lame-dev,
    # libopus-dev, libvorbis-dev, libogg-dev.
    --pkg-config-flags=--static
    --extra-cflags='-static'
    --extra-ldflags='-static'

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
## export PKG_CONFIG_PATH="/usr/lib/pkgconfig"
export PKG_CONFIG_PATH="/usr/lib/x86_64-linux-gnu/pkgconfig:/usr/lib/pkgconfig"
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
make -j"$(nproc)"

echo "Installing FFmpeg..."
make install

# Гард от регрессии: если кодеки снова уедут в динамику, пейлоад будет требовать
# системных .so и упадёт на чистой машине с невнятным exit 127.
if readelf -d "$BASE_DIR/$OUTPUT_DIR/bin/ffmpeg" | grep -Eq 'libmp3lame|libopus|libvorbis'; then
    echo "ERROR: $OUTPUT_DIR/bin/ffmpeg still links codec libraries dynamically:" >&2
    readelf -d "$BASE_DIR/$OUTPUT_DIR/bin/ffmpeg" | grep -E 'libmp3lame|libopus|libvorbis' >&2
    exit 1
fi

chown $(stat -c '%u:%g' $BASE_DIR) -R $BASE_DIR/$OUTPUT_DIR
echo "Build completed successfully!"
