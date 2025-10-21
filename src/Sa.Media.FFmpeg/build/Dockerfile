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

# Второй этап для уменьшения размера образа (опционально)
FROM ubuntu:22.04

# Копируем только результаты сборки из builder stage
COPY --from=builder /ffmpef_build /ffmpef_build

# Устанавливаем только runtime зависимости
RUN apt-get update && apt-get install -y \
    libopus0 \
    libvorbis0a \
    libvorbisenc2 \
    libmp3lame0 \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

WORKDIR /ffmpef_build

# Добавляем путь к ffmpeg в PATH
ENV PATH="/ffmpef_build/artifacts/ffmpeg-7.1.2-audio-x86_64-linux-gnu/bin:${PATH}"

# Проверяем что ffmpeg доступен
RUN ls -la /ffmpef_build/artifacts/ffmpeg-7.1.2-audio-x86_64-linux-gnu/bin/ && \
    /ffmpef_build/artifacts/ffmpeg-7.1.2-audio-x86_64-linux-gnu/bin/ffmpeg -version

# Установка точки входа для возможности использования bash
CMD ["/bin/bash"]
