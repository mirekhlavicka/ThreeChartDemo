using ThreeChartDemo.Models;
using ThreeChartDemo.Services;

namespace ThreeChartDemo.Meshing;

/// <summary>Builds one adaptively refined mesh of f(x, y) for a fixed time.</summary>
public static class AdaptiveMesher
{
    /// <summary>Number of Laplacian smoothing passes (used when ChartSettings.LaplacianSmoothing is on).</summary>
    private const int SmoothingPasses = 3;

    public static async Task<AdaptiveFrame> BuildFrameAsync(
        Func<double, double, double> f,
        ChartSettings s,
        double time,
        TimeSlicer slicer,
        Action<double>? progress = null,
        CancellationToken ct = default)
    {
        // 1) initial uniform mesh: grid, each cell cut into two triangles
        var mesh = TriMesh.CreateGrid(s.XMin, s.XMax, s.YMin, s.YMax, s.DivisionsX, s.DivisionsY);

        // 2) f in vertices (and the range of f on the base grid, before any refinement)
        double fMin = double.PositiveInfinity, fMax = double.NegativeInfinity;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            double fv = f(mesh.X(v), mesh.Y(v));
            mesh.SetF(v, fv);

            if (double.IsFinite(fv))
            {
                if (fv < fMin) fMin = fv;
                if (fv > fMax) fMax = fv;
            }
            if ((v & 255) == 0) await slicer.YieldIfNeededAsync(ct);
        }

        // Epsilon is relative to the range of f. For a constant function (range 0) there is nothing to
        // resolve, so fall back to epsilon itself as an absolute value instead of a threshold of 0.
        double range = fMin <= fMax ? fMax - fMin : 0;
        double threshold = range > 0 ? s.Epsilon * range : s.Epsilon;

        progress?.Invoke(0.1);

        // 3) refinement passes
        //    Pass 0 tests every triangle. Later passes test only the triangles created by the previous
        //    pass: all other triangles have the same vertices as before, so the same error as before.
        int[] candidates = Enumerable.Range(0, mesh.TriangleCount).ToArray();
        var refineList = new List<int>();

        for (int pass = 0; pass < s.MaxRefinements; pass++)
        {
            // 3a) find triangles where f(centre) differs from the linear approximation
            refineList.Clear();
            for (int i = 0; i < candidates.Length; i++)
            {
                int t = candidates[i];
                if (mesh.IsFlagged(t)) continue;                 // already in the list

                if (NeedsRefinement(mesh, t, f, threshold))
                {
                    mesh.SetFlag(t, true);
                    refineList.Add(t);
                }
                if ((i & 255) == 0) await slicer.YieldIfNeededAsync(ct);
            }
            if (refineList.Count == 0) break;

            // 3b) refine. A triangle of the list may already have been divided as a side effect of
            //     propagation (then its flag is cleared) - such triangles are skipped.
            bool capped = false;
            for (int k = 0; k < refineList.Count; k++)
            {
                int t = refineList[k];
                if (!mesh.IsFlagged(t)) continue;

                if (mesh.TriangleCount >= s.MaxTriangles) { capped = true; break; }

                mesh.SetFlag(t, false);
                mesh.Refine(t, f);

                if ((k & 63) == 0) await slicer.YieldIfNeededAsync(ct);
            }

            candidates = mesh.TakeTouched();     // the triangles to test in the next pass

            progress?.Invoke(0.1 + 0.8 * (pass + 1.0) / s.MaxRefinements);
            if (capped) break;
        }

        // 4) optional Laplacian smoothing: several passes, then evaluate f once at the final positions
        if (s.LaplacianSmoothing)
        {
            var movedSet = new HashSet<int>();
            for (int pass = 0; pass < SmoothingPasses; pass++)
            {
                foreach (int v in mesh.LaplacianSmooth())
                    movedSet.Add(v);                      // a vertex may move in several passes
            }

            int n = 0;
            foreach (int v in movedSet)
            {
                mesh.SetF(v, f(mesh.X(v), mesh.Y(v)));
                if ((++n & 255) == 0) await slicer.YieldIfNeededAsync(ct);
            }
        }

        progress?.Invoke(1.0);
        return mesh.ToFrame(time);
    }

    /// <summary>
    /// The centre is the average of the vertices, so the linear approximation at the centre is simply
    /// the average of the three vertex values. The triangle needs refinement when the difference
    /// exceeds <paramref name="threshold"/> (epsilon * range of f on the base grid).
    /// Undefined (NaN / infinite) values: refine where defined and undefined points meet,
    /// to resolve the boundary of the domain of f.
    /// </summary>
    private static bool NeedsRefinement(TriMesh mesh, int t, Func<double, double, double> f, double threshold)
    {
        int a = mesh.Vertex(t, 0), b = mesh.Vertex(t, 1), c = mesh.Vertex(t, 2);
        double fa = mesh.F(a), fb = mesh.F(b), fc = mesh.F(c);

        double cx = (mesh.X(a) + mesh.X(b) + mesh.X(c)) / 3.0;
        double cy = (mesh.Y(a) + mesh.Y(b) + mesh.Y(c)) / 3.0;
        double fCenter = f(cx, cy);

        int finite = (double.IsFinite(fa) ? 1 : 0) + (double.IsFinite(fb) ? 1 : 0)
                   + (double.IsFinite(fc) ? 1 : 0) + (double.IsFinite(fCenter) ? 1 : 0);

        if (finite == 0) return false;          // completely outside the domain of f
        if (finite < 4) return true;            // border of the domain of f

        double linear = (fa + fb + fc) / 3.0;
        return Math.Abs(fCenter - linear) > threshold;
    }
}
