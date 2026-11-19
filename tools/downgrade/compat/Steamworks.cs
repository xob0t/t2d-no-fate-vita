using System;
using System.Runtime.InteropServices;
using System.Text;

// Stand-ins for the Steamworks.NET types the game's code still names once the platform pass has run
// (compat/rewriter/PlatformPasses.cs). The pass stubs SteamManager, sends SetAchievement and SetStat to Store.cs and
// cuts the Steam end of Achievements.unlock, so no method of the game calls into Steamworks any more; what is left
// are the types of a few fields and parameters (SteamManager's callbacks and call results, their closures' fields,
// LeaderboardState.Handle) and one attribute's typeof (SteamManager.SteamAPIDebugTextHook's MonoPInvokeCallback).
// So the pipeline leaves the Steamworks assembly out (its root "-Steamworks"), with its 72 Rewind gap methods and the
// references to Unity 6's runtime its recovered code makes, and the rewriter's naming convention sends those types
// here: Steamworks.X is T2DCompat.Steamworks_X. Each keeps its kind (struct, class, enum, delegate) and generic arity,
// which the game's signatures encode; none has members, since nothing uses them.
// The list is every top-level Steamworks type Assembly-CSharp's metadata names (25 in the pipeline's recovery, 24 in the assessment's), not only the 9 still used: the
// platform pass runs before the rewriter's type retargeting (it matches the game's own names), and Cecil's list of a
// read module's type references is the file's, so the retargeting still meets the types only the removed code used.
// Those aren't written; the nested ones (CallResult`1/APIDispatchDelegate, Callback`1/DispatchDelegate,
// Packsize/ValvePackingSentinel_t) are skipped by the retargeting and must not be used, which the rewriter's Verify and
// PlatformCheck check.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    // Still used after the platform pass: field, parameter and attribute types.
    public struct Steamworks_SteamLeaderboard_t { }
    public struct Steamworks_GameOverlayActivated_t { }
    public struct Steamworks_UserStatsReceived_t { }
    public struct Steamworks_LeaderboardFindResult_t { }
    public struct Steamworks_LeaderboardScoreUploaded_t { }
    public struct Steamworks_LeaderboardScoresDownloaded_t { }
    public sealed class Steamworks_Callback<T> { }
    public sealed class Steamworks_CallResult<T> { }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Steamworks_SteamAPIWarningMessageHook_t(int nSeverity, StringBuilder pchDebugText);

    // Named only by code the platform pass removes.
    public static class Steamworks_CallbackDispatcher { }
    public static class Steamworks_CSteamAPIContext { }
    public static class Steamworks_SteamAPI { }
    public static class Steamworks_SteamFriends { }
    public static class Steamworks_SteamUserStats { }
    public static class Steamworks_Packsize { }
    public static class Steamworks_NativeMethods { }
    public class Steamworks_InteropHelp { }
    public struct Steamworks_CSteamID { }
    public struct Steamworks_LeaderboardEntry_t { }
    public struct Steamworks_RemoteStorageEnumerateUserSubscribedFilesResult_t { }
    public struct Steamworks_SteamAPICall_t { }
    public struct Steamworks_SteamLeaderboardEntries_t { }
    public enum Steamworks_ELeaderboardUploadScoreMethod { }
    public enum Steamworks_EResult { }
    public enum Steamworks_ESteamAPIInitResult { }
}
