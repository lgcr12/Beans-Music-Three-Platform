using System.Numerics;

namespace Beans.Windows.Rebuild.Controls;

// A planar fracture network, cut from one intact pane. Slightly offset impact
// lines make splinters at the impact and broad irregular facets at the perimeter.
public static class PortalFractureMesh
{
    public sealed record Fragment(Vector2[] Points, int Side, int Depth, int Delay);

    public static IReadOnlyList<Fragment> Create()
    {
        List<Vector2[]> faces = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1)]];
        (Vector2 Origin, float Angle)[] impacts =
        [
            (new(.486f, .493f), 12), (new(.503f, .505f), 49),
            (new(.483f, .508f), 83), (new(.512f, .479f), 128),
            (new(.477f, .527f), 158)
        ];
        foreach (var (origin, angle) in impacts)
        {
            var radians = angle * MathF.PI / 180;
            var normal = new Vector2(-MathF.Sin(radians), MathF.Cos(radians));
            faces = faces.SelectMany(face => Split(face, origin, normal)).ToList();
        }

        // Secondary fractures terminate at an existing edge. They divide long
        // sectors into irregular plates, rather than identical radial triangles.
        var result = new List<Fragment>();
        var index = 0;
        foreach (var face in faces)
        {
            var center = Centroid(face);
            var divided = Area(face) > .038f
                ? Split(face, Vector2.Lerp(center, new(.49f, .5f), .10f),
                    Vector2.Normalize(center - new Vector2(.49f, .5f)))
                : new[] { face };
            foreach (var points in divided)
            {
                center = Centroid(points);
                var side = center.X < .493f ? -1 : 1;
                var distance = Vector2.Distance(center, new(.49f, .5f));
                // A fracture front propagates from the impact to the perimeter.
                var delay = (int)(distance * 500) + index % 3 * 19;
                result.Add(new(points, side, index % 4, delay));
                index++;
            }
        }
        // Cut a second joint in the long central tips. The resulting fragments
        // detach at separate times, leaving no single giant spoke at the edge.
        var final = new List<Fragment>();
        foreach (var fragment in result)
        {
            var center = Centroid(fragment.Points);
            var distance = Vector2.Distance(center, new(.49f, .5f));
            if (distance is > .08f and < .34f && Area(fragment.Points) > .015f)
            {
                foreach (var points in Split(fragment.Points,
                    Vector2.Lerp(center, new(.49f, .5f), .16f),
                    Vector2.Normalize(center - new Vector2(.49f, .5f))))
                {
                    var localCenter = Centroid(points);
                    final.Add(fragment with { Points = points,
                        Delay = (int)(Vector2.Distance(localCenter, new(.49f, .5f)) * 500),
                        Depth = localCenter.X < center.X ? 1 : 3 });
                }
            }
            else final.Add(fragment);
        }
        return final;
    }

    private static Vector2 Centroid(Vector2[] face) => new(face.Average(p => p.X), face.Average(p => p.Y));

    private static float Area(Vector2[] face)
    {
        float area = 0;
        for (var i = 0; i < face.Length; i++)
        {
            var a = face[i]; var b = face[(i + 1) % face.Length];
            area += a.X * b.Y - a.Y * b.X;
        }
        return MathF.Abs(area) / 2;
    }

    private static IEnumerable<Vector2[]> Split(Vector2[] face, Vector2 origin, Vector2 normal)
    {
        var positive = new List<Vector2>();
        var negative = new List<Vector2>();
        for (var i = 0; i < face.Length; i++)
        {
            var a = face[i]; var b = face[(i + 1) % face.Length];
            var da = Vector2.Dot(a - origin, normal);
            var db = Vector2.Dot(b - origin, normal);
            if (da >= 0) positive.Add(a);
            if (da <= 0) negative.Add(a);
            if ((da > 0 && db < 0) || (da < 0 && db > 0))
            {
                var intersection = Vector2.Lerp(a, b, da / (da - db));
                positive.Add(intersection);
                negative.Add(intersection);
            }
        }
        if (positive.Count >= 3) yield return positive.ToArray();
        if (negative.Count >= 3) yield return negative.ToArray();
    }
}
