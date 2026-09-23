namespace Beans.Windows.Rebuild.Services.Platforms;

public interface IPreviewModePolicy
{
    bool IsEnabled { get; }
}

public sealed class BuildPreviewModePolicy : IPreviewModePolicy
{
#if DEBUG
    public bool IsEnabled => true;
#else
    public bool IsEnabled => false;
#endif
}

public sealed class FixedPreviewModePolicy(bool isEnabled) : IPreviewModePolicy
{
    public bool IsEnabled { get; } = isEnabled;
}

public sealed class PreviewContentUnavailableException : InvalidOperationException
{
    public PreviewContentUnavailableException() : base("预览内容已禁用，在线服务尚未连接") { }
}
