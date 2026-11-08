using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 출산 품질의 "산모 나이" 항목은 실제 나이 기준이라 수명이 850살인 드라고니안도 65살이 넘으면 보너스가 0이 된다.
    // 20살까지는 그대로 두고, 그 뒤의 나이만 인간 수명 대비 비율로 줄여서 곡선에 넣는다. 인간은 결과가 같다.
    // 출산 창에 보이는 나이는 실제 나이 그대로다.
    [HarmonyPatchCategory(Features.BirthAgeByLifespan)]
    [HarmonyPatch]
    internal static class BirthAgeByLifespan_Patch
    {
        private const float Pivot = 20f;

        private static readonly AccessTools.FieldRef<Pawn_AgeTracker, Pawn> TrackerPawn = AccessTools.FieldRefAccess<Pawn_AgeTracker, Pawn>("pawn");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(RitualOutcomeComp_PawnAge), nameof(RitualOutcomeComp_PawnAge.Count));
            yield return AccessTools.Method(typeof(RitualOutcomeComp_PawnAge), nameof(RitualOutcomeComp_PawnAge.GetDesc));
            yield return AccessTools.Method(typeof(RitualOutcomeComp_PawnAge), nameof(RitualOutcomeComp_PawnAge.GetQualityFactor));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            MethodInfo ageGetter = AccessTools.PropertyGetter(typeof(Pawn_AgeTracker), nameof(Pawn_AgeTracker.AgeBiologicalYearsFloat));
            MethodInfo replacement = AccessTools.Method(typeof(BirthAgeByLifespan_Patch), nameof(EffectiveAge));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(ageGetter))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException($"Pawn_AgeTracker.AgeBiologicalYearsFloat call not found in RitualOutcomeComp_PawnAge.{original.Name}");
            }
        }

        private static float EffectiveAge(Pawn_AgeTracker tracker)
        {
            float age = tracker.AgeBiologicalYearsFloat;
            float lifeExpectancy = TrackerPawn(tracker)?.RaceProps?.lifeExpectancy ?? 0f;
            float humanLifeExpectancy = ThingDefOf.Human.race.lifeExpectancy;
            if (age <= Pivot || lifeExpectancy <= 0f || lifeExpectancy == humanLifeExpectancy)
            {
                return age;
            }

            return Pivot + (age - Pivot) * humanLifeExpectancy / lifeExpectancy;
        }
    }
}
