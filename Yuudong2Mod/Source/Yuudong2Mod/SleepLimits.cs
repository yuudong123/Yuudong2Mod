using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 잠이 짧아지면 같은 침대에서 사랑을 나눌 기회가 줄어 기분이 떨어진다. 잠을 줄이는 쪽만 막고 늘리는 쪽(페널티)은 그대로 둔다.
    // 수면 효율(잘 때 회복)은 종족 기본값 + 유전자를 넘지 못하고, 피로 누적 속도(깨어 있을 때)는 종족 기본값 + 유전자 + 생체리듬 이식물 밑으로 내려가지 않는다.
    // 침대 휴식 효율의 상한(1)은 Patches/SleepLimits.xml에서 건다.
    internal static class SleepLimitsUtility
    {
        // 사용자가 원래대로 두기로 한 생체리듬 이식물. 피로 하한 계산에 넣는다.
        private static readonly HashSet<string> ExemptHediffs = new HashSet<string> { "CircadianAssistant", "CircadianHalfCycler" };

        public static float BaseAndGenes(Pawn pawn, StatDef stat, bool withExemptHediffs)
        {
            float value = pawn.def.statBases.GetStatValueFromList(stat, stat.defaultBaseValue);
            List<Gene> genes = pawn.genes?.GenesListForReading;
            List<Hediff> hediffs = withExemptHediffs ? pawn.health?.hediffSet?.hediffs : null;
            if (genes != null)
            {
                for (int i = 0; i < genes.Count; i++)
                {
                    if (genes[i].Active)
                    {
                        value += genes[i].def.statOffsets.GetStatOffsetFromList(stat);
                    }
                }
            }

            if (hediffs != null)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    if (ExemptHediffs.Contains(hediffs[i].def.defName) && hediffs[i].CurStage != null)
                    {
                        value += hediffs[i].CurStage.statOffsets.GetStatOffsetFromList(stat);
                    }
                }
            }

            if (genes != null)
            {
                for (int i = 0; i < genes.Count; i++)
                {
                    if (genes[i].Active)
                    {
                        value *= genes[i].def.statFactors.GetStatFactorFromList(stat);
                    }
                }
            }

            if (hediffs != null)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    if (ExemptHediffs.Contains(hediffs[i].def.defName) && hediffs[i].CurStage != null)
                    {
                        value *= hediffs[i].CurStage.statFactors.GetStatFactorFromList(stat);
                    }
                }
            }

            return value;
        }
    }

    // 수면 효율에는 상한, 피로 누적 속도에는 하한을 건다. XML에서 우선순위를 가장 낮게 줘서 다른 보정이 다 들어간 뒤에 적용된다.
    public class StatPart_SleepLimit : StatPart
    {
#pragma warning disable CS0649
        private bool floor;
#pragma warning restore CS0649

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (TryGetLimit(req, val, out float limit))
            {
                val = limit;
            }
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (!req.HasThing || !(req.Thing is Pawn pawn))
            {
                return null;
            }

            float limit = SleepLimitsUtility.BaseAndGenes(pawn, parentStat, floor);
            return (floor ? "Yuudong2.SleepLimit.Min" : "Yuudong2.SleepLimit.Max").Translate(limit.ToStringPercent());
        }

        private bool TryGetLimit(StatRequest req, float val, out float limit)
        {
            limit = 0f;
            if (!req.HasThing || !(req.Thing is Pawn pawn))
            {
                return false;
            }

            limit = SleepLimitsUtility.BaseAndGenes(pawn, parentStat, floor);
            return floor ? val < limit : val > limit;
        }
    }

    // 상태 이상이 직접 곱하는 피로 배율(인공자아 핵 ×0.75, 갈비뼈(웨이크업) ×0.9 등)은 스탯을 거치지 않는다. 피로를 줄이는 쪽만 무시한다.
    [HarmonyPatchCategory(Features.SleepLimits)]
    [HarmonyPatch(typeof(HediffSet), nameof(HediffSet.RestFallFactor), MethodType.Getter)]
    internal static class HediffSet_RestFallFactor_SleepLimits_Patch
    {
        private static bool Prefix(HediffSet __instance, ref float __result)
        {
            float value = 1f;
            List<Hediff> hediffs = __instance.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                HediffStage stage = hediffs[i].CurStage;
                if (stage != null && stage.restFallFactor > 1f)
                {
                    value *= stage.restFallFactor;
                }
            }

            for (int i = 0; i < hediffs.Count; i++)
            {
                HediffStage stage = hediffs[i].CurStage;
                if (stage != null && stage.restFallFactorOffset > 0f)
                {
                    value += stage.restFallFactorOffset;
                }
            }

            __result = Mathf.Max(value, 0f);
            return false;
        }
    }
}
