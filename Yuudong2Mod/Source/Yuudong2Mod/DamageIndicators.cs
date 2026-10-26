using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Damage Indicators 포팅. 피해량, 보호막 흡수량, 근접 공격 빗나감을 화면에 숫자로 띄운다.
    internal static class DamageIndicatorsUtility
    {
        private const float MoteDuration = 3.65f;

        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> currentTarget = AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");

        public static bool ShouldDisplay(Thing target, Thing instigator)
        {
            Yuudong2Settings settings = Yuudong2Settings.Current;
            if (!settings.damageNeutralFaction && (target.Faction == null || (instigator != null && instigator.Faction == null)))
            {
                return false;
            }

            if (settings.damagePawnsOnly && !(target is Pawn))
            {
                return false;
            }

            return !settings.damagePawnInstigatorOnly || instigator is Pawn;
        }

        public static void ThrowDamage(float damage, Map map, Vector3 location)
        {
            Color color = damage >= 90f ? Color.cyan
                : damage >= 70f ? Color.magenta
                : damage >= 50f ? Color.red
                : damage >= 30f ? Color.Lerp(Color.red, Color.yellow, 0.5f)
                : damage >= 10f ? Color.yellow
                : Color.white;
            MoteMaker.ThrowText(location, map, damage.ToString("F0"), color, MoteDuration);
        }

        public static void ThrowText(Vector3 location, Map map, string text)
        {
            MoteMaker.ThrowText(location, map, text, MoteDuration);
        }

        public static Thing MeleeTarget(Verb verb)
        {
            LocalTargetInfo target = currentTarget(verb);
            return target.HasThing ? target.Thing : null;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Yuudong2.Damage.Neutral".Translate(), ref settings.damageNeutralFaction, "Yuudong2.Damage.Neutral.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Damage.PawnsOnly".Translate(), ref settings.damagePawnsOnly, "Yuudong2.Damage.PawnsOnly.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Damage.PawnInstigatorOnly".Translate(), ref settings.damagePawnInstigatorOnly, "Yuudong2.Damage.PawnInstigatorOnly.Desc".Translate());
            listing.End();
        }
    }

    [HarmonyPatchCategory(Features.DamageIndicators)]
    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class Thing_TakeDamage_DamageIndicators_Patch
    {
        private static void Postfix(DamageInfo dinfo, Thing __instance, DamageWorker.DamageResult __result)
        {
            float damage = __result.totalDamageDealt;
            if (damage > 0.01f && __instance.Map != null && DamageIndicatorsUtility.ShouldDisplay(__instance, dinfo.Instigator))
            {
                DamageIndicatorsUtility.ThrowDamage(damage, __instance.Map, __instance.DrawPos);
            }
        }
    }

    [HarmonyPatchCategory(Features.DamageIndicators)]
    [HarmonyPatch(typeof(CompShield), nameof(CompShield.PostPreApplyDamage))]
    internal static class CompShield_PostPreApplyDamage_DamageIndicators_Patch
    {
        private static void Postfix(DamageInfo dinfo, bool absorbed, CompShield __instance)
        {
            Pawn owner = __instance.parent is Apparel apparel ? apparel.Wearer : __instance.parent as Pawn;
            if (!absorbed || owner?.Map == null)
            {
                return;
            }

            if (__instance.ShieldState == ShieldState.Resetting)
            {
                DamageIndicatorsUtility.ThrowText(owner.DrawPos, owner.Map, "Yuudong2.Damage.ShieldBroken".Translate());
                return;
            }

            float energyLoss = dinfo.Amount * __instance.Props.energyLossPerDamage * 100f;
            float maxEnergy = __instance.parent.GetStatValue(StatDefOf.EnergyShieldEnergyMax) * 100f;
            DamageIndicatorsUtility.ThrowText(owner.DrawPos, owner.Map, "(- " + energyLoss.ToString("F0") + "/ " + maxEnergy + ")");
        }
    }

    [HarmonyPatchCategory(Features.DamageIndicators)]
    [HarmonyPatch(typeof(Verb_MeleeAttack), "SoundMiss")]
    internal static class Verb_MeleeAttack_SoundMiss_DamageIndicators_Patch
    {
        private static void Postfix(Verb_MeleeAttack __instance)
        {
            Thing target = DamageIndicatorsUtility.MeleeTarget(__instance);
            if (target?.Map != null)
            {
                DamageIndicatorsUtility.ThrowText(target.DrawPos, target.Map, "Yuudong2.Damage.Miss".Translate());
            }
        }
    }
}
