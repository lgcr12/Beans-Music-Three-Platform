using Beans.Core;
using Xunit;

namespace Beans.Core.Tests;

public sealed class SyncOutboxTests
{
    [Fact]
    public async Task PullRebasesPendingChangeAndAcknowledgeClearsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beans-sync-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SyncOutbox(path);
            var user = Guid.NewGuid();
            var device = Guid.NewGuid();
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.EnqueueAsync(user, "theme", "current", false, new byte[32], TestContext.Current.CancellationToken);

            var remote = new SyncEnvelope(Guid.NewGuid(), "theme", "current", Guid.NewGuid(), 0, 5, false, Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
            await store.IngestAsync(user, new SyncPage(5, false, [remote]), TestContext.Current.CancellationToken);

            var pending = await store.PendingAsync(user, device, ct: TestContext.Current.CancellationToken);
            Assert.Single(pending);
            Assert.Equal(5, pending[0].BaseRevision);

            var accepted = pending[0] with { Revision = 6, UpdatedAt = DateTimeOffset.UtcNow };
            await store.AcknowledgeAsync(user, [accepted], TestContext.Current.CancellationToken);
            Assert.Empty(await store.PendingAsync(user, device, ct: TestContext.Current.CancellationToken));
            Assert.Equal(6, (await store.ReadMirrorAsync(user, "theme", TestContext.Current.CancellationToken)).Single().Revision);
            Assert.Equal(6, (await store.AccountStateAsync(user, TestContext.Current.CancellationToken)).Cursor);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
