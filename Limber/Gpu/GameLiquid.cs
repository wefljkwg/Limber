using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Limber.BodyRig;
using Limber.Rig;

namespace Limber.Gpu;

/// <summary>
/// The Climax liquid drawn by the game itself, so it's lit, shadowed and post-processed like everything else. A few large
/// vanilla prop models are placed in the scene as carriers (render objects only this client has), their vertex and index
/// buffers swapped for writable copies, and the liquid's surface (<see cref="LiquidSurface"/>, made on a background thread)
/// written into them, spread across every mesh of every carrier (what isn't used is folded away into nothing). Their own
/// textures are swapped, through the mod manager, for plain ones in the liquid's colour.
/// </summary>
public static unsafe class GameLiquid
{
    public sealed record Candidate(string Path, string Name, MdlRigger.CarrierInfo Info)
    {
        /// <summary>The triangles it can hold (every usable mesh of its LoD 0).</summary>
        public int Capacity => Usable(Info).Sum(m => (int)(m.Indices / 3));
    }

    /// <summary>The meshes of a carrier the liquid can be written into: a position it can write, and room enough.</summary>
    private static IEnumerable<MdlRigger.CarrierMesh> Usable(MdlRigger.CarrierInfo info) =>
        info.Meshes.Where(m => m.Indices >= 300 && m.Vertices >= 100 && m.Elements.Any(e => e.Usage == 0 && e.Type is 2 or 3 or 14));

    // ---- Finding carriers (background)

    /// <summary>Bumped whenever the way carriers are picked changes (what was picked before is picked again).</summary>
    public const int PickVersion = 2;

    private static Task<List<Candidate>>? _search;
    private static bool _searchedOnce;
    public static string SearchStatus { get; private set; } = "";
    public static List<Candidate> Candidates { get; private set; } = new();

    public static bool Searching => _search is { IsCompleted: false };

    /// <summary>Looks through every housing and world prop for models with room for a lot of geometry.</summary>
    public static void Search(HousingProps housing)
    {
        if(Searching)
            return;
        var items = new List<HousingItem>();
        if(housing.Catalog is { } c)
            items.AddRange(c);
        if(housing.WorldCatalog is { } w)
            items.AddRange(w);
        if(items.Count == 0)
        {
            SearchStatus = "Reading the prop list...";
            return;
        }
        _searchedOnce = true;
        SearchStatus = $"Looking through {items.Count} props for carriers...";
        _search = Task.Run(() =>
        {
            var found = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int looked = 0;
            foreach(var item in items)
            {
                var path = item.MainModelPath;
                if(string.IsNullOrEmpty(path) || !seen.Add(path))
                    continue;
                looked++;
                try
                {
                    var data = Plugin.DataManager.GetFile(path)?.Data;
                    if(data == null)
                        continue;
                    var info = MdlRigger.CarrierInfoOf(data);
                    if(info == null)
                        continue;
                    var candidate = new Candidate(path, item.Name, info);
                    if(candidate.Capacity < 4000)
                        continue;
                    // (only plain materials: dyeable ones are drawn in the prop's dye colour, black when it has none)
                    var shaders = ShadersOf(path, info);
                    if(shaders.Count == 0 || shaders.Any(x => !x.Equals("bg.shpk", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    found.Add(candidate);
                }
                catch
                {
                }
            }
            SearchStatus = $"Looked at {looked} models: {found.Count} could carry the liquid.";
            return found.OrderByDescending(f => f.Capacity).Take(24).ToList();
        });
    }

    private static readonly Dictionary<string, Candidate?> _lookedUp = new();

    /// <summary>A carrier by its path (read from the game's data), or null.</summary>
    public static Candidate? CandidateFor(string path)
    {
        if(string.IsNullOrEmpty(path))
            return null;
        var known = Candidates.FirstOrDefault(c => c.Path == path);
        if(known != null)
            return known;
        if(_lookedUp.TryGetValue(path, out var had))
            return had;
        Candidate? found = null;
        try
        {
            var data = Plugin.DataManager.GetFile(path)?.Data;
            var info = data != null ? MdlRigger.CarrierInfoOf(data) : null;
            if(info != null)
                found = new Candidate(path, System.IO.Path.GetFileNameWithoutExtension(path), info);
        }
        catch
        {
        }
        _lookedUp[path] = found;
        return found;
    }

    /// <summary>Framework thread. <paramref name="wanted"/>: the liquid is to be drawn (a search is started if none was).</summary>
    public static void Tick(HousingProps? housing, bool wanted)
    {
        if(_search is { IsCompleted: true } s)
        {
            try
            {
                Candidates = s.Result;
            }
            catch(Exception e)
            {
                SearchStatus = "Couldn't look: " + e.Message;
            }
            _search = null;
        }
        if(wanted && !_searchedOnce && !Searching && housing != null)
            Search(housing);
    }

    // ---- The carriers in the scene

    private sealed class Carrier
    {
        public string Path = "";
        public MdlRigger.CarrierInfo Info = null!;
        public MdlRigger.CarrierMesh[] Meshes = [];
        public BgObject* Object;
        public float Scale = 1f;
        public bool Ready, Taken, Failed;
        public int Tries, Wait;
        public uint[] IndexSizes = [];
        public LiveMesh.Found? Vb, Ib;
        public nint VbCopy, IbCopy;
        public byte[] Vertices = [], Indices = [];
        public float Wind = 1f;
        public string Note = "loading";
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    /// <summary>How each carrier is doing (one line each).</summary>
    public static string CarrierReport => string.Join("\n", _carriers.Select(c => $"{c.Name} ({c.Meshes.Length} meshes; "
        + $"{string.Join(" ", c.Meshes[0].Elements.Select(e => $"{e.Usage}:{e.Type}"))}): {c.Note}"));

    private static readonly List<Carrier> _carriers = new();
    private static string _key = "";
    private static Vector3 _origin, _drawnOrigin;
    private static bool _placed;
    public static string Status { get; private set; } = "";
    public static bool On => _carriers.Count > 0;

    /// <summary>The triangles the carriers in the scene can hold, all together (the ones taken over).</summary>
    public static int Room => _carriers.Where(c => c.Taken && !c.Failed).Sum(c => c.Meshes.Sum(m => (int)(m.Indices / 3)));

    /// <summary>Framework thread: places these carriers (or takes them all away: none).</summary>
    public static void Use(IReadOnlyList<string> paths)
    {
        var key = string.Join("|", paths);
        if(key == _key)
            return;
        // (a change of carriers waits until they've stopped changing for a moment: ticking several in a row doesn't place
        // them all again each time)
        if(paths.Count > 0)
        {
            if(key != _pendingKey)
            {
                _pendingKey = key;
                _pendingSince = Environment.TickCount64;
                return;
            }
            if(Environment.TickCount64 - _pendingSince < 750)
                return;
        }
        lock(_gate)
            UseNow(paths, key);
    }

    private static readonly object _gate = new();
    private static string _pendingKey = "";
    private static long _pendingSince;

    private static void UseNow(IReadOnlyList<string> paths, string key)
    {
        StopNow();
        _key = key;
        if(paths.Count == 0)
            return;
        var candidates = paths.Select(CandidateFor).Where(c => c != null).Select(c => c!).ToList();
        Retexture(candidates);
        foreach(var candidate in candidates)
        {
            try
            {
                var data = Plugin.DataManager.GetFile(candidate.Path)?.Data;
                if(data == null)
                    continue;
                var c = new Carrier { Path = candidate.Path, Info = candidate.Info, Meshes = Usable(candidate.Info).ToArray() };
                if(c.Meshes.Length == 0)
                    continue;
                var (offset, size, _) = MdlRigger.Layout(data);
                c.IndexSizes = [BitConverter.ToUInt32(data, 0x34), BitConverter.ToUInt32(data, 0x38), BitConverter.ToUInt32(data, 0x3C)];
                var block = data.AsSpan((int)offset[0], (int)size[0]).ToArray();
                // (each mesh's first vertex as a template for every element the liquid doesn't set itself)
                c.Vertices = new byte[c.Info.VertexBlock];
                c.Indices = new byte[c.Info.IndexBlock];
                foreach(var mesh in c.Meshes)
                    for(int v = 0; v < mesh.Vertices; v++)
                        foreach(var (_, at, stride, type) in mesh.Elements)
                        {
                            int n = ElementSize(type);
                            if(at + n <= block.Length && at + v * stride + n <= c.Vertices.Length)
                                Buffer.BlockCopy(block, at, c.Vertices, at + v * stride, n);
                        }
                c.Wind = WindingOf(data, block, c.Meshes[0]);
                // big enough to hold liquid a couple of metres across wherever it is: scaled up, and the geometry written scaled down
                var half = Vector3.Max(Vector3.Abs(c.Info.BoxMax), Vector3.Abs(c.Info.BoxMin));
                float extent = MathF.Max(half.X, MathF.Max(half.Y, half.Z));
                c.Scale = Math.Clamp(1.5f / MathF.Max(extent, 0.01f), 0.05f, 500f);
                c.Object = BgObject.Create(c.Path, string.Empty);
                if(c.Object != null)
                    _carriers.Add(c);
            }
            catch
            {
            }
        }
        _placed = false;
        Status = _carriers.Count > 0 ? $"Placing {_carriers.Count} carriers..." : "The game didn't make any carriers.";
    }

    /// <summary>Takes the carriers away (and gives their models their own buffers back). Framework thread.</summary>
    public static void Stop()
    {
        lock(_gate)
            StopNow();
    }

    private static void StopNow()
    {
        Unretexture();
        foreach(var c in _carriers)
        {
            if(c.Vb != null && c.VbCopy != 0)
                LiveMesh.Restore(c.Vb, c.VbCopy);
            if(c.Ib != null && c.IbCopy != 0)
                LiveMesh.Restore(c.Ib, c.IbCopy);
            if(c.Object != null)
            {
                try
                {
                    var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)c.Object;
                    obj->CleanupRender();
                    obj->Dtor(1);
                }
                catch
                {
                }
            }
        }
        _carriers.Clear();
        _key = "";
        Status = "";
    }

    /// <summary>Takes the carriers away, to be placed again (with the colour as it is now) on the next tick.</summary>
    public static void Reapply() => Stop();

    /// <summary>Framework thread: keeps the carriers where the liquid is.</summary>
    public static void Place(Vector3 around)
    {
        bool anyTaken = false, allSettled = true;
        foreach(var c in _carriers)
        {
            var handle = c.Object->ModelResourceHandle;
            if(!c.Ready && handle != null && ((ResourceHandle*)handle)->LoadState == 7)
                c.Ready = true;
            anyTaken |= c.Taken && !c.Failed;
            allSettled &= c.Ready;
        }
        // (moved only when the liquid has gone a good way from them: the surface is written relative to where they are)
        bool move = !_placed || Vector3.Distance(_origin, around) > 0.6f;
        if(move)
            _origin = around;
        if(!_placed)
            _drawnOrigin = _origin;
        foreach(var c in _carriers)
        {
            if(!c.Ready)
                continue;
            var obj = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)c.Object;
            // (where the surface written last was made for: moved with it, not before it)
            obj->Position = _drawnOrigin;
            obj->Rotation = Quaternion.Identity;
            // (kept tiny until its buffers are Limber's, so the prop itself never shows)
            obj->Scale = new Vector3(c.Taken && !c.Failed ? c.Scale : 0.0001f);
            var draw = (DrawObject*)c.Object;
            draw->UpdateTransforms(false);
            draw->UpdateCulling();
        }
        _placed = anyTaken && allSettled;
    }

    // ---- Every frame (render thread): the liquid into the carriers

    private static ClimaxService.Piece[] _take = new ClimaxService.Piece[1024];
    private static readonly LiquidSurface _surface = new();
    private static Task? _job;
    private static bool _jobDone;
    private static Vector3 _jobOrigin;
    private static float _dropSize = 0.5f, _coatSpread = 1.4f, _coatThickness = 0.5f;
    private static int _smoothing = 2;

    /// <summary>How big the drops are in the surface (times how they're drawn: smaller, as close drops melt together and
    /// swell), how far liquid on skin spreads and how thick it lies, and how many passes of smoothing.</summary>
    public static void SetLook(ClimaxLook l)
    {
        _dropSize = Math.Clamp(l.GameDropSize, 0.1f, 3f);
        _coatSpread = Math.Clamp(l.CoatSpread, 0.2f, 4f);
        _coatThickness = Math.Clamp(l.GameCoatSize, 0.05f, 2f);
        _smoothing = Math.Clamp(l.GameSmoothing, 0, 8);
        _airSize = Math.Clamp(l.GameAirSize, 0.1f, 4f);
        _stringSize = Math.Clamp(l.GameStringSize, 0.1f, 4f);
        _detail = Math.Clamp(l.GameDetail, 0.0004f, 0.01f);
        _rippleSize = Math.Clamp(l.GameRippleSize, 0.002f, 0.5f);
        _surface.SetShape(l.GameMelt, l.GameFullness, l.GameSmoothStrength, l.GameCoatOut);
    }

    private static float _airSize = 1f, _stringSize = 1f, _detail = 0.001f, _rippleSize = 0.03f;

    public static void Render(ClimaxService climax)
    {
        // (never while the carriers are being placed or taken away on the other thread)
        if(!System.Threading.Monitor.TryEnter(_gate))
            return;
        try
        {
            RenderNow(climax);
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    private static void RenderNow(ClimaxService climax)
    {
        if(_carriers.Count == 0)
            return;
        try
        {
            foreach(var c in _carriers)
                if(c.Ready && !c.Taken && !c.Failed)
                {
                    try
                    {
                        TakeOver(c);
                    }
                    catch(Exception e)
                    {
                        c.Failed = true;
                        c.Note = "couldn't be taken over: " + e.Message;
                    }
                }
            foreach(var c in _carriers)
                if(c.Taken && !c.Failed && (!LiveMesh.StillHolds(c.Vb!, c.VbCopy) || !LiveMesh.StillHolds(c.Ib!, c.IbCopy)))
                {
                    c.Failed = true;
                    c.Note = "the game reloaded it";
                }
            var live = _carriers.Where(c => c.Taken && !c.Failed).ToList();
            if(live.Count == 0)
            {
                Status = _carriers.All(c => c.Failed) ? "None of the carriers could be used (see each one below)." : "Waiting for the carriers...";
                return;
            }
            // the surface is made on a background thread; when one is done it's written in and the next is started
            if(_job != null && !_job.IsCompleted)
                return;
            if(_job != null)
            {
                _job = null;
                if(_jobDone)
                {
                    int drawn = Write(live);
                    _drawnOrigin = _jobOrigin;
                    foreach(var c in live)
                    {
                        LiveMesh.Upload(c.VbCopy, c.Vb!.Desc, c.Vertices);
                        LiveMesh.Upload(c.IbCopy, c.Ib!.Desc, c.Indices);
                    }
                    int waiting = _carriers.Count(c => !c.Taken && !c.Failed), failed = _carriers.Count(c => c.Failed);
                    Status = $"Drawn by the game: {drawn} of {_surface.TriangleCount} triangles, in {live.Count} carriers (room for {Room}); "
                        + $"cell {_surface.Cell * 1000f:0.0} mm{(_surface.CutShort ? " (cut short: getting coarser)" : "")}"
                        + (waiting > 0 ? $"; {waiting} carriers still loading" : "") + (failed > 0 ? $"; {failed} couldn't be used" : "") + ".";
                }
            }
            int count = climax.Take(ref _take);
            var pieces = _take;
            float size = _dropSize, spread = _coatSpread, thick = _coatThickness, air = _airSize, str = _stringSize, detail = _detail;
            int smooth = _smoothing;
            int maxT = live.Sum(c => c.Meshes.Sum(m => (int)(m.Indices / 3)));
            int maxV = live.Sum(c => c.Meshes.Sum(m => m.Vertices));
            // (a little room kept for corners repeated where the surface is split between meshes)
            maxV = (int)(maxV * 0.85f);
            _jobOrigin = _origin;
            _jobDone = false;
            _job = Task.Run(() =>
            {
                for(int i = 0; i < count; i++)
                {
                    // (each kind sized on its own: liquid on skin, flying drops, the strings between them)
                    bool coat = pieces[i].B.W < 0, ellipsoid = pieces[i].U.W > 0.5f;
                    bool ball = !coat && !ellipsoid && pieces[i].A.X == pieces[i].B.X && pieces[i].A.Y == pieces[i].B.Y && pieces[i].A.Z == pieces[i].B.Z;
                    float k = size * (coat ? 1f : ellipsoid || ball ? air : str);
                    if(k == 1f)
                        continue;
                    pieces[i].A.W *= k;
                    if(!ellipsoid)
                        pieces[i].B.W *= k;
                    else
                    {
                        pieces[i].U = new Vector4(new Vector3(pieces[i].U.X, pieces[i].U.Y, pieces[i].U.Z) * k, pieces[i].U.W);
                        pieces[i].V = new Vector4(new Vector3(pieces[i].V.X, pieces[i].V.Y, pieces[i].V.Z) * k, pieces[i].V.W);
                        pieces[i].B = new Vector4(new Vector3(pieces[i].B.X, pieces[i].B.Y, pieces[i].B.Z) * k, pieces[i].B.W);
                    }
                }
                _surface.Build(pieces, count, spread, thick, maxV, maxT, detail, smooth);
                _jobDone = true;
            });
        }
        catch(Exception e)
        {
            Status = "Stopped making the surface: " + e.GetType().Name + ": " + e.Message;
            _job = null;
        }
    }

    /// <summary>Finds a carrier's buffers and swaps in writable copies (looked for every half second, for 10 seconds: the game
    /// may make them a little after the model has loaded).</summary>
    private static void TakeOver(Carrier c)
    {
        if(c.Wait-- > 0)
            return;
        c.Wait = 30;
        c.Tries++;
        var found = LiveMesh.ProbeFrom([(nint)c.Object, (nint)c.Object->ModelResourceHandle], c.Path);
        // (the game rounds its buffers up to a multiple of 128 bytes: exactly the size first, else rounded up)
        uint rounded = (c.Info.VertexBlock + 127u) / 128u * 128u;
        c.Vb = found.FirstOrDefault(f => f.IsVertex && f.Desc.ByteWidth == c.Info.VertexBlock)
            ?? found.FirstOrDefault(f => f.IsVertex && f.Desc.ByteWidth == rounded);
        // (the indices: exactly LoD 0's, else all LoDs' together, else the nearest a little bigger, as the game may pad it)
        uint allIndices = c.IndexSizes.Aggregate(0u, (a, b) => a + b);
        var indexBuffers = found.Where(f => (f.Desc.BindFlags & 2) != 0 && !f.IsVertex).ToList();
        c.Ib = indexBuffers.FirstOrDefault(f => f.Desc.ByteWidth == c.Info.IndexBlock)
            ?? indexBuffers.FirstOrDefault(f => f.Desc.ByteWidth == allIndices)
            ?? indexBuffers.Where(f => f.Desc.ByteWidth > c.Info.IndexBlock && f.Desc.ByteWidth <= c.Info.IndexBlock + 256)
                .OrderBy(f => f.Desc.ByteWidth).FirstOrDefault();
        if(c.Vb == null || c.Ib == null)
        {
            var vs = found.Where(f => f.IsVertex).Select(f => f.Desc.ByteWidth).Distinct().OrderBy(w => Math.Abs((long)w - c.Info.VertexBlock)).Take(4);
            var ix = indexBuffers.Select(f => f.Desc.ByteWidth).Distinct().OrderBy(w => Math.Abs((long)w - c.Info.IndexBlock)).Take(4);
            c.Note = $"looking for its buffers (try {c.Tries}/20): vertices {(c.Vb != null ? "found" : $"not found (wanted {c.Info.VertexBlock}, nearest {string.Join(", ", vs)})")}, "
                + $"indices {(c.Ib != null ? "found" : $"not found (wanted {c.Info.IndexBlock}, nearest {string.Join(", ", ix)})")}";
            if(c.Tries >= 20)
            {
                c.Failed = true;
                c.Note = "gave up " + c.Note;
            }
            return;
        }
        // (the copies exactly as big as the game's buffers, in case those are padded)
        if(c.Vertices.Length < c.Vb.Desc.ByteWidth)
            Array.Resize(ref c.Vertices, (int)c.Vb.Desc.ByteWidth);
        if(c.Indices.Length < c.Ib.Desc.ByteWidth)
            Array.Resize(ref c.Indices, (int)c.Ib.Desc.ByteWidth);
        c.VbCopy = LiveMesh.Replace(c.Vb, c.Vertices);
        c.IbCopy = LiveMesh.Replace(c.Ib, c.Indices);
        c.Taken = true;
        c.Note = "in use";
        if(c.VbCopy == 0 || c.IbCopy == 0)
        {
            c.Failed = true;
            c.Note = "its buffers couldn't be swapped";
        }
    }

    /// <summary>
    /// The surface into the carriers' copies: its triangles in order, into one mesh after another (each mesh with its own
    /// corners, so a corner where the surface moves on to the next mesh is written in both). Every triangle not used is folded
    /// into nothing. Returns how many triangles were written.
    /// </summary>
    private static int Write(List<Carrier> live)
    {
        var sf = _surface;
        if(_local.Length < sf.VertexCount)
        {
            _local = new int[sf.Positions.Length];
            _stamp = new int[sf.Positions.Length];
        }
        foreach(var c in live)
            Array.Clear(c.Indices);
        int t = 0;
        foreach(var c in live)
        {
            foreach(var mesh in c.Meshes)
            {
                if(t >= sf.TriangleCount)
                    break;
                _stampNow++;
                var pos = mesh.Elements.First(e => e.Usage == 0 && e.Type is 2 or 3 or 14);
                var nrm = mesh.Elements.FirstOrDefault(e => e.Usage == 3);
                var uv = mesh.Elements.FirstOrDefault(e => e.Usage == 4);
                bool hasNormal = mesh.Elements.Any(e => e.Usage == 3), hasUv = mesh.Elements.Any(e => e.Usage == 4);
                var tangents = mesh.Elements.Where(e => e.Usage is 5 or 6).ToArray();
                var colours = mesh.Elements.Where(e => e.Usage == 7).ToArray();
                int maxT = (int)(mesh.Indices / 3), maxV = Math.Min(mesh.Vertices, 65535);
                int usedT = 0, usedV = 0;
                int baseIndex = (int)mesh.StartIndex * 2;
                for(; t < sf.TriangleCount && usedT < maxT; t++)
                {
                    int a = sf.Triangles[t * 3], b = sf.Triangles[t * 3 + 1], d = sf.Triangles[t * 3 + 2];
                    int need = (_stamp[a] != _stampNow ? 1 : 0) + (_stamp[b] != _stampNow ? 1 : 0) + (_stamp[d] != _stampNow ? 1 : 0);
                    if(usedV + need > maxV)
                        break;
                    // wound the way the carrier's own triangles are (facing out of the liquid)
                    var face = Vector3.Cross(sf.Positions[b] - sf.Positions[a], sf.Positions[d] - sf.Positions[a]);
                    if(Vector3.Dot(face, sf.Normals[a] + sf.Normals[b] + sf.Normals[d]) * c.Wind < 0f)
                        (b, d) = (d, b);
                    int at = baseIndex + usedT * 6;
                    if(at + 6 > c.Indices.Length)
                        break;
                    BitConverter.TryWriteBytes(c.Indices.AsSpan(at), (ushort)Corner(a));
                    BitConverter.TryWriteBytes(c.Indices.AsSpan(at + 2), (ushort)Corner(b));
                    BitConverter.TryWriteBytes(c.Indices.AsSpan(at + 4), (ushort)Corner(d));
                    usedT++;

                    int Corner(int g)
                    {
                        if(_stamp[g] == _stampNow)
                            return _local[g];
                        _stamp[g] = _stampNow;
                        int v = usedV++;
                        _local[g] = v;
                        WriteVec(c.Vertices, pos.At + v * pos.Stride, pos.Type, (sf.Positions[g] - _jobOrigin) / c.Scale);
                        if(hasNormal)
                            WriteNormal(c.Vertices, nrm.At + v * nrm.Stride, nrm.Type, sf.Normals[g]);
                        if(hasUv)
                        {
                            // (spread over the surface by where it is, so the ripples in its normal map show)
                            var w = sf.Positions[g] / _rippleSize;
                            WriteUv(c.Vertices, uv.At + v * uv.Stride, uv.Type, new Vector2(w.X + w.Z * 0.7f, w.Y + w.Z * 0.4f));
                        }
                        // (a direction along the surface, so the game's shading has one that fits the normal; and plain white)
                        if(tangents.Length > 0 && WriteDirections)
                        {
                            var n = sf.Normals[g];
                            var along = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, n));
                            foreach(var e in tangents)
                                WriteTangent(c.Vertices, e.At + v * e.Stride, e.Type, e.Usage == 6 ? along : Vector3.Cross(n, along));
                        }
                        if(WriteColours)
                            foreach(var e in colours)
                                WriteWhite(c.Vertices, e.At + v * e.Stride, e.Type);
                        return v;
                    }
                }
            }
        }
        return t;
    }

    private static int[] _local = [], _stamp = [];
    private static int _stampNow;

    /// <summary>Which way a carrier's triangles are wound against their normals (+1 or -1), from its first few hundred.</summary>
    private static float WindingOf(byte[] file, byte[] block, MdlRigger.CarrierMesh mesh)
    {
        try
        {
            var pos = mesh.Elements.FirstOrDefault(e => e.Usage == 0);
            var nrm = mesh.Elements.FirstOrDefault(e => e.Usage == 3);
            if(!mesh.Elements.Any(e => e.Usage == 3))
                return 1f;
            int indexAt = (int)BitConverter.ToUInt32(file, 0x1C) + (int)mesh.StartIndex * 2;
            float votes = 0f;
            for(int t = 0; t < Math.Min((int)mesh.Indices / 3, 300); t++)
            {
                int a = BitConverter.ToUInt16(file, indexAt + t * 6), b = BitConverter.ToUInt16(file, indexAt + t * 6 + 2), c = BitConverter.ToUInt16(file, indexAt + t * 6 + 4);
                if(a >= mesh.Vertices || b >= mesh.Vertices || c >= mesh.Vertices)
                    continue;
                Vector3 P(int v) => MdlRigger.ReadVector(block, pos.At + v * pos.Stride, pos.Type);
                Vector3 N(int v) => ReadNormal(block, nrm.At + v * nrm.Stride, nrm.Type);
                var face = Vector3.Cross(P(b) - P(a), P(c) - P(a));
                votes += MathF.Sign(Vector3.Dot(face, N(a) + N(b) + N(c)));
            }
            return votes < 0 ? -1f : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    // ---- The liquid's look: the carriers' own textures swapped (through the mod manager) for plain ones Limber makes

    private const string ModTag = "LimberLiquid";
    private const int ModPriority = 99;
    private static bool _retextured;
    private static int _generation;
    private static Vector3 _colour = new(0.95f, 0.94f, 0.9f);
    private static float _gloss = 0.8f, _ripples = 0.25f;
    private static Vector4 _shineMap = new(0.8f, 0.8f, 0.8f, 1f);
    public static string LookStatus { get; private set; } = "";

    /// <summary>The colour (and how glossy) the next placed carriers get.</summary>
    public static void SetColour(Vector3 colour, float gloss)
    {
        _colour = Vector3.Clamp(colour, Vector3.Zero, Vector3.One);
        _gloss = Math.Clamp(gloss, 0f, 1f);
    }

    /// <summary>The shine map's channels and how strong the ripples are (for the next placed carriers).</summary>
    public static void SetMaterial(Vector4 shineMap, float ripples)
    {
        _shineMap = Vector4.Clamp(shineMap, Vector4.Zero, Vector4.One);
        _ripples = Math.Clamp(ripples, 0f, 1f);
    }

    /// <summary>
    /// The carriers' materials swapped (through the mod manager) for copies whose textures are Limber's own plain ones, at new
    /// paths only the liquid uses: so nothing else in the scene changes, even what shares the props' textures.
    /// </summary>
    /// <summary>Troubleshooting: the liquid's own material (else the props' own), and which of the corners' extra shading data
    /// Limber writes (else the props' own, copied from their first corner).</summary>
    public static bool OwnMaterial = true, WriteColours = true, WriteDirections = true;

    private static void Retexture(List<Candidate> carriers)
    {
        Unretexture();
        LookStatus = "";
        if(!OwnMaterial)
        {
            LookStatus = "Using the props' own materials (troubleshooting).";
            return;
        }
        try
        {
            var dir = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Climax");
            System.IO.Directory.CreateDirectory(dir);
            var swaps = new Dictionary<string, string>();
            int materials = 0;
            _generation++;
            foreach(var carrier in carriers)
            {
                var folder = carrier.Path[..Math.Max(carrier.Path.IndexOf("/bgparts/", StringComparison.Ordinal), 0)];
                foreach(var material in carrier.Info.Materials)
                {
                    var path = material.StartsWith('/') || !material.Contains('/') ? $"{folder}/material/{material.TrimStart('/')}" : material;
                    if(swaps.ContainsKey(path))
                        continue;
                    var data = Plugin.DataManager.FileExists(path) ? Plugin.DataManager.GetFile(path)?.Data : null;
                    if(data == null)
                        continue;
                    var copy = RewriteTextures(data, tex =>
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(tex);
                        int cut = name.LastIndexOf('_');
                        var kind = cut >= 0 ? name[cut..] : "_d";
                        // (a new path every time, so the game can't keep showing an older colour it still has loaded)
                        var to = $"bgcommon/limber/climax/liquid{_generation}{kind}.tex";
                        if(!swaps.ContainsKey(to))
                        {
                            var shine = _shineMap * new Vector4(_gloss, _gloss, _gloss, 1f);
                            var file = System.IO.Path.Combine(dir, $"liquid{kind}.tex");
                            System.IO.File.WriteAllBytes(file, kind == "_n" ? RippleTexture(_ripples)
                                : kind is "_s" or "_m" ? PlainTexture([B(shine.Z), B(shine.Y), B(shine.X), B(shine.W)])
                                : PlainTexture([B(_colour.Z), B(_colour.Y), B(_colour.X), 255]));
                            swaps[to] = file;
                        }
                        return to;
                    });
                    if(copy == null)
                        continue;
                    var local = System.IO.Path.Combine(dir, $"carrier{materials++}.mtrl");
                    System.IO.File.WriteAllBytes(local, copy);
                    swaps[path] = local;
                }
            }
            if(materials == 0)
            {
                LookStatus = "Couldn't read the carriers' materials: they keep their own look.";
                return;
            }
            int result = Plugin.PluginInterface.GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>("Penumbra.AddTemporaryModAll.V5")
                .InvokeFunc(ModTag, swaps, "", ModPriority);
            _retextured = result == 0;
            LookStatus = _retextured ? $"{materials} materials given the liquid's look." : $"The mod manager didn't take the liquid's look (it said {result}).";
        }
        catch(Exception e)
        {
            LookStatus = "Couldn't give it the liquid's look (is the mod manager running?): " + e.Message;
        }
    }

    /// <summary>The material path a carrier's material name stands for.</summary>
    private static string MaterialPath(string model, string material)
    {
        var folder = model[..Math.Max(model.IndexOf("/bgparts/", StringComparison.Ordinal), 0)];
        return material.StartsWith('/') || !material.Contains('/') ? $"{folder}/material/{material.TrimStart('/')}" : material;
    }

    /// <summary>The shader each of a model's materials is drawn with.</summary>
    private static List<string> ShadersOf(string model, MdlRigger.CarrierInfo info)
    {
        var list = new List<string>();
        foreach(var material in info.Materials)
        {
            var path = MaterialPath(model, material);
            var mtrl = Plugin.DataManager.FileExists(path) ? Plugin.DataManager.GetFile(path)?.Data : null;
            if(mtrl == null || mtrl.Length < 16)
                continue;
            int textures = mtrl[12], uvSets = mtrl[13], colorSets = mtrl[14];
            int at = 16 + 4 * (textures + uvSets + colorSets) + BitConverter.ToUInt16(mtrl, 10);
            int end = at;
            while(end < mtrl.Length && mtrl[end] != 0)
                end++;
            if(end > at)
                list.Add(System.Text.Encoding.UTF8.GetString(mtrl, at, end - at));
        }
        return list;
    }

    /// <summary>A copy of a material file with every texture path changed (the new paths added to its strings).</summary>
    private static byte[]? RewriteTextures(byte[] mtrl, Func<string, string> to)
    {
        if(mtrl.Length < 16)
            return null;
        int stringsSize = BitConverter.ToUInt16(mtrl, 8);
        int textures = mtrl[12], uvSets = mtrl[13], colorSets = mtrl[14];
        int strings = 16 + 4 * (textures + uvSets + colorSets);
        if(strings + stringsSize > mtrl.Length)
            return null;
        var added = new List<byte>();
        var offsets = new ushort[textures];
        for(int t = 0; t < textures; t++)
        {
            int at = strings + BitConverter.ToUInt16(mtrl, 16 + t * 4);
            int end = at;
            while(end < strings + stringsSize && mtrl[end] != 0)
                end++;
            var name = System.Text.Encoding.UTF8.GetString(mtrl, at, end - at);
            offsets[t] = (ushort)(stringsSize + added.Count);
            added.AddRange(System.Text.Encoding.UTF8.GetBytes(to(name)));
            added.Add(0);
        }
        while(added.Count % 4 != 0)
            added.Add(0);
        if(stringsSize + added.Count > ushort.MaxValue || mtrl.Length + added.Count > ushort.MaxValue)
            return null;
        var copy = new byte[mtrl.Length + added.Count];
        Buffer.BlockCopy(mtrl, 0, copy, 0, strings + stringsSize);
        added.CopyTo(copy, strings + stringsSize);
        Buffer.BlockCopy(mtrl, strings + stringsSize, copy, strings + stringsSize + added.Count, mtrl.Length - strings - stringsSize);
        for(int t = 0; t < textures; t++)
            BitConverter.TryWriteBytes(copy.AsSpan(16 + t * 4), offsets[t]);
        BitConverter.TryWriteBytes(copy.AsSpan(8), (ushort)(stringsSize + added.Count));
        BitConverter.TryWriteBytes(copy.AsSpan(4), (ushort)Math.Min(BitConverter.ToUInt16(mtrl, 4) + added.Count, ushort.MaxValue));
        return copy;
    }

    private static void Unretexture()
    {
        if(!_retextured)
            return;
        _retextured = false;
        try
        {
            Plugin.PluginInterface.GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5").InvokeFunc(ModTag, ModPriority);
        }
        catch
        {
        }
    }

    /// <summary>The texture paths a material file uses.</summary>
    private static List<string> TexturesOf(byte[] mtrl)
    {
        var list = new List<string>();
        if(mtrl.Length < 16)
            return list;
        int stringsSize = BitConverter.ToUInt16(mtrl, 8);
        int textures = mtrl[12], uvSets = mtrl[13], colorSets = mtrl[14];
        int strings = 16 + 4 * (textures + uvSets + colorSets);
        for(int t = 0; t < textures; t++)
        {
            int at = strings + BitConverter.ToUInt16(mtrl, 16 + t * 4);
            int end = at;
            while(end < mtrl.Length && end < strings + stringsSize && mtrl[end] != 0)
                end++;
            if(end > at)
                list.Add(System.Text.Encoding.UTF8.GetString(mtrl, at, end - at));
        }
        return list;
    }

    private static byte B(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);

    /// <summary>A normal map of soft ripples (64x64, wrapping round), <paramref name="strength"/> 0: flat.</summary>
    private static byte[] RippleTexture(float strength)
    {
        const int side = 64;
        var file = Header(side);
        for(int y = 0; y < side; y++)
            for(int x = 0; x < side; x++)
            {
                float u = x * MathF.Tau / side, v = y * MathF.Tau / side;
                // the slope of a few overlapping waves (whole numbers of them across, so it wraps)
                float dx = MathF.Cos(u * 2 + MathF.Sin(v * 1) * 1.3f) * 2 + MathF.Cos(u * 3 - v * 2) * 3 * 0.5f + MathF.Cos(u * 5 + v * 4) * 5 * 0.2f;
                float dy = MathF.Cos(v * 2 + MathF.Sin(u * 1) * 1.1f) * 2 + MathF.Cos(v * 3 + u * 1) * 3 * 0.5f + MathF.Cos(v * 5 - u * 3) * 5 * 0.2f;
                var n = Vector3.Normalize(new Vector3(-dx * strength * 0.15f, -dy * strength * 0.15f, 1f));
                int at = 80 + (y * side + x) * 4;
                file[at] = B(n.Z * 0.5f + 0.5f);
                file[at + 1] = B(n.Y * 0.5f + 0.5f);
                file[at + 2] = B(n.X * 0.5f + 0.5f);
                file[at + 3] = 255;
            }
        return file;
    }

    private static byte[] Header(int side)
    {
        var file = new byte[80 + side * side * 4];
        BitConverter.TryWriteBytes(file.AsSpan(0), 0x00800000u);   // a 2D texture
        BitConverter.TryWriteBytes(file.AsSpan(4), 0x1450u);       // B8G8R8A8
        BitConverter.TryWriteBytes(file.AsSpan(8), (ushort)side);
        BitConverter.TryWriteBytes(file.AsSpan(10), (ushort)side);
        BitConverter.TryWriteBytes(file.AsSpan(12), (ushort)1);    // depth
        file[14] = 1;                                              // mip levels
        file[15] = 1;                                              // array size
        BitConverter.TryWriteBytes(file.AsSpan(28), 80u);          // where the first surface starts
        return file;
    }

    /// <summary>An 8x8 texture of one colour (B G R A), in the game's texture format.</summary>
    private static byte[] PlainTexture(byte[] bgra)
    {
        const int side = 8;
        var file = new byte[80 + side * side * 4];
        BitConverter.TryWriteBytes(file.AsSpan(0), 0x00800000u);   // a 2D texture
        BitConverter.TryWriteBytes(file.AsSpan(4), 0x1450u);       // B8G8R8A8
        BitConverter.TryWriteBytes(file.AsSpan(8), (ushort)side);
        BitConverter.TryWriteBytes(file.AsSpan(10), (ushort)side);
        BitConverter.TryWriteBytes(file.AsSpan(12), (ushort)1);    // depth
        file[14] = 1;                                              // mip levels
        file[15] = 1;                                              // array size
        BitConverter.TryWriteBytes(file.AsSpan(28), 80u);          // where the first surface starts
        for(int i = 0; i < side * side; i++)
            Buffer.BlockCopy(bgra, 0, file, 80 + i * 4, 4);
        return file;
    }

    // ---- Vertex elements

    private static Vector3 ReadNormal(byte[] data, int at, byte type)
    {
        if(type is 5 or 8)
            return new Vector3(data[at] / 127.5f - 1f, data[at + 1] / 127.5f - 1f, data[at + 2] / 127.5f - 1f);
        return MdlRigger.ReadVector(data, at, type);
    }

    private static int ElementSize(byte type) => type switch
    {
        0 => 4, 1 => 8, 2 => 12, 3 => 16, 5 => 4, 8 => 4, 13 => 4, 14 => 8, 17 => 8, _ => 4,
    };

    private static void WriteVec(byte[] into, int at, byte type, Vector3 v)
    {
        if(at < 0 || at + ElementSize(type) > into.Length)
            return;
        MdlRigger.WriteVector(into, at, type, v);
    }

    private static void WriteNormal(byte[] into, int at, byte type, Vector3 n)
    {
        if(at < 0 || at + ElementSize(type) > into.Length)
            return;
        if(type is 2 or 3 or 14)
            MdlRigger.WriteVector(into, at, type, n);
        else if(type is 5 or 8)
        {
            into[at] = (byte)Math.Clamp((n.X * 0.5f + 0.5f) * 255f, 0f, 255f);
            into[at + 1] = (byte)Math.Clamp((n.Y * 0.5f + 0.5f) * 255f, 0f, 255f);
            into[at + 2] = (byte)Math.Clamp((n.Z * 0.5f + 0.5f) * 255f, 0f, 255f);
        }
    }

    private static void WriteUv(byte[] into, int at, byte type, Vector2 uv)
    {
        if(at < 0 || at + ElementSize(type) > into.Length)
            return;
        // (wrapped into a small range: half floats lose precision far from zero)
        uv = new Vector2(uv.X - MathF.Floor(uv.X / 16f) * 16f, uv.Y - MathF.Floor(uv.Y / 16f) * 16f);
        if(type is 1 or 2 or 3)
            for(int k = 0; k < (type == 1 ? 2 : type == 2 ? 3 : 4); k++)
                BitConverter.TryWriteBytes(into.AsSpan(at + k * 4), k % 2 == 0 ? uv.X : uv.Y);
        else if(type is 13 or 14)
            for(int k = 0; k < (type == 13 ? 2 : 4); k++)
                BitConverter.TryWriteBytes(into.AsSpan(at + k * 2), (Half)(k % 2 == 0 ? uv.X : uv.Y));
    }

    private static void WriteTangent(byte[] into, int at, byte type, Vector3 t)
    {
        if(at < 0 || at + ElementSize(type) > into.Length)
            return;
        if(type is 5 or 8)
        {
            into[at] = (byte)Math.Clamp((t.X * 0.5f + 0.5f) * 255f, 0f, 255f);
            into[at + 1] = (byte)Math.Clamp((t.Y * 0.5f + 0.5f) * 255f, 0f, 255f);
            into[at + 2] = (byte)Math.Clamp((t.Z * 0.5f + 0.5f) * 255f, 0f, 255f);
            into[at + 3] = 255;
        }
        else if(type is 2 or 3 or 14)
            MdlRigger.WriteVector(into, at, type, t);
    }

    private static void WriteWhite(byte[] into, int at, byte type)
    {
        if(at < 0 || at + ElementSize(type) > into.Length)
            return;
        if(type is 5 or 8)
            into[at] = into[at + 1] = into[at + 2] = into[at + 3] = 255;
        else if(type == 3)
            for(int k = 0; k < 4; k++)
                BitConverter.TryWriteBytes(into.AsSpan(at + k * 4), 1f);
        else if(type == 14)
            for(int k = 0; k < 4; k++)
                BitConverter.TryWriteBytes(into.AsSpan(at + k * 2), (Half)1f);
    }
}
