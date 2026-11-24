using ThreeChartDemo.Meshing;
using ThreeChartDemo.Models;

namespace ThreeChartDemo.Services;

/// <summary>Display buffers of one frame, ready to be sent to ThreeJS.</summary>
public sealed record FrameBuffers(float[] Positions, float[] Colors, uint[] Indices, int VertexCount, int TriangleCount);

/// <summary>
/// All time steps of an adaptive chart: every step has its own mesh (precomputed up front,
/// because refinement is too slow for the animation loop). Height scaling uses the range of
/// ALL frames so the surface does not "breathe".
/// </summary>
public sealed class AdaptiveSurface
{
    private readonly List<AdaptiveFrame> _frames;
    private readonly double _x0, _x1, _y0, _y1;

    private AdaptiveSurface(List<AdaptiveFrame> frames, ChartSettings s, double fMin, double fMax)
    {
        _frames = frames;
        _x0 = s.XMin; _x1 = s.XMax; _y0 = s.YMin; _y1 = s.YMax;
        FMin = fMin; FMax = fMax;
    }

    public double FMin { get; }
    public double FMax { get; }
    public int TimeDivisions => _frames.Count - 1;
    public double TimeAt(int k) => _frames[k].Time;
    public double[] Bounds => new[] { _x0, _x1, _y0, _y1, FMin, FMax };

    public int VertexCount(int k) => _frames[k].VertexCount;
    public int TriangleCount(int k) => _frames[k].TriangleCount;

    public long TotalTriangles => _frames.Sum(fr => (long)fr.TriangleCount);
    public double ApproxMegabytes => _frames.Sum(fr => fr.VertexCount * 12L + fr.TriangleCount * 12L) / 1e6;

    public static async Task<AdaptiveSurface> BuildAsync(
        FunctionEvaluator function,
        ChartSettings s,
        Action<double>? progress = null,
        CancellationToken ct = default)
    {
        var slicer = new TimeSlicer();
        int nt = s.DivisionsT;
        var frames = new List<AdaptiveFrame>(nt + 1);
        double fMin = double.PositiveInfinity, fMax = double.NegativeInfinity;

        for (int k = 0; k <= nt; k++)
        {
            double t = s.TMin + (s.TMax - s.TMin) * k / nt;
            int frameIndex = k;

            Func<double, double, double> f = (x, y) => function.Evaluate(x, y, t);
            var frame = await AdaptiveMesher.BuildFrameAsync(f, s, t, slicer,
                p => progress?.Invoke((frameIndex + p) / (nt + 1)), ct);

            frames.Add(frame);
            foreach (float v in frame.F)
            {
                if (!float.IsFinite(v)) continue;
                if (v < fMin) fMin = v;
                if (v > fMax) fMax = v;
            }

            await slicer.YieldIfNeededAsync(ct);
        }

        if (fMin > fMax)
            throw new InvalidOperationException("The function has no finite values in the given intervals.");

        return new AdaptiveSurface(frames, s, fMin, fMax);
    }

    /// <summary>Normalises frame k into the display box and builds colours + index buffer.</summary>
    public FrameBuffers GetBuffers(int k)
    {
        var fr = _frames[k];
        int n = fr.VertexCount;

        var positions = new float[n * 3];
        var colors = new float[n * 3];
        var ok = new bool[n];

        double range = FMax - FMin;
        double sx = SurfaceMeshBuilder.BoxSize / (_x1 - _x0);
        double sy = SurfaceMeshBuilder.BoxSize / (_y1 - _y0);

        for (int i = 0; i < n; i++)
        {
            float f = fr.F[i];
            bool finite = float.IsFinite(f);
            ok[i] = finite;

            double t = finite ? (range > 0 ? (f - FMin) / range : 0.5) : 0;

            // math (x, y, f) -> three.js (X, Y = f, Z = -y)
            positions[i * 3 + 0] = (float)(-SurfaceMeshBuilder.BoxSize / 2 + (fr.X[i] - _x0) * sx);
            positions[i * 3 + 1] = (float)(t * SurfaceMeshBuilder.BoxHeight);
            positions[i * 3 + 2] = (float)(SurfaceMeshBuilder.BoxSize / 2 - (fr.Y[i] - _y0) * sy);

            var (r, g, b) = SurfaceMeshBuilder.Colormap(t);
            colors[i * 3 + 0] = r; colors[i * 3 + 1] = g; colors[i * 3 + 2] = b;
        }

        var indices = new uint[fr.Triangles.Length];
        int m = 0;
        for (int t = 0; t < fr.Triangles.Length; t += 3)
        {
            int a = fr.Triangles[t], b = fr.Triangles[t + 1], c = fr.Triangles[t + 2];
            if (ok[a] && ok[b] && ok[c])
            {
                indices[m++] = (uint)a; indices[m++] = (uint)b; indices[m++] = (uint)c;
            }
        }

        return new FrameBuffers(positions, colors, indices.AsSpan(0, m).ToArray(), n, m / 3);
    }
}
