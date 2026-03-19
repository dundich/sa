#  Example with Sa.Media.FFmpeg

On Ubuntu/Debian:

```bash
sudo apt update
sudo apt install libmp3lame0 libopus0 libvorbis0a libvorbisenc2
```

On Alpine Linux:

```bash
sudo apk add lame-libs opus libvorbis
```


wsl build
```
dotnet nuget locals all --clear
dotnet restore -r linux-x64
dotnet build -c Debug -r linux-x64

# dotnet run
```