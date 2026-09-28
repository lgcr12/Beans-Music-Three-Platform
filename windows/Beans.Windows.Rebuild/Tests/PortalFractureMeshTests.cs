using System.Numerics;
using Beans.Windows.Rebuild.Controls;
using Xunit;

namespace Beans.Windows.Rebuild.Tests;

public sealed class PortalFractureMeshTests
{
    [Fact]
    public void FragmentsCoverTheScreenWithoutHolesOrOverlaps()
    {
        var mesh = PortalFractureMesh.Create();
        Assert.InRange(mesh.Count, 24, 48);
        Assert.Contains(mesh, f => f.Side == -1);
        Assert.Contains(mesh, f => f.Side == 1);
        double totalArea = 0;
        foreach (var face in mesh)
        {
            double twiceArea = 0;
            for (var i = 0; i < face.Points.Length; i++)
            {
                var a = face.Points[i]; var b = face.Points[(i + 1) % face.Points.Length];
                twiceArea += a.X * b.Y - b.X * a.Y;
                Assert.InRange(a.X, 0, 1);
                Assert.InRange(a.Y, 0, 1);
            }
            totalArea += Math.Abs(twiceArea) / 2;
        }
        Assert.InRange(totalArea, .999999, 1.000001);
        // Sample away from exact edges. A hole flashes the destination before the
        // mirror opens; an overlap makes a piece leave a duplicate image behind.
        for (var y = 0; y < 160; y++)
        for (var x = 0; x < 240; x++)
        {
            var sample = new Vector2((x + .371f) / 240, (y + .613f) / 160);
            Assert.Equal(1, mesh.Count(f => Contains(f.Points, sample)));
        }
    }

    private static bool Contains(Vector2[] points, Vector2 p)
    {
        var inside = false;
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
