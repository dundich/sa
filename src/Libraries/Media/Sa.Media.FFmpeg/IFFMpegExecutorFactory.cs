namespace Sa.Media.FFmpeg;

public interface IFFMpegExecutorFactory
{
    IFFMpegExecutor CreateFFMpegExecutor(FFMpegOptions? options = null);
    IFFProbeExecutor CreateFFProbeExecutor(FFMpegOptions? options = null);
}
