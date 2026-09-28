using Beans.Windows.Rebuild.Controls;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class AnimeSceneMotionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void SceneSelectionCallsRandomOnlyOnce(int result, int expected)
    {
        var calls = 0;
        var selection = new AnimeSceneSelection(() => { calls++; return result; });
        for (var i = 0; i < 30; i++) Assert.Equal((AnimeSceneKind)expected, selection.Scene.Kind);
        Assert.Equal(1, calls);
    }
    [Fact]
    public void EitherMissingPoseOrMissingBackgroundMakesSceneUnavailable()
    {
        var scene = new AnimeSceneDefinition(AnimeSceneKind.Spring);
        Assert.True(scene.IsAvailable(_ => true));
        foreach (var asset in scene.RequiredAssets)
            Assert.False(scene.IsAvailable(path => !path.EndsWith("/" + asset, StringComparison.Ordinal)));
    }
    [Fact]
    public void EnhancedSceneProfilesHaveVisibleDepthAndDensity()
    {
        var summer = new AnimeSceneDefinition(AnimeSceneKind.Summer);
        var spring = new AnimeSceneDefinition(AnimeSceneKind.Spring);

        Assert.Equal(20, summer.ParticleCount);
        Assert.Equal(3, summer.ParticleDepthLayers);
        Assert.Equal(9, summer.WindStreakCount);
        Assert.Equal(48, spring.ParticleCount);
        Assert.Equal(4, spring.ParticleDepthLayers);
        Assert.Equal(0, spring.WindStreakCount);
    }
    [Fact]
    public void ResumeDuringSettlingInvalidatesOldCompletion()
    {
        var state = new AnimeMotionPolicy();
        state.Update(true, true, false, true);
        var revision = state.Update(false, true, false, true);
        Assert.Equal(AnimeMotionState.Settling, state.State);
        state.Update(true, true, false, true);
        Assert.False(state.CompleteSettling(revision));
        Assert.Equal(AnimeMotionState.Playing, state.State);
    }
    [Fact]
    public void PauseCompletesOnceAndDoesNotResumeUntilPlaying()
    {
        var state = new AnimeMotionPolicy();
        state.Update(true, true, false, true);
        var revision = state.Update(false, true, false, true);
        Assert.True(state.CompleteSettling(revision));
        Assert.False(state.CompleteSettling(revision));
        state.Update(false, true, false, true);
        Assert.Equal(AnimeMotionState.Paused, state.State);
    }
    [Fact]
    public void HiddenReducedMissingAndDisposedAlwaysStopMotion()
    {
        var state = new AnimeMotionPolicy();
        state.Update(true, false, false, true);
        Assert.Equal(AnimeMotionState.Hidden, state.State);
        state.Update(true, true, true, true);
        Assert.Equal(AnimeMotionState.Static, state.State);
        state.Update(true, true, false, false);
        Assert.Equal(AnimeMotionState.Static, state.State);
        state.Update(true, true, false, true);
        Assert.Equal(AnimeMotionState.Playing, state.State);
        state.Dispose();
        state.Update(true, true, false, true);
        Assert.Equal(AnimeMotionState.Disposed, state.State);
    }
    [Fact]
    public void PackagedScenesContainAllAssets()
    {
        foreach (var kind in Enum.GetValues<AnimeSceneKind>())
        {
            var scene = new AnimeSceneDefinition(kind);
            Assert.True(scene.IsAvailable(relative => File.Exists(Path.Combine(AppContext.BaseDirectory, relative))));
        }
    }
}
