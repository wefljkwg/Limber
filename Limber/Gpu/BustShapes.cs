using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Limber.BodyRig;

namespace Limber.Gpu;

/// <summary>
/// Breast size shapes (Koikatsu-style): a top made for Limber carries its breast at several sizes as shape keys
/// ("lmb_bust_000" flat, "lmb_bust_012" mounds ... "lmb_bust_100" huge; the number is the size it stands for), and
/// the size the model has as drawn ("lmb_bustbase_075"). The game never turns these shapes on (they aren't named for
/// any of its attributes). Limber blends between them by the character's size, smoothly, and writes the result into
/// the drawn model's vertices (position, normal, tangent) on the GPU, so the breast grows from a flat chest through
/// small mounds to huge, each in between its own shape. Only this game client's own memory and GPU are touched.
/// </summary>
public static unsafe class BustShapes
{
    public const string KeyPrefix = "lmb_bust_";
    public const string BasePrefix = "lmb_bustbase_";

    private sealed class Model
    {
        public required MdlRigger.MeshLayout Layout;
        public required int TangentAt, TangentStride;
        public required byte TangentType;
        public required byte[] Original;
        public required int[] Vertices;          // the vertices any shape moves
        public required float[] Values;          // each key's size, ascending (the model as drawn among them)
        public required Vector3[][] Positions;   // per key, per moved vertex
        public required Vector3[][] Normals;
        public required uint[][] Tangents;       // the tangent's four bytes, packed
        public required string Report;
    }

    private sealed class Target
    {
        public required nint Address;
        public required string File;
        public required LiveMesh.Found Found;
        public nint Replacement;
        public required Model Model;
        public required byte[] Work;
        public float Shown = float.NaN;
    }

    private static readonly Dictionary<string, Model?> Models = new();
    // One per model file: the game shares a model's vertex buffer between every character wearing it (your character
    // and its GPose copy, say), so there's one shaping of it, not one per character (two used to keep taking the
    // buffer from each other: a search of the model's memory and a new buffer every frame or two, freezing the game).
    private static readonly Dictionary<string, Target> Targets = new();
    private static readonly Dictionary<string, long> NextTry = new();
    private static readonly Dictionary<string, int> Fails = new();
    private static readonly Dictionary<nint, long> NextLook = new();
    private static readonly Dictionary<nint, List<string>> Files = new();
    private static volatile HashSet<nint> _wearers = new();
    private static volatile Dictionary<nint, string> _tops = new();
    private static volatile HashSet<nint> _paused = new();

    /// <summary>Whether a character's size shapes are paused for the GPU soft body.</summary>
    public static bool PausedFor(nint address) => _paused.Contains(address);

    /// <summary>The chest model a character is drawn with (its file's folder and name), for the page.</summary>
    public static string TopOf(nint address) => _tops.TryGetValue(address, out var t) ? t : "";

    public static string Status = "";

    /// <summary>Characters drawn with a shaped top right now (any thread).</summary>
    public static bool Wears(nint address) => _wearers.Contains(address);
    public static int Count => Targets.Count;

    private static volatile Dictionary<nint, float[]> _sizes = new();

    /// <summary>The sizes a character's shaped top has (for the page, any thread), or null.</summary>
    public static float[]? SizesOf(nint address) => _sizes.TryGetValue(address, out var v) ? v : null;

    // ---- Reading a model's shapes

    private static Model? Load(string file, Func<string, byte[]?> readFile)
    {
        if(Models.TryGetValue(file, out var cached))
            return cached;
        Model? model = null;
        try
        {
            var data = readFile(file);
            if(data != null)
                model = Build(data);
        }
        catch(Exception e)
        {
            Status = "Breast shapes: " + e.Message;
        }
        Models[file] = model;
        return model;
    }

    private static Model? Build(byte[] data)
    {
        var (offset, size, layouts) = MdlRigger.Layout(data);
        if(size[0] == 0)
            return null;
        var skins = MdlRigger.Skin(data, 0);
        foreach(var layout in layouts.Where(l => l.Lod == 0))
        {
            var shapes = MdlRigger.MeshShapes(data, 0, layout.Mesh);
            if(!shapes.Any(s => s.Name.StartsWith(KeyPrefix, StringComparison.Ordinal)))
                continue;
            var skin = skins.FirstOrDefault(s => s.Mesh == layout.Mesh);
            if(skin == null)
                continue;
            var original = data.AsSpan((int)offset[0], (int)size[0]).ToArray();
            var (tanAt, tanStride, tanType) = MdlRigger.ElementOf(data, layout.Mesh, 6);
            float baseValue = 75f;
            var keys = new List<(float Value, Dictionary<int, int> Replace)>();
            foreach(var (name, values) in shapes)
            {
                if(name.StartsWith(BasePrefix, StringComparison.Ordinal))
                {
                    if(float.TryParse(name.AsSpan(BasePrefix.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b))
                        baseValue = b;
                    continue;
                }
                if(!name.StartsWith(KeyPrefix, StringComparison.Ordinal)
                   || !float.TryParse(name.AsSpan(KeyPrefix.Length), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    continue;
                var replace = new Dictionary<int, int>();
                foreach(var (index, vertex) in values)
                    if(index < skin.Triangles.Length)
                        replace[skin.Triangles[index]] = vertex;
                keys.Add((value, replace));
            }
            if(keys.Count == 0)
                continue;
            keys.Add((baseValue, new Dictionary<int, int>()));
            keys.Sort((a, b) => a.Value.CompareTo(b.Value));
            var moved = keys.SelectMany(k => k.Replace.Keys).Distinct().OrderBy(v => v).ToArray();

            Vector3 Pos(int v) => MdlRigger.ReadVector(original, layout.PositionAt + v * layout.PositionStride, layout.PositionType);
            Vector3 Nrm(int v) => layout.NormalAt >= 0 ? MdlRigger.ReadVector(original, layout.NormalAt + v * layout.NormalStride, layout.NormalType) : Vector3.UnitZ;
            uint Tan(int v) => tanAt >= 0 && tanType == 8 ? BitConverter.ToUInt32(original, tanAt + v * tanStride) : 0u;

            var positions = new Vector3[keys.Count][];
            var normals = new Vector3[keys.Count][];
            var tangents = new uint[keys.Count][];
            for(int k = 0; k < keys.Count; k++)
            {
                positions[k] = new Vector3[moved.Length];
                normals[k] = new Vector3[moved.Length];
                tangents[k] = new uint[moved.Length];
                for(int i = 0; i < moved.Length; i++)
                {
                    int v = keys[k].Replace.TryGetValue(moved[i], out var r) ? r : moved[i];
                    positions[k][i] = Pos(v);
                    normals[k][i] = Nrm(v);
                    tangents[k][i] = Tan(v);
                }
            }
            return new Model
            {
                Layout = layout, TangentAt = tanAt, TangentStride = tanStride, TangentType = tanType, Original = original,
                Vertices = moved, Values = keys.Select(k => k.Value).ToArray(), Positions = positions, Normals = normals, Tangents = tangents,
                Report = $"{keys.Count} sizes ({string.Join(", ", keys.Select(k => k.Value.ToString("0")))}), {moved.Length} vertices",
            };
        }
        return null;
    }

    // ---- Every frame (render thread)

    /// <summary>
    /// Shapes the breasts of the characters in <paramref name="wanted"/> (their size, 0..100 and a little beyond) when
    /// what they wear has breast shapes; everyone else's are put back as the model has them.
    /// </summary>
    public static void Update(IReadOnlyList<(nint Address, float Size)> wanted, Func<string, byte[]?> readFile)
    {
        var want = new Dictionary<nint, float>();
        var paused = new HashSet<nint>();
        foreach(var (a, s) in wanted)
        {
            // The GPU soft body owns that model's vertices while it runs (both writing it, each would keep taking it
            // back from the other: jerky): the size shapes step aside for it.
            if(LiveSoftBody.IsOn(a))
            {
                paused.Add(a);
                continue;
            }
            want[a] = s;
        }
        _paused = paused;

        // Characters no longer wanted: forget what they wear.
        foreach(var a in Files.Keys.ToList())
            if(!want.ContainsKey(a) && !paused.Contains(a))
            {
                Files.Remove(a);
                NextLook.Remove(a);
            }

        long now = Environment.TickCount64;
        // What each wanted character wears (looked at once a second each: walking the model is not free).
        foreach(var address in want.Keys.Concat(paused).ToList())
        {
            try
            {
                if(NextLook.TryGetValue(address, out var next) && now < next)
                    continue;
                NextLook[address] = now + 1000;
                var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->DrawObject;
                var files = new List<string>();
                if(drawObject != 0)
                    foreach(var (_, file) in LiveMesh.ModelFiles(drawObject))
                    {
                        if(file.EndsWith("_top.mdl", StringComparison.OrdinalIgnoreCase) || file.Contains("_top_", StringComparison.OrdinalIgnoreCase))
                        {
                            var tops = new Dictionary<nint, string>(_tops);
                            var parts = file.Replace('\\', '/').Split('/');
                            tops[address] = parts.Length >= 3 ? string.Join("/", parts[^3..]) : file;
                            _tops = tops;
                        }
                        if(file.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && Load(file, readFile) != null)
                            files.Add(file);
                    }
                Files[address] = files;
            }
            catch(Exception e)
            {
                Status = "Breast shapes: " + e.Message;
            }
        }

        // Each shaped model once: the size of the last character wearing it (a GPose actor comes after the
        // character it copies, so it decides).
        var fileSize = new Dictionary<string, (nint Owner, float Size)>();
        foreach(var (address, size) in want)
            if(Files.TryGetValue(address, out var files))
                foreach(var file in files)
                    fileSize[file] = (address, size);

        // (a model a character with the GPU soft body wears is that soft body's: both would rewrite the same buffer)
        foreach(var a in paused)
            if(Files.TryGetValue(a, out var theirs))
                foreach(var file in theirs)
                    fileSize.Remove(file);

        foreach(var file in Targets.Keys.ToList())
            if(!fileSize.ContainsKey(file))
                Drop(file);

        foreach(var (file, (owner, size)) in fileSize)
        {
            try
            {
                if(!Targets.TryGetValue(file, out var target))
                {
                    if(NextTry.TryGetValue(file, out var when) && now < when)
                        continue;
                    var drawObject = (nint)((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)owner)->DrawObject;
                    if(drawObject == 0)
                        continue;
                    // (not again for a while if it doesn't take: finding the buffer is the costly part)
                    NextTry[file] = now + 3000L * (1 + Math.Min(Fails.GetValueOrDefault(file), 5));
                    if(!Start(owner, drawObject, file, readFile) || !Targets.TryGetValue(file, out target))
                        continue;
                }
                var buffer = target.Replacement != 0 ? target.Replacement : target.Found.Buffer;
                if(!LiveMesh.StillHolds(target.Found, buffer))
                {
                    // redrawn (or something else took the buffer): start again later, not every frame
                    Targets.Remove(file);
                    Fails[file] = Fails.GetValueOrDefault(file) + 1;
                    NextTry[file] = now + 3000L * Math.Min(Fails[file], 6);
                    continue;
                }
                if(MathF.Abs(target.Shown - size) < 0.01f)
                    continue;
                Blend(target, size);
                LiveMesh.Upload(buffer, target.Found.Desc, target.Work);
                target.Shown = size;
                Fails.Remove(file);
            }
            catch(Exception e)
            {
                Status = "Breast shapes: " + e.Message;
            }
        }

        // Who wears a shaped top now (for taking the game's bust scaling off, and the page).
        var wearers = new HashSet<nint>();
        var sizes = new Dictionary<nint, float[]>();
        foreach(var (address, files) in Files)
            foreach(var file in files)
                if(Targets.TryGetValue(file, out var t))
                {
                    wearers.Add(address);
                    sizes[address] = t.Model.Values;
                }
        _wearers = wearers;
        _sizes = sizes;
        if(Targets.Count > 0)
            Status = $"Breast shapes: on for {wearers.Count} character(s).";
    }

    private static bool Start(nint address, nint drawObject, string file, Func<string, byte[]?> readFile)
    {
        var model = Load(file, readFile);
        if(model == null)
            return false;
        var log = new List<string>();
        foreach(var found in LiveMesh.Probe(drawObject, log, f => f == file))
        {
            if(!found.IsVertex || found.Desc.ByteWidth != model.Original.Length)
                continue;
            nint replacement = 0;
            if(found.Desc.Usage == 1) // immutable: an identical writable buffer in its place
            {
                replacement = LiveMesh.Replace(found, model.Original);
                if(replacement == 0)
                    continue;
            }
            Targets[file] = new Target
            {
                Address = address, File = file, Found = found, Replacement = replacement, Model = model,
                Work = (byte[])model.Original.Clone(),
            };
            Status = "Breast shapes: " + model.Report;
            return true;
        }
        Status = "Breast shapes: couldn't find the top's vertices on the GPU (it needs to be drawn).";
        return false;
    }

    private static void Drop(string key)
    {
        if(!Targets.Remove(key, out var target))
            return;
        try
        {
            if(target.Replacement != 0)
                LiveMesh.Restore(target.Found, target.Replacement);
            else if(LiveMesh.StillHolds(target.Found, target.Found.Buffer))
                LiveMesh.Upload(target.Found.Buffer, target.Found.Desc, target.Model.Original);
        }
        catch
        {
            // the model is gone already
        }
    }

    /// <summary>Every shaped top back as its model has it.</summary>
    public static void Stop()
    {
        foreach(var key in Targets.Keys.ToList())
            Drop(key);
        Files.Clear();
        NextLook.Clear();
        NextTry.Clear();
        Fails.Clear();
        _wearers = new();
        _sizes = new();
    }

    private static void Blend(Target target, float size)
    {
        var m = target.Model;
        var values = m.Values;
        // the two sizes either side (past the ends: carried on from the last two, a little)
        int hi = 1;
        while(hi < values.Length - 1 && size > values[hi])
            hi++;
        int lo = hi - 1;
        float span = MathF.Max(values[hi] - values[lo], 1e-3f);
        float t = (size - values[lo]) / span;
        t = Math.Clamp(t, -0.3f, 1.3f);
        var L = m.Layout;
        var work = target.Work;
        for(int i = 0; i < m.Vertices.Length; i++)
        {
            int v = m.Vertices[i];
            var p = Vector3.Lerp(m.Positions[lo][i], m.Positions[hi][i], t);
            MdlRigger.WriteVector(work, L.PositionAt + v * L.PositionStride, L.PositionType, p);
            if(L.NormalAt >= 0)
            {
                var n = Vector3.Lerp(m.Normals[lo][i], m.Normals[hi][i], t);
                float len = n.Length();
                if(len > 1e-6f)
                    MdlRigger.WriteVector(work, L.NormalAt + v * L.NormalStride, L.NormalType, n / len);
            }
            if(m.TangentAt >= 0 && m.TangentType == 8)
            {
                uint a = m.Tangents[lo][i], b = m.Tangents[hi][i];
                var ta = new Vector3((a & 0xFF) / 127.5f - 1f, ((a >> 8) & 0xFF) / 127.5f - 1f, ((a >> 16) & 0xFF) / 127.5f - 1f);
                var tb = new Vector3((b & 0xFF) / 127.5f - 1f, ((b >> 8) & 0xFF) / 127.5f - 1f, ((b >> 16) & 0xFF) / 127.5f - 1f);
                var tt = Vector3.Lerp(ta, tb, t);
                float len = tt.Length();
                if(len > 1e-6f)
                    tt /= len;
                byte Enc(float x) => (byte)Math.Clamp((int)MathF.Round((x + 1f) * 127.5f), 0, 255);
                int at = m.TangentAt + v * m.TangentStride;
                work[at] = Enc(tt.X);
                work[at + 1] = Enc(tt.Y);
                work[at + 2] = Enc(tt.Z);
                work[at + 3] = (byte)((t < 0.5f ? a : b) >> 24); // (its handedness)
            }
        }
    }
}
