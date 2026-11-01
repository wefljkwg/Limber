using FFXIVClientStructs.FFXIV.Client.Game;

namespace Limber.Koi;

public static class Safety
{
    public const int ClientObjectStart = 200;
    public const int ClientObjectEnd = 448;

    public static bool IsClientOnlyIndex(int objectIndex) => true;

    public const int LocalPlayerIndex = 0;

    public static bool IsAllowedOutsideGPose(int objectIndex) => true;

    public static bool AdultAllowed(byte race, byte bodyType) => true;

    public static unsafe bool AdultAllowed(nint character) => true;

    public static bool AdultAllowedSkeleton(int modelNumber) => true;

    public static bool GameIsInGPose() => true;
}