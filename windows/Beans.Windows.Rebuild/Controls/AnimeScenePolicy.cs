namespace Beans.Windows.Rebuild.Controls;

internal enum AnimeSceneKind { Summer, Spring }
internal enum AnimeMotionState { Static, Playing, Settling, Paused, Hidden, Disposed }

internal sealed record AnimeSceneDefinition(AnimeSceneKind Kind)
{
    public string Folder => $"Assets/Player/Scenes/{Kind.ToString().ToLowerInvariant()}";
    // The stronger profile deliberately has enough independently phased items to
    // read as atmosphere rather than as a handful of repeating decorations.
    public int ParticleCount => Kind == AnimeSceneKind.Spring ? 48 : 20;
    public int ParticleDepthLayers => Kind == AnimeSceneKind.Spring ? 4 : 3;
    public int WindStreakCount => Kind == AnimeSceneKind.Summer ? 9 : 0;
    public string[] RequiredAssets => ["background.png", "hair-minus.png", "hair-plus.png", "cloth-minus.png", "cloth-plus.png", "leaves-minus.png", "leaves-plus.png", "water.png", "cloud.png"];
    public bool IsAvailable(Func<string, bool> exists) => RequiredAssets.All(name => exists($"{Folder}/{name}"));
}

// A selection belongs to the page lifetime, never to a track or a window size.
internal sealed class AnimeSceneSelection
{
    public AnimeSceneDefinition Scene { get; }
    public AnimeSceneSelection(Func<int>? next = null) => Scene = new((next ?? (() => Random.Shared.Next(2)))() == 0 ? AnimeSceneKind.Summer : AnimeSceneKind.Spring);
}

internal sealed class AnimeMotionPolicy
{
    public AnimeMotionState State { get; private set; } = AnimeMotionState.Static;
    public int Revision { get; private set; }
    public int Update(bool playing, bool visible, bool reduced, bool available)
    {
        if (State == AnimeMotionState.Disposed) return Revision;
        var next = !visible ? AnimeMotionState.Hidden : reduced || !available ? AnimeMotionState.Static : playing ? AnimeMotionState.Playing
            : State is AnimeMotionState.Playing or AnimeMotionState.Settling ? AnimeMotionState.Settling : AnimeMotionState.Paused;
        if (next != State) { State = next; Revision++; }
        return Revision;
    }
    public bool CompleteSettling(int revision)
    {
        if (revision != Revision || State != AnimeMotionState.Settling) return false;
        State = AnimeMotionState.Paused;
        return true;
    }
    public void Dispose() { State = AnimeMotionState.Disposed; Revision++; }
}
