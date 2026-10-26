using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Limber.Koi.Mathx;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Limber.Koi.Posing;

/// <summary>
/// Cached description of one actor's skeleton: bone names, hierarchy and the reference pose in
/// model space for every partial (body, face, hair, tail...). Rebuilt whenever the game swaps
/// any partial skeleton (race change, hairstyle change, actor redraw).
/// </summary>
public sealed unsafe class SkeletonModel
{
    public sealed class Partial
    {
        public int Index;
        public nint HkSkeleton;
        public string[] Names = [];
        public short[] Parents = [];
        public Vector3[] RefLocalPos = [];
        public Quaternion[] RefLocalRot = [];
        public Vector3[] RefLocalScale = [];
        public Vector3[] RefModelPos = [];
        public Quaternion[] RefModelRot = [];
        public Vector3[] RefModelScale = [];

        /// <summary>Root bones of this partial and the body (partial 0) bone each one is attached to.</summary>
        public List<(int Root, int BodyParent)> Attachments = [];
    }

    public nint RenderSkeletonPtr { get; }
    public List<Partial> Partials { get; } = [];
    private readonly Dictionary<string, (int Partial, int Bone)> _byName = new(StringComparer.Ordinal);

    public int FacePartial { get; private set; } = -1;

    /// <summary>+1 when the character's left side is +X in model space.</summary>
    public float LeftSign { get; private set; } = 1f;

    /// <summary>+1 when the character faces +Z in model space.</summary>
    public float ForwardSign { get; private set; } = 1f;

    /// <summary>Hip-to-ankle length in the reference pose (model units).</summary>
    public float LegLength { get; private set; } = 1f;

    public bool IsUsable => Partials.Count > 0 && Partials[0].Names.Length > 0;

    private SkeletonModel(RenderSkeleton* skeleton)
    {
        RenderSkeletonPtr = (nint)skeleton;
    }

    public static SkeletonModel? Create(RenderSkeleton* skeleton)
    {
        if (skeleton == null || skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletons == null)
            return null;

        var model = new SkeletonModel(skeleton);
        model.Build(skeleton);
        return model.IsUsable ? model : null;
    }

    /// <summary>True when the live skeleton still matches what this cache was built from.</summary>
    public bool Matches(RenderSkeleton* skeleton)
    {
        if ((nint)skeleton != RenderSkeletonPtr || skeleton->PartialSkeletonCount != Partials.Count)
            return false;
        for (int i = 0; i < Partials.Count; i++)
        {
            var pose = GetPose(skeleton, i);
            var hk = pose != null ? (nint)pose->Skeleton : 0;
            if (hk != Partials[i].HkSkeleton)
                return false;
        }
        return true;
    }

    public static hkaPose* GetPose(RenderSkeleton* skeleton, int partialIndex)
    {
        if (skeleton == null || partialIndex < 0 || partialIndex >= skeleton->PartialSkeletonCount)
            return null;
        var partial = &skeleton->PartialSkeletons[partialIndex];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null)
            return null;
        return pose;
    }

    public bool TryFind(string name, out int partial, out int bone)
    {
        if (_byName.TryGetValue(name, out var v))
        {
            partial = v.Partial;
            bone = v.Bone;
            return true;
        }
        partial = bone = -1;
        return false;
    }

    public bool TryRefPos(string name, out Vector3 pos)
    {
        if (TryFind(name, out var p, out var b))
        {
            pos = Partials[p].RefModelPos[b];
            return true;
        }
        pos = default;
        return false;
    }

    /// <summary>Average reference-pose position of several bones; false if any is missing.</summary>
    public bool TryRefPoint(string[] names, out Vector3 pos)
    {
        pos = Vector3.Zero;
        foreach (var n in names)
        {
            if (!TryRefPos(n, out var p))
                return false;
            pos += p;
        }
        pos /= names.Length;
        return true;
    }

    private void Build(RenderSkeleton* skeleton)
    {
        int count = skeleton->PartialSkeletonCount;
        for (int pi = 0; pi < count; pi++)
        {
            var part = new Partial { Index = pi };
            Partials.Add(part);

            var pose = GetPose(skeleton, pi);
            if (pose == null)
                continue;

            var hk = pose->Skeleton;
            part.HkSkeleton = (nint)hk;
            int n = hk->Bones.Length;
            if (n <= 0 || hk->ReferencePose.Length < n || hk->ParentIndices.Length < n)
                continue;

            part.Names = new string[n];
            part.Parents = new short[n];
            part.RefLocalPos = new Vector3[n];
            part.RefLocalRot = new Quaternion[n];
            part.RefLocalScale = new Vector3[n];
            part.RefModelPos = new Vector3[n];
            part.RefModelRot = new Quaternion[n];
            part.RefModelScale = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                part.Names[i] = hk->Bones[i].Name.String ?? string.Empty;
                part.Parents[i] = hk->ParentIndices[i];
                var t = hk->ReferencePose[i];
                part.RefLocalPos[i] = t.Translation.ToVec();
                part.RefLocalRot[i] = Quaternion.Normalize(t.Rotation.ToQuat());
                part.RefLocalScale[i] = t.Scale.ToVec();
            }

            // Work out where this partial hangs off the body, mirroring how posing tools re-parent
            // partials: a single-root partial uses the connected bone indices, multi-root partials
            // are matched to body bones by name.
            if (pi > 0 && Partials[0].Names.Length > 0)
            {
                var roots = new List<int>();
                for (int i = 0; i < n; i++)
                    if (part.Parents[i] < 0)
                        roots.Add(i);

                var native = &skeleton->PartialSkeletons[pi];
                if (roots.Count == 1)
                {
                    int bodyParent = native->ConnectedParentBoneIndex;
                    int root = native->ConnectedBoneIndex;
                    if (root < 0 || root >= n) root = roots[0];
                    if (bodyParent >= 0 && bodyParent < Partials[0].Names.Length)
                        part.Attachments.Add((root, bodyParent));
                }
                else
                {
                    foreach (var r in roots)
                    {
                        var idx = Array.IndexOf(Partials[0].Names, part.Names[r]);
                        if (idx >= 0)
                            part.Attachments.Add((r, idx));
                    }
                }
            }

            ComputeReferenceModel(part);

            for (int i = 0; i < n; i++)
                _byName.TryAdd(part.Names[i], (pi, i)); // body partial wins name clashes

            if (FacePartial < 0 && pi > 0 && Array.IndexOf(part.Names, "j_f_eye_l") >= 0)
                FacePartial = pi;
        }

        // Model-space conventions of this skeleton, measured rather than assumed.
        if (TryRefPos("j_ude_a_l", out var armL) && TryRefPos("j_ude_a_r", out var armR))
            LeftSign = armL.X - armR.X >= 0 ? 1f : -1f;
        if (TryRefPos("j_asi_d_l", out var ankle) && TryRefPos("j_asi_e_l", out var toe))
            ForwardSign = toe.Z - ankle.Z >= 0 ? 1f : -1f;

        if (TryRefPos("j_asi_a_l", out var thigh) && TryRefPos("j_asi_c_l", out var knee) && TryRefPos("j_asi_d_l", out var ank))
        {
            var len = Vector3.Distance(thigh, knee) + Vector3.Distance(knee, ank);
            if (len > 1e-3f) LegLength = len;
        }
    }

    private void ComputeReferenceModel(Partial part)
    {
        int n = part.Names.Length;
        for (int i = 0; i < n; i++)
        {
            int parent = part.Parents[i];
            if (parent >= 0 && parent < i)
            {
                QMath.Compose(part.RefModelPos[parent], part.RefModelRot[parent], part.RefModelScale[parent],
                    part.RefLocalPos[i], part.RefLocalRot[i], part.RefLocalScale[i],
                    out part.RefModelPos[i], out part.RefModelRot[i], out part.RefModelScale[i]);
                continue;
            }

            // Root bone. Body roots sit at the model origin; other partial roots take the
            // transform of the body bone they are attached to.
            var attached = part.Attachments.FindIndex(a => a.Root == i);
            if (part.Index > 0 && attached >= 0)
            {
                var body = Partials[0];
                var bp = part.Attachments[attached].BodyParent;
                part.RefModelPos[i] = body.RefModelPos[bp];
                part.RefModelRot[i] = body.RefModelRot[bp];
                part.RefModelScale[i] = body.RefModelScale[bp];
            }
            else
            {
                part.RefModelPos[i] = part.RefLocalPos[i];
                part.RefModelRot[i] = part.RefLocalRot[i];
                part.RefModelScale[i] = part.RefLocalScale[i];
            }
        }
    }
}
