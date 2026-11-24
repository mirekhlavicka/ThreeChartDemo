using System.Diagnostics;
using ThreeChartDemo.Models;

namespace ThreeChartDemo.Services;

/// <summary>
/// Owns the mesh buffers for one chart. The grid topology is fixed, so every animation frame
/// only re-evaluates f(x, y, t) and rewrites the Y coordinate and colour of each vertex.
/// The index buffer is rebuilt only when the set of defined (finite) vertices changes.
/// </summary>
public sealed class SurfaceMeshBuilder
{
    public const double BoxSize = 10.0;   // X and Z extent of the display box
    public const double BoxHeight = 5.0;  // Y extent of the display box

    // Viridis-like colour map.
    private static readonly (float R, float G, float B)[] Stops =
    {
        (0.267f, 0.005f, 0.329f),
        (0.231f, 0.322f, 0.546f),
        (0.128f, 0.567f, 0.551f),
        (0.369f, 0.789f, 0.383f),
        (0.993f, 0.906f, 0.144f),
    };

    private readonly FunctionEvaluator _f;
    private readonly int _nx, _ny, _nt, _w, _h;
    private readonly double _x0, _x1, _y0, _y1, _t0, _t1;

    private readonly double[] _values;
    private readonly float[] _positions;
    private readonly float[] _colors;
    private readonly bool[] _valid;
    private readonly bool[] _prevValid;
    private readonly uint[] _indices;
    private int _indexCount;
    private bool _hasIndices;

    /// <summary>
    /// Value range mapped to the display height. It only ever grows, so the surface does not
    /// "breathe" during the animation (it is pre-seeded by <see cref="EstimateRange"/>).
    /// </summary>
    public double FMin { get; private set; } = double.PositiveInfinity;
    public double FMax { get; private set; } = double.NegativeInfinity;

    public SurfaceMeshBuilder(FunctionEvaluator function, ChartSettings s)
    {
        _f = function;
        _nx = s.DivisionsX; _ny = s.DivisionsY; _nt = s.DivisionsT;
        _w = _nx + 1; _h = _ny + 1;
        _x0 = s.XMin; _x1 = s.XMax;
        _y0 = s.YMin; _y1 = s.YMax;
        _t0 = s.TMin; _t1 = s.TMax;

        int count = _w * _h;
        _values = new double[count];
        _positions = new float[count * 3];
        _colors = new float[count * 3];
        _valid = new bool[count];
        _prevValid = new bool[count];
        _indices = new uint[_nx * _ny * 6];

        // math (x, y, f) -> three.js (X, Z = -y, Y = f). X and Z never change, fill them once.
        for (int j = 0; j < _h; j++)
        {
            float z = (float)(BoxSize / 2 - BoxSize * j / _ny);
            for (int i = 0; i < _w; i++)
            {
                int k = j * _w + i;
                _positions[k * 3 + 0] = (float)(-BoxSize / 2 + BoxSize * i / _nx);
                _positions[k * 3 + 2] = z;
            }
        }
    }

    public int TimeDivisions => _nt;
    public int VertexCount => _w * _h;
    public int TriangleCount => _indexCount / 3;
    public double TimeAt(int k) => _t0 + (_t1 - _t0) * k / _nt;

    public ReadOnlySpan<float> Positions => _positions;
    public ReadOnlySpan<float> Colors => _colors;
    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, _indexCount);

    /// <summary>x min/max, y min/max, f min/max - for the axis labels.</summary>
    public double[] Bounds => new[] { _x0, _x1, _y0, _y1, FMin, FMax };

    /// <summary>
    /// Quickly samples f on a coarse grid at several times to seed the display range
    /// (so the height scale is stable from the very first frame).
    /// </summary>
    public void EstimateRange()
    {
        int cx = Math.Min(_nx, 40), cy = Math.Min(_ny, 40);
        int samples = Math.Min(_nt, 24);

        for (int s = 0; s <= samples; s++)
        {
            double t = _t0 + (_t1 - _t0) * s / samples;
            for (int j = 0; j <= cy; j++)
            {
                double y = _y0 + (_y1 - _y0) * j / cy;
                for (int i = 0; i <= cx; i++)
                {
                    double v = _f.Evaluate(_x0 + (_x1 - _x0) * i / cx, y, t);
                    if (double.IsFinite(v)) Expand(v);
                }
            }
        }
    }

    /// <summary>
    /// Evaluates f at time <paramref name="t"/> on the whole grid and refreshes the buffers.
    /// Returns true when the index buffer changed (first call, or the holes moved).
    /// The browser has one thread, so very long evaluations yield now and then to keep the UI alive.
    /// </summary>
    public async Task<bool> UpdateAsync(double t, Action<double>? progress = null, CancellationToken ct = default)
    {
        double frameMin = double.PositiveInfinity, frameMax = double.NegativeInfinity;

        var sw = Stopwatch.StartNew();
        for (int j = 0; j < _h; j++)
        {
            double y = _y0 + (_y1 - _y0) * j / _ny;
            for (int i = 0; i < _w; i++)
            {
                double v = _f.Evaluate(_x0 + (_x1 - _x0) * i / _nx, y, t);
                _values[j * _w + i] = v;
                if (double.IsFinite(v))
                {
                    if (v < frameMin) frameMin = v;
                    if (v > frameMax) frameMax = v;
                }
            }

            if (sw.ElapsedMilliseconds > 40)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke((j + 1.0) / _h);
                await Task.Delay(1, ct);   // lets the UI repaint
                sw.Restart();
            }
        }

        if (frameMin <= frameMax) { Expand(frameMin); Expand(frameMax); }
        if (FMin > FMax)
            throw new InvalidOperationException("The function has no finite values in the given intervals.");

        double range = FMax - FMin;

        for (int k = 0; k < _values.Length; k++)
        {
            double v = _values[k];
            bool ok = double.IsFinite(v);
            _valid[k] = ok;

            double tt = ok ? (range > 0 ? (v - FMin) / range : 0.5) : 0;
            _positions[k * 3 + 1] = (float)(tt * BoxHeight);

            var (r, g, b) = Colormap(tt);
            _colors[k * 3 + 0] = r;
            _colors[k * 3 + 1] = g;
            _colors[k * 3 + 2] = b;
        }

        progress?.Invoke(1.0);

        bool changed = !_hasIndices || !_valid.AsSpan().SequenceEqual(_prevValid);
        if (changed)
        {
            RebuildIndices();
            _valid.CopyTo(_prevValid, 0);
            _hasIndices = true;
        }
        return changed;
    }

    /// <summary>2 triangles per cell; cells touching an undefined vertex are skipped (a hole).</summary>
    private void RebuildIndices()
    {
        int n = 0;
        for (int j = 0; j < _ny; j++)
        {
            for (int i = 0; i < _nx; i++)
            {
                int v00 = j * _w + i, v10 = v00 + 1, v01 = v00 + _w, v11 = v01 + 1;

                if (_valid[v00] && _valid[v10] && _valid[v01])
                {
                    _indices[n++] = (uint)v00; _indices[n++] = (uint)v10; _indices[n++] = (uint)v01;
                }
                if (_valid[v10] && _valid[v11] && _valid[v01])
                {
                    _indices[n++] = (uint)v10; _indices[n++] = (uint)v11; _indices[n++] = (uint)v01;
                }
            }
        }
        _indexCount = n;
    }

    private void Expand(double v)
    {
        if (v < FMin) FMin = v;
        if (v > FMax) FMax = v;
    }

    public static (float R, float G, float B) Colormap(double t)
    {
        t = Math.Clamp(t, 0, 1) * (Stops.Length - 1);
        int i = Math.Min((int)t, Stops.Length - 2);
        float f = (float)(t - i);
        var a = Stops[i];
        var b = Stops[i + 1];
        return (a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f);
    }
}
