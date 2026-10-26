using System.Collections.Generic;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using GameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using System;
using System.Numerics;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;
using SystemTask = FFXIVClientStructs.FFXIV.Client.System.Framework.Task;

namespace Limber.Core;

// Everything needed to read and change one character's body pose this frame.
// Only ever built and used inside the frame update, and never kept between frames.
public unsafe struct ActorSkeleton
{
    public CharacterBase* CharacterBase;
    public RenderSkeleton* Skeleton;
    public hkaPose* Pose;          // body (partial skeleton 0)
    public SkeletonInfo Info;

    public Vector3 RootPosition;   // where the skeleton is drawn
    public Quaternion RootRotation;
    public Vector3 RootScale;
    public Matrix4x4 ModelToWorld;
    public Matrix4x4 WorldToModel;

    // ---- Finding a character's skeleton

    // An object slot still holds the same object, and that object has a character model.
    public static bool TryGet(nint address, ushort objectIndex, SkeletonInfoCache cache, out ActorSkeleton result)
    {
        result = default;

        var gameObject = GetObject(objectIndex);
        if(gameObject == null || (nint)gameObject != address)
            return false;

        var drawObject = gameObject->DrawObject;
        if(drawObject == null)
            return false;

        var sceneObject = (SceneObject*)drawObject;
        if(sceneObject->GetObjectType() != SceneObjectType.CharacterBase)
            return false;

        var characterBase = (CharacterBase*)drawObject;
        var skeleton = characterBase->Skeleton;
        if(skeleton == null || skeleton->PartialSkeletonCount < 1 || skeleton->PartialSkeletons == null)
            return false;

        var pose = GetPose(&skeleton->PartialSkeletons[0]);
        if(pose == null || pose->Skeleton == null)
            return false;

        var info = cache.Get(pose->Skeleton);
        if(info == null)
            return false;

        result.CharacterBase = characterBase;
        result.Skeleton = skeleton;
        result.Pose = pose;
        result.Info = info;

        // Place and turn the skeleton exactly as it is drawn; size comes from the model and its height scale.
        var transform = skeleton->Transform;
        result.RootPosition = transform.Position;
        result.RootRotation = Quaternion.Normalize(transform.Rotation);

        Vector3 scale = sceneObject->Scale;
        float heightScale = ReadHeightScale(characterBase);
        result.RootScale = scale * heightScale;
        if(!MathUtil.IsFinite(result.RootScale) || result.RootScale.X <= 0.01f || result.RootScale.Y <= 0.01f || result.RootScale.Z <= 0.01f)
            result.RootScale = Vector3.One;

        result.ModelToWorld = Matrix4x4.CreateScale(result.RootScale)
            * Matrix4x4.CreateFromQuaternion(result.RootRotation)
            * Matrix4x4.CreateTranslation(result.RootPosition);

        if(!Matrix4x4.Invert(result.ModelToWorld, out result.WorldToModel))
            return false;

        return true;
    }

    public static GameObject* GetObject(ushort objectIndex)
    {
        var manager = GameObjectManager.Instance();
        if(manager == null)
            return null;

        var objects = manager->Objects.IndexSorted;
        if(objectIndex >= objects.Length)
            return null;

        return objects[objectIndex].Value;
    }

    private static hkaPose* GetPose(FFXIVClientStructs.FFXIV.Client.Graphics.Render.PartialSkeleton* partial)
    {
        if(partial == null)
            return null;

        for(int i = 0; i < 4; i++)
        {
            var pose = partial->GetHavokPose(i);
            if(pose != null)
                return pose;
        }
        return null;
    }

    // The two scale factors the game multiplies a character's size by (race height and the height slider).
    private static float ReadHeightScale(CharacterBase* characterBase)
    {
        var bytes = (byte*)characterBase;
        float a = *(float*)(bytes + 0x2A0);
        float b = *(float*)(bytes + 0x2A4);
        float result = a * b;
        return float.IsFinite(result) && result > 0.05f && result < 20f ? result : 1f;
    }

    // ---- Reading and writing bones (model space = relative to the character's root)

    public readonly Frame GetModelFrame(int boneIndex)
    {
        var t = Pose->AccessBoneModelSpace(boneIndex, PropagateOrNot.DontPropagate);
        return new Frame(ReadPosition(t), Quaternion.Normalize(ReadRotation(t)));
    }

    public readonly Vector3 GetModelPosition(int boneIndex)
        => ReadPosition(Pose->AccessBoneModelSpace(boneIndex, PropagateOrNot.DontPropagate));

    public readonly Vector3 GetModelScale(int boneIndex)
        => *(Vector3*)&Pose->AccessBoneModelSpace(boneIndex, PropagateOrNot.DontPropagate)->Scale;

    // Scale a bone (model space); everything below it scales with it.
    public readonly void SetModelScale(int boneIndex, Vector3 scale)
    {
        if(!MathUtil.IsFinite(scale))
            return;
        var t = Pose->AccessBoneModelSpace(boneIndex, PropagateOrNot.Propagate);
        *(Vector3*)&t->Scale = scale;
    }

    /// <summary>A bone's rest transform relative to its parent (the skeleton's reference pose).</summary>
    public readonly bool TryGetReferenceLocal(int boneIndex, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        var skeleton = Pose->Skeleton;
        if(skeleton == null || boneIndex < 0 || boneIndex >= skeleton->ReferencePose.Length)
            return false;
        var t = skeleton->ReferencePose[boneIndex];
        position = *(Vector3*)&t.Translation;
        rotation = Quaternion.Normalize(*(Quaternion*)&t.Rotation);
        return true;
    }

    // Turn a bone; everything below it turns with it.
    public readonly void SetModelRotation(int boneIndex, Quaternion rotation)
    {
        if(!MathUtil.IsFinite(rotation))
            return;

        var t = Pose->AccessBoneModelSpace(boneIndex, PropagateOrNot.Propagate);
        *(Quaternion*)&t->Rotation = Quaternion.Normalize(rotation);
    }

    // Move a bone; everything below it moves with it.
    public readonly void SetModelPosition(int boneIndex, Vector3 position)
    {
        if(!MathUtil.IsFinite(position))
            return;

        var t = Pose->AccessBoneModelSpace(boneIndex, PropagateOrNot.Propagate);
        *(Vector3*)&t->Translation = position;
    }

    private static Vector3 ReadPosition(hkQsTransformf* t) => *(Vector3*)&t->Translation;

    private static Quaternion ReadRotation(hkQsTransformf* t) => *(Quaternion*)&t->Rotation;

    // ---- Model space <-> world space

    public readonly Vector3 ModelToWorldPoint(Vector3 model) => Vector3.Transform(model, ModelToWorld);

    public readonly Vector3 WorldToModelPoint(Vector3 world) => Vector3.Transform(world, WorldToModel);

    public readonly Quaternion ModelToWorldRotation(Quaternion model) => Quaternion.Normalize(RootRotation * model);

    public readonly Quaternion WorldToModelRotation(Quaternion world) => Quaternion.Normalize(Quaternion.Inverse(RootRotation) * world);

    public readonly Frame GetWorldFrame(int boneIndex)
    {
        var model = GetModelFrame(boneIndex);
        return new Frame(ModelToWorldPoint(model.Position), ModelToWorldRotation(model.Rotation));
    }

    // ---- Keeping the rest of the character in step after the body changed

    /// <summary>Diagnostics: every skeleton of this character that has a bone of this name, and where it is.</summary>
    public readonly System.Collections.Generic.List<string> DescribeBone(string name, SkeletonInfoCache cache)
    {
        var lines = new System.Collections.Generic.List<string>();
        int partialCount = Skeleton->PartialSkeletonCount;
        for(int p = 0; p < partialCount; p++)
        {
            var partial = &Skeleton->PartialSkeletons[p];
            var pose = GetPose(partial);
            if(pose == null || pose->Skeleton == null)
                continue;
            var info = cache.Get(pose->Skeleton);
            if(info == null)
                continue;
            string file = "";
            var handle = (byte*)partial->SkeletonResourceHandle;
            if(handle != null)
            {
                ulong length = *(ulong*)(handle + 0x48 + 0x10), capacity = *(ulong*)(handle + 0x48 + 0x18);
                if(length > 0 && length < 512)
                {
                    byte* text = capacity >= 16 ? *(byte**)(handle + 0x48) : handle + 0x48;
                    if(text != null)
                        file = System.IO.Path.GetFileName(System.Text.Encoding.UTF8.GetString(text, (int)length).Split('|')[^1]);
                }
            }
            int bone = info.IndexOf(name);
            if(bone < 0)
            {
                lines.Add($"skeleton {p} ({file}, {info.Count} bones): not in it");
                continue;
            }
            var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.DontPropagate);
            var at = ReadPosition(t);
            string parent = info.Parents[bone] >= 0 ? info.Names[info.Parents[bone]] : "(root)";
            lines.Add($"skeleton {p} ({file}): bone {bone}, parent {parent}, at {at.X:0.000} {at.Y:0.000} {at.Z:0.000}");
        }
        return lines;
    }

    /// <summary>
    /// A bone a skeleton mod adds (IVCS's iv_ bones, which YAS uses too, and YAS's own ya_ bones), whichever
    /// skeleton mod it is: they may also have a copy in an extra skeleton, which is the one the model follows.
    /// </summary>
    public static bool IsSkeletonModBone(string name)
        => name.StartsWith("iv_", StringComparison.Ordinal) || name.StartsWith("ya_", StringComparison.Ordinal);

    /// <summary>
    /// A bone's world frame, from the skeleton the model actually follows: an extra skeleton's copy of it if one
    /// has it (body mod bones may live there too, moved by that skeleton's own physics), otherwise the body's.
    /// </summary>
    public readonly bool TryGetDrawnWorldFrame(string name, SkeletonInfoCache cache, out Frame frame)
    {
        frame = Frame.Identity;
        if(IsSkeletonModBone(name) || Info.IndexOf(name) < 0)
        {
            int partialCount = Skeleton->PartialSkeletonCount;
            for(int p = 1; p < partialCount; p++)
            {
                var pose = GetPose(&Skeleton->PartialSkeletons[p]);
                if(pose == null || pose->Skeleton == null)
                    continue;
                var info = cache.Get(pose->Skeleton);
                int bone = info?.IndexOf(name) ?? -1;
                if(bone < 0)
                    continue;
                var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.DontPropagate);
                frame = new Frame(ModelToWorldPoint(ReadPosition(t)), ModelToWorldRotation(Quaternion.Normalize(ReadRotation(t))));
                return true;
            }
        }
        int body = Info.IndexOf(name);
        if(body < 0)
            return false;
        frame = GetWorldFrame(body);
        return true;
    }

    // ---- The extra skeletons (face, hair, tail, and any a mod adds): bones the body skeleton doesn't have

    public readonly int PartialCount => Skeleton->PartialSkeletonCount;

    public readonly hkaPose* PartialPose(int partial)
        => partial >= 0 && partial < Skeleton->PartialSkeletonCount ? GetPose(&Skeleton->PartialSkeletons[partial]) : null;

    public readonly SkeletonInfo? PartialInfo(int partial, SkeletonInfoCache cache)
    {
        var pose = PartialPose(partial);
        return pose == null || pose->Skeleton == null ? null : cache.Get(pose->Skeleton);
    }

    /// <summary>Every bone name: the body's, then the ones only the extra skeletons have.</summary>
    public readonly string[] AllBoneNames(SkeletonInfoCache cache)
    {
        var names = new List<string>(Info.Names);
        var seen = new HashSet<string>(Info.Names);
        for(int p = 1; p < Skeleton->PartialSkeletonCount; p++)
        {
            var info = PartialInfo(p, cache);
            if(info == null)
                continue;
            foreach(var name in info.Names)
                if(seen.Add(name))
                    names.Add(name);
        }
        return names.ToArray();
    }

    /// <summary>The body bone an extra skeleton hangs from (-1 if unknown).</summary>
    public readonly int PartialParentBone(int partial)
        => partial > 0 && partial < Skeleton->PartialSkeletonCount ? Skeleton->PartialSkeletons[partial].ConnectedParentBoneIndex : -1;

    /// <summary>A bone that lives in one of the extra skeletons only (or the drawn copy of a body mod bone).</summary>
    public readonly bool TryFindExtraBone(string name, SkeletonInfoCache cache, out int partial, out int bone)
    {
        partial = -1;
        bone = -1;
        if(Info.IndexOf(name) >= 0 && !IsSkeletonModBone(name))
            return false;
        for(int p = 1; p < Skeleton->PartialSkeletonCount; p++)
        {
            var info = PartialInfo(p, cache);
            int b = info?.IndexOf(name) ?? -1;
            if(b < 0)
                continue;
            partial = p;
            bone = b;
            return true;
        }
        return false;
    }

    /// <summary>A bone's rest rotation relative to its parent, in one of the extra skeletons.</summary>
    public readonly bool TryGetPartialReferenceLocal(int partial, int bone, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        var pose = PartialPose(partial);
        if(pose == null || pose->Skeleton == null || bone < 0 || bone >= pose->Skeleton->ReferencePose.Length)
            return false;
        var t = pose->Skeleton->ReferencePose[bone];
        rotation = Quaternion.Normalize(*(Quaternion*)&t.Rotation);
        return true;
    }

    public readonly Frame GetPartialFrame(int partial, int bone)
    {
        var pose = PartialPose(partial);
        if(pose == null)
            return Frame.Identity;
        var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.DontPropagate);
        return new Frame(ReadPosition(t), Quaternion.Normalize(ReadRotation(t)));
    }

    public readonly void SetPartialRotation(int partial, int bone, Quaternion rotation)
    {
        var pose = PartialPose(partial);
        if(pose == null || !MathUtil.IsFinite(rotation))
            return;
        var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.Propagate);
        *(Quaternion*)&t->Rotation = Quaternion.Normalize(rotation);
    }

    public readonly void SetPartialPosition(int partial, int bone, Vector3 position)
    {
        var pose = PartialPose(partial);
        if(pose == null || !MathUtil.IsFinite(position))
            return;
        var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.Propagate);
        *(Vector3*)&t->Translation = position;
    }

    /// <summary>The frame a bone in an extra skeleton hangs from: its parent there, or the body bone the extra
    /// skeleton is attached to.</summary>
    public readonly Frame GetPartialParentFrame(int partial, int bone, SkeletonInfoCache cache)
    {
        var info = PartialInfo(partial, cache);
        int parent = info?.ParentOf(bone) ?? -1;
        if(parent >= 0)
            return GetPartialFrame(partial, parent);
        int body = info != null ? Info.IndexOf(info.Names[bone]) : -1;
        if(body >= 0)
            body = Info.ParentOf(body);
        if(body < 0)
            body = PartialParentBone(partial);
        return body >= 0 && body < Info.Count ? GetModelFrame(body) : Frame.Identity;
    }

    // The face, hair and other extra skeletons are positioned from the body when the game builds the pose.
    // Bones moved after that must be copied onto them again, or the face stays where the head used to be.
    public readonly void RealignPartials(SkeletonInfoCache cache)
    {
        int partialCount = Skeleton->PartialSkeletonCount;
        for(int p = 1; p < partialCount; p++)
        {
            var partial = &Skeleton->PartialSkeletons[p];
            var pose = GetPose(partial);
            if(pose == null || pose->Skeleton == null)
                continue;

            var info = cache.Get(pose->Skeleton);
            if(info == null)
                continue;

            for(int bone = 0; bone < info.Count; bone++)
            {
                // Roots of the extra skeleton, and body mod bones (IVCS / YAS genitals etc.: some skeleton mods put a
                // copy of them in an extra skeleton, and the model follows that copy) take the body's pose.
                bool bodyMod = IsSkeletonModBone(info.Names[bone]);
                if(info.Parents[bone] >= 0 && !bodyMod)
                    continue;

                int bodyBone = Info.IndexOf(info.Names[bone]);
                if(bodyBone < 0 && bone == partial->ConnectedBoneIndex)
                    bodyBone = partial->ConnectedParentBoneIndex;

                if(bodyBone < 0 || bodyBone >= Info.Count)
                    continue;

                var source = Pose->AccessBoneModelSpace(bodyBone, PropagateOrNot.DontPropagate);
                var target = pose->AccessBoneModelSpace(bone, PropagateOrNot.Propagate);
                target->Translation = source->Translation;
                target->Rotation = source->Rotation;
                target->Scale = source->Scale;
            }
        }
    }

    /// <summary>Move, turn (about the model's origin) and scale every bone of every part of the skeleton, in
    /// model space: p' = move + turn * (p * scale). Each bone is written directly, so it doesn't depend on how
    /// the game carries a parent's scale down to its children.</summary>
    public readonly void TransformAllBones(Vector3 move, Quaternion turn, float scale)
    {
        int partialCount = Skeleton->PartialSkeletonCount;
        for(int p = 0; p < partialCount; p++)
        {
            var pose = GetPose(&Skeleton->PartialSkeletons[p]);
            if(pose == null || pose->Skeleton == null)
                continue;
            int count = pose->Skeleton->Bones.Length;
            for(int bone = 0; bone < count; bone++)
            {
                var t = pose->AccessBoneModelSpace(bone, PropagateOrNot.DontPropagate);
                var position = *(Vector3*)&t->Translation;
                var rotation = *(Quaternion*)&t->Rotation;
                var size = *(Vector3*)&t->Scale;
                *(Vector3*)&t->Translation = move + Vector3.Transform(position * scale, turn);
                *(Quaternion*)&t->Rotation = Quaternion.Normalize(turn * rotation);
                *(Vector3*)&t->Scale = size * scale;
            }
        }
    }

    // Weapons and other attached models follow a hand bone; let the game place them again.
    public readonly void RefreshAttachedModels()
    {
        var sceneObject = (SceneObject*)CharacterBase;
        var first = sceneObject->ChildObject;
        if(first == null)
            return;

        var child = first;
        int guard = 0;
        do
        {
            if(child->GetObjectType() == SceneObjectType.CharacterBase)
            {
                var childBase = (CharacterBase*)child;
                int type = childBase->Attach.ExecuteType;
                if(type == 3 || type == 4)
                    ((SystemTask*)&childBase->Attach)->Execute(null);
            }

            child = child->NextSiblingObject;
        }
        while(child != null && child != first && ++guard < 16);
    }
}
