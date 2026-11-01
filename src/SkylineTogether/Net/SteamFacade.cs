using System;
using System.Runtime.CompilerServices;
using Steamworks;

namespace SkylineTogether.Net
{
    /// <summary>
    /// The only place the session layer asks Steam who the player is (fork, direct mode). Mono JIT-compiles a whole method
    /// and loads every type it references, also in a branch that is not taken; so the Steamworks calls live in these
    /// methods, each <see cref="MethodImplOptions.NoInlining"/>, and a caller in direct mode never reaches them
    /// (<see cref="Session.SessionManager.DirectOnly"/>). On the player's game copy Steamworks loads but answers with bogus
    /// or failing results; every method catches and returns <c>false</c>, <c>0</c> or <c>null</c>. Main thread.
    /// </summary>
    internal static class SteamFacade
    {
        /// <summary>Steam is running with a logged-in user.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsAvailable()
        {
            try
            {
                return SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The logged-in user's SteamID64, or 0.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong LocalId()
        {
            try
            {
                return SteamUser.GetSteamID().m_SteamID;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The logged-in user's Steam persona name, or null.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string PersonaName()
        {
            try
            {
                return SteamFriends.GetPersonaName();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
