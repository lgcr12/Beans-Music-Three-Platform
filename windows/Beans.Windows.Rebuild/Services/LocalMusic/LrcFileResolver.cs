using System.Text;

namespace Beans.Windows.Rebuild.Services.LocalMusic;

public sealed class LocalLrcFileResolver : ILrcFileResolver
{
    public Task<(string? Path, LocalLyricSource Source)> ResolveAsync(string audioPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(audioPath);
        var stem = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
            return Task.FromResult<(string?, LocalLyricSource)>((null, LocalLyricSource.None));
        var exact = Path.Combine(directory, stem + ".lrc");
        if (File.Exists(exact)) return Task.FromResult<(string?, LocalLyricSource)>((exact, LocalLyricSource.LocalFile));
        var normalized = Normalize(stem);
        var match = Directory.EnumerateFiles(directory, "*.lrc", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Normalize(Path.GetFileNameWithoutExtension(path)) == normalized);
        return Task.FromResult<(string?, LocalLyricSource)>(match is null ? (null, LocalLyricSource.None) : (match, LocalLyricSource.LocalFile));
    }

    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode : Encoding.UTF8;
        return encoding.GetString(bytes).TrimStart('\uFEFF');
    }

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
