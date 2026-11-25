namespace ThreeChartDemo.Meshing;

/// <summary>
/// Mutable 2D triangular mesh with vertex values f and triangle adjacency, supporting
/// conforming refinement by longest-edge bisection with longest-edge propagation (LEPP).
///
/// Conventions
///  - triangles are counter-clockwise, corners 0,1,2;
///  - "edge i" is the edge OPPOSITE to corner i, i.e. between corners (i+1)%3 and (i+2)%3;
///  - Neighbor(t, i) is the triangle across edge i, or -1 on the boundary;
///  - when a triangle is bisected, its id is REUSED by the first child, the second child gets a new id.
///    A per-triangle version counter tells whether a triangle id has been split since you last looked.
/// </summary>
public sealed class TriMesh
{
    private double[] _x, _y, _f;
    private int _vertexCount;

    private int[] _v;       // 3 vertex ids per triangle
    private int[] _n;       // 3 neighbour ids per triangle (-1 = boundary)
    private int[] _version; // incremented every time the triangle id is split
    private bool[] _flag;   // "is in the refine list"
    private int _triangleCount;

    // ids of triangles created / changed by splitting since the last TakeTouched() call
    private readonly List<int> _touched = new();

    private TriMesh(int vertexCapacity, int triangleCapacity)
    {
        _x = new double[vertexCapacity];
        _y = new double[vertexCapacity];
        _f = new double[vertexCapacity];
        _v = new int[triangleCapacity * 3];
        _n = new int[triangleCapacity * 3];
        _version = new int[triangleCapacity];
        _flag = new bool[triangleCapacity];
    }

    public int VertexCount => _vertexCount;
    public int TriangleCount => _triangleCount;

    public double X(int v) => _x[v];
    public double Y(int v) => _y[v];
    public double F(int v) => _f[v];
    public void SetF(int v, double value) => _f[v] = value;

    public int Vertex(int tri, int corner) => _v[tri * 3 + corner];
    public int Neighbor(int tri, int edge) => _n[tri * 3 + edge];

    public bool IsFlagged(int tri) => _flag[tri];
    public void SetFlag(int tri, bool value) => _flag[tri] = value;

    // ------------------------------------------------------------------ construction

    /// <summary>
    /// Uniform grid (nx × ny cells, each cut into two triangles along the same diagonal).
    /// Vertex ids are j*(nx+1)+i. Values f are NaN until you call <see cref="SetF"/>.
    /// </summary>
    public static TriMesh CreateGrid(double x0, double x1, double y0, double y1, int nx, int ny)
    {
        var mesh = new TriMesh((nx + 1) * (ny + 1) * 2, nx * ny * 4);

        for (int j = 0; j <= ny; j++)
        {
            double y = y0 + (y1 - y0) * j / ny;
            for (int i = 0; i <= nx; i++)
                mesh.AddVertex(x0 + (x1 - x0) * i / nx, y, double.NaN);
        }

        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v00 = j * (nx + 1) + i, v10 = v00 + 1, v01 = v00 + nx + 1, v11 = v01 + 1;
                int cell = j * nx + i;
                int a = 2 * cell, b = a + 1;

                // A = (v00, v10, v01), B = (v10, v11, v01); common edge is the diagonal v10-v01.
                mesh.AddTriangle(v00, v10, v01);
                mesh.AddTriangle(v10, v11, v01);

                mesh.SetNeighbors(a, b,                              // edge 0 (v10-v01): diagonal -> B
                                  i > 0 ? 2 * (cell - 1) + 1 : -1,   // edge 1 (v01-v00): left cell's B
                                  j > 0 ? 2 * (cell - nx) + 1 : -1); // edge 2 (v00-v10): lower cell's B
                mesh.SetNeighbors(b,
                                  j < ny - 1 ? 2 * (cell + nx) : -1, // edge 0 (v11-v01): upper cell's A
                                  a,                                 // edge 1 (v01-v10): diagonal -> A
                                  i < nx - 1 ? 2 * (cell + 1) : -1); // edge 2 (v10-v11): right cell's A
            }
        }
        return mesh;
    }

    private int AddVertex(double x, double y, double f)
    {
        if (_vertexCount == _x.Length)
        {
            int size = _x.Length * 2;
            Array.Resize(ref _x, size);
            Array.Resize(ref _y, size);
            Array.Resize(ref _f, size);
        }
        _x[_vertexCount] = x; _y[_vertexCount] = y; _f[_vertexCount] = f;
        return _vertexCount++;
    }

    private int AddTriangle(int a, int b, int c)
    {
        if (_triangleCount == _version.Length)
        {
            int size = _version.Length * 2;
            Array.Resize(ref _v, size * 3);
            Array.Resize(ref _n, size * 3);
            Array.Resize(ref _version, size);
            Array.Resize(ref _flag, size);
        }
        int t = _triangleCount++;
        _v[t * 3] = a; _v[t * 3 + 1] = b; _v[t * 3 + 2] = c;
        _n[t * 3] = _n[t * 3 + 1] = _n[t * 3 + 2] = -1;
        return t;
    }

    private void SetNeighbors(int t, int n0, int n1, int n2)
    {
        _n[t * 3] = n0; _n[t * 3 + 1] = n1; _n[t * 3 + 2] = n2;
    }

    // ------------------------------------------------------------------ geometry helpers

    private double Length2(int a, int b)
    {
        double dx = _x[a] - _x[b], dy = _y[a] - _y[b];
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Strict total order on edges: by length, ties (within a relative tolerance) broken by vertex ids.
    /// Both triangles sharing an edge therefore always agree which of their edges is the longer one,
    /// which is what makes the propagation terminate.
    /// </summary>
    private bool EdgeLonger(int a1, int b1, int a2, int b2)
    {
        double l1 = Length2(a1, b1), l2 = Length2(a2, b2);
        double tol = 1e-9 * Math.Max(l1, l2);
        if (l1 > l2 + tol) return true;
        if (l2 > l1 + tol) return false;

        int lo1 = Math.Min(a1, b1), hi1 = Math.Max(a1, b1);
        int lo2 = Math.Min(a2, b2), hi2 = Math.Max(a2, b2);
        return lo1 != lo2 ? lo1 > lo2 : hi1 > hi2;
    }

    /// <summary>Index of the edge (= opposite corner) which is the longest edge of the triangle.</summary>
    public int LongestEdge(int tri)
    {
        int best = 0;
        for (int e = 1; e < 3; e++)
        {
            if (EdgeLonger(Vertex(tri, (e + 1) % 3), Vertex(tri, (e + 2) % 3),
                           Vertex(tri, (best + 1) % 3), Vertex(tri, (best + 2) % 3)))
                best = e;
        }
        return best;
    }

    /// <summary>Which edge of <paramref name="tri"/> leads to <paramref name="neighbor"/>.</summary>
    private int EdgeTowards(int tri, int neighbor)
    {
        for (int e = 0; e < 3; e++)
            if (_n[tri * 3 + e] == neighbor) return e;
        throw new InvalidOperationException("Mesh adjacency is inconsistent.");
    }

    public double Longest2(int tri)
    {
        int e = LongestEdge(tri);
        return Length2(Vertex(tri, (e + 1) % 3), Vertex(tri, (e + 2) % 3));
    }

    // ------------------------------------------------------------------ refinement

    /// <summary>
    /// Longest-edge propagation refinement of triangle <paramref name="tri"/>:
    /// walk to the neighbour across the longest edge while that edge is not also the neighbour's
    /// longest edge; then bisect the terminal edge (together with its neighbour, so the mesh stays
    /// conforming). Repeat until <paramref name="tri"/> itself has been bisected.
    /// Triangles that are split on the way lose their "in refine list" flag.
    /// </summary>
    public void Refine(int tri, Func<double, double, double> f)
    {
        int startVersion = _version[tri];

        for (int guard = 0; _version[tri] == startVersion && guard < 10_000; guard++)
        {
            int cur = tri;
            for (int step = 0; step < 10_000; step++)
            {
                int e = LongestEdge(cur);
                int nb = Neighbor(cur, e);

                if (nb < 0 || LongestEdge(nb) == EdgeTowards(nb, cur))
                {
                    BisectTerminal(cur, e, f);   // terminal edge: shared longest edge, or boundary edge
                    break;
                }
                cur = nb;
            }
        }
    }

    /// <summary>Bisects edge <paramref name="edge"/> of <paramref name="tri"/> and of its neighbour (if any).</summary>
    private void BisectTerminal(int tri, int edge, Func<double, double, double> f)
    {
        int nb = Neighbor(tri, edge);
        int p = Vertex(tri, (edge + 1) % 3), q = Vertex(tri, (edge + 2) % 3);

        double mx = 0.5 * (_x[p] + _x[q]), my = 0.5 * (_y[p] + _y[q]);
        int m = AddVertex(mx, my, f(mx, my));

        int nbEdge = nb >= 0 ? EdgeTowards(nb, tri) : -1;

        int t2 = SplitTriangle(tri, edge, m);       // tri -> (r,p,m), t2 = (r,m,q)
        if (nb >= 0)
        {
            int n2 = SplitTriangle(nb, nbEdge, m);  // nb  -> (s,q,m), n2 = (s,m,p)

            // Link the halves across the (now halved) shared edge. Edge 0 of every child is that edge.
            _n[tri * 3] = n2; _n[n2 * 3] = tri;     // (p,m) side
            _n[t2 * 3] = nb;  _n[nb * 3] = t2;      // (m,q) side
        }
    }

    /// <summary>
    /// Splits triangle t (apex r = corner i) by the segment r-m, m being the midpoint of the opposite edge (p,q).
    ///   first child  (reuses id t):  (r, p, m)
    ///   second child (new id)     :  (r, m, q)
    /// Edge 0 of both children (the half of the bisected edge) is left at -1 for the caller to link.
    /// </summary>
    private int SplitTriangle(int t, int i, int m)
    {
        int r = Vertex(t, i), p = Vertex(t, (i + 1) % 3), q = Vertex(t, (i + 2) % 3);
        int across_qr = _n[t * 3 + (i + 1) % 3];   // neighbour across edge (q, r)
        int across_rp = _n[t * 3 + (i + 2) % 3];   // neighbour across edge (r, p)

        int t2 = AddTriangle(r, m, q);

        // first child (in place)
        _v[t * 3] = r; _v[t * 3 + 1] = p; _v[t * 3 + 2] = m;
        SetNeighbors(t, -1, t2, across_rp);

        // second child
        SetNeighbors(t2, -1, across_qr, t);

        // the neighbour across (q, r) used to point to t, now it borders the second child
        if (across_qr >= 0)
            _n[across_qr * 3 + EdgeTowards(across_qr, t)] = t2;

        _version[t]++;
        _flag[t] = false;     // removed from the refine list: it has been divided
        _flag[t2] = false;
        _touched.Add(t);
        _touched.Add(t2);
        return t2;
    }

    /// <summary>
    /// Returns (and forgets) the ids of all triangles that were created or changed by refinement since
    /// the last call. Only these can need another test in the next refinement pass, because every other
    /// triangle still has exactly the same vertices and therefore the same error as before.
    /// </summary>
    public int[] TakeTouched()
    {
        var result = new HashSet<int>(_touched).ToArray();   // a triangle may be split several times
        _touched.Clear();
        return result;
    }

    // ------------------------------------------------------------------ smoothing

    /// <summary>
    /// One Jacobi step of Laplacian smoothing: every interior vertex moves to the average of the
    /// vertices connected to it. Boundary vertices stay where they are, so the domain keeps its shape.
    /// A move that would flip or squash a triangle is cancelled. Values of f are NOT updated here;
    /// the returned ids of the moved vertices tell the caller where f must be evaluated again.
    /// </summary>
    public int[] LaplacianSmooth()
    {
        int nv = _vertexCount;
        var boundary = new bool[nv];
        var sumX = new double[nv];
        var sumY = new double[nv];
        var count = new int[nv];

        for (int t = 0; t < _triangleCount; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                if (_n[t * 3 + e] < 0)
                {
                    boundary[_v[t * 3 + (e + 1) % 3]] = true;
                    boundary[_v[t * 3 + (e + 2) % 3]] = true;
                }
            }

            // Each interior vertex is visited once per incident triangle and every neighbour of it
            // appears in exactly two of these triangles, so all neighbours get the same weight.
            for (int c = 0; c < 3; c++)
            {
                int v = _v[t * 3 + c];
                int a = _v[t * 3 + (c + 1) % 3];
                int b = _v[t * 3 + (c + 2) % 3];
                sumX[v] += _x[a] + _x[b];
                sumY[v] += _y[a] + _y[b];
                count[v] += 2;
            }
        }

        var newX = new double[nv];
        var newY = new double[nv];
        for (int v = 0; v < nv; v++)
        {
            if (boundary[v] || count[v] == 0) { newX[v] = _x[v]; newY[v] = _y[v]; }
            else { newX[v] = sumX[v] / count[v]; newY[v] = sumY[v] / count[v]; }
        }

        // Safety: cancel moves of vertices of triangles that would be flipped or nearly collapsed.
        // Cancelling one vertex changes its neighbours' triangles, so repeat until everything is valid.
        bool changed;
        do
        {
            changed = false;
            for (int t = 0; t < _triangleCount; t++)
            {
                int a = _v[t * 3], b = _v[t * 3 + 1], c = _v[t * 3 + 2];
                double oldArea = (_x[b] - _x[a]) * (_y[c] - _y[a]) - (_x[c] - _x[a]) * (_y[b] - _y[a]);
                double newArea = (newX[b] - newX[a]) * (newY[c] - newY[a]) - (newX[c] - newX[a]) * (newY[b] - newY[a]);

                if (newArea > 0.2 * oldArea) continue;

                foreach (int v in new[] { a, b, c })
                {
                    if (newX[v] != _x[v] || newY[v] != _y[v])
                    {
                        newX[v] = _x[v]; newY[v] = _y[v];
                        changed = true;
                    }
                }
            }
        } while (changed);

        var moved = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            if (newX[v] == _x[v] && newY[v] == _y[v]) continue;
            _x[v] = newX[v]; _y[v] = newY[v];
            moved.Add(v);
        }
        return moved.ToArray();
    }

    // ------------------------------------------------------------------ export

    public AdaptiveFrame ToFrame(double time)
    {
        var x = new float[_vertexCount];
        var y = new float[_vertexCount];
        var f = new float[_vertexCount];
        for (int i = 0; i < _vertexCount; i++)
        {
            x[i] = (float)_x[i]; y[i] = (float)_y[i]; f[i] = (float)_f[i];
        }
        var tris = new int[_triangleCount * 3];
        Array.Copy(_v, tris, tris.Length);

        return new AdaptiveFrame { Time = time, X = x, Y = y, F = f, Triangles = tris };
    }
}

/// <summary>Compact, immutable result of one adaptive meshing (one time step).</summary>
public sealed class AdaptiveFrame
{
    public required double Time { get; init; }
    public required float[] X { get; init; }
    public required float[] Y { get; init; }
    public required float[] F { get; init; }
    public required int[] Triangles { get; init; }   // 3 vertex ids per triangle

    public int VertexCount => X.Length;
    public int TriangleCount => Triangles.Length / 3;
}
