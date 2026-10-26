using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Durable Clothes (Continued) 포팅. 기본 설정값(마모 5%)을 그대로 쓴다.
    [HarmonyPatchCategory(Features.DurableClothes)]
    [HarmonyPatch(typeof(Pawn_ApparelTracker), "TakeWearoutDamageForDay")]
    internal static class Pawn_ApparelTracker_TakeWearoutDamageForDay_Patch
    {
        private const float WearFactor = 0.05f;

        private static bool Prefix(Thing ap, Pawn_ApparelTracker __instance)
        {
            if (ap?.def == null || !ap.def.useHitPoints)
            {
                return true;
            }

            int damage = GenMath.RoundRandom(ap.def.apparel.wearPerDay * WearFactor);
            if (damage > 0)
            {
                ap.TakeDamage(new DamageInfo(DamageDefOf.Deterioration, damage));
            }

            if (ap.Destroyed && PawnUtility.ShouldSendNotificationAbout(__instance.pawn) && !__instance.pawn.Dead)
            {
                string text = "MessageWornApparelDeterioratedAway".Translate(GenLabel.ThingLabel(ap.def, ap.Stuff), __instance.pawn).CapitalizeFirst();
                Messages.Message(text, __instance.pawn, MessageTypeDefOf.NegativeEvent);
            }

            return false;
        }
    }

    // Infinite apparel durability 포팅. 플레이어 쪽 폰이 입은 옷의 내구도를 계속 최대로 유지한다.
    [HarmonyPatchCategory(Features.InfiniteApparelDurability)]
    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.ApparelTrackerTickInterval))]
    internal static class Pawn_ApparelTracker_ApparelTrackerTickInterval_Patch
    {
        private static void Postfix(Pawn_ApparelTracker __instance, int delta)
        {
            Pawn pawn = __instance.pawn;
            if (!pawn.IsHashIntervalTick(59, delta) || !pawn.Spawned || pawn.Dead)
            {
                return;
            }

            if (!pawn.IsColonist && !pawn.IsColonyMechPlayerControlled && !pawn.IsColonySubhumanPlayerControlled)
            {
                return;
            }

            foreach (Apparel apparel in __instance.WornApparel)
            {
                if (apparel.def.useHitPoints)
                {
                    apparel.HitPoints = apparel.MaxHitPoints;
                }
            }
        }
    }
}
