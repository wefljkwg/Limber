using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Limber.BodyRig;
using Limber.Gpu;

namespace Limber.Koi.Props;

/// <summary>
/// Placed Koikatsu props, each with a model of its own (so each can be coloured and posed on its own): its colours
/// (per material, up to three each, as Koikatsu's colour mask splits them), its bones turned by hand, its blend shapes
/// and its dynamic bone chains swinging. Colours make its textures again (it's drawn anew); bones and shapes move its
/// vertices on the graphics card every frame, the way Limber's live rig does.
/// </summary>
public static unsafe class KoiPropRuntime
{
    public sealed class Instance
    {
        public required string Key;
        public required KoiRig Rig;
        public required int Number;
        public Guid Prop;
        public int Version;
        public Vector4[][] Colours = [];
        public readonly Dictionary<string, float> Shapes = new();
        public Vector3[] Turn = [];          // each bone, degrees (yaw, pitch, roll) on top of how it rests
        public bool Dynamics = true;
        // How it's shaded (made into its textures): brightness, contrast, colour strength, and its shine.
        public float Brightness = 1f, Contrast = 1f, Saturation = 1f, Shine = 0.16f, Roughness = 0.5f;
        public bool FlipFaces, FlipNormals; // fixes to try when it's lit wrongly
        public Vector3 Glow;                // how much it lights itself (0: not at all)
        public float Bumps = 1f;            // how much of its own normal map (bumps) shows (0: smooth)
        public bool BumpsFlipGreen = true;  // Unity's normal maps have green the other way up from the game's
        public readonly Dictionary<uint, float[]> Constants = new(); // its materials' shader values, changed by hand
        public float DynamicsStrength = 1f;
        public string Path = "";
        public List<string> Files = new();
        public MdlLayout? Layout;
        // the model's buffer, swapped for Limber's copy
        public LiveMesh.Found? Vb;
        public nint VbCopy;
        public byte[] Vertices = [];
        public int Tries, Wait;
        public bool Failed, Dirty = true;
        public string Note = "loading";
        // dynamic bone chains: each bone's place now and last step (prefab space), and their lengths
        public Vector3[] Now = [], Last = [];
        public bool Settled;
    }

    private static readonly Dictionary<Guid, Instance> Instances = new();
    /// <summary>Held while the copies change (placing is on the framework thread, posing on the render thread).</summary>
    public static readonly object Gate = new();
    private static readonly Dictionary<string, KoiRig?> Rigs = new();
    private static int _next = 1;
    public static HousingProps? Housing;

    private static string RuntimeDir => System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "koikatsu_runtime");

    /// <summary>Plugin start: last session's copies cleared away.</summary>
    public static void Start()
    {
        try
        {
            if(Directory.Exists(RuntimeDir))
                Directory.Delete(RuntimeDir, true);
        }
        catch
        {
        }
        HousingProps.Instancer = Instance_;
    }

    public static Instance? For(Guid prop) => Instances.TryGetValue(prop, out var i) ? i : null;

    private static KoiRig? RigOf(string key)
    {
        if(!Rigs.TryGetValue(key, out var rig))
        {
            try
            {
                rig = KoiRig.Load(KoiProps.RigPath(key));
            }
            catch
            {
                rig = null;
            }
            Rigs[key] = rig;
        }
        return rig;
    }

    // A placed prop: if it's a Koikatsu one with its bones and colours saved, its own copy of the model.
    private static string? Instance_(SpawnedProp prop)
    {
        lock(Gate)
            return MakeInstance(prop);
    }

    private static string? MakeInstance(SpawnedProp prop)
    {
        string path = prop.Item.Sgb ?? "";
        if(!path.StartsWith(KoiProps.GameRoot + "/", StringComparison.OrdinalIgnoreCase))
            return null;
        var imported = KoiProps.Imported.FirstOrDefault(p => p.Model.Equals(path, StringComparison.OrdinalIgnoreCase));
        if(imported == null || RigOf(imported.Key) is not { } rig)
            return null;
        var inst = new Instance
        {
            Key = imported.Key, Rig = rig, Number = _next++, Prop = prop.Id,
            Colours = rig.Materials.Select(m => (Vector4[])m.Colours.Clone()).ToArray(),
            Turn = new Vector3[rig.Bones.Count],
        };
        try
        {
            if(!Write(inst))
                return null;
        }
        catch(Exception e)
        {
            Plugin.Log.Information($"Limber: couldn't make a copy of {imported.Name} ({e.Message})");
            return null;
        }
        Instances[prop.Id] = inst;
        return inst.Path;
    }

    // Its files for this version (colours): textures, materials and the model, written, and given to the mod manager.
    private static bool Write(Instance inst)
    {
        if(KoiProps.Template() is not { } template)
            return false;
        var (mdl, mtrl, slots) = template;
        string folder = $"{KoiProps.GameRoot}/{inst.Key}";
        string tag = $"i{inst.Number}v{inst.Version}";
        var dir = System.IO.Path.Combine(RuntimeDir, $"{inst.Key}_{tag}");
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, byte[]>();
        var materials = new List<string>();
        // (its own shine: the specular texture's strength, and how rough the surface is)
        string spec = $"{folder}/texture/koi_{inst.Key}_{tag}_s.tex";
        byte shine = (byte)Math.Clamp(inst.Shine * 255f, 0f, 255f), rough = (byte)Math.Clamp(inst.Roughness * 255f, 0f, 255f);
        files[spec] = TexWriter.Solid(shine, rough, shine, 255);
        for(int m = 0; m < inst.Rig.Materials.Count; m++)
        {
            var material = inst.Rig.Materials[m];
            string tex = $"{folder}/texture/koi_{inst.Key}_{m}_{tag}_d.tex";
            string mat = $"{folder}/material/koi_{inst.Key}_{m}_{tag}.mtrl";
            var px = KoiRig.Tint(material, inst.Colours[m]);
            Shade(px, inst.Brightness, inst.Contrast, inst.Saturation);
            files[tex] = TexWriter.Bgra(px, material.W, material.H);
            // (its bumps: its own normal map, as strong as asked, else flat)
            string normal = KoiProps.FlatNormal;
            if(inst.Bumps > 0.001f && KoiRig.NormalMap(material, inst.Bumps, inst.BumpsFlipGreen) is { } bumps)
            {
                normal = $"{folder}/texture/koi_{inst.Key}_{m}_{tag}_n.tex";
                files[normal] = TexWriter.Bgra(bumps, material.W, material.H);
            }
            var bytes = MtrlBuilder.SetConstant(MtrlBuilder.With(mtrl, slots.Select(s => s == "d" ? tex : s == "n" ? normal : spec).ToList()),
                MtrlBuilder.EmissiveColor, [inst.Glow.X, inst.Glow.Y, inst.Glow.Z]);
            foreach(var (id, values) in inst.Constants)
                if(id != MtrlBuilder.EmissiveColor)
                    bytes = MtrlBuilder.SetConstant(bytes, id, values);
            files[mat] = bytes;
            materials.Add(mat);
        }
        string model = $"{folder}/bgparts/koi_{inst.Key}_{tag}.mdl";
        MdlBuilder.FlipFaces = inst.FlipFaces;
        MdlBuilder.FlipNormals = inst.FlipNormals;
        MdlLayout? layout;
        try
        {
            files[model] = MdlBuilder.Build(mdl, inst.Rig.Meshes, materials, out layout);
        }
        finally
        {
            MdlBuilder.FlipFaces = MdlBuilder.FlipNormals = false;
        }
        // (the old version's files dropped)
        foreach(var old in inst.Files)
            KoiProps.Extra.Remove(old);
        inst.Files.Clear();
        foreach(var (game, bytes) in files)
        {
            var disk = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(game));
            File.WriteAllBytes(disk, bytes);
            KoiProps.Extra[game] = disk;
            inst.Files.Add(game);
        }
        KoiProps.Register();
        inst.Path = model;
        inst.Layout = layout;
        inst.Vb = null;
        inst.VbCopy = 0;
        inst.Tries = 0;
        inst.Wait = 0;
        inst.Failed = layout == null;
        inst.Note = layout == null ? "too big to move live (a mesh over 65535 vertices)" : "loading";
        inst.Vertices = layout?.Block.ToArray() ?? [];
        inst.Dirty = true;
        inst.Settled = false;
        return true;
    }

    // Brightness, contrast and colour strength into a texture's pixels (RGBA).
    private static void Shade(byte[] px, float brightness, float contrast, float saturation)
    {
        if(brightness == 1f && contrast == 1f && saturation == 1f)
            return;
        for(int i = 0; i + 3 < px.Length; i += 4)
        {
            var c = new Vector3(px[i], px[i + 1], px[i + 2]) / 255f;
            float grey = Vector3.Dot(c, new Vector3(0.299f, 0.587f, 0.114f));
            c = Vector3.Lerp(new Vector3(grey), c, saturation);
            c = (c - new Vector3(0.5f)) * contrast + new Vector3(0.5f);
            c *= brightness;
            px[i] = (byte)Math.Clamp(c.X * 255f + 0.5f, 0f, 255f);
            px[i + 1] = (byte)Math.Clamp(c.Y * 255f + 0.5f, 0f, 255f);
            px[i + 2] = (byte)Math.Clamp(c.Z * 255f + 0.5f, 0f, 255f);
        }
    }

    /// <summary>Its colours changed: made again with them (framework thread).</summary>
    public static void Recolour(Instance inst)
    {
        lock(Gate)
            RecolourNow(inst);
    }

    private static void RecolourNow(Instance inst)
    {
        Release(inst);
        inst.Version++;
        if(Write(inst))
            Housing?.ReplaceModel(inst.Prop, inst.Path);
    }

    private static void Release(Instance inst)
    {
        if(inst.Vb != null && inst.VbCopy != 0)
        {
            try
            {
                LiveMesh.Restore(inst.Vb, inst.VbCopy);
            }
            catch
            {
            }
        }
        inst.Vb = null;
        inst.VbCopy = 0;
    }

    /// <summary>Limber unloading: every model's own buffer back.</summary>
    public static void Stop()
    {
        lock(Gate)
            StopNow();
    }

    private static void StopNow()
    {
        foreach(var inst in Instances.Values)
            Release(inst);
        Instances.Clear();
        HousingProps.Instancer = null;
    }

    // ---- Every frame (render thread): bones and shapes into the buffers

    public static void Update()
    {
        if(Instances.Count == 0 || Housing == null)
            return;
        if(!System.Threading.Monitor.TryEnter(Gate))
            return;
        try
        {
            UpdateNow();
        }
        finally
        {
            System.Threading.Monitor.Exit(Gate);
        }
    }

    private static void UpdateNow()
    {
        if(Housing == null)
            return;
        var placed = Housing.Spawned.ToDictionary(p => p.Id);
        foreach(var (id, inst) in Instances.ToArray())
        {
            if(!placed.TryGetValue(id, out var prop))
            {
                Release(inst);
                foreach(var f in inst.Files)
                    KoiProps.Extra.Remove(f);
                Instances.Remove(id);
                continue;
            }
            if(inst.Failed || prop.Objects.Length == 0 || prop.Objects[0] == null || !prop.Ready[0])
                continue;
            try
            {
                if(inst.Vb == null)
                {
                    TakeOver(inst, prop);
                    continue;
                }
                if(!LiveMesh.StillHolds(inst.Vb, inst.VbCopy))
                {
                    inst.Vb = null;
                    inst.VbCopy = 0;
                    continue;
                }
                bool moving = inst.Dynamics && inst.Rig.Chains.Count > 0 && !inst.Settled;
                if(!inst.Dirty && !moving)
                    continue;
                inst.Dirty = false;
                Pose(inst, prop);
                LiveMesh.Upload(inst.VbCopy, inst.Vb.Desc, inst.Vertices);
                inst.Note = "posable";
            }
            catch(Exception e)
            {
                inst.Failed = true;
                inst.Note = "stopped: " + e.Message;
            }
        }
    }

    private static void TakeOver(Instance inst, SpawnedProp prop)
    {
        if(inst.Wait-- > 0)
            return;
        inst.Wait = 30;
        var obj = prop.Objects[0];
        var found = LiveMesh.ProbeFrom([(nint)obj, (nint)obj->ModelResourceHandle], inst.Path);
        int size = inst.Layout!.Block.Length;
        uint rounded = (uint)((size + 127) / 128 * 128);
        var vb = found.FirstOrDefault(f => f.IsVertex && f.Desc.ByteWidth == size) ?? found.FirstOrDefault(f => f.IsVertex && f.Desc.ByteWidth == rounded);
        if(vb == null)
        {
            if(++inst.Tries >= 20)
            {
                inst.Failed = true;
                inst.Note = "its vertex buffer wasn't found";
            }
            return;
        }
        if(inst.Vertices.Length < vb.Desc.ByteWidth)
        {
            var bigger = new byte[vb.Desc.ByteWidth];
            inst.Vertices.CopyTo(bigger, 0);
            inst.Vertices = bigger;
        }
        inst.VbCopy = LiveMesh.Replace(vb, inst.Vertices);
        if(inst.VbCopy == 0)
        {
            inst.Failed = true;
            inst.Note = "its buffer couldn't be swapped";
            return;
        }
        inst.Vb = vb;
        inst.Dirty = true;
    }

    // ---- Posing

    private static Matrix4x4 TurnOf(Vector3 degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        return Matrix4x4.CreateFromYawPitchRoll(r.X, r.Y, r.Z);
    }

    private static void Pose(Instance inst, SpawnedProp prop)
    {
        var rig = inst.Rig;
        int n = rig.Bones.Count;
        var world = new Matrix4x4[n];
        for(int b = 0; b < n; b++)
        {
            var bone = rig.Bones[b];
            var local = inst.Turn[b] == Vector3.Zero ? bone.RestLocal : TurnOf(inst.Turn[b]) * bone.RestLocal;
            world[b] = bone.Parent >= 0 && bone.Parent < b ? local * world[bone.Parent] : bone.Parent < 0 ? local : local * world[bone.Parent];
        }
        if(inst.Dynamics && rig.Chains.Count > 0)
            Swing(inst, prop, world);
        var skin = new Matrix4x4[n];
        for(int b = 0; b < n; b++)
            skin[b] = Matrix4x4.Invert(rig.Bones[b].Rest, out var inv) ? inv * world[b] : Matrix4x4.Identity;

        var t = KoiProps.Template()!.Value.Mdl;
        var layout = inst.Layout!;
        for(int k = 0; k < rig.Meshes.Count && k < layout.Meshes.Count; k++)
        {
            var mesh = rig.Meshes[k];
            var (offsets, count) = layout.Meshes[k];
            // (blend shapes first, in the rest pose)
            Vector3[]? moved = null;
            foreach(var (name, deltas) in mesh.Shapes)
            {
                float w = inst.Shapes.GetValueOrDefault(name) / 100f;
                if(w == 0f)
                    continue;
                moved ??= mesh.P.ToArray();
                foreach(var (v, d) in deltas)
                    if(v < moved.Length)
                        moved[v] += d * w;
            }
            for(int v = 0; v < count && v < mesh.P.Count; v++)
            {
                var p = moved != null ? moved[v] : mesh.P[v];
                var nrm = mesh.N[v];
                // (the game's space back into Unity's for its bones, then out again: x mirrored)
                var pu = new Vector3(-p.X, p.Y, p.Z);
                var nu = new Vector3(-nrm.X, nrm.Y, nrm.Z);
                var (b0, b1, b2, b3) = mesh.Bones[v];
                var wt = mesh.Weights[v];
                var q = Vector3.Zero;
                var qn = Vector3.Zero;
                void Add(int bone, float weight)
                {
                    if(weight <= 0f || bone < 0 || bone >= n)
                        return;
                    q += Vector3.Transform(pu, skin[bone]) * weight;
                    qn += Vector3.TransformNormal(nu, skin[bone]) * weight;
                }
                Add(b0, wt.X);
                Add(b1, wt.Y);
                Add(b2, wt.Z);
                Add(b3, wt.W);
                float total = wt.X + wt.Y + wt.Z + wt.W;
                if(total > 1e-5f && MathF.Abs(total - 1f) > 1e-3f)
                {
                    q /= total;
                    qn /= total;
                }
                var outP = new Vector3(-q.X, q.Y, q.Z);
                var outN = qn.LengthSquared() > 1e-12f ? Vector3.Normalize(new Vector3(-qn.X, qn.Y, qn.Z)) : nrm;
                if(inst.FlipNormals)
                    outN = -outN;
                foreach(var (s, o, type, usage) in t.Elements)
                {
                    if(s > 2 || t.Strides[s] == 0)
                        continue;
                    int at = (int)offsets[s] + v * t.Strides[s] + o;
                    if(usage == 0)
                        WriteVec(inst.Vertices, at, type, outP, 1f, false);
                    else if(usage == 3)
                        WriteVec(inst.Vertices, at, type, outN, 0f, true);
                }
            }
        }
    }

    private static void WriteVec(byte[] into, int at, byte type, Vector3 v, float w, bool unit)
    {
        var span = into.AsSpan(at);
        switch(type)
        {
            case 2:
                BitConverter.TryWriteBytes(span, v.X); BitConverter.TryWriteBytes(span[4..], v.Y); BitConverter.TryWriteBytes(span[8..], v.Z);
                break;
            case 3:
                BitConverter.TryWriteBytes(span, v.X); BitConverter.TryWriteBytes(span[4..], v.Y); BitConverter.TryWriteBytes(span[8..], v.Z);
                BitConverter.TryWriteBytes(span[12..], w);
                break;
            case 14:
                BitConverter.TryWriteBytes(span, (Half)v.X); BitConverter.TryWriteBytes(span[2..], (Half)v.Y);
                BitConverter.TryWriteBytes(span[4..], (Half)v.Z); BitConverter.TryWriteBytes(span[6..], (Half)w);
                break;
            case 8:
            case 5:
                if(unit)
                {
                    var u = v * 0.5f + new Vector3(0.5f);
                    span[0] = (byte)Math.Clamp((int)(u.X * 255f + 0.5f), 0, 255);
                    span[1] = (byte)Math.Clamp((int)(u.Y * 255f + 0.5f), 0, 255);
                    span[2] = (byte)Math.Clamp((int)(u.Z * 255f + 0.5f), 0, 255);
                }
                break;
        }
    }

    // ---- Dynamic bones (Koikatsu's swinging chains): each bone below a chain's root a point that swings and springs
    // back toward where its parent holds it, then the bones turned to follow the points.

    private static void Swing(Instance inst, SpawnedProp prop, Matrix4x4[] world)
    {
        var rig = inst.Rig;
        int n = rig.Bones.Count;
        if(inst.Now.Length != n)
        {
            inst.Now = new Vector3[n];
            inst.Last = new Vector3[n];
            for(int b = 0; b < n; b++)
                inst.Now[b] = inst.Last[b] = world[b].Translation;
        }
        // (which bones each chain moves: everything below its root)
        var inChain = new int[n];
        Array.Fill(inChain, -1);
        for(int c = 0; c < rig.Chains.Count; c++)
            for(int b = 0; b < n; b++)
                for(int a = rig.Bones[b].Parent; a >= 0; a = rig.Bones[a].Parent)
                    if(a == rig.Chains[c].Root)
                    {
                        inChain[b] = c;
                        break;
                    }
        // (down, in the prop's own space: the prop's turn taken out)
        var turn = prop.Turn * (MathF.PI / 180f);
        var inverse = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(turn.X, turn.Y, turn.Z));
        var down = Vector3.Transform(-Vector3.UnitY, inverse);
        down.X = -down.X; // (Unity's space)
        float strength = Math.Clamp(inst.DynamicsStrength, 0f, 2f);
        float moved = 0f;
        for(int b = 0; b < n; b++)
        {
            int c = inChain[b];
            int parent = rig.Bones[b].Parent;
            if(c < 0 || parent < 0)
            {
                inst.Now[b] = inst.Last[b] = world[b].Translation;
                continue;
            }
            var chain = rig.Chains[c];
            // where its parent (as it is now) would hold it
            var parentWorld = world[parent];
            parentWorld.Translation = inst.Now[parent];
            var target = Vector3.Transform(rig.Bones[b].RestLocal.Translation, parentWorld);
            float length = rig.Bones[b].RestLocal.Translation.Length() * new Vector3(parentWorld.M11, parentWorld.M12, parentWorld.M13).Length();
            var velocity = (inst.Now[b] - inst.Last[b]) * (1f - Math.Clamp(chain.Damping, 0f, 1f));
            inst.Last[b] = inst.Now[b];
            var gravity = (down * chain.Gravity.Length() + chain.Force) * strength;
            var p = inst.Now[b] + velocity + gravity;
            p += (target - p) * Math.Clamp(chain.Elasticity, 0f, 1f);
            float reach = length * 2f * (1f - Math.Clamp(chain.Stiffness, 0f, 1f));
            var off = p - target;
            if(off.Length() > reach && reach > 0f)
                p = target + Vector3.Normalize(off) * reach;
            var fromParent = p - inst.Now[parent];
            if(fromParent.LengthSquared() > 1e-12f && length > 0f)
                p = inst.Now[parent] + Vector3.Normalize(fromParent) * length;
            moved = MathF.Max(moved, Vector3.Distance(p, inst.Now[b]));
            inst.Now[b] = p;
        }
        inst.Settled = moved < 1e-5f;
        // The bones turned to point at their swung children, top down.
        for(int b = 0; b < n; b++)
        {
            int parent = rig.Bones[b].Parent;
            if(parent >= 0)
            {
                var local = inst.Turn[b] == Vector3.Zero ? rig.Bones[b].RestLocal : TurnOf(inst.Turn[b]) * rig.Bones[b].RestLocal;
                world[b] = local * world[parent];
            }
            if(inChain[b] < 0 && !rig.Chains.Any(c => c.Root == b))
                continue;
            int child = -1;
            for(int k = b + 1; k < n; k++)
                if(rig.Bones[k].Parent == b && inChain[k] >= 0)
                {
                    child = k;
                    break;
                }
            if(child < 0)
                continue;
            var head = world[b].Translation;
            var restChild = Vector3.Transform(rig.Bones[child].RestLocal.Translation, world[b]);
            var from = restChild - head;
            var to = inst.Now[child] - head;
            if(from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f)
                continue;
            var spin = MathUtilKoi.FromTo(Vector3.Normalize(from), Vector3.Normalize(to));
            world[b] = world[b] * Matrix4x4.CreateTranslation(-head) * Matrix4x4.CreateFromQuaternion(spin) * Matrix4x4.CreateTranslation(head);
        }
    }

    // ---- The Props page: this prop's colours, blend shapes, bones and dynamic bones

    public static void DrawEditor(SpawnedProp prop) => KoiPropEditor.Draw(prop);
}

internal static class MathUtilKoi
{
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if(d > 0.999999f)
            return Quaternion.Identity;
        if(d < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if(axis.LengthSquared() < 1e-6f)
                axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1f + d));
    }
}
