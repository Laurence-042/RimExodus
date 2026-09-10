using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Optional direct-fire implementations register behind this small facade.  Core combat code must not
    /// name third-party verb/projectile types: doing so would turn an optional compatibility layer into a
    /// loader dependency.
    /// </summary>
    public static class SeamlessDirectFireSupport
    {
        public static bool IsSupportedVerb(Verb verb)
        {
            if (verb == null || verb.verbProps == null || verb.verbProps.IsMeleeAttack) return false;
            if (verb is Verb_LaunchProjectile vanilla)
                return vanilla.Projectile?.projectile != null;
            if (verb is Verb_CastAbility abilityVerb)
            {
                var launch = abilityVerb.Ability?.CompOfType<CompAbilityEffect_LaunchProjectile>();
                if (launch?.Props?.projectileDef != null) return true;
            }
            return SeamlessCombatExtendedCompat.IsSupportedVerb(verb);
        }

        public static bool IsExternalProjectile(Thing thing) =>
            SeamlessCombatExtendedCompat.IsProjectile(thing);
    }
}
