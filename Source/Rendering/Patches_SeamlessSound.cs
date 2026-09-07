using HarmonyLib;
using Verse;
using Verse.Sound;

namespace RimExodus
{
    /// <summary>
    /// Spatial one-shot sounds from a visible active neighbor belong to the same composite tactical view.
    /// Rebuild their SoundInfo on the focused map at the projected location; sustainers and camera-only
    /// sounds use different entry points and deliberately retain vanilla behavior.
    /// </summary>
    [HarmonyPatch(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot),
        new[] { typeof(SoundDef), typeof(SoundInfo) })]
    public static class Patch_SoundStarter_PlayOneShot_SeamlessView
    {
        public static void Prefix(ref SoundInfo info)
        {
            if (info.IsOnCamera) return;
            var maker = info.Maker;
            var sourceMap = maker.Map;
            if (sourceMap == null || sourceMap == Find.CurrentMap) return;
            if (!SeamlessViewProjection.TryProjectVisibleToCurrent(sourceMap, maker.CenterVector3,
                    out var projected))
            {
                return;
            }

            var original = info;
            var redirected = SoundInfo.InMap(
                new TargetInfo(projected.ToIntVec3(), Find.CurrentMap), original.Maintenance);
            redirected.volumeFactor = original.volumeFactor;
            redirected.pitchFactor = original.pitchFactor;
            redirected.testPlay = original.testPlay;
            foreach (var parameter in original.DefinedParameters)
            {
                redirected.SetParameter(parameter.Key, parameter.Value);
            }
            info = redirected;
        }
    }
}
