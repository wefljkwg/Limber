using System.Collections.Generic;
using System;

namespace Limber;

// Settings for the Limber tab (IK posing). Stored inside the main Configuration as "Ik".
[Serializable]
public sealed class RigSettings
{
    // Master switch: when off, nothing is changed on any character (goals are kept).
    public bool Enabled { get; set; } = true;

    // Keep posing characters outside GPose too.
    public bool WorkOutsideGPose { get; set; } = true;

    public bool ShowHandles { get; set; } = true;
    public bool ShowAllBones { get; set; } = false;
    public bool ShowFaceBones { get; set; } = false;
    /// <summary>Dots / list entries for the physics rig's bones (breast, butt, testicle lattices); off by default.</summary>
    public bool ShowRigBones { get; set; } = false;
    public bool ShowRotationGizmo { get; set; } = true;
    public bool HideHandlesWithGameUi { get; set; } = false;
    public float HandleSize { get; set; } = 10f;

    // Gizmo on the selected handle: move (arrows) instead of rotate (rings), and world axes instead of the bone's own.
    public bool GizmoMove { get; set; } = false;
    public bool GizmoWorldAxes { get; set; } = false;

    // Undo / redo posing changes with the hotkeys below (Hotkeys tab).
    public bool UndoHotkeys { get; set; } = true;
    public Hotkey UndoKey { get; set; } = new() { Key = 0x5A, Ctrl = true }; // Ctrl+Z
    public Hotkey RedoKey { get; set; } = new() { Key = 0x59, Ctrl = true }; // Ctrl+Y
    /// <summary>Opens / closes Limber's window (another way in besides /limber and Dalamud's plugin list).</summary>
    public Hotkey OpenKey { get; set; } = new() { Key = 0x4C, Ctrl = true, Shift = true }; // Ctrl+Shift+L

    // Moving the hips keeps the chest and feet where they are (off: the hips carry the whole body).
    public bool HipsMoveAlone { get; set; } = true;

    // Pose whichever character is targeted (GPose target in GPose), instead of a fixed pick.
    public bool FollowTarget { get; set; } = true;

    // Breast rig (extra bones for the soft body): grid shape, and whether to preview it on the selected character.
    public Limber.BodyRig.BreastRigShape BreastRig { get; set; } = new();
    public bool PreviewBreastRig { get; set; } = false;
    public Limber.BodyRig.SoftBodySettings SoftBody { get; set; } = new();
    public Limber.BodyRig.BreastRigShape TesticleRig { get; set; } = Limber.BodyRig.RigParts.LegacyTesticles.DefaultShape();
    // Lattice rigs (what's moved now). BreastRig / TesticleRig above are the old grids, still built as they were.
    public Limber.BodyRig.BreastRigShape? BreastLattice { get; set; }
    public Limber.BodyRig.BreastRigShape? TesticleLattice { get; set; }
    public Limber.BodyRig.SoftBodySettings SoftBodyTesticles { get; set; } = Limber.BodyRig.SoftBodySettings.TesticleDefaults();
    public Limber.BodyRig.BreastRigShape? ButtLattice { get; set; }
    // The body shape grids' placement (by part key), fitted when rigging.
    public System.Collections.Generic.Dictionary<string, Limber.BodyRig.BreastRigShape> ShapeLattices { get; set; } = new();
    public Limber.BodyRig.SoftBodySettings SoftBodyButt { get; set; } = Limber.BodyRig.SoftBodySettings.ButtDefaults();
    // Props tab: pill-shaped colliders the soft bodies always collide with.
    public System.Collections.Generic.List<Limber.BodyRig.PropCollider> Props { get; set; } = new();
    // How hard props push the soft body bones (Props tab), 0..1.
    public float PropBoneStrength { get; set; } = 1f;
    // Devices tab: size of every hand collider at once.
    public float HandColliderSize { get; set; } = 1f;
    // Props tab: motion props (piston, wheel, cycle) that move a bone on a loop.
    public System.Collections.Generic.List<Limber.Rig.MotionProp> Motions { get; set; } = new();
    // Master switch for the wireframes (collisions stay on either way).
    public bool PropWiresShown { get; set; } = true;
    // One-click rig: fit each part's box to the worn models first.
    public bool RigAutoFit { get; set; } = true;
    // Baked into the rigged models (when rigging): how round the part is made, and how much the middle crease is filled.
    public float TesticleRoundness { get; set; } = 0f;
    public float TesticleFill { get; set; } = 0f;
    // Draw every visible prop's wireframe all the time (otherwise only while the Props tab is open).
    public bool PropWiresAlways { get; set; } = false;
    public bool ShowBodyColliders { get; set; } = false;
    public Limber.Gpu.LiveMeshSettings? LiveMesh { get; set; }
    public bool PropGizmo { get; set; } = true;
    public bool PropGizmoMove { get; set; } = true;
    public bool PropGizmoWorldAxes { get; set; } = false;

    // Body Shape tab: each character's slider values (by character name), the shape last built into its models,
    // saved presets, and whether the shape shows live.
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, float>> BodyShapes { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, float>> BodyShapeBuilt { get; set; } = new();
    public System.Collections.Generic.List<Limber.BodyShape.BodyPreset> BodyPresets { get; set; } = new();
    public bool BodyShapePreview { get; set; } = true;

    // Oral page.
    public Limber.Rig.OralSettings Oral { get; set; } = new();

    // Dynamic Bones page: per character (by name).
    public System.Collections.Generic.Dictionary<string, Limber.Rig.DynamicBoneSettings> DynamicBones { get; set; } = new();

    /// <summary>FK poses (FK Posing page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.FkPoseSettings> FkPoses { get; set; } = new();

    /// <summary>Throbbing (Limber Animations page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.ThrobSettings> Throb { get; set; } = new();

    /// <summary>Twerking (Limber Animations page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.TwerkSettings> Twerk { get; set; } = new();

    /// <summary>Ahegao face (Limber Animations page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.AhegaoSettings> Ahegao { get; set; } = new();

    /// <summary>Expansion (Limber Animations page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.ExpansionSettings> Expansion { get; set; } = new();

    /// <summary>Breast size for tops with size shapes (Breast size page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.BustShapeSettings> BustShapes { get; set; } = new();

    /// <summary>Testicle throbbing (Limber Animations page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.BallThrobSettings> BallThrob { get; set; } = new();

    /// <summary>Appearance &amp; gear page: saved looks, and whether changes go on as they're made.</summary>
    public System.Collections.Generic.List<Limber.Rig.AppearanceData> AppearancePresets { get; set; } = new();
    public bool AppearanceLive { get; set; } = true;

    /// <summary>Camera page: saved shots, and the unlocked limits.</summary>
    public System.Collections.Generic.List<Limber.Rig.CameraShot> CameraShots { get; set; } = new();
    public bool CameraUnlock { get; set; }
    public float CameraMaxDistance { get; set; } = 60f;

    /// <summary>The Timeline page's animations, which one is open, and its options.</summary>
    public System.Collections.Generic.List<Limber.Rig.TimelineClip> Timelines { get; set; } = new();
    public int TimelineIndex { get; set; }
    public bool TimelineAutoKey { get; set; }
    public float TimelineSnap { get; set; } = 1f / 30f;

    /// <summary>Head / eye tracking (Look at page), per character name.</summary>
    public System.Collections.Generic.Dictionary<string, Limber.Rig.LookAtSettings> LookAt { get; set; } = new();

    // Renderer page: bones marked as breast / butt / testicle bones (pattern with "{side}" -> part key, or "none").
    public Dictionary<string, string> SoftBoneMarks { get; set; } = new();

    // Bone scale page: per character (by name), per bone.
    public Dictionary<string, Dictionary<string, Limber.BodyShape.BoneScaleEntry>> BoneScales { get; set; } = new();

    // The page the main window shows (its left-hand list).
    public string UiPage { get; set; } = "start";
    public bool BodyShapeKeepFeet { get; set; } = true;
    // Body Shape tab: build again by itself a moment after a detail slider is let go.
    public bool BodyShapeAutoBuild { get; set; }

    // Which part the Body rig tab shows ("breasts" / "testicles" / "butt").
    public string RigPartKey { get; set; } = "breasts";

    [NonSerialized]
    private Action? _save;

    public void Initialize(Action save) => _save = save;

    public void Save() => _save?.Invoke();
}
