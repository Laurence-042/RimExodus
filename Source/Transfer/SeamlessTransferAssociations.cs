using System;
using System.Collections.Generic;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Extensible bridge for relationships represented by multiple spawned pawns.
    /// A provider may capture an association before the primary pawn is despawned,
    /// move its associated pawns through the normal transfer routine, and restore
    /// the relationship after every participant has arrived.
    /// </summary>
    internal static class SeamlessTransferAssociations
    {
        internal interface ISession
        {
            void TransferAndRestore(Func<Pawn, bool> transferAssociatedPawn);
        }

        private static readonly List<Func<Pawn, ISession>> providers = new List<Func<Pawn, ISession>>();

        internal static void Register(Func<Pawn, ISession> provider)
        {
            if (provider != null) providers.Add(provider);
        }

        internal static List<ISession> Capture(Pawn pawn)
        {
            List<ISession> result = null;
            for (var i = 0; i < providers.Count; i++)
            {
                try
                {
                    var session = providers[i](pawn);
                    if (session == null) continue;
                    if (result == null) result = new List<ISession>();
                    result.Add(session);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RimExodus] Transfer association capture failed; primary pawn transfer continues: {ex}");
                }
            }
            return result;
        }

        internal static void Restore(List<ISession> sessions, Func<Pawn, bool> transferAssociatedPawn)
        {
            if (sessions == null) return;
            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    sessions[i].TransferAndRestore(transferAssociatedPawn);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RimExodus] Transfer association restore failed; primary pawn remains transferred: {ex}");
                }
            }
        }
    }
}
