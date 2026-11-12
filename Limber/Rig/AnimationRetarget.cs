using System;
using System.Collections.Generic;
using System.Numerics;
using Limber.Core;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace Limber.Rig;

/// <summary>
/// An animation from another skeleton (Animations page): another race's, or a creature's, played on the chosen
/// character even though their skeletons don't match.
///
/// How: a hidden helper actor of that race or creature plays the animation with its own skeleton (so the game picks and
/// plays the right file, the way it does for that creature). Each frame its bones are read and put on the character by
/// name (with a few names monsters spell differently): each bone turned from its rest pose the way the source's bone
/// turned from its own rest pose (in the model's space, so bones whose axes differ still go the right way), and the hips
/// moved as the source's did, scaled to the character's size. Bones the source hasn't got (tails, ears, wings the other
/// way) follow their parents. In GPose (actors can only be added there, or with "Also run outside GPose"). Only on this PC.
/// </summary>
public sealed unsafe class AnimationRetarget
{
    /// <summary>Where the animation comes from: a playable race (body id, 101 ... 1801) or a creature (its model number).</summary>
    public sealed record Source(string Name, ushort RaceBody, int Model);

    private readonly ActorSpawner _spawner;
    private readonly SkeletonInfoCache _cache = new();

    private nint _target;
    private ushort _targetIndex;
    private int _helper = -1;
    private nint _helperAddress;
    private Source? _source;
    private ushort _timeline;
    private bool _played;
    private int _frames;

    /// <summary>The hips move the way the source's do (scaled to the character's size).</summary>
    public bool MoveHips { get; set; } = true;
    /// <summary>Show the helper (the source) too, for checking.</summary>
    public bool ShowSource { get; set; }
    /// <summary>How much of the source's pose is put on (0..1).</summary>
    public float Weight { get; set; } = 1f;
    public string Status { get; private set; } = "";
    public nint Target => _target;
    public Source? Playing => _target != 0 ? _source : null;
    public ushort Timeline => _timeline;
    public int Matched { get; private set; }

    public AnimationRetarget(ActorSpawner spawner) => _spawner = spawner;

    /// <summary>Plays <paramref name="timeline"/> from <paramref name="source"/>'s skeleton on the character.</summary>
    public string Start(nint target, ushort targetIndex, Source source, ushort timeline)
    {
        // (the same character and source: only the animation changes)
        if(target == _target && _source == source && HelperAlive())
        {
            _timeline = timeline;
            _played = false;
            _frames = 0;
            return Status = $"Playing {Animations.KeyOf(timeline)} from {source.Name}...";
        }
        Stop();
        var obj = ActorSkeleton.GetObject(targetIndex);
        if(obj == null || (nint)obj != target || !obj->IsCharacter())
            return Status = "The character isn't there.";
        if(Plugin.ObjectTable.CreateObjectReference(target) is not Dalamud.Game.ClientState.Objects.Types.ICharacter character)
            return Status = "The character isn't there.";
        int index = _spawner.SpawnHelper(character);
        if(index < 0)
            return Status = "Couldn't add the helper that plays it: " + (_spawner.LastError ?? "?");
        var made = ActorSkeleton.GetObject((ushort)index);
        if(made == null)
            return Status = "The helper didn't appear.";
        // Its race or creature, written in before it's first drawn.
        if(source.Model > 0)
            Entities.WriteUndrawn((ushort)index, source.Model);
        else if(source.RaceBody > 0)
        {
            var look = Appearance.Read(target, targetIndex)?.Copy();
            if(look != null)
            {
                var (race, tribe, sex) = RaceOf(source.RaceBody);
                look.Customize[0] = race;
                look.Customize[1] = sex;
                look.Customize[4] = tribe;
                if(look.Customize[5] is 0 or > 4)
                    look.Customize[5] = 1;
                if(look.Customize[22] is 0 or > 4)
                    look.Customize[22] = 1;
                Appearance.WriteUndrawn((NativeCharacter*)made, look);
            }
        }
        *(int*)&made->RenderFlags |= 2; // (hidden from the start)
        _target = target;
        _targetIndex = targetIndex;
        _helper = index;
        _helperAddress = (nint)made;
        _source = source;
        _timeline = timeline;
        _played = false;
        _frames = 0;
        _map = null;
        return Status = $"Getting {source.Name} ready to play {Animations.KeyOf(timeline)}...";
    }

    public void Stop()
    {
        if(_helper >= 0 && _spawner.StillOurs((ushort)_helper))
            _spawner.Despawn((ushort)_helper);
        _helper = -1;
        _helperAddress = 0;
        _target = 0;
        _played = false;
        _map = null;
        Matched = 0;
    }

    private bool HelperAlive()
    {
        if(_helper < 0)
            return false;
        var h = ActorSkeleton.GetObject((ushort)_helper);
        return h != null && (nint)h == _helperAddress && _spawner.StillOurs((ushort)_helper);
    }

    private static (byte Race, byte Tribe, byte Sex) RaceOf(ushort body)
    {
        int n = body / 100;
        byte sex = (byte)(n % 2 == 0 ? 1 : 0);
        return (n - sex) switch
        {
            1 => (1, 1, sex), 3 => (1, 2, sex), 5 => (2, 3, sex), 11 => (3, 5, sex), 7 => (4, 7, sex),
            9 => (5, 9, sex), 13 => (6, 11, sex), 15 => (7, 13, sex), 17 => (8, 15, sex),
            _ => ((byte)1, (byte)1, sex),
        };
    }

    // ---- Framework update: the helper kept hidden, by the character, and playing

    public void Update()
    {
        if(_target == 0)
            return;
        var target = ActorSkeleton.GetObject(_targetIndex);
        if(target == null || (nint)target != _target)
        {
            Stop();
            Status = "The character went away.";
            return;
        }
        if(!HelperAlive())
        {
            Stop();
            Status = "The helper went away (GPose ended?). Play it again to start over.";
            return;
        }
        var helper = (GameObject*)_helperAddress;
        var flags = (int*)&helper->RenderFlags;
        if(ShowSource)
            *flags &= ~2;
        else
            *flags |= 2;
        // (kept where the character is, so the game keeps it animating as one of the scene's characters)
        helper->Position = target->Position;
        helper->Rotation = target->Rotation;
        if(helper->DrawObject != null && target->DrawObject != null)
        {
            helper->DrawObject->Object.Position = target->DrawObject->Object.Position;
            helper->DrawObject->Object.Rotation = target->DrawObject->Object.Rotation;
        }
        if(helper->DrawObject == null)
        {
            if(++_frames > 600)
            {
                Stop();
                Status = "The helper never got drawn (that creature may not load here).";
            }
            return;
        }
        if(!_played && ++_frames > 10)
        {
            Animations.Play(_helperAddress, (ushort)_helper, _timeline);
            _played = true;
            Status = $"Playing {Animations.KeyOf(_timeline)} from {_source?.Name}.";
        }
    }

    // ---- Every frame (bone update): the source's pose put on the character

    private sealed class Map
    {
        public required SkeletonInfo Source, Target;
        public required int[] From;          // per target bone: the source bone (-1: none)
        public required Quaternion[] SourceRest, TargetRest;
        public required Vector3[] SourceRestAt, TargetRestAt;
        public int Hips = -1;
    }

    private Map? _map;

    public void UpdateFrame()
    {
        if(_target == 0 || !_played || _helper < 0)
            return;
        if(!ActorSkeleton.TryGet(_helperAddress, (ushort)_helper, _cache, out var src) || src.Info.Count == 0)
            return;
        if(!ActorSkeleton.TryGet(_target, _targetIndex, _cache, out var tgt) || tgt.Info.Count == 0)
            return;
        var map = _map;
        if(map == null || !ReferenceEquals(map.Source, src.Info) || !ReferenceEquals(map.Target, tgt.Info))
            _map = map = Build(src, tgt);
        if(map == null)
            return;
        float w = Math.Clamp(Weight, 0f, 1f);
        if(w <= 0f)
            return;
        // The hips first (moving them carries everything below), then each bone's turn, parents first.
        if(MoveHips && map.Hips >= 0 && map.From[map.Hips] is int sh and >= 0)
        {
            float a = map.TargetRestAt[map.Hips].Y, b = map.SourceRestAt[sh].Y;
            float scale = a > 0.05f && b > 0.05f ? a / b : 1f;
            var at = map.TargetRestAt[map.Hips] + (src.GetModelFrame(sh).Position - map.SourceRestAt[sh]) * scale;
            if(w < 1f)
                at = Vector3.Lerp(tgt.GetModelFrame(map.Hips).Position, at, w);
            if(MathUtil.IsFinite(at))
                tgt.SetModelPosition(map.Hips, at);
        }
        for(int i = 0; i < map.From.Length; i++)
        {
            int s = map.From[i];
            if(s < 0)
                continue;
            var turn = Quaternion.Normalize(src.GetModelFrame(s).Rotation * Quaternion.Inverse(map.SourceRest[s]));
            var want = Quaternion.Normalize(turn * map.TargetRest[i]);
            if(w < 1f)
                want = Quaternion.Slerp(tgt.GetModelFrame(i).Rotation, want, w);
            tgt.SetModelRotation(i, want);
        }
    }

    // A few bones monsters (and some demihumans) name differently.
    private static IEnumerable<string> Names(string name)
    {
        yield return name;
        (string, string)[] pairs =
        [
            ("j_oya_", "j_teoya_"), ("j_hito_", "j_tehito_"), ("j_naka_", "j_tenaka_"), ("j_ko_", "j_teko_"), ("j_kusu_", "j_tekusu_"),
        ];
        foreach(var (a, b) in pairs)
        {
            if(name.StartsWith(a, StringComparison.Ordinal))
                yield return b + name[a.Length..];
            if(name.StartsWith(b, StringComparison.Ordinal))
                yield return a + name[b.Length..];
        }
        switch(name)
        {
            case "j_kubi": yield return "j_kubi_a"; break;
            case "j_kubi_a": yield return "j_kubi"; break;
            case "j_sebo_b": yield return "j_sebo"; break;
            case "j_sebo": yield return "j_sebo_b"; break;
        }
    }

    private Map? Build(in ActorSkeleton src, in ActorSkeleton tgt)
    {
        var from = new int[tgt.Info.Count];
        int matched = 0, hips = -1;
        for(int i = 0; i < from.Length; i++)
        {
            from[i] = -1;
            string name = tgt.Info.Names[i];
            if(name == "j_kosi")
                hips = i;
            // (the body's joints only: n_ bones are attach points, j_f_ the face's own)
            if(!name.StartsWith("j_", StringComparison.Ordinal) || name.StartsWith("j_f_", StringComparison.Ordinal))
                continue;
            foreach(var n in Names(name))
                if(src.Info.IndexOf(n) is int s and >= 0)
                {
                    from[i] = s;
                    matched++;
                    break;
                }
        }
        Matched = matched;
        if(matched == 0)
        {
            Status = $"{_source?.Name} has no bones named like the character's: nothing to put on.";
            return null;
        }
        Rest(src, out var sr, out var sa);
        Rest(tgt, out var tr, out var ta);
        return new Map
        {
            Source = src.Info, Target = tgt.Info, From = from, SourceRest = sr, TargetRest = tr, SourceRestAt = sa, TargetRestAt = ta,
            Hips = hips >= 0 && from[hips] >= 0 ? hips : -1,
        };
    }

    // The body skeleton's rest pose in model space.
    private static void Rest(in ActorSkeleton sk, out Quaternion[] rotation, out Vector3[] position)
    {
        int n = sk.Info.Count;
        rotation = new Quaternion[n];
        position = new Vector3[n];
        for(int i = 0; i < n; i++)
        {
            if(!sk.TryGetReferenceLocal(i, out var lp, out var lr))
            {
                lp = Vector3.Zero;
                lr = Quaternion.Identity;
            }
            int parent = sk.Info.ParentOf(i);
            if(parent >= 0 && parent < i)
            {
                rotation[i] = Quaternion.Normalize(rotation[parent] * lr);
                position[i] = position[parent] + Vector3.Transform(lp, rotation[parent]);
            }
            else
            {
                rotation[i] = lr;
                position[i] = lp;
            }
        }
    }
}
