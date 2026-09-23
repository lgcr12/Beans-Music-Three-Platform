namespace Beans.Windows.Rebuild.Services.LocalMusic;

public interface ILocalPlaybackSourceFactory
{
    Task<LocalPlaybackSource> CreateAsync(LocalTrack track, CancellationToken cancellationToken = default);
}

public sealed class LocalPlaybackSourceFactory : ILocalPlaybackSourceFactory
{
    public Task<LocalPlaybackSource> CreateAsync(LocalTrack track, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (track.IsMissing || !File.Exists(track.NormalizedPath)) return Task.FromResult(LocalPlaybackSource.Failure("文件已移动或删除"));
        try { using var stream = File.Open(track.NormalizedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Task.FromResult(LocalPlaybackSource.Success(new Uri(track.NormalizedPath).AbsoluteUri)); }
        catch (UnauthorizedAccessException) { return Task.FromResult(LocalPlaybackSource.Failure("没有权限访问该本地文件")); }
        catch (IOException) { return Task.FromResult(LocalPlaybackSource.Failure("本地文件暂时不可用")); }
    }
}
