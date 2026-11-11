using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Limber.Core;

namespace Limber.Rig;

/// <summary>Dataset capture (Extras page): which shots to take and where they go.</summary>
public sealed class DatasetSettings
{
    public bool FullBody { get; set; } = true;
    public bool ThighUp { get; set; } = true;
    public bool UpperBody { get; set; } = true;
    public bool Face { get; set; } = true;
    /// <summary>Square shots at 1280 x 1280 (else 1024 x 1024). Full-body and thigh-up shots are 1024 x 1280.</summary>
    public bool LargeSquares { get; set; }
    /// <summary>Where the pictures go (a folder for each run inside it; empty: Pictures\Limber dataset).</summary>
    public string Folder { get; set; } = "";
    /// <summary>A caption file (.txt) next to each picture: the trigger word, then the shot's tags.</summary>
    public bool Captions { get; set; } = true;
    public string Trigger { get; set; } = "";
}

/// <summary>
/// Takes a set of pictures of the chosen character for training an image model: full body, thigh up, upper body and
/// face, from the front, the sides, behind, above and below. For each shot the camera is aimed at the right part of
/// the body (found from its bones), turned to the angle round it, and moved in or out until that part fills the
/// picture (checked on screen each frame, so it works whatever the camera's conventions); once it has settled, the
/// game's picture is copied straight off the graphics card (before any plugin window is drawn), cropped round the
/// character and saved as a PNG, with a caption file. The camera is put back afterwards. Render thread (Limber's draw).
/// </summary>
public sealed unsafe class DatasetCapture
{
    public enum Framing { FullBody, ThighUp, UpperBody, Face }

    public sealed record Shot(Framing Framing, float YawDegrees, float ElevationDegrees);

    private enum Phase { Idle, Hook, CalibrateA, CalibrateB, Aim }

    public bool Running => _phase != Phase.Idle;
    public int Done { get; private set; }
    public int Total => _shots.Count;
    public string Note { get; private set; } = "";
    public string LastFolder { get; private set; } = "";

    private Phase _phase = Phase.Idle;
    private List<Shot> _shots = new();
    private int _index;
    private int _frames, _stable, _shotFrames;
    private nint _address;
    private ushort _objectIndex;
    private string _name = "";
    private string _folder = "";
    private DatasetSettings _settings = new();
    private CameraShot? _before;
    private float _signH = 1f, _signV = 1f;
    private float _az0, _el0, _h0, _v0, _dv;
    private readonly SkeletonInfoCache _cache = new();
    private readonly List<Task> _saving = new();

    /// <summary>Starts taking the pictures of this character. Returns what happened.</summary>
    public string Start(nint address, ushort objectIndex, string name, DatasetSettings settings)
    {
        if(Running)
            return "Already taking pictures.";
        if(address == 0)
            return "Choose a character at the top first.";
        if(!CameraControl.Available)
            return "The camera isn't there (in GPose?).";
        _shots = Shots(settings);
        if(_shots.Count == 0)
            return "Tick at least one kind of shot.";
        string root = settings.Folder.Trim().Length > 0 ? settings.Folder.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Limber dataset");
        string safe = string.Concat((name.Length > 0 ? name : "character").Split(Path.GetInvalidFileNameChars()));
        _folder = Path.Combine(root, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch(Exception e)
        {
            return "Couldn't make the folder: " + e.Message;
        }
        _address = address;
        _objectIndex = objectIndex;
        _name = name;
        _settings = settings;
        _before = CameraControl.Read();
        _index = 0;
        Done = 0;
        _frames = 0;
        _stable = 0;
        _shotFrames = 0;
        LastFolder = _folder;
        _phase = Phase.Hook;
        CameraControl.SetAim(Here());
        Note = "Getting the camera ready...";
        return $"Taking {_shots.Count} pictures...";
    }

    public void Stop(string why = "Stopped.")
    {
        if(!Running)
            return;
        _phase = Phase.Idle;
        CameraControl.SetAim(null);
        if(_before != null)
            CameraControl.Write(_before);
        Note = why;
    }

    // The shots: angles round the character (0: from the front, + its left), and heights (+ from above).
    public static List<Shot> Shots(DatasetSettings s)
    {
        var list = new List<Shot>();
        void Add(Framing f, float elevation, params float[] yaws)
        {
            foreach(var y in yaws)
                list.Add(new Shot(f, y, elevation));
        }
        if(s.FullBody)
        {
            Add(Framing.FullBody, 3f, 0f, 45f, 90f, 135f, 180f, -135f, -90f, -45f);
            Add(Framing.FullBody, 22f, 0f, 45f, -45f);
            Add(Framing.FullBody, -10f, 0f, 30f, -30f);
        }
        if(s.ThighUp)
            Add(Framing.ThighUp, 4f, 0f, 30f, -30f, 60f, -60f, 180f);
        if(s.UpperBody)
        {
            Add(Framing.UpperBody, 5f, 0f, 30f, -30f, 60f, -60f, 90f, -90f);
            Add(Framing.UpperBody, 20f, 0f, 35f);
        }
        if(s.Face)
        {
            Add(Framing.Face, 2f, 0f, 20f, -20f, 45f, -45f, 70f, -70f);
            Add(Framing.Face, 15f, 0f);
            Add(Framing.Face, -10f, 0f);
        }
        return list;
    }

    // ---- Each frame (Limber's draw, before plugin windows are drawn into the picture)

    public void Step()
    {
        if(!Running)
            return;
        try
        {
            StepNow();
        }
        catch(Exception e)
        {
            Plugin.Log.Warning(e, "Limber: dataset capture");
            Stop("Stopped: " + e.Message);
        }
    }

    private Vector3? Here()
        => ActorSkeleton.TryGet(_address, _objectIndex, _cache, out var sk) && sk.TryGetDrawnWorldFrame("j_kosi", _cache, out var f) ? f.Position : null;

    private static float Wrap(float a)
    {
        while(a > MathF.PI) a -= MathF.Tau;
        while(a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // Where the game put the camera round what it looks at: its angle round (world) and its height angle.
    private static bool Measure(out float azimuth, out float elevation, out float distance)
    {
        azimuth = elevation = distance = 0f;
        if(!CameraControl.RawValid)
            return false;
        var d = CameraControl.RawPosition - CameraControl.RawLook;
        float flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        distance = d.Length();
        if(distance < 1e-4f)
            return false;
        azimuth = MathF.Atan2(d.X, d.Z);
        elevation = MathF.Atan2(d.Y, flat);
        return true;
    }

    private void StepNow()
    {
        var o = Plugin.ObjectTable[_objectIndex];
        if(o == null || o.Address != _address)
        {
            Stop("Stopped: the character left.");
            return;
        }
        _frames++;
        switch(_phase)
        {
            case Phase.Hook:
                // (the camera's own place, read once the game has made it a few times with Limber listening)
                CameraControl.SetAim(Here());
                if(_frames < 4 || !Measure(out _az0, out _el0, out _))
                {
                    if(_frames > 120)
                        Stop("Stopped: couldn't read the camera.");
                    return;
                }
                var now = CameraControl.Read();
                if(now == null)
                {
                    Stop("Stopped: couldn't read the camera.");
                    return;
                }
                _h0 = now.Yaw;
                _v0 = now.Pitch;
                // Which way the game's angles turn the camera: turned a little each way, and looked at.
                _dv = _v0 > 0f ? -0.15f : 0.15f;
                CameraControl.Write(new CameraShot { Yaw = _h0 + 0.3f, Pitch = _v0 + _dv }, distance: false, fov: false, tilt: false);
                _phase = Phase.CalibrateA;
                _frames = 0;
                return;
            case Phase.CalibrateA:
                CameraControl.SetAim(Here());
                if(_frames < 6)
                    return;
                if(!Measure(out float az1, out float el1, out _))
                    return;
                float dAz = Wrap(az1 - _az0), dEl = el1 - _el0;
                if(MathF.Abs(dAz) < 0.02f || MathF.Abs(dEl) < 0.005f)
                {
                    Stop("Stopped: the camera didn't turn (is it locked by another plugin?).");
                    return;
                }
                _signH = MathF.Sign(dAz);
                _signV = MathF.Sign(dEl) * MathF.Sign(_dv);
                _phase = Phase.Aim;
                _frames = 0;
                _stable = 0;
                _shotFrames = 0;
                Note = $"Picture 1 of {_shots.Count}...";
                return;
            case Phase.Aim:
                Aim();
                return;
        }
    }

    // One shot: aim, turn, move in or out until it holds still, then take it.
    private void Aim()
    {
        var shot = _shots[_index];
        _shotFrames++;
        if(!ActorSkeleton.TryGet(_address, _objectIndex, _cache, out var sk))
            return;
        if(!Frame(sk, shot.Framing, out var target, out var top, out var bottom, out float fill))
        {
            Note = "Couldn't find the character's bones: skipped a picture.";
            Next();
            return;
        }
        CameraControl.SetAim(target);
        var cam = CameraControl.Read();
        if(cam == null || !Measure(out float az, out float el, out _))
            return;
        var o = Plugin.ObjectTable[_objectIndex];
        float facing = o?.Rotation ?? 0f;
        float wantAz = Wrap(facing + shot.YawDegrees * MathF.PI / 180f);
        float wantEl = shot.ElevationDegrees * MathF.PI / 180f;
        float errAz = Wrap(wantAz - az), errEl = wantEl - el;

        // Size on screen: the framed part's top and bottom, against the picture's height.
        float screenH = Dalamud.Bindings.ImGui.ImGui.GetMainViewport().Size.Y;
        float wantSpan = screenH * fill;
        float distance = cam.Distance;
        float sizeErr = 1f;
        bool seen = Plugin.GameGui.WorldToScreen(top, out var sTop) & Plugin.GameGui.WorldToScreen(bottom, out var sBottom);
        if(seen)
        {
            float span = Vector2.Distance(sTop, sBottom);
            if(span > 1f)
            {
                sizeErr = span / wantSpan;
                distance = Math.Clamp(distance * Math.Clamp(sizeErr, 0.5f, 2f), 0.15f, 40f);
            }
        }
        else
            distance = Math.Clamp(distance * 1.25f, 0.15f, 40f);
        CameraControl.Write(new CameraShot { Yaw = cam.Yaw + _signH * errAz, Pitch = cam.Pitch + _signV * errEl, Distance = distance },
            fov: false, tilt: false);

        bool settled = seen && MathF.Abs(errAz) < 0.012f && MathF.Abs(errEl) < 0.012f && MathF.Abs(sizeErr - 1f) < 0.03f;
        _stable = settled ? _stable + 1 : 0;
        // (held still a few frames: the picture is from this aim; or taken anyway after 6 seconds or so)
        if(_stable < 5 && _shotFrames < 400)
            return;
        Take(shot, target);
        Next();
    }

    private void Next()
    {
        _index++;
        _stable = 0;
        _shotFrames = 0;
        if(_index >= _shots.Count)
        {
            Stop($"Done: {Done} pictures in {_folder}");
            return;
        }
        Note = $"Picture {_index + 1} of {_shots.Count}...";
    }

    // What to frame: the point to aim at, the top and bottom that should fill the picture, and how much of it they fill.
    private bool Frame(ActorSkeleton sk, Framing f, out Vector3 target, out Vector3 top, out Vector3 bottom, out float fill)
    {
        target = top = bottom = default;
        fill = 0.9f;
        if(!sk.TryGetDrawnWorldFrame("j_kao", _cache, out var head) || !sk.TryGetDrawnWorldFrame("j_kubi", _cache, out var neck)
           || !sk.TryGetDrawnWorldFrame("j_kosi", _cache, out var hips))
            return false;
        float s = Math.Clamp(Vector3.Distance(hips.Position, neck.Position) / 0.55f, 0.3f, 3f);
        var up = Vector3.UnitY;
        // The lowest foot bone (the feet), or below the hips by a leg's length.
        float feetY = float.MaxValue;
        foreach(var b in new[] { "j_asi_e_l", "j_asi_e_r", "j_asi_d_l", "j_asi_d_r" })
            if(sk.TryGetDrawnWorldFrame(b, _cache, out var foot))
                feetY = MathF.Min(feetY, foot.Position.Y);
        if(feetY == float.MaxValue)
            feetY = hips.Position.Y - 0.95f * s;
        var headTop = head.Position + up * 0.14f * s;
        var feet = new Vector3(hips.Position.X, feetY - 0.03f * s, hips.Position.Z);
        switch(f)
        {
            case Framing.FullBody:
                top = headTop;
                bottom = new Vector3(headTop.X, feet.Y, headTop.Z);
                fill = 0.9f;
                break;
            case Framing.ThighUp:
                top = headTop;
                bottom = new Vector3(headTop.X, hips.Position.Y - (hips.Position.Y - feet.Y) * 0.5f, headTop.Z);
                fill = 0.95f;
                break;
            case Framing.UpperBody:
                top = headTop;
                bottom = new Vector3(headTop.X, hips.Position.Y - 0.04f * s, headTop.Z);
                fill = 0.94f;
                break;
            default:
            {
                var centre = head.Position + up * 0.05f * s;
                top = centre + up * 0.15f * s;
                bottom = centre - up * 0.17f * s;
                fill = 0.82f;
                break;
            }
        }
        target = (top + bottom) / 2f;
        return MathUtil.IsFinite(target);
    }

    // The picture now, cropped round the aim point and saved (in the background).
    private void Take(Shot shot, Vector3 target)
    {
        var rgba = Limber.Gpu.FrameGrab.Grab(out int w, out int h, out string error);
        if(rgba == null)
        {
            Note = "Couldn't take a picture: " + error;
            return;
        }
        // Where the aim point is in the picture (the picture may be bigger than the window's size in points).
        var viewport = Dalamud.Bindings.ImGui.ImGui.GetMainViewport();
        var view = viewport.Size;
        var corner = viewport.Pos;
        float scaleX = view.X > 0 ? w / view.X : 1f, scaleY = view.Y > 0 ? h / view.Y : 1f;
        float cx = w / 2f, cy = h / 2f;
        if(Plugin.GameGui.WorldToScreen(target, out var at))
        {
            cx = (at.X - corner.X) * scaleX;
            cy = (at.Y - corner.Y) * scaleY;
        }
        bool portrait = shot.Framing is Framing.FullBody or Framing.ThighUp;
        int ow = portrait ? 1024 : _settings.LargeSquares ? 1280 : 1024;
        int oh = portrait ? 1280 : ow;
        // The crop: the picture's full height, as wide as the shape needs.
        float ch = h, cw = h * (float)ow / oh;
        int number = ++Done;
        string file = Path.Combine(_folder, $"{number:D3}_{Name(shot)}");
        string caption = Caption(shot);
        bool captions = _settings.Captions;
        var task = Task.Run(() =>
        {
            try
            {
                var rgb = Limber.Gpu.FrameGrab.CropScale(rgba, w, h, cx, cy, cw, ch, ow, oh);
                Limber.Gpu.FrameGrab.SavePng(file + ".png", rgb, ow, oh);
                if(captions)
                    File.WriteAllText(file + ".txt", caption);
            }
            catch(Exception e)
            {
                Plugin.Log.Warning(e, "Limber: couldn't save a dataset picture");
            }
        });
        lock(_saving)
        {
            _saving.RemoveAll(t => t.IsCompleted);
            _saving.Add(task);
        }
    }

    private static string Name(Shot s)
    {
        string f = s.Framing switch
        {
            Framing.FullBody => "full",
            Framing.ThighUp => "thigh",
            Framing.UpperBody => "upper",
            _ => "face",
        };
        return $"{f}_{(int)s.YawDegrees:+0;-0;0}_{(int)s.ElevationDegrees:+0;-0;0}";
    }

    private string Caption(Shot s)
    {
        var tags = new List<string>();
        if(_settings.Trigger.Trim().Length > 0)
            tags.Add(_settings.Trigger.Trim());
        tags.Add(s.Framing switch
        {
            Framing.FullBody => "full body",
            Framing.ThighUp => "cowboy shot",
            Framing.UpperBody => "upper body",
            _ => "portrait, close-up",
        });
        float y = MathF.Abs(s.YawDegrees);
        tags.Add(y <= 22f ? "front view" : y <= 67f ? "three-quarter view" : y <= 112f ? "side view" : y <= 157f ? "three-quarter back view" : "from behind");
        if(s.ElevationDegrees >= 15f)
            tags.Add("from above");
        else if(s.ElevationDegrees <= -8f)
            tags.Add("from below");
        tags.Add("simple background");
        return string.Join(", ", tags);
    }
}
