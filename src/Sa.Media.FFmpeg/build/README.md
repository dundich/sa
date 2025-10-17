# ffmpeg-7.1.1

## Ubuntu/Debian:

```bash
sudo apt update
sudo apt install -y \
    build-essential \
    curl \
    tar \
    nasm \
    pkg-config \
    libmp3lame-dev \
    libvorbis-dev \
    libopus-dev
```

## Run build build/artifacts/ffmpeg-7.1.1-audio-x86_64-linux-gnu/bin
```
chmod +x build-linux.sh
./build-linux.sh
```


# output 25-10-17

```
External libraries:
libmp3lame              libopus                 libvorbis

External libraries providing hardware acceleration:
v4l2_m2m

Libraries:
avcodec                 avfilter                avformat                avutil                  postproc                swresample

Programs:
ffmpeg                  ffprobe

Enabled decoders:
aac                     mp2float                pcm_alaw                pcm_s16be_planar        pcm_s8                  wavpack
aac_latm                mp3                     pcm_bluray              pcm_s16le               pcm_s8_planar           wmalossless
ac3                     mp3adu                  pcm_dvd                 pcm_s16le_planar        pcm_u16be               wmapro
flac                    mp3adufloat             pcm_f32be               pcm_s24be               pcm_u16le               wmav1
gsm                     mp3float                pcm_f32le               pcm_s24daud             pcm_u24be               wmav2
gsm_ms                  mp3on4                  pcm_f64be               pcm_s24le               pcm_u24le               wmavoice
libvorbis               mp3on4float             pcm_f64le               pcm_s24le_planar        pcm_u32be
mp1                     mpc7                    pcm_lxf                 pcm_s32be               pcm_u32le
mp1float                mpc8                    pcm_mulaw               pcm_s32le               pcm_u8
mp2                     opus                    pcm_s16be               pcm_s32le_planar        vorbis

Enabled encoders:
a64multi                avui                    huffyuv                 pcm_f64be               phm                     text
a64multi5               bitpacked               jpeg2000                pcm_f64le               ppm                     tiff
aac                     bmp                     jpegls                  pcm_mulaw               prores                  truehd
ac3                     cfhd                    libmp3lame              pcm_s16be               prores_aw               tta
ac3_fixed               cinepak                 libopus                 pcm_s16be_planar        prores_ks               ttml
adpcm_adx               cljr                    libvorbis               pcm_s16le               qoi                     utvideo
adpcm_argo              comfortnoise            ljpeg                   pcm_s16le_planar        qtrle                   v210
adpcm_g722              dca                     magicyuv                pcm_s24be               r10k                    v308
adpcm_g726              dfpwm                   mjpeg                   pcm_s24daud             r210                    v408
adpcm_g726le            dnxhd                   mlp                     pcm_s24le               ra_144                  v410
adpcm_ima_alp           dpx                     movtext                 pcm_s24le_planar        rawvideo                vbn
adpcm_ima_amv           dvbsub                  mp2                     pcm_s32be               roq                     vc2
adpcm_ima_apm           dvdsub                  mp2fixed                pcm_s32le               roq_dpcm                vnull
adpcm_ima_qt            dvvideo                 mpeg1video              pcm_s32le_planar        rpza                    vorbis
adpcm_ima_ssi           dxv                     mpeg2video              pcm_s64be               rv10                    vp8_v4l2m2m
adpcm_ima_wav           eac3                    mpeg4                   pcm_s64le               rv20                    wavpack
adpcm_ima_ws            ffv1                    mpeg4_v4l2m2m           pcm_s8                  s302m                   wbmp
adpcm_ms                ffvhuff                 msmpeg4v2               pcm_s8_planar           sbc                     webvtt
adpcm_swf               fits                    msmpeg4v3               pcm_u16be               sgi                     wmav1
adpcm_yamaha            flac                    msrle                   pcm_u16le               smc                     wmav2
alac                    flv                     msvideo1                pcm_u24be               snow                    wmv1
alias_pix               g723_1                  nellymoser              pcm_u24le               sonic                   wmv2
amv                     gif                     opus                    pcm_u32be               sonic_ls                wrapped_avframe
anull                   h261                    pam                     pcm_u32le               speedhq                 xbm
aptx                    h263                    pbm                     pcm_u8                  srt                     xface
aptx_hd                 h263_v4l2m2m            pcm_alaw                pcm_vidc                ssa                     xsub
ass                     h263p                   pcm_bluray              pcx                     subrip                  xwd
asv1                    h264_v4l2m2m            pcm_dvd                 pfm                     sunrast                 y41p
asv2                    hdr                     pcm_f32be               pgm                     svq1                    yuv4
avrp                    hevc_v4l2m2m            pcm_f32le               pgmyuv                  targa
```