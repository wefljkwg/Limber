using FFXIVClientStructs.FFXIV.Client.Game;

namespace Limber.Koi;

/// <summary>
/// The rules that keep Limber purely client-side. Every write path goes through these checks.
///
///  * Only GPose actors are ever modified. They live in the client-only object table range
///    (index 200 and up). Slots 0-199 are the characters the server knows about (you, other
///    players, NPCs, minions) and are refused even if the config file names one.
///  * Only while the game itself reports GPose, read live from game memory on the frame we write.
///  * Only the render skeleton (Havok pose) is touched. Never GameObject position, rotation,
///    emotes, actions, chat, targets, or anything the client reports to the server.
/// </summary>
public static class Safety
{
    /// <summary>First object-table slot of client-only objects (Dalamud's IObjectTable.ClientObjects starts here).</summary>
    public const int ClientObjectStart = 200;

    /// <summary>
    /// Exclusive end of the range we accept. Dalamud's ClientObjects is 200..448 inclusive; slot 448 is
    /// deliberately left out (conservative) and 449+ (event/stand/reaction objects) are never touched.
    /// </summary>
    public const int ClientObjectEnd = 448;

    public static bool IsClientOnlyIndex(int objectIndex)
        => objectIndex >= ClientObjectStart && objectIndex < ClientObjectEnd;

    /// <summary>Object-table slot of your own character in the open world.</summary>
    public const int LocalPlayerIndex = 0;

    /// <summary>
    /// The one actor that may be posed outside GPose (only when the user enabled it): your own character.
    /// Its render skeleton is still only changed on your screen; the server never receives bone data.
    /// </summary>
    public static bool IsAllowedOutsideGPose(int objectIndex) => objectIndex == LocalPlayerIndex;

    /// <summary>
    /// The one check for adult features (body mod bones, genitals, breast physics, the body rig and soft bodies):
    /// an adult body (BodyType 1) of an allowed race. Refused when the character can't be read.
    /// </summary>
    public static bool AdultAllowed(byte race, byte bodyType) => race != 3 && bodyType == 1;

    public static unsafe bool AdultAllowed(nint character)
    {
        if(character == 0)
            return false;
        var customize = ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)character)->DrawData.CustomizeData;
        return AdultAllowed(customize.Race, customize.BodyType);
    }

    /// <summary>The same rule for skeleton files (chara/human/cNNxx), which have no character to read.</summary>
    public static bool AdultAllowedSkeleton(int modelNumber) => modelNumber is not (11 or 12);

    /// <summary>
    /// Reads the game's own GPose flag at the moment of the call (Dalamud's IClientState.IsGPosing wraps the
    /// same function). Calling it again inside the skeleton hook catches a GPose exit on the same frame.
    /// </summary>
    public static bool GameIsInGPose()
    {
        try
        {
            return GameMain.IsInGPose();
        }
        catch
        {
            return false; // fail closed
        }
    }
}
