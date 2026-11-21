using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 다른 플레이어의 세이브에서 가져온 정착민(Real Ruins 등)은 그쪽 모드에만 있는 특성 단계를 달고 올 수 있다.
    // 없는 단계는 볼 때마다 오류를 찍으므로, 특성을 줄 때 정의된 첫 단계로 바꾼다.
    public static class TraitDegreeFix
    {
        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(TraitSet), nameof(TraitSet.GainTrait)), prefix: new HarmonyMethod(typeof(TraitDegreeFix), nameof(Prefix)));
            harmony.Patch(AccessTools.Method(typeof(Verse.AI.HaulAIUtility), nameof(Verse.AI.HaulAIUtility.PawnCanAutomaticallyHaulFast)),
                prefix: new HarmonyMethod(typeof(TraitDegreeFix), nameof(HaulablePrefix)));
        }

        // 운반 후보 목록을 잠깐 기억해 두는 모드(AllowTool의 긴급 운반)는 그사이 사라진 물건을 넘길 수 있고, 바닐라는 그 물건의 맵을 읽다가 오류를 낸다.
        private static bool HaulablePrefix(Thing t, ref bool __result)
        {
            if (t != null && (t.Spawned || t.MapHeld != null))
            {
                return true;
            }

            __result = false;
            return false;
        }

        public static bool Fix(Trait trait)
        {
            if (trait?.def == null || trait.def.degreeDatas.NullOrEmpty())
            {
                return false;
            }

            int degree = trait.Degree;
            for (int i = 0; i < trait.def.degreeDatas.Count; i++)
            {
                if (trait.def.degreeDatas[i].degree == degree)
                {
                    return false;
                }
            }

            Traverse.Create(trait).Field("degree").SetValue(trait.def.degreeDatas[0].degree);
            return true;
        }

        private static void Prefix(Trait trait)
        {
            Fix(trait);
        }
    }
}
