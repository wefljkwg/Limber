using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Limber.Protocol;

namespace Limber;

[Serializable]
public class FaceBoneDelta
{
    public float Rx, Ry, Rz, Rw = 1f; // local rotation delta (quaternion) relative to reference pose
    public float Px, Py, Pz;          // local translation delta relative to reference pose
}

/// <summary>A captured FFXIV facial pose: bone name -> local delta from the reference face.</summary>
[Serializable]
public class FacePreset
{
    public Dictionary<string, FaceBoneDelta> Bones { get; set; } = new();
}

/// <summary>One link: Koikatsu channel N drives one GPose actor.</summary>
[Serializable]
public class LinkConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    /// <summary>Koikatsu channel, 0-based (shown as 1-8).</summary>
    public int Channel { get; set; }
    /// <summary>0 = current GPose target, -1 = my character, otherwise a GPose actor's object-table slot (200+).</summary>
    public int TargetObjectIndex { get; set; }

    /// <summary>
    /// Koikatsu face-region displacements recorded as "neutral" for this link (x,y,z per region), or null.
    /// Subtracted before mirroring, so a character whose default face isn't fully relaxed maps cleanly.
    /// Replaced (never mutated) so the hook can read it safely.
    /// </summary>
    public float[]? FaceNeutral { get; set; }
}

public enum ExpressionMode
{
    Off = 0,
    Automatic = 1,
    CapturedPresets = 2,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 3;
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Settings for the Limber tab (IK posing).</summary>
    public RigSettings Ik { get; set; } = new();

    /// <summary>The window opens by itself once, so a fresh install visibly does something.</summary>
    public bool HasShownWindow { get; set; } = false;

    // Connection
    public int Port { get; set; } = Wire.DefaultPort;
    public float TimeoutSeconds { get; set; } = 1.0f;
    /// <summary>Blend between Koikatsu frames so motion is smooth when FFXIV runs at a higher frame rate.</summary>
    public bool Interpolate { get; set; } = true;

    /// <summary>Also drive your own character outside GPose (links set to "My character" only).</summary>
    public bool AllowOutsideGPose { get; set; } = false;

    // Sync (two players)
    /// <summary>Your relay on your own Cloudflare account, e.g. https://limber-relay.lmb-1a2b3c.workers.dev</summary>
    public string? RelayUrl { get; set; }
    /// <summary>Random key only your game and your relay know; stops anyone else using your relay.</summary>
    public string? RelayOwnerToken { get; set; }
    public string? RelayAccountId { get; set; }
    /// <summary>Connect to your relay automatically when the game starts.</summary>
    public bool SyncEnabled { get; set; } = false;
    /// <summary>Opt-in: allow creating invite codes and receiving sync requests.</summary>
    public bool SyncAcceptRequests { get; set; } = false;
    /// <summary>When you host: Koikatsu channel (0-based) for your character, and for your partner's (-1 = none).</summary>
    public int SyncHostChannel { get; set; } = 0;
    public int SyncPartnerChannel { get; set; } = 1;
    /// <summary>Pose updates per second sent to your partner when you host.</summary>
    public int SyncRate { get; set; } = 30;
    /// <summary>Allow syncing your character and your partner's in the open world, not only in GPose.</summary>
    public bool SyncOutsideGPose { get; set; } = false;
    /// <summary>On your screen, when following: allow showing these from the host's setup.</summary>
    public bool SyncShowGenitals { get; set; } = true;
    public bool SyncShowBreasts { get; set; } = true;

    /// <summary>The whole plugin on / off (/limber on|off): posing, soft bodies, props, the live mesh and the links.</summary>
    public bool PluginEnabled { get; set; } = true;
    public bool OutsideGPoseUnified { get; set; }
    /// <summary>Show the unfinished Video and VLC Live tabs (main Advanced tab).</summary>
    public bool ShowVideoTab { get; set; } = false;
    public bool ShowVlcTab { get; set; } = false;

    // Links
    /// <summary>Master switch for all links.</summary>
    public bool Enabled { get; set; } = false;
    public List<LinkConfig> Links { get; set; } = new();

    /// <summary>Legacy (v1) single target; migrated into <see cref="Links"/> on load.</summary>
    public int TargetObjectIndex { get; set; } = 0;

    // Body
    public bool DriveTorso { get; set; } = true;
    public bool DriveHead { get; set; } = true;
    public bool DriveArms { get; set; } = true;
    public bool DriveHands { get; set; } = true;
    public bool DriveLegs { get; set; } = true;
    /// <summary>YAS / IVCS genital bones, when the actor's skeleton has them.</summary>
    public bool DriveGenitals { get; set; } = true;
    /// <summary>Copy Koikatsu breast physics (bounce) onto j_mune.</summary>
    public bool DriveBreasts { get; set; } = true;
    public float BreastStrength { get; set; } = 1.0f;

    // Body tracking model for the Video and VLC Live tabs: accurate (RTMW3D) or fast (MediaPipe); graphics card or CPU.
    public bool TrackerAccurate { get; set; } = true;
    public bool TrackerGpu { get; set; } = true;

    // Video tab (motion from videos)
    public bool VideoTrackFingers { get; set; } = false;
    /// <summary>Use the model's guesses for hidden / cut-off limbs (kept to the person's own limb lengths).</summary>
    public bool VideoGuessHiddenLimbs { get; set; } = true;
    public int VideoMaxFps { get; set; } = 30;
    public float VideoSmoothing { get; set; } = 0.4f;
    /// <summary>0-based Koikatsu channel clips play on (shown as 1-8).</summary>
    public int VideoChannel { get; set; } = 7;
    /// <summary>Two-person clips: person 2's channel (0-based).</summary>
    public int VideoChannel2 { get; set; } = 5;
    public int VideoPeople { get; set; } = 1;
    public bool VideoSwapSides { get; set; } = false;
    public bool VideoMirror { get; set; } = false;
    public bool VideoLoop { get; set; } = true;
    public float VideoSpeed { get; set; } = 1f;
    /// <summary>Seconds over which the end of a loop eases into its start (0 = a hard jump).</summary>
    public float VideoLoopBlend { get; set; } = 0.25f;
    // GVHMR (optional, set up separately in WSL).
    public bool GvhmrStaticCamera { get; set; } = false;
    public string GvhmrDistro { get; set; } = "";

    // VLC live tab
    public int LiveVlcPort { get; set; } = 8080;
    /// <summary>VLC's own web-interface password (Lua HTTP). Only ever sent to VLC on this PC (127.0.0.1).</summary>
    public string LiveVlcPassword { get; set; } = "";
    /// <summary>0-based channel live poses play on (shown as 1-8).</summary>
    public int LiveChannel { get; set; } = 6;
    public int LiveChannel2 { get; set; } = 4;
    public int LivePeople { get; set; } = 1;
    public bool LiveSwapSides { get; set; } = false;
    public bool LiveFingers { get; set; } = false;
    public bool LiveGuessHiddenLimbs { get; set; } = true;
    public bool LiveMirror { get; set; } = false;
    public float LiveSmoothing { get; set; } = 0.3f;

    public bool ApplyRootHeight { get; set; } = true;
    public bool ApplyRootHorizontal { get; set; } = false;
    public bool MirrorLeftRight { get; set; } = false;
    public bool FlipFacing { get; set; } = false;
    /// <summary>0 = no smoothing, 0.9 = heavy smoothing.</summary>
    public float Smoothing { get; set; } = 0.35f;
    /// <summary>Degrees, compensates for the different eye/head-joint geometry of the two rigs.</summary>
    public float HeadPitchOffset { get; set; } = 0f;

    // Face
    /// <summary>
    /// Master switch. Off = never write any face bone (expressions, jaw, gaze), so in-game facial
    /// emotes or a posing plugin (Brio / Ktisis) can control the face. The head itself is still posed.
    /// </summary>
    public bool DriveFace { get; set; } = true;
    public ExpressionMode ExpressionMode { get; set; } = ExpressionMode.Automatic;
    public float ExpressionStrength { get; set; } = 1.0f;
    public bool ExpressionEyelids { get; set; } = true;
    /// <summary>Multiplier on eyelid movement only (on top of ExpressionStrength).</summary>
    public float EyelidStrength { get; set; } = 1.0f;
    /// <summary>Extra gap between the lids and the eyeball while they move, as a fraction of the lid's distance from the eye centre.</summary>
    public float EyelidClearance { get; set; } = 0.05f;
    public bool ExpressionBrows { get; set; } = true;
    public bool ExpressionMouth { get; set; } = true;
    public bool ExpressionCheeks { get; set; } = true;
    public bool DriveGaze { get; set; } = true;
    public float GazeMaxAngle { get; set; } = 25f;
    public bool DriveJaw { get; set; } = true;
    public float JawMaxAngle { get; set; } = 18f;
    public bool DriveFacePresets { get; set; } = true;
    public bool InvertEyeOpen { get; set; } = false;
    public float ExpressionBlendSpeed { get; set; } = 8f;

    /// <summary>Koikatsu pattern id -> FFXIV preset name, per category.</summary>
    public Dictionary<int, string> EyesPatternMap { get; set; } = new();
    public Dictionary<int, string> MouthPatternMap { get; set; } = new();
    public Dictionary<int, string> EyebrowPatternMap { get; set; } = new();

    /// <summary>Preset library keyed by "tribe_sex" so each FFXIV face can have its own captures.</summary>
    public Dictionary<string, Dictionary<string, FacePreset>> FacePresets { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
