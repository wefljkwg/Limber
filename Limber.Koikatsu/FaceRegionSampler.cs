using System;
using System.Collections.Generic;
using Limber.Protocol;
using UnityEngine;
using Joint = Limber.Protocol.Joint;

namespace Limber.Koikatsu
{
    /// <summary>
    /// Measures Koikatsu facial expressions as movement of ~20 points on the face (eyelids, brows,
    /// lips, mouth corners, cheeks), so FFXIV can reproduce any expression without per-pattern tables.
    ///
    /// Koikatsu expressions are blend shapes. At setup, for every region we pick the face-mesh vertices
    /// around a named face bone and pre-compute how far each blend shape moves them (in a head-rigid
    /// face frame). Each frame the region displacement is then just the weighted sum of the current
    /// blend-shape weights - cheap, and it covers every eye/mouth/brow pattern, blinking, lip sync and
    /// anything another plugin does to the face shapes.
    /// </summary>
    internal sealed class FaceRegionSampler
    {
        private const int RegionCount = (int)FaceRegion.Count;

        private sealed class MeshEntry
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            /// <summary>[shape][region] displacement at weight 100, already normalized into face units.</summary>
            public Vector3[][] Contribution;
            public int[] ActiveShapes;
        }

        private readonly List<MeshEntry> _meshes = new List<MeshEntry>();
        private readonly Vector3[] _regionPos = new Vector3[RegionCount];
        private readonly bool[] _regionValid = new bool[RegionCount];
        private readonly Vector3[] _disp = new Vector3[RegionCount];
        private ChaControl _owner;
        private GameObject _ownerHead;
        private bool _failed;
        private float _retryAt;

        public bool IsReady { get { return _meshes.Count > 0; } }
        public Vector3[] RegionPos { get { return _regionPos; } }
        public bool[] RegionValid { get { return _regionValid; } }

        public void Clear()
        {
            _meshes.Clear();
            for (int i = 0; i < RegionCount; i++)
                _regionValid[i] = false;
            _owner = null;
            _ownerHead = null;
            _failed = false;
        }

        /// <summary>Rebuild when the character, its head object or any face mesh changed.</summary>
        public void EnsureFor(ChaControl chara, BoneCache bones)
        {
            bool stale = _owner != chara || _ownerHead != chara.objHead;
            if (!stale)
            {
                for (int i = 0; i < _meshes.Count; i++)
                {
                    var m = _meshes[i];
                    if (m.Renderer == null || m.Renderer.sharedMesh != m.Mesh) { stale = true; break; }
                }
            }
            if (!stale && (IsReady || (_failed && Time.unscaledTime < _retryAt)))
                return;

            Clear();
            _owner = chara;
            _ownerHead = chara.objHead;
            try
            {
                Build(chara, bones);
            }
            catch (Exception e)
            {
                LimberSenderPlugin.Logger.LogDebug("Limber: face sampler setup failed: " + e.Message);
                _meshes.Clear();
            }
            if (!IsReady)
            {
                _failed = true;
                _retryAt = Time.unscaledTime + 3f;
            }
        }

        /// <summary>Current displacement of every region (face units). Check RegionValid for each entry.</summary>
        public Vector3[] Sample()
        {
            for (int k = 0; k < RegionCount; k++)
                _disp[k] = Vector3.zero;

            for (int m = 0; m < _meshes.Count; m++)
            {
                var entry = _meshes[m];
                var r = entry.Renderer;
                if (r == null)
                    continue;
                var shapes = entry.ActiveShapes;
                for (int i = 0; i < shapes.Length; i++)
                {
                    int s = shapes[i];
                    float w = r.GetBlendShapeWeight(s) * 0.01f;
                    if (w == 0f)
                        continue;
                    var c = entry.Contribution[s];
                    for (int k = 0; k < RegionCount; k++)
                        _disp[k] += c[k] * w;
                }
            }
            return _disp;
        }

        // ------------------------------------------------------------------------------------

        private void Build(ChaControl chara, BoneCache bones)
        {
            if (chara.objHead == null)
                return;

            // Face frame from the eyes and mouth.
            var eyeL = bones.Get(Joint.EyeL);
            var eyeR = bones.Get(Joint.EyeR);
            var head = bones.Get(Joint.Head);
            if (eyeL == null || eyeR == null || head == null)
                return;

            var named = new Dictionary<string, Transform>();
            foreach (var t in chara.objHeadBone != null
                         ? chara.objHeadBone.GetComponentsInChildren<Transform>(true)
                         : new Transform[0])
            {
                if (!named.ContainsKey(t.name))
                    named.Add(t.name, t);
            }

            Vector3 pEyeL = eyeL.position, pEyeR = eyeR.position;
            Vector3 eyeMid = (pEyeL + pEyeR) * 0.5f;
            float d = Vector3.Distance(pEyeL, pEyeR);
            if (d < 1e-5f)
                return;
            Vector3 right = (pEyeR - pEyeL) / d;

            Vector3 mouthL, mouthR, lipUp, lipLow;
            bool hasMouthL = TryPos(named, "cf_J_Mouth_L", out mouthL);
            bool hasMouthR = TryPos(named, "cf_J_Mouth_R", out mouthR);
            bool hasLipUp = TryPos(named, "cf_J_Mouthup", out lipUp);
            bool hasLipLow = TryPos(named, "cf_J_MouthLow", out lipLow);

            // Approximate "down" from the head bone before we know the mouth.
            Vector3 roughUp = head.up;
            Vector3 mouthMid;
            if (hasLipUp && hasLipLow) mouthMid = (lipUp + lipLow) * 0.5f;
            else if (hasMouthL && hasMouthR) mouthMid = (mouthL + mouthR) * 0.5f;
            else mouthMid = eyeMid - roughUp * (0.9f * d);

            Vector3 up = Vector3.ProjectOnPlane(eyeMid - mouthMid, right);
            float em = up.magnitude;
            if (em < 1e-5f)
                return;
            up /= em;
            Vector3 fwd = Vector3.Cross(right, up).normalized;
            if (Vector3.Dot(fwd, eyeMid - head.position) < 0f)
                fwd = -fwd;

            if (!hasMouthL) mouthL = mouthMid - right * (0.22f * d);
            if (!hasMouthR) mouthR = mouthMid + right * (0.22f * d);
            if (!hasLipUp) lipUp = mouthMid + up * (0.06f * em);
            if (!hasLipLow) lipLow = mouthMid - up * (0.06f * em);

            // Region anchors (world space) and the scale used to normalize each one's movement.
            var anchor = new Vector3[RegionCount];
            var lidScale = new float[RegionCount];
            var have = new bool[RegionCount];

            SetLids(named, "L", pEyeL, up, d, anchor, have, lidScale, FaceRegion.LidUpL, FaceRegion.LidLowL);
            SetLids(named, "R", pEyeR, up, d, anchor, have, lidScale, FaceRegion.LidUpR, FaceRegion.LidLowR);
            SetBrow(named, "L", pEyeL, up, right, d, em, anchor, have, FaceRegion.BrowInL, FaceRegion.BrowMidL, FaceRegion.BrowOutL, +1f);
            SetBrow(named, "R", pEyeR, up, right, d, em, anchor, have, FaceRegion.BrowInR, FaceRegion.BrowMidR, FaceRegion.BrowOutR, -1f);

            Put(anchor, have, FaceRegion.MouthCornerL, mouthL);
            Put(anchor, have, FaceRegion.MouthCornerR, mouthR);
            Put(anchor, have, FaceRegion.LipUp, lipUp);
            Put(anchor, have, FaceRegion.LipLow, lipLow);
            Put(anchor, have, FaceRegion.LipUpL, Vector3.Lerp(lipUp, mouthL, 0.5f));
            Put(anchor, have, FaceRegion.LipUpR, Vector3.Lerp(lipUp, mouthR, 0.5f));
            Put(anchor, have, FaceRegion.LipLowL, Vector3.Lerp(lipLow, mouthL, 0.5f));
            Put(anchor, have, FaceRegion.LipLowR, Vector3.Lerp(lipLow, mouthR, 0.5f));

            Vector3 cheek;
            Put(anchor, have, FaceRegion.CheekL, TryPos(named, "cf_J_CheekUp_L", out cheek) ? cheek
                : Vector3.Lerp(pEyeL, mouthL, 0.5f) - right * (0.15f * d));
            Put(anchor, have, FaceRegion.CheekR, TryPos(named, "cf_J_CheekUp_R", out cheek) ? cheek
                : Vector3.Lerp(pEyeR, mouthR, 0.5f) + right * (0.15f * d));

            // Region positions in face units (sent so FFXIV can match its own bones to them).
            for (int k = 0; k < RegionCount; k++)
            {
                var rel = anchor[k] - eyeMid;
                _regionPos[k] = new Vector3(Vector3.Dot(rel, right) / d, Vector3.Dot(rel, up) / em, Vector3.Dot(rel, fwd) / d);
            }

            // Find the face meshes.
            SkinnedMeshRenderer face = null, brows = null;
            foreach (var smr in chara.objHead.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                if (face == null && smr.name == "cf_O_face") face = smr;
                else if (brows == null && smr.name == "cf_O_mayuge") brows = smr;
            }
            if (face == null)
                return;

            // Search radius per region (world units).
            var radius = new float[RegionCount];
            for (int k = 0; k < RegionCount; k++)
            {
                var region = (FaceRegion)k;
                if (IsLid(region)) radius[k] = Mathf.Max(0.25f * lidScale[k], 0.03f * d);
                else if (IsBrow(region)) radius[k] = 0.12f * d;
                else if (region == FaceRegion.CheekL || region == FaceRegion.CheekR) radius[k] = 0.14f * d;
                else radius[k] = 0.07f * d;
            }

            var frame = new FaceFrame { Right = right, Up = up, Fwd = fwd, D = d, Em = em, LidScale = lidScale };

            // Brows come from the eyebrow mesh when it exists, everything else from the face mesh.
            var faceRegions = new bool[RegionCount];
            var browRegions = new bool[RegionCount];
            for (int k = 0; k < RegionCount; k++)
            {
                if (!have[k]) continue;
                if (IsBrow((FaceRegion)k) && brows != null) browRegions[k] = true;
                else faceRegions[k] = true;
            }

            var faceEntry = Measure(face, anchor, radius, faceRegions, fwd, frame);
            if (faceEntry != null) _meshes.Add(faceEntry);
            if (brows != null)
            {
                var browEntry = Measure(brows, anchor, radius, browRegions, fwd, frame);
                if (browEntry != null) _meshes.Add(browEntry);
            }
        }

        private struct FaceFrame
        {
            public Vector3 Right, Up, Fwd;
            public float D, Em;
            public float[] LidScale;
        }

        private MeshEntry Measure(SkinnedMeshRenderer smr, Vector3[] anchor, float[] radius, bool[] use,
            Vector3 fwd, FaceFrame frame)
        {
            var mesh = smr.sharedMesh;
            int shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0)
                return null;

            Vector3[] verts = mesh.vertices; // empty if the mesh isn't readable
            int n = verts.Length;
            if (n == 0)
                return null;
            BoneWeight[] weights = mesh.boneWeights;
            Matrix4x4[] bindposes = mesh.bindposes;
            Transform[] bonesArr = smr.bones;
            Vector3[] normals = mesh.normals;

            // Skin each vertex with its strongest bone - accurate enough for a head-rigid face.
            var skin = new Matrix4x4[n];
            var world = new Vector3[n];
            for (int v = 0; v < n; v++)
            {
                Matrix4x4 m = smr.transform.localToWorldMatrix;
                if (weights.Length == n && bindposes.Length > 0)
                {
                    int bi = weights[v].boneIndex0;
                    if (bi >= 0 && bi < bindposes.Length && bi < bonesArr.Length && bonesArr[bi] != null)
                        m = bonesArr[bi].localToWorldMatrix * bindposes[bi];
                }
                skin[v] = m;
                world[v] = m.MultiplyPoint3x4(verts[v]);
            }

            // Region -> selected vertex indices.
            var selection = new List<int>[RegionCount];
            int usable = 0;
            for (int k = 0; k < RegionCount; k++)
            {
                if (!use[k]) continue;
                var list = new List<int>();
                float r2 = radius[k] * radius[k];
                for (int v = 0; v < n; v++)
                {
                    if ((world[v] - anchor[k]).sqrMagnitude > r2) continue;
                    // Skip inner-mouth / back-facing geometry.
                    if (normals.Length == n && Vector3.Dot(skin[v].MultiplyVector(normals[v]).normalized, fwd) < 0.1f) continue;
                    list.Add(v);
                }
                if (list.Count < 3)
                {
                    // Fall back to the nearest few vertices.
                    list.Clear();
                    var best = new List<KeyValuePair<float, int>>();
                    for (int v = 0; v < n; v++)
                        best.Add(new KeyValuePair<float, int>((world[v] - anchor[k]).sqrMagnitude, v));
                    best.Sort((a, b) => a.Key.CompareTo(b.Key));
                    for (int i = 0; i < 6 && i < best.Count; i++)
                        list.Add(best[i].Value);
                }
                selection[k] = list;
                usable++;
            }
            if (usable == 0)
                return null;

            // Pre-compute each blend shape's effect on each region.
            var entry = new MeshEntry { Renderer = smr, Mesh = mesh, Contribution = new Vector3[shapeCount][] };
            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var dt = new Vector3[n];
            var active = new List<int>();
            for (int s = 0; s < shapeCount; s++)
            {
                var c = new Vector3[RegionCount];
                entry.Contribution[s] = c;
                int frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;
                mesh.GetBlendShapeFrameVertices(s, frames - 1, dv, dn, dt);
                bool any = false;
                for (int k = 0; k < RegionCount; k++)
                {
                    var sel = selection[k];
                    if (sel == null) continue;
                    Vector3 sum = Vector3.zero;
                    for (int i = 0; i < sel.Count; i++)
                    {
                        int v = sel[i];
                        sum += skin[v].MultiplyVector(dv[v]);
                    }
                    var w = sum / sel.Count;
                    // World -> face units.
                    float x = Vector3.Dot(w, frame.Right), y = Vector3.Dot(w, frame.Up), z = Vector3.Dot(w, frame.Fwd);
                    if (IsLid((FaceRegion)k))
                    {
                        float h = Mathf.Max(frame.LidScale[k], 1e-5f);
                        c[k] = new Vector3(x / h, y / h, z / h);
                    }
                    else
                    {
                        c[k] = new Vector3(x / frame.D, y / frame.Em, z / frame.D);
                    }
                    if (c[k].sqrMagnitude > 1e-8f) any = true;
                }
                if (any) active.Add(s);
            }
            entry.ActiveShapes = active.ToArray();
            if (entry.ActiveShapes.Length == 0)
                return null;
            // Only regions on a mesh we actually keep are valid.
            for (int k = 0; k < RegionCount; k++)
                if (selection[k] != null)
                    _regionValid[k] = true;
            return entry;
        }

        private static bool IsLid(FaceRegion r)
        {
            return r == FaceRegion.LidUpL || r == FaceRegion.LidUpR || r == FaceRegion.LidLowL || r == FaceRegion.LidLowR;
        }

        private static bool IsBrow(FaceRegion r)
        {
            return r >= FaceRegion.BrowInL && r <= FaceRegion.BrowOutR;
        }

        private static void Put(Vector3[] anchor, bool[] have, FaceRegion r, Vector3 p)
        {
            anchor[(int)r] = p;
            have[(int)r] = true;
        }

        private static bool TryPos(Dictionary<string, Transform> named, string name, out Vector3 p)
        {
            Transform t;
            if (named.TryGetValue(name, out t) && t != null)
            {
                p = t.position;
                return true;
            }
            p = Vector3.zero;
            return false;
        }

        /// <summary>Upper lid = middle upper-lash bones (Eye02-04), lower lid = lower-lash bones (Eye06-08).</summary>
        private static void SetLids(Dictionary<string, Transform> named, string s, Vector3 eye, Vector3 up, float d,
            Vector3[] anchor, bool[] have, float[] lidScale, FaceRegion upper, FaceRegion lower)
        {
            Vector3 top, bottom;
            if (!Average(named, s, new[] { 2, 3, 4 }, out top)) top = eye + up * (0.12f * d);
            if (!Average(named, s, new[] { 6, 7, 8 }, out bottom)) bottom = eye - up * (0.10f * d);
            float h = Vector3.Dot(top - bottom, up);
            if (h < 0.05f * d) h = 0.2f * d;
            Put(anchor, have, upper, top);
            Put(anchor, have, lower, bottom);
            lidScale[(int)upper] = h;
            lidScale[(int)lower] = h;
        }

        private static bool Average(Dictionary<string, Transform> named, string side, int[] ids, out Vector3 p)
        {
            p = Vector3.zero;
            int n = 0;
            foreach (var id in ids)
            {
                Vector3 q;
                if (TryPos(named, "cf_J_Eye0" + id + "_s_" + side, out q)) { p += q; n++; }
            }
            if (n == 0) return false;
            p /= n;
            return true;
        }

        /// <summary>sideSign: +1 for the left brow (inner side is towards +right), -1 for the right brow.</summary>
        private static void SetBrow(Dictionary<string, Transform> named, string s, Vector3 eye, Vector3 up, Vector3 right,
            float d, float em, Vector3[] anchor, bool[] have, FaceRegion inner, FaceRegion mid, FaceRegion outer, float sideSign)
        {
            Vector3 center;
            if (!TryPos(named, "cf_J_Mayu_" + s, out center))
                center = eye + up * (0.35f * em);
            Vector3 pin, pout;
            if (!TryPos(named, "cf_J_MayuMid_s_" + s, out pin)) pin = center + right * (sideSign * 0.2f * d);
            if (!TryPos(named, "cf_J_MayuTip_s_" + s, out pout)) pout = center - right * (sideSign * 0.2f * d);
            Put(anchor, have, inner, pin);
            Put(anchor, have, mid, Vector3.Lerp(pin, pout, 0.5f));
            Put(anchor, have, outer, pout);
        }
    }
}
