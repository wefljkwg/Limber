using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Limber.BodyRig;
using Limber.Core;

namespace Limber.Rig;

/// <summary>
/// A character's real skin for the Climax liquid to land on: the vertices of every model it has on (whatever they are:
/// the game's own or a mod's, as the game loaded them), a point every few millimetres, moved with its bones each frame.
/// Read once per outfit (in the background), skinned each bone update. Only adult characters.
/// </summary>
public sealed class ClimaxSurface
{
    /// <summary>The points at rest (model space), with their normals and their (up to four) bones.</summary>
    private sealed class Rest
    {
        public required Vector3[] Pos, Nrm;
        public required int[] Bone;     // 4 per point: slots in Names (-1 none)
        public required float[] Weight; // 4 per point
        public required string[] Names;
        public required string Key;
    }

    /// <summary>One character's skin this frame (world space), and a grid to find the points near a place.</summary>
    public sealed class Frame
    {
        public Vector3[] Pos = [], Nrm = [];
        public bool[] Valid = [];
        public int Count;
        internal int[] CellStart = new int[TableSize + 1], CellIdx = [], Hash = [], Fill = new int[TableSize];
    }

    private const int TableSize = 1 << 16;
    private const float Cell = 0.02f;   // grid cell (m)
    private const float Spacing = 0.007f; // a point every this far (m)

    private sealed class Person
    {
        public string Key = "";
        public Task<Rest?>? Making;
        public Rest? Rest;
        public SkeletonInfo? RestInfo;
        public int[] Real = [];             // per slot: the body bone (or the head for a face bone), -1 none
        public Matrix4x4[] RestInverse = [];
        public long NextLook;
        public Frame Frame = new();
    }

    private readonly ConcurrentDictionary<nint, Person> _people = new();

    /// <summary>Points in use (for the page).</summary>
    public int Points { get; private set; }

    /// <summary>Bone update: the character's skin this frame, or null (not read yet, or no models).</summary>
    public unsafe Frame? Get(nint address, in ActorSkeleton sk, long now)
    {
        var person = _people.GetOrAdd(address, _ => new Person());
        // what it wears (looked at twice a second)
        if(now >= person.NextLook && person.Making == null)
        {
            person.NextLook = now + 500;
            var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address;
            if(obj != null && obj->DrawObject != null)
            {
                var files = Limber.Gpu.LiveMesh.ModelFiles((nint)obj->DrawObject).Select(f => f.File)
                    .Where(f => f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
                string key = string.Join(";", files);
                if(key != person.Key && files.Count > 0)
                {
                    person.Key = key;
                    person.Making = Task.Run(() => Read(files, key));
                }
            }
        }
        if(person.Making is { IsCompleted: true } making)
        {
            try
            {
                if(making.Result is { } made && made.Key == person.Key)
                {
                    person.Rest = made;
                    person.RestInfo = null;
                }
            }
            catch
            {
            }
            person.Making = null;
        }
        var rest = person.Rest;
        if(rest == null)
            return null;
        if(!ReferenceEquals(person.RestInfo, sk.Info))
            Bind(person, rest, sk);
        Skin(person, rest, sk);
        return person.Frame;
    }

    /// <summary>Characters no longer around let go of.</summary>
    public void Keep(ICollection<nint> addresses)
    {
        foreach(var a in _people.Keys)
            if(!addresses.Contains(a))
                _people.TryRemove(a, out _);
    }

    // ---- Reading the models (background)

    private static Rest? Read(List<string> files, string key)
    {
        var names = new List<string>();
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var bones = new List<int>();
        var weights = new List<float>();
        var seen = new HashSet<(int, int, int)>();
        Span<int> bi = stackalloc int[4];
        Span<float> bw = stackalloc float[4];
        foreach(var file in files)
        {
            byte[]? data;
            try
            {
                data = Limber.UI.LimberTab.ReadModelFile(file);
            }
            catch
            {
                continue;
            }
            if(data == null)
                continue;
            try
            {
                var (offset, size, layouts) = MdlRigger.Layout(data);
                if(size[0] == 0 || offset[0] + size[0] > data.Length)
                    continue;
                var block = data.AsSpan((int)offset[0], (int)size[0]).ToArray();
                var skins = MdlRigger.Skin(data, 0);
                foreach(var layout in layouts.Where(l => l.Lod == 0))
                {
                    var skin = skins.FirstOrDefault(k => k.Mesh == layout.Mesh);
                    if(skin == null)
                        continue;
                    bool normals = layout.NormalAt >= 0 && layout.NormalType is (2 or 3 or 14);
                    for(int v = 0; v < layout.VertexCount; v++)
                    {
                        int pa = layout.PositionAt + v * layout.PositionStride;
                        if(pa + 12 > block.Length)
                            break;
                        var p = MdlRigger.ReadVector(block, pa, layout.PositionType);
                        if(!MathUtil.IsFinite(p))
                            continue;
                        // (a point every few millimetres: one per little cube)
                        var cellKey = ((int)MathF.Floor(p.X / Spacing), (int)MathF.Floor(p.Y / Spacing), (int)MathF.Floor(p.Z / Spacing));
                        if(!seen.Add(cellKey))
                            continue;
                        var n = Vector3.UnitY;
                        if(normals)
                        {
                            int na = layout.NormalAt + v * layout.NormalStride;
                            if(na + 4 <= block.Length)
                            {
                                var nn = MdlRigger.ReadVector(block, na, layout.NormalType);
                                if(MathUtil.IsFinite(nn) && nn.LengthSquared() > 1e-8f)
                                    n = Vector3.Normalize(nn);
                            }
                        }
                        // its four strongest bones
                        bi.Fill(-1);
                        bw.Clear();
                        for(int k = 0; k < skin.Slots; k++)
                        {
                            float w = skin.Weights[v * 8 + k];
                            if(w <= 0f)
                                continue;
                            int local = skin.Indices[v * 8 + k];
                            if(local >= skin.Bones.Length)
                                continue;
                            string bone = skin.Bones[local];
                            if(!slotOf.TryGetValue(bone, out int slot))
                            {
                                slot = names.Count;
                                names.Add(bone);
                                slotOf[bone] = slot;
                            }
                            // (into the four, replacing the weakest)
                            int weakest = 0;
                            for(int q = 1; q < 4; q++)
                                if(bw[q] < bw[weakest])
                                    weakest = q;
                            if(w > bw[weakest])
                            {
                                bw[weakest] = w;
                                bi[weakest] = slot;
                            }
                        }
                        float total = bw[0] + bw[1] + bw[2] + bw[3];
                        if(total <= 1e-4f)
                            continue;
                        pos.Add(p);
                        nrm.Add(n);
                        for(int q = 0; q < 4; q++)
                        {
                            bones.Add(bi[q]);
                            weights.Add(bw[q] / total);
                        }
                    }
                }
            }
            catch
            {
                // (a model it can't read is left out)
            }
        }
        if(pos.Count == 0)
            return null;
        return new Rest { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Bone = bones.ToArray(), Weight = weights.ToArray(), Names = names.ToArray(), Key = key };
    }

    // ---- Matching the bones to the skeleton (once per skeleton)

    private static unsafe void Bind(Person person, Rest rest, in ActorSkeleton sk)
    {
        var info = sk.Info;
        // the skeleton at rest (model space), parent by parent
        int n = info.Count;
        var restM = new Matrix4x4[n];
        var reference = sk.Pose->Skeleton->ReferencePose;
        var pos = new Vector3[n];
        var rot = new Quaternion[n];
        var scl = new Vector3[n];
        for(int i = 0; i < n; i++)
        {
            Vector3 lp = Vector3.Zero, ls = Vector3.One;
            var lq = Quaternion.Identity;
            if(i < reference.Length)
            {
                var t = reference[i];
                lp = *(Vector3*)&t.Translation;
                lq = Quaternion.Normalize(*(Quaternion*)&t.Rotation);
                ls = *(Vector3*)&t.Scale;
            }
            int parent = info.ParentOf(i);
            if(parent >= 0 && parent < i)
            {
                pos[i] = pos[parent] + Vector3.Transform(scl[parent] * lp, rot[parent]);
                rot[i] = Quaternion.Normalize(rot[parent] * lq);
                scl[i] = scl[parent] * ls;
            }
            else
            {
                pos[i] = lp;
                rot[i] = lq;
                scl[i] = ls;
            }
            restM[i] = Matrix4x4.CreateScale(scl[i]) * Matrix4x4.CreateFromQuaternion(rot[i]) * Matrix4x4.CreateTranslation(pos[i]);
        }
        int head = info.IndexOf("j_kao");
        var real = new int[rest.Names.Length];
        var inv = new Matrix4x4[rest.Names.Length];
        for(int s = 0; s < real.Length; s++)
        {
            string name = rest.Names[s];
            int b = info.IndexOf(name);
            // (a bone the body skeleton hasn't got: the face's and the hair's own bones go with the head; anything else is left out)
            if(b < 0 && (name.StartsWith("j_f_", StringComparison.Ordinal) || name.StartsWith("j_ago", StringComparison.Ordinal) || name.StartsWith("j_ex_h", StringComparison.Ordinal)))
                b = head;
            real[s] = b;
            if(b < 0 || !Matrix4x4.Invert(restM[b], out inv[s]))
                inv[s] = Matrix4x4.Identity;
        }
        person.Real = real;
        person.RestInverse = inv;
        person.RestInfo = info;
    }

    // ---- Each frame: every point where the body has it now

    private static void Skin(Person person, Rest rest, in ActorSkeleton sk)
    {
        var real = person.Real;
        var mats = new Matrix4x4[real.Length];
        var ok = new bool[real.Length];
        var toWorld = sk.ModelToWorld;
        for(int s = 0; s < real.Length; s++)
        {
            int b = real[s];
            if(b < 0)
                continue;
            var f = sk.GetModelFrame(b);
            var sc = sk.GetModelScale(b);
            mats[s] = person.RestInverse[s] * (Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(f.Rotation) * Matrix4x4.CreateTranslation(f.Position)) * toWorld;
            ok[s] = MathUtil.IsFinite(mats[s].Translation);
        }
        var frame = person.Frame;
        int n = rest.Pos.Length;
        if(frame.Pos.Length != n)
        {
            frame.Pos = new Vector3[n];
            frame.Nrm = new Vector3[n];
            frame.Valid = new bool[n];
            frame.CellIdx = new int[n];
            frame.Hash = new int[n];
        }
        frame.Count = n;
        Parallel.For(0, n, i =>
        {
            var p = Vector3.Zero;
            var nn = Vector3.Zero;
            float used = 0f;
            for(int q = 0; q < 4; q++)
            {
                int s = rest.Bone[i * 4 + q];
                if(s < 0 || !ok[s])
                    continue;
                float w = rest.Weight[i * 4 + q];
                p += Vector3.Transform(rest.Pos[i], mats[s]) * w;
                nn += Vector3.TransformNormal(rest.Nrm[i], mats[s]) * w;
                used += w;
            }
            if(used < 0.5f)
            {
                frame.Valid[i] = false;
                return;
            }
            frame.Pos[i] = p / used;
            frame.Nrm[i] = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitY;
            frame.Valid[i] = true;
        });
        // the grid
        Array.Clear(frame.CellStart);
        for(int i = 0; i < n; i++)
        {
            int h = frame.Valid[i] ? HashOf(frame.Pos[i]) : TableSize - 1;
            frame.Hash[i] = h;
            frame.CellStart[h + 1]++;
        }
        for(int k = 0; k < TableSize; k++)
            frame.CellStart[k + 1] += frame.CellStart[k];
        Array.Copy(frame.CellStart, frame.Fill, TableSize);
        for(int i = 0; i < n; i++)
            frame.CellIdx[frame.Fill[frame.Hash[i]]++] = i;
    }

    private static int HashOf(Vector3 p) => HashOf((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

    private static int HashOf(int x, int y, int z) => (int)(((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791)) % (TableSize - 1));

    /// <summary>
    /// The skin point nearest this place within <paramref name="reach"/> (-1 if none), and how far out from the skin it is
    /// (along that point's normal: negative is inside).
    /// </summary>
    public static int Nearest(Frame f, Vector3 at, float reach, out float outward)
    {
        outward = 0f;
        int cx = (int)MathF.Floor(at.X / Cell), cy = (int)MathF.Floor(at.Y / Cell), cz = (int)MathF.Floor(at.Z / Cell);
        int cells = Math.Clamp((int)MathF.Ceiling(reach / Cell), 1, 2);
        float best = reach * reach;
        int found = -1;
        for(int a = -cells; a <= cells; a++)
            for(int b = -cells; b <= cells; b++)
                for(int c = -cells; c <= cells; c++)
                {
                    int h = HashOf(cx + a, cy + b, cz + c);
                    for(int k = f.CellStart[h]; k < f.CellStart[h + 1]; k++)
                    {
                        int i = f.CellIdx[k];
                        if(!f.Valid[i])
                            continue;
                        float d2 = Vector3.DistanceSquared(at, f.Pos[i]);
                        if(d2 < best)
                        {
                            best = d2;
                            found = i;
                        }
                    }
                }
        if(found >= 0)
            outward = Vector3.Dot(at - f.Pos[found], f.Nrm[found]);
        return found;
    }
}
