using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 무기는 불에 탈 때만 내구도가 깎인다. 들고 있든 바닥에 있든 같다.
    // 마모·피격·비바람 부패·폭발 등 다른 피해는 모두 막는다. 옷·방어구는 바닐라대로 둔다.
    internal static class EquipmentDurabilityUtility
    {
        private static bool[] protectedDefs = new bool[0];

        // 맥주처럼 무기로도 들 수 있지만 쌓이는 물건은 빼서 바닐라대로 둔다.
        private static bool IsEquipment(ThingDef def)
        {
            return def.category == ThingCategory.Item && def.useHitPoints && def.stackLimit == 1
                && def.equipmentType == EquipmentType.Primary && !def.IsApparel;
        }

        public static void ApplyDefPatches()
        {
            protectedDefs = new bool[DefDatabase<ThingDef>.DefCount];
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (IsEquipment(def))
                {
                    protectedDefs[def.index] = true;
                    // 부패 표시와 지붕 밑으로 옮기는 우선순위도 없앤다.
                    def.SetStatBaseValue(StatDefOf.DeteriorationRate, 0f);
                }
            }
        }

        public static bool IsProtected(Thing thing)
        {
            ushort index = thing.def.index;
            return index < protectedDefs.Length && protectedDefs[index];
        }

        public static bool IsFire(DamageDef def)
        {
            return def.workerClass != null && typeof(DamageWorker_Flame).IsAssignableFrom(def.workerClass);
        }
    }

    [HarmonyPatchCategory(Features.EquipmentDurability)]
    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class Thing_TakeDamage_EquipmentDurability_Patch
    {
        private static bool Prefix(Thing __instance, DamageInfo dinfo, ref DamageWorker.DamageResult __result)
        {
            if (dinfo.Def == null || !EquipmentDurabilityUtility.IsProtected(__instance) || EquipmentDurabilityUtility.IsFire(dinfo.Def))
            {
                return true;
            }

            __result = new DamageWorker.DamageResult();
            return false;
        }
    }
}
