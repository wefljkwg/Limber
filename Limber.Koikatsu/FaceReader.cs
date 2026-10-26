using System;
using System.Collections.Generic;
using System.Reflection;
using Limber.Protocol;
using UnityEngine;
using Joint = Limber.Protocol.Joint;

namespace Limber.Koikatsu
{
    internal struct FaceState
    {
        public bool HasPatterns;
        public bool HasEyeOpen;
        public bool HasMouthOpen;
        public bool HasGaze;
        public int EyesPattern;
        public int MouthPattern;
        public int EyebrowPattern;
        public float EyesOpenMax;
        public float EyeOpen;
        public float MouthOpen;
        public Vector3 GazeWorld;
    }

    /// <summary>
    /// Reads expression state from a ChaControl.
    /// Pattern ids and eye-open max come from ChaControl.fileStatus (public API).
    /// Live blink / mouth openness live on the face blend-shape controllers, which are
    /// partly private, so those are read through cached reflection and simply reported
    /// as unavailable if a game build names them differently.
    /// </summary>
    internal sealed class FaceReader
    {
        private static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Dictionary<string, MemberInfo> _memberCache = new Dictionary<string, MemberInfo>();
        private readonly HashSet<string> _missing = new HashSet<string>();

        public FaceState Read(ChaControl chara, BoneCache bones)
        {
            var s = new FaceState { EyeOpen = 1f, EyesOpenMax = 1f };

            try
            {
                var fs = chara.fileStatus;
                if (fs != null)
                {
                    s.EyesPattern = (int)fs.eyesPtn;
                    s.MouthPattern = (int)fs.mouthPtn;
                    s.EyebrowPattern = (int)fs.eyebrowPtn;
                    s.EyesOpenMax = Mathf.Clamp01((float)fs.eyesOpenMax);
                    s.HasPatterns = true;
                }
            }
            catch (Exception e)
            {
                LogOnce("fileStatus", e);
            }

            // Face blend-shape controller: ChaControl.fbsCtrl (FaceBlendShape)
            object fbs = GetMember(chara, "fbsCtrl");
            if (fbs != null)
            {
                // Blink: FaceBlendShape.BlinkCtrl.GetOpenRate()  (1 = open)
                var blink = GetMember(fbs, "BlinkCtrl") ?? GetMember(fbs, "blinkCtrl");
                float rate;
                if (blink != null && TryCallFloat(blink, "GetOpenRate", out rate))
                {
                    s.EyeOpen = Mathf.Clamp01(rate);
                    s.HasEyeOpen = true;
                }
                else
                {
                    var eyes = GetMember(fbs, "EyesCtrl") ?? GetMember(fbs, "eyesCtrl");
                    if (eyes != null && TryGetFloat(eyes, "openRate", out rate))
                    {
                        s.EyeOpen = Mathf.Clamp01(rate);
                        s.HasEyeOpen = true;
                    }
                }

                var mouth = GetMember(fbs, "MouthCtrl") ?? GetMember(fbs, "mouthCtrl");
                if (mouth != null && TryGetFloat(mouth, "openRate", out rate))
                {
                    s.MouthOpen = Mathf.Clamp01(rate);
                    s.HasMouthOpen = true;
                }
            }

            // Gaze: where the eyes are told to look.
            try
            {
                var look = chara.eyeLookCtrl;
                // Pattern 0 = "look forward": the target still points at the camera, so send no gaze
                // and let FFXIV keep the eyes neutral.
                object ptn = look != null ? GetMember(look, "ptnNo") : null;
                bool lookForward = ptn is int && (int)ptn == 0;
                if (look != null && look.target != null && !lookForward)
                {
                    var l = bones.Get(Joint.EyeL);
                    var r = bones.Get(Joint.EyeR);
                    var head = bones.Get(Joint.Head);
                    Vector3 origin = l != null && r != null ? (l.position + r.position) * 0.5f
                        : head != null ? head.position : chara.transform.position;
                    var dir = look.target.position - origin;
                    if (dir.sqrMagnitude > 1e-6f)
                    {
                        s.GazeWorld = dir.normalized;
                        s.HasGaze = true;
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce("eyeLookCtrl", e);
            }

            return s;
        }

        private object GetMember(object owner, string name)
        {
            if (owner == null)
                return null;
            var type = owner.GetType();
            var key = type.FullName + "::" + name;
            MemberInfo m;
            if (!_memberCache.TryGetValue(key, out m))
            {
                m = FindMember(type, name);
                _memberCache[key] = m;
            }
            try
            {
                var f = m as FieldInfo;
                if (f != null) return f.GetValue(owner);
                var p = m as PropertyInfo;
                if (p != null) return p.GetValue(owner, null);
            }
            catch (Exception e)
            {
                LogOnce(key, e);
            }
            return null;
        }

        private bool TryGetFloat(object owner, string name, out float value)
        {
            value = 0f;
            var v = GetMember(owner, name);
            if (v is float)
            {
                value = (float)v;
                return true;
            }
            return false;
        }

        private bool TryCallFloat(object owner, string method, out float value)
        {
            value = 0f;
            var type = owner.GetType();
            var key = type.FullName + "::" + method + "()";
            MemberInfo m;
            if (!_memberCache.TryGetValue(key, out m))
            {
                MethodInfo found = null;
                for (var t = type; t != null && found == null; t = t.BaseType)
                {
                    var mi = t.GetMethod(method, All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (mi != null && mi.ReturnType == typeof(float))
                        found = mi;
                }
                m = found;
                _memberCache[key] = m;
            }

            var method_ = m as MethodInfo;
            if (method_ == null)
                return false;
            try
            {
                value = (float)method_.Invoke(owner, null);
                return true;
            }
            catch (Exception e)
            {
                LogOnce(key, e);
                return false;
            }
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                var p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            return null;
        }

        private void LogOnce(string key, Exception e)
        {
            if (_missing.Add(key))
                LimberSenderPlugin.Logger.LogDebug("Limber face read '" + key + "' unavailable: " + e.Message);
        }
    }
}
