// Shared wire protocol between the Koikatsu sender (BepInEx, .NET 3.5 / C# 7.3)
// and the FFXIV receiver (Dalamud API 15, .NET 10). Keep this file compatible
// with C# 7.3 and the .NET 3.5 BCL: it is compiled into both projects.
//
// Transport: UDP datagrams to 127.0.0.1:<port> (default 47820), one packet per streamed character
// per frame. Several characters share the port; each is tagged with its channel (0-7).
// All values little-endian. Positions are in the Koikatsu character's local space
// (ChaControl.transform), Unity convention: +X = character's right, +Y = up, +Z = forward.
//
// Packet layout (version 2):
//   uint32  Magic            0x58464B4B ("KKFX")
//   uint16  Version          2
//   uint16  JointCount       number of joint records that follow (== Joint.Count)
//   uint32  Frame            monotonically increasing frame counter
//   byte    Channel          0..MaxChannels-1, which link this character belongs to
//   JointCount x {
//     byte   Valid           0 = joint not found on this character
//     float  X, Y, Z
//   }
//   byte    FaceFlags        bit0 face pattern data valid
//                            bit1 eye-open value valid
//                            bit2 mouth-open value valid
//                            bit3 gaze vector valid
//   int32   EyesPattern
//   int32   MouthPattern
//   int32   EyebrowPattern
//   float   EyesOpenMax      0..1 (character's max eye opening)
//   float   EyeOpen          0..1 live eye openness (blink), 1 = fully open
//   float   MouthOpen        0..1 live mouth openness (lip sync / pattern)
//   float   GazeX, GazeY, GazeZ   world gaze direction expressed in character-local space
//   uint16  NameLength
//   byte[]  Name (UTF-8)
//   --- optional, may be absent in older senders ---
//   byte    RegionCount      number of face regions that follow (== FaceRegion.Count, or 0)
//   RegionCount x {
//     byte   Valid
//     float  PosX, PosY, PosZ     where the region sits on the face (see below)
//     float  DispX, DispY, DispZ  how far the expression has moved it from the neutral mesh
//   }
//
//   --- optional, may be absent in older senders ---
//   byte    SenderFlags      bit0 the sender has paused Koikatsu's animations
//
// Control packets (FFXIV -> Koikatsu), UDP to 127.0.0.1:ControlPort(port) (default 47821):
//   uint32  Magic            ControlMagic
//   byte    Command          CmdPause / CmdResume / CmdToggle
// or, about once a second, which FFXIV actor each channel drives (for Koikatsu's channel window):
//   uint32  Magic            ControlMagic
//   byte    Command          CmdLinks
//   byte    Count
//   Count x {
//     byte   Channel         0..MaxChannels-1
//     byte   Active          1 = the link is on and its actor is in the scene
//     uint16 NameLength
//     byte[] Name (UTF-8)    the FFXIV actor's name (or "GPose target" / "My character")
//   }
//
// Face regions are measured in a face frame that is the same on both rigs, so no handedness
// conversion is needed: X = towards the character's right eye, Y = up (mouth -> eyes), Z = out of the face.
// Origin = midpoint between the eyes. Units: X and Z in eye-to-eye distance, Y in eye-line-to-mouth
// distance. Eyelid displacement (LidUp*/LidLow*) is instead in units of that eye's open height.

namespace Limber.Protocol
{
    public enum Joint : byte
    {
        Hips = 0,
        Spine1,
        Spine2,
        Spine3,
        Neck,
        Head,

        ShoulderL,
        UpperArmL,
        ElbowL,
        WristL,
        ShoulderR,
        UpperArmR,
        ElbowR,
        WristR,

        ThighL,
        KneeL,
        AnkleL,
        FootL,
        ToeL,
        ThighR,
        KneeR,
        AnkleR,
        FootR,
        ToeR,

        ThumbL1, ThumbL2, ThumbL3,
        IndexL1, IndexL2, IndexL3,
        MiddleL1, MiddleL2, MiddleL3,
        RingL1, RingL2, RingL3,
        LittleL1, LittleL2, LittleL3,

        ThumbR1, ThumbR2, ThumbR3,
        IndexR1, IndexR2, IndexR3,
        MiddleR1, MiddleR2, MiddleR3,
        RingR1, RingR2, RingR3,
        LittleR1, LittleR2, LittleR3,

        EyeL,
        EyeR,

        // Genital chain (only present on characters with an uncensor / futa body).
        // DanP0..DanP6 are 7 points resampled evenly along Koikatsu's shaft chain
        // (cm_J_dan100_00 -> cm_J_dan109_00), base to tip.
        DanP0, DanP1, DanP2, DanP3, DanP4, DanP5, DanP6,
        TesticleL,
        TesticleR,

        // Breast physics. Root/Tip are the physics chain's base and nipple end after physics; Rest is
        // where the tip would sit without bounce (tracked over time in the chest frame). FFXIV applies
        // the Rest -> Tip swing to its own breast bones.
        BustRootL, BustTipL, BustRestL,
        BustRootR, BustTipR, BustRestR,

        Count
    }

    /// <summary>Points on the face whose movement is mirrored (see packet description).</summary>
    public enum FaceRegion : byte
    {
        LidUpL, LidUpR, LidLowL, LidLowR,
        BrowInL, BrowInR, BrowMidL, BrowMidR, BrowOutL, BrowOutR,
        MouthCornerL, MouthCornerR,
        LipUp, LipUpL, LipUpR,
        LipLow, LipLowL, LipLowR,
        CheekL, CheekR,

        Count
    }

    public static class Wire
    {
        public const uint Magic = 0x58464B4B; // "KKFX" little-endian
        public const ushort Version = 2;
        public const int MaxChannels = 8;
        public const int DefaultPort = 47820;
        public const int MaxPacketSize = 4096;

        public const byte FaceHasPatterns = 1 << 0;
        public const byte FaceHasEyeOpen = 1 << 1;
        public const byte FaceHasMouthOpen = 1 << 2;
        public const byte FaceHasGaze = 1 << 3;

        public const byte SenderAnimPaused = 1 << 0;

        public const uint ControlMagic = 0x4D43424C; // "LBCM" little-endian
        public const byte CmdPause = 1;
        public const byte CmdResume = 2;
        public const byte CmdToggle = 3;
        public const byte CmdLinks = 4;

        /// <summary>Port the Koikatsu sender listens on for control packets: the pose port + 1.</summary>
        public static int ControlPort(int posePort)
        {
            return posePort >= 65535 ? posePort - 1 : posePort + 1;
        }
    }
}
