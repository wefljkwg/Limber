using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// One smooth surface around all the liquid (for the game to draw): every drop, string and coat adds to a soft field on a grid
/// (the drops melt into each other where they're close), and a surface is put where the field crosses one level. The surface
/// is made with "surface nets": a corner per grid cell the surface passes through (the middle of where it crosses that cell's
/// edges), joined into four-sided faces. The cell size grows and shrinks by itself to keep the surface within what the carrier
/// can draw.
/// </summary>
internal sealed class LiquidSurface
{
    // Out: corners (world space), their normals (pointing out of the liquid), and triangles.
    public Vector3[] Positions = new Vector3[4096];
    public Vector3[] Normals = new Vector3[4096];
    public int VertexCount;
    public int[] Triangles = new int[12288];
    public int TriangleCount;
    public float Cell { get; private set; } = 0.004f;
    public bool CutShort { get; private set; }

    // How far each piece reaches (times its radius) and the level the surface is at: a lone drop's surface is at its radius.
    private float Reach = 2f;
    private float Level = 0.421875f;
    private float _smoothStrength = 0.5f, _coatOut = 0.5f;

    /// <summary>The surface's shape settings: how far drops reach to melt together (times their radius), how full the surface
    /// is (1: a lone drop's surface at its radius; lower swells it, higher shrinks it), how strongly each smoothing pass pulls,
    /// and how far out of the skin a coat sits (0: centred on the skin, 1: all of it outside).</summary>
    public void SetShape(float reach, float fullness, float smoothStrength, float coatOut)
    {
        Reach = Math.Clamp(reach, 1.2f, 4f);
        Level = MathF.Pow(1f - 1f / (Reach * Reach), 3f) * Math.Clamp(fullness, 0.2f, 1.8f);
        _smoothStrength = Math.Clamp(smoothStrength, 0.05f, 0.9f);
        _coatOut = Math.Clamp(coatOut, 0f, 1f);
    }
    private const int MaxCorners = 3_000_000;

    private float[] _field = [];
    private int[] _vertexOf = [];
    private int _nx, _ny, _nz;
    private Vector3 _min;
    private float _h;
    private float _coatThickness = 0.5f;
    private Vector3[] _sum = [], _smoothed = [];
    private int[] _links = [];
    private int[] _vertexFrame = [], _rowLo = [], _rowHi = [];
    private int _frame;
    private System.Collections.Generic.List<int>[] _slabPieces = [];

    /// <summary>
    /// Builds the surface. <paramref name="coatSpread"/>: how far liquid lying on skin spreads (times its radius);
    /// <paramref name="maxVertices"/>/<paramref name="maxTriangles"/>: what the carrier can hold; <paramref name="finest"/>: the
    /// smallest cell size allowed.
    /// </summary>
    public void Build(ClimaxService.Piece[] pieces, int count, float coatSpread, float coatThickness, int maxVertices, int maxTriangles, float finest, int smoothing)
    {
        // (when it doesn't fit, made again straight away a step or more coarser: never drawn with holes where it was cut short)
        for(int attempt = 0; attempt < 5; attempt++)
        {
            BuildOnce(pieces, count, coatSpread, coatThickness, maxVertices, maxTriangles, finest, smoothing);
            if(!CutShort)
                break;
        }
    }

    private void BuildOnce(ClimaxService.Piece[] pieces, int count, float coatSpread, float coatThickness, int maxVertices, int maxTriangles, float finest, int smoothing)
    {
        _coatThickness = Math.Clamp(coatThickness, 0.05f, 2f);
        VertexCount = 0;
        TriangleCount = 0;
        CutShort = false;
        if(count <= 0)
            return;
        // ---- the grid: around everything (each piece's reach), with the cell size kept to what fits
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for(int i = 0; i < count; i++)
        {
            Bounds(pieces[i], coatSpread, out var a, out var b);
            lo = Vector3.Min(lo, a);
            hi = Vector3.Max(hi, b);
        }
        // (the cell size in steps, and the grid fixed in the world: the surface doesn't shimmer as the liquid moves or grows)
        var size = hi - lo;
        int step = _step;
        while((long)(size.X / CellOf(step, finest) + 4) * (long)(size.Y / CellOf(step, finest) + 4) * (long)(size.Z / CellOf(step, finest) + 4) > MaxCorners)
            step++;
        float h = CellOf(step, finest);
        _h = h;
        _min = new Vector3(MathF.Floor(lo.X / h) - 1f, MathF.Floor(lo.Y / h) - 1f, MathF.Floor(lo.Z / h) - 1f) * h;
        _nx = (int)((hi.X - _min.X) / h) + 3;
        _ny = (int)((hi.Y - _min.Y) / h) + 3;
        _nz = (int)((hi.Z - _min.Z) / h) + 3;
        int corners = _nx * _ny * _nz;
        if(_field.Length < corners)
        {
            _field = new float[corners];
            _vertexOf = new int[corners];
            _vertexFrame = new int[corners];
        }
        else
            Array.Clear(_field, 0, corners);
        int rows = _ny * _nz;
        if(_rowLo.Length < rows)
        {
            _rowLo = new int[rows];
            _rowHi = new int[rows];
        }
        Array.Fill(_rowLo, int.MaxValue, 0, rows);
        Array.Fill(_rowHi, -1, 0, rows);
        _frame++;

        // ---- every piece into the field: the grid cut into slabs along z, each slab filled by its own thread (from the
        // pieces reaching into it)
        int slabs = Math.Clamp(Environment.ProcessorCount, 1, 16);
        slabs = Math.Min(slabs, Math.Max(_nz / 4, 1));
        if(_slabPieces.Length < slabs)
            _slabPieces = Enumerable.Range(0, slabs).Select(_ => new List<int>()).ToArray();
        for(int k = 0; k < slabs; k++)
            _slabPieces[k].Clear();
        float slabDepth = _nz / (float)slabs;
        for(int i = 0; i < count; i++)
        {
            Bounds(pieces[i], coatSpread, out var a, out var b);
            int k0 = Math.Clamp((int)(((a.Z - _min.Z) / h - 1f) / slabDepth), 0, slabs - 1);
            int k1 = Math.Clamp((int)(((b.Z - _min.Z) / h + 1f) / slabDepth), 0, slabs - 1);
            for(int k = k0; k <= k1; k++)
                _slabPieces[k].Add(i);
        }
        System.Threading.Tasks.Parallel.For(0, slabs, k =>
        {
            int zFrom = (int)(k * slabDepth), zTo = k == slabs - 1 ? _nz - 1 : (int)((k + 1) * slabDepth) - 1;
            foreach(int i in _slabPieces[k])
                Splat(pieces[i], coatSpread, zFrom, zTo);
        });

        // ---- a corner of the surface per cell it passes through
        int sx = 1, sy = _nx, sz = _nx * _ny;
        Span<float> v = stackalloc float[8];
        for(int z = 0; z < _nz - 1; z++)
            for(int y = 0; y < _ny - 1; y++)
            {
                // (only along the stretch of the row the liquid reached: everywhere else the field is empty)
                int r0 = y + z * _ny, r1 = r0 + 1, r2 = r0 + _ny, r3 = r2 + 1;
                int xFrom = Math.Max(Math.Min(Math.Min(_rowLo[r0], _rowLo[r1]), Math.Min(_rowLo[r2], _rowLo[r3])) - 1, 0);
                int xTo = Math.Min(Math.Max(Math.Max(_rowHi[r0], _rowHi[r1]), Math.Max(_rowHi[r2], _rowHi[r3])), _nx - 2);
                for(int x = xFrom; x <= xTo; x++)
                {
                    int c = x + y * sy + z * sz;
                    int inside = 0;
                    for(int k = 0; k < 8; k++)
                    {
                        v[k] = _field[c + (k & 1) * sx + ((k >> 1) & 1) * sy + ((k >> 2) & 1) * sz];
                        if(v[k] > Level)
                            inside |= 1 << k;
                    }
                    if(inside == 0 || inside == 255)
                        continue;
                    if(VertexCount >= maxVertices || VertexCount >= 65535)
                    {
                        CutShort = true;
                        continue;
                    }
                    // the middle of where the surface crosses the cell's twelve edges
                    var sum = Vector3.Zero;
                    int crossings = 0;
                    for(int e = 0; e < 12; e++)
                    {
                        int a = Edges[e * 2], b = Edges[e * 2 + 1];
                        bool ia = (inside >> a & 1) != 0, ib = (inside >> b & 1) != 0;
                        if(ia == ib)
                            continue;
                        float t = (Level - v[a]) / (v[b] - v[a]);
                        sum += Vector3.Lerp(CornerOffset(a), CornerOffset(b), t);
                        crossings++;
                    }
                    var local = sum / crossings;
                    // the normal: the way the field falls fastest
                    var grad = new Vector3(
                        (v[1] - v[0]) + (v[3] - v[2]) + (v[5] - v[4]) + (v[7] - v[6]),
                        (v[2] - v[0]) + (v[3] - v[1]) + (v[6] - v[4]) + (v[7] - v[5]),
                        (v[4] - v[0]) + (v[5] - v[1]) + (v[6] - v[2]) + (v[7] - v[3]));
                    var n = grad.LengthSquared() > 1e-20f ? -Vector3.Normalize(grad) : Vector3.UnitY;
                    if(VertexCount >= Positions.Length)
                    {
                        Array.Resize(ref Positions, Positions.Length * 2);
                        Array.Resize(ref Normals, Normals.Length * 2);
                    }
                    Positions[VertexCount] = _min + (new Vector3(x, y, z) + local) * h;
                    Normals[VertexCount] = n;
                    _vertexOf[c] = VertexCount++;
                    _vertexFrame[c] = _frame;
                }
            }

        // ---- the faces: one across every grid edge the surface crosses, joining the four cells around that edge
        for(int z = 1; z < _nz - 1; z++)
            for(int y = 1; y < _ny - 1; y++)
            {
                int r0 = y + z * _ny, r1 = r0 + 1, r2 = r0 + _ny;
                int xFrom = Math.Max(Math.Min(_rowLo[r0], Math.Min(_rowLo[r1], _rowLo[r2])) - 1, 1);
                int xTo = Math.Min(Math.Max(_rowHi[r0], Math.Max(_rowHi[r1], _rowHi[r2])), _nx - 2);
                for(int x = xFrom; x <= xTo; x++)
                {
                    int c = x + y * sy + z * sz;
                    bool here = _field[c] > Level;
                    // along x (to x+1): the cells around it are at y-1/y and z-1/z
                    if(x < _nx - 2 && here != _field[c + sx] > Level)
                        Quad(c, c - sy - sz, c - sz, c, c - sy, here, maxTriangles);
                    if(y < _ny - 2 && here != _field[c + sy] > Level)
                        Quad(c, c - sx - sz, c - sx, c, c - sz, here, maxTriangles);
                    if(z < _nz - 2 && here != _field[c + sz] > Level)
                        Quad(c, c - sx - sy, c - sy, c, c - sx, here, maxTriangles);
                }
            }

        // ---- smoothed (the corners pulled towards their neighbours, then pushed back a little so it doesn't shrink), and the
        // normals from the smoothed surface itself
        if(smoothing > 0 && VertexCount > 0)
            Smooth(smoothing);

        // ---- the cell size for next time: as fine as fits, changed in steps (a step finer only when that would still fit
        // comfortably, and not straight after a change, so it doesn't flick between two sizes)
        float use = MathF.Max(TriangleCount / (float)Math.Max(maxTriangles, 1), VertexCount / (float)Math.Max(maxVertices, 1));
        _step = step;
        if(_cooldown > 0)
            _cooldown--;
        if(CutShort || use > 0.95f)
        {
            _step += Math.Max(1, (int)MathF.Ceiling(MathF.Log(MathF.Sqrt(MathF.Max(use, 1f) / 0.8f)) / MathF.Log(StepRatio)));
            _cooldown = 8;
        }
        else if(use * StepRatio * StepRatio < 0.8f && _step > 0 && _cooldown == 0)
        {
            _step--;
            _cooldown = 8;
        }
        Cell = CellOf(_step, finest);
    }

    private const float StepRatio = 1.15f;
    private int _step = 8, _cooldown;

    private static float CellOf(int step, float finest) => finest * MathF.Pow(StepRatio, step);

    private void Quad(int edgeAt, int c0, int c1, int c2, int c3, bool insideFirst, int maxTriangles)
    {
        if(_vertexFrame[c0] != _frame || _vertexFrame[c1] != _frame || _vertexFrame[c2] != _frame || _vertexFrame[c3] != _frame)
            return;
        int a = _vertexOf[c0], b = _vertexOf[c1], c = _vertexOf[c2], d = _vertexOf[c3];
        if(TriangleCount + 2 > maxTriangles)
        {
            CutShort = true;
            return;
        }
        if(Triangles.Length < (TriangleCount + 2) * 3)
            Array.Resize(ref Triangles, Triangles.Length * 2);
        // (wound one consistent way here; the carrier's own winding is matched when written)
        if(!insideFirst)
            (b, d) = (d, b);
        int at = TriangleCount * 3;
        Triangles[at] = a;
        Triangles[at + 1] = b;
        Triangles[at + 2] = c;
        Triangles[at + 3] = a;
        Triangles[at + 4] = c;
        Triangles[at + 5] = d;
        TriangleCount += 2;
    }

    private void Smooth(int passes)
    {
        int n = VertexCount;
        if(_sum.Length < n)
        {
            _sum = new Vector3[Positions.Length];
            _smoothed = new Vector3[Positions.Length];
            _links = new int[Positions.Length];
        }
        for(int pass = 0; pass < passes * 2; pass++)
        {
            float k = pass % 2 == 0 ? _smoothStrength : -(_smoothStrength + 0.03f);
            Array.Clear(_sum, 0, n);
            Array.Clear(_links, 0, n);
            for(int t = 0; t < TriangleCount; t++)
            {
                int a = Triangles[t * 3], b = Triangles[t * 3 + 1], c = Triangles[t * 3 + 2];
                _sum[a] += Positions[b] + Positions[c];
                _sum[b] += Positions[a] + Positions[c];
                _sum[c] += Positions[a] + Positions[b];
                _links[a] += 2;
                _links[b] += 2;
                _links[c] += 2;
            }
            for(int v = 0; v < n; v++)
                _smoothed[v] = _links[v] > 0 ? Positions[v] + (_sum[v] / _links[v] - Positions[v]) * k : Positions[v];
            Array.Copy(_smoothed, Positions, n);
        }
        // normals: each corner's triangles' (by their area), the way out of the liquid kept from the field's
        Array.Clear(_sum, 0, n);
        for(int t = 0; t < TriangleCount; t++)
        {
            int a = Triangles[t * 3], b = Triangles[t * 3 + 1], c = Triangles[t * 3 + 2];
            var face = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
            if(Vector3.Dot(face, Normals[a] + Normals[b] + Normals[c]) < 0f)
                face = -face;
            _sum[a] += face;
            _sum[b] += face;
            _sum[c] += face;
        }
        for(int v = 0; v < n; v++)
            if(_sum[v].LengthSquared() > 1e-24f)
                Normals[v] = Vector3.Normalize(_sum[v]);
    }

    // ---- each piece

    private void Bounds(in ClimaxService.Piece p, float coatSpread, out Vector3 lo, out Vector3 hi)
    {
        var a = new Vector3(p.A.X, p.A.Y, p.A.Z);
        if(p.U.W > 0.5f)
        {
            float r = p.A.W * Reach;
            lo = a - new Vector3(r);
            hi = a + new Vector3(r);
            return;
        }
        var b = new Vector3(p.B.X, p.B.Y, p.B.Z);
        float ra = MathF.Abs(p.A.W) * Reach * (p.B.W < 0 ? MathF.Max(coatSpread, _coatThickness) + _coatThickness : 1f), rb = p.B.W < 0 ? ra : MathF.Abs(p.B.W) * Reach;
        lo = Vector3.Min(a - new Vector3(ra), b - new Vector3(rb));
        hi = Vector3.Max(a + new Vector3(ra), b + new Vector3(rb));
    }

    private void Splat(in ClimaxService.Piece p, float coatSpread, int zFrom, int zTo)
    {
        Bounds(p, coatSpread, out var lo, out var hi);
        int x0 = Math.Max((int)MathF.Floor((lo.X - _min.X) / _h), 0), x1 = Math.Min((int)MathF.Ceiling((hi.X - _min.X) / _h), _nx - 1);
        int y0 = Math.Max((int)MathF.Floor((lo.Y - _min.Y) / _h), 0), y1 = Math.Min((int)MathF.Ceiling((hi.Y - _min.Y) / _h), _ny - 1);
        int z0 = Math.Max((int)MathF.Floor((lo.Z - _min.Z) / _h), Math.Max(zFrom, 0)), z1 = Math.Min((int)MathF.Ceiling((hi.Z - _min.Z) / _h), Math.Min(zTo, _nz - 1));
        if(x0 > x1 || y0 > y1 || z0 > z1)
            return;
        var a = new Vector3(p.A.X, p.A.Y, p.A.Z);
        bool ellipsoid = p.U.W > 0.5f;
        Vector3 b = new(p.B.X, p.B.Y, p.B.Z), e1 = default, e2 = default, e3 = default;
        float ra = MathF.Abs(p.A.W), rb = MathF.Abs(p.B.W);
        // liquid lying on skin: a flat round cushion along the skin (when which way is out is known), most of it outside
        Vector3 up = new(p.U.X, p.U.Y, p.U.Z);
        bool coat = p.B.W < 0, flat = coat && up.LengthSquared() > 0.25f;
        float across = 1f, thick = 1f;
        if(coat)
        {
            ra *= coatSpread;
            rb = ra;
            if(flat)
            {
                up = Vector3.Normalize(up);
                across = ra;
                thick = MathF.Max(MathF.Abs(p.A.W) * _coatThickness, 1e-4f);
                a += up * (thick * _coatOut);
            }
        }
        if(ellipsoid)
        {
            // (each axis as long as that radius: a point's distance in "radii" along each)
            e1 = new Vector3(p.U.X, p.U.Y, p.U.Z);
            e2 = new Vector3(p.V.X, p.V.Y, p.V.Z);
            e3 = b;
            e1 /= MathF.Max(e1.LengthSquared(), 1e-12f);
            e2 /= MathF.Max(e2.LengthSquared(), 1e-12f);
            e3 /= MathF.Max(e3.LengthSquared(), 1e-12f);
        }
        var ab = b - a;
        float abLen2 = ab.LengthSquared();
        float inv = 1f / (Reach * Reach);
        for(int z = z0; z <= z1; z++)
            for(int y = y0; y <= y1; y++)
            {
                int row = y * _nx + z * _nx * _ny;
                int span = y + z * _ny;
                if(x0 < _rowLo[span])
                    _rowLo[span] = x0;
                if(x1 > _rowHi[span])
                    _rowHi[span] = x1;
                for(int x = x0; x <= x1; x++)
                {
                    var q = _min + new Vector3(x, y, z) * _h;
                    float s2;   // the distance from the piece, squared, in its own radii (1: on its surface)
                    if(flat)
                    {
                        var d = q - a;
                        float along = Vector3.Dot(d, up);
                        float side = MathF.Max(d.LengthSquared() - along * along, 0f);
                        s2 = side / (across * across) + along * along / (thick * thick);
                    }
                    else if(ellipsoid)
                    {
                        var d = q - a;
                        float u = Vector3.Dot(d, e1), w = Vector3.Dot(d, e2), t3 = Vector3.Dot(d, e3);
                        s2 = u * u + w * w + t3 * t3;
                    }
                    else
                    {
                        float t = abLen2 > 1e-14f ? Math.Clamp(Vector3.Dot(q - a, ab) / abLen2, 0f, 1f) : 0f;
                        float r = ra + (rb - ra) * t;
                        s2 = Vector3.DistanceSquared(q, a + ab * t) / MathF.Max(r * r, 1e-12f);
                    }
                    float k = 1f - s2 * inv;
                    if(k > 0f)
                        _field[row + x] += k * k * k;
                }
            }
    }

    // The corners of a cell (bit 0: x, bit 1: y, bit 2: z) and its twelve edges.
    private static Vector3 CornerOffset(int k) => new(k & 1, (k >> 1) & 1, (k >> 2) & 1);

    private static readonly int[] Edges =
    [
        0, 1, 2, 3, 4, 5, 6, 7,   // along x
        0, 2, 1, 3, 4, 6, 5, 7,   // along y
        0, 4, 1, 5, 2, 6, 3, 7,   // along z
    ];
}
