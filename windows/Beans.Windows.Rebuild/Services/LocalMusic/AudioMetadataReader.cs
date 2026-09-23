using System.Buffers.Binary;

namespace Beans.Windows.Rebuild.Services.LocalMusic;

public sealed class BasicAudioMetadataReader : IAudioMetadataReader
{
    public async Task<LocalAudioMetadata> ReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var format = info.Extension.TrimStart('.').ToUpperInvariant();
        var title = Path.GetFileNameWithoutExtension(path);
        if (format == "WAV")
        {
            try
            {
                var duration = await ReadWaveDurationAsync(path, cancellationToken);
                return new(title, "未知歌手", "未知专辑", "", null, null, null, duration, null, null, format, null, LocalMetadataState.Fallback);
            }
            catch (InvalidDataException) { }
        }
        return new(title, "未知歌手", "未知专辑", "", null, null, null, TimeSpan.Zero, null, null, format, null, LocalMetadataState.Fallback);
    }

    private static async Task<TimeSpan> ReadWaveDurationAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[12];
        if (await stream.ReadAsync(header, cancellationToken) != 12 ||
            System.Text.Encoding.ASCII.GetString(header, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
            throw new InvalidDataException("Invalid WAV header");
        int channels = 0, sampleRate = 0, bits = 0, byteRate = 0;
        long dataBytes = 0;
        var chunkHeader = new byte[8];
        while (await stream.ReadAsync(chunkHeader, cancellationToken) == 8)
        {
            var name = System.Text.Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            if (size < 0) throw new InvalidDataException("Invalid WAV chunk");
            if (name == "fmt ")
            {
                var fmt = new byte[Math.Min(size, 32)];
                await stream.ReadExactlyAsync(fmt, cancellationToken);
                if (fmt.Length >= 16)
                {
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                    byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(8, 4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                }
                if (size > fmt.Length) stream.Seek(size - fmt.Length, SeekOrigin.Current);
            }
            else if (name == "data") { dataBytes = size; stream.Seek(size, SeekOrigin.Current); }
            else stream.Seek(size, SeekOrigin.Current);
            if ((size & 1) == 1) stream.Seek(1, SeekOrigin.Current);
        }
        if (byteRate <= 0 || dataBytes <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)dataBytes / byteRate);
    }
}
