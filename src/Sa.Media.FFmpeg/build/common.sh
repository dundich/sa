#!/usr/bin/env bash

# apt-get install -y build-essential autoconf automake libtool pkg-config libopus-dev libasound2-dev libvorbis-dev libogg-dev libmp3lame-dev libflac-dev libsoxr-dev wget


FFMPEG_VERSION=7.1.1
FFMPEG_TARBALL=ffmpeg-$FFMPEG_VERSION.tar.gz
FFMPEG_TARBALL_URL=http://ffmpeg.org/releases/$FFMPEG_TARBALL

FFMPEG_CONFIGURE_FLAGS=(
 
    --disable-shared
    --enable-static
    --enable-pic

    --disable-x86asm
    --disable-doc
    --disable-debug


    --disable-avdevice
    --disable-swscale
    --disable-programs
    --enable-ffmpeg
    --enable-ffprobe

    --disable-network
    --disable-muxers
    --disable-demuxers
    --disable-zlib
    --disable-lzma
    --disable-bzlib
    --disable-iconv
    --disable-libxcb
    --disable-bsfs


    --disable-indevs
    --disable-outdevs
    --disable-hwaccels
    --disable-nvenc
    --disable-videotoolbox
    --disable-audiotoolbox


    --enable-gpl
    --enable-nonfree
    --enable-version3

    --disable-filters

    --enable-filter=aresample
    --enable-filter=channelsplit 
    --enable-filter=copy
    --enable-filter=aformat
    --enable-filter=anull
    --enable-filter=atrim
    --enable-filter=format
    --enable-filter=null
    --enable-filter=setpts
    --enable-filter=trim
    --enable-filter=pan 
    --enable-filter=showinfo
    --enable-filter=concat
    --enable-filter=amerge
    --enable-filter=join
    
    --enable-swresample

    --disable-protocols
    --enable-protocol=file
    --enable-protocol=pipe

    --disable-muxers
    --enable-muxer=mp3,mp4,ac3,flac,wav,ogg

    --disable-demuxers
    --enable-demuxer=aac
    --enable-demuxer=ac3
    --enable-demuxer=avi
    --enable-demuxer=flac
    --enable-demuxer=flv
    --enable-demuxer=matroska
    --enable-demuxer=mov
    --enable-demuxer=m4v
    --enable-demuxer=mp3
    --enable-demuxer=mpc
    --enable-demuxer=mpc8
    --enable-demuxer=ogg
    --enable-demuxer=pcm_alaw
    --enable-demuxer=pcm_mulaw
    --enable-demuxer=pcm_f64be
    --enable-demuxer=pcm_f64le
    --enable-demuxer=pcm_f32be
    --enable-demuxer=pcm_f32le
    --enable-demuxer=pcm_s32be
    --enable-demuxer=pcm_s32le
    --enable-demuxer=pcm_s24be
    --enable-demuxer=pcm_s24le
    --enable-demuxer=pcm_s16be
    --enable-demuxer=pcm_s16le
    --enable-demuxer=pcm_s8
    --enable-demuxer=pcm_u32be
    --enable-demuxer=pcm_u32le
    --enable-demuxer=pcm_u24be
    --enable-demuxer=pcm_u24le
    --enable-demuxer=pcm_u16be
    --enable-demuxer=pcm_u16le
    --enable-demuxer=pcm_u8

    --enable-demuxer=wav
    --enable-demuxer=wv
    --enable-demuxer=xwma

    --disable-decoders
    --enable-decoder=aac
    --enable-decoder=aac_latm
    --enable-decoder=ac3
    --enable-decoder=flac

    --enable-decoder=mp1
    --enable-decoder=mp1float
    --enable-decoder=mp2
    --enable-decoder=mp2float
    --enable-decoder=mp3
    --enable-decoder=mp3adu
    --enable-decoder=mp3adufloat
    --enable-decoder=mp3float
    --enable-decoder=mp3on4
    --enable-decoder=mp3on4float
    --enable-decoder=mpc7
    --enable-decoder=mpc8
    --enable-decoder=vorbis
    --enable-decoder=opus

    --enable-decoder=wavpack
    --enable-decoder=wmalossless
    --enable-decoder=wmapro
    --enable-decoder=wmav1
    --enable-decoder=wmav2
    --enable-decoder=wmavoice

	--enable-decoder=gsm
	--enable-decoder=gsm_ms

    --enable-decoder=pcm_alaw
    --enable-decoder=pcm_bluray
    --enable-decoder=pcm_dvd
    --enable-decoder=pcm_f32be
    --enable-decoder=pcm_f32le
    --enable-decoder=pcm_f64be
    --enable-decoder=pcm_f64le
    --enable-decoder=pcm_lxf
    --enable-decoder=pcm_mulaw
    --enable-decoder=pcm_s8
    --enable-decoder=pcm_s8_planar
    --enable-decoder=pcm_s16be
    --enable-decoder=pcm_s16be_planar
    --enable-decoder=pcm_s16le
    --enable-decoder=pcm_s16le_planar
    --enable-decoder=pcm_s24be
    --enable-decoder=pcm_s24daud
    --enable-decoder=pcm_s24le
    --enable-decoder=pcm_s24le_planar
    --enable-decoder=pcm_s32be
    --enable-decoder=pcm_s32le
    --enable-decoder=pcm_s32le_planar
    --enable-decoder=pcm_u8
    --enable-decoder=pcm_u16be
    --enable-decoder=pcm_u16le
    --enable-decoder=pcm_u24be
    --enable-decoder=pcm_u24le
    --enable-decoder=pcm_u32be
    --enable-decoder=pcm_u32le
    
    --disable-parsers
    --enable-parser=aac
    --enable-parser=ac3
    --enable-parser=aac_latm
    --enable-parser=flac
    --enable-parser=mpegaudio
)
