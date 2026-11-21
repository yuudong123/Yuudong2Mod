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
