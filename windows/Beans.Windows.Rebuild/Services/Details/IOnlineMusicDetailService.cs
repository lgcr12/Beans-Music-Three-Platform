using Beans.Windows.Rebuild.Models;

namespace Beans.Windows.Rebuild.Services.Details;

public interface IOnlineMusicDetailService
{
    Task<OnlineMusicDetailResponse> GetDetailAsync(OnlineMusicDetailQuery query, CancellationToken cancellationToken);
    void Invalidate(PlatformId platform, OnlineMusicDetailKind kind, string nativeId);
}
