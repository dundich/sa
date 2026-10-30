FROM ubuntu:22.04 AS builder

# Установка необходимых пакетов
RUN apt-get update && apt-get install -y \
    build-essential \
    curl \
    tar \
    yasm \
    nasm \
    pkg-config \
    libmp3lame-dev \
    libvorbis-dev \
    libopus-dev

# Создание рабочей папки
WORKDIR /ffmpef_build

# Сначала копируем архив FFmpeg (если существует)
COPY ffmpeg-*.tar.gz ./

# Копирование всех .sh файлов из текущей директории
COPY *.sh ./

# Сделать все .sh файлы исполняемыми
RUN chmod +x *.sh

# После копирования скриптов
RUN ls -la *.sh

# Запуск с логированием в файл и вывод в реальном времени
RUN ./build-linux.sh 2>&1 | tee build.log

# Установка точки входа для возможности использования bash
CMD ["/bin/bash"]

# Второй этап — только артефакты сборки. Рантайм-пакеты кодеков (libopus0/libvorbis0a/
# libvorbisenc2/libmp3lame0) больше не нужны: linux-пейлоад собирается статически
# (--pkg-config-flags=--static + -static в build-linux.sh).
FROM ubuntu:22.04

# Копируем только результаты сборки из builder stage
COPY --from=builder /ffmpef_build /ffmpef_build

WORKDIR /ffmpef_build

# Проверяем что ffmpeg доступен
RUN ls -la /ffmpef_build/artifacts/*/bin/ && \
    /ffmpef_build/artifacts/*/bin/ffmpeg -version

# Установка точки входа для возможности использования bash
CMD ["/bin/bash"]
