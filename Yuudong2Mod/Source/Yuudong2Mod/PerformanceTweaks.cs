using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 틱 측정(정착민 18명, 초고속 약 630 TPS)에서 찾은 비용을 줄이는 기능들.
    internal static class PerformanceUtility
    {
        // 3배속 이상을 고속으로 본다.
        public static bool FastSpeed => Find.TickManager != null && Find.TickManager.CurTimeSpeed >= TimeSpeed.Superfast;
    }

    // 맵 밖 폰 재우기: 바닐라는 효과(comp)가 붙은 상태 이상이 하나라도 있으면 맵 밖 폰을 재우지 않고 매 틱 돌린다.
    // 사후 처리장치처럼 죽을 때만 작동하는 이식물이나 맵 밖에서 의미 없는 회복 효과는 재우는 데 방해가 되지 않게 한다.
    // 잠든 폰은 틱만 멈추고, 맵에 다시 나오면 바닐라대로 깨어난다.
    [HarmonyPatchCategory(Features.WorldPawnSleep)]
    [HarmonyPatch(typeof(WorldPawns), "DefPreventingMothball")]
    internal static class WorldPawns_DefPreventingMothball_Patch
    {
        private static readonly Type[] InertCompTypes = { typeof(HediffComp_DissolveGearOnDeath), typeof(HediffComp_HealPermanentWounds) };

        private static readonly HashSet<WorldPawnSituation> LowPrioritySituations =
            (HashSet<WorldPawnSituation>)AccessTools.Field(typeof(WorldPawns), "lowPrioritySituations").GetValue(null);

        private static void Postfix(WorldPawns __instance, Pawn p, ref HediffDef __result)
        {
            if (__result == null)
            {
                return;
            }

            // 바닐라 조건으로 막는 상태 이상을 다시 찾되, 무해한 것은 건너뛴다.
            WorldPawnSituation situation = __instance.GetSituation(p);
            List<Hediff> hediffs = p.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                if (!hediff.def.AlwaysAllowMothball && !hediff.IsPermanent() && !(hediff is Hediff_MissingPart { Bleeding: false })
                    && (!hediff.def.allowMothballIfLowPriorityWorldPawn || !LowPrioritySituations.Contains(situation)) && !IsInert(hediff))
                {
                    __result = hediff.def;
                    return;
                }
            }

            __result = null;
        }

        private static bool IsInert(Hediff hediff)
        {
            if (hediff is Hediff_Implant || hediff is Hediff_AddedPart)
            {
                return true;
            }

            if (!(hediff is HediffWithComps withComps) || withComps.comps.NullOrEmpty())
            {
                return false;
            }

            foreach (HediffComp comp in withComps.comps)
            {
                if (Array.IndexOf(InertCompTypes, comp.GetType()) < 0)
                {
                    return false;
                }
            }

            return hediff.def.stages.NullOrEmpty() || hediff.def.stages.TrueForAll(s => s.deathMtbDays <= 0f && s.hediffGivers.NullOrEmpty());
        }
    }

    // 고속 간격 틱: 바닐라는 화면 안에서 가까이 보이는 물건의 간격 틱(TickInterval)을 매 틱에 가깝게 돌리고,
    // 화면 밖이면 15틱마다 돌린다. 3배속 이상에서는 화면 안이어도 15틱마다 돌린다.
    // 간격 틱은 지난 틱 수를 받아 한꺼번에 계산하므로 결과는 같고, 보이는 변화만 덜 매끄러워진다.
    [HarmonyPatchCategory(Features.FastTickInterval)]
    [HarmonyPatch]
    internal static class Thing_UpdateRateTicks_FastTickInterval_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in GenTypes.AllTypes)
            {
                if (!typeof(Thing).IsAssignableFrom(type) || type.ContainsGenericParameters)
                {
                    continue;
                }

                MethodInfo getter = type.GetProperty(nameof(Thing.UpdateRateTicks), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetGetMethod();
                if (getter != null && !getter.IsAbstract)
                {
                    yield return getter;
                }
            }
        }

        private static void Postfix(ref int __result)
        {
            if (__result < 15 && PerformanceUtility.FastSpeed)
            {
                __result = 15;
            }
        }
    }

    // 고속 기분 재판정: 바닐라는 폰마다 150틱마다 상황 생각을 전부 다시 판정한다.
    // 3배속 이상에서는 두 번에 한 번만 다시 판정한다. 기억 생각과 욕구 변화는 그대로 150틱마다 돈다.
    [HarmonyPatchCategory(Features.FastMoodThoughts)]
    [HarmonyPatch(typeof(Need_Mood), nameof(Need_Mood.NeedInterval))]
    internal static class Need_Mood_NeedInterval_FastMoodThoughts_Patch
    {
        private static readonly ConditionalWeakTable<ThoughtHandler, object> SkipNext = new ConditionalWeakTable<ThoughtHandler, object>();
        private static readonly object Marker = new object();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo thoughtInterval = AccessTools.Method(typeof(ThoughtHandler), nameof(ThoughtHandler.ThoughtInterval));
            MethodInfo replacement = AccessTools.Method(typeof(Need_Mood_NeedInterval_FastMoodThoughts_Patch), nameof(ThoughtInterval));
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(thoughtInterval))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction);
                    replaced = true;
                    continue;
                }

                yield return instruction;
            }

            if (!replaced)
            {
                throw new InvalidOperationException("ThoughtHandler.ThoughtInterval call not found in Need_Mood.NeedInterval");
            }
        }

        private static void ThoughtInterval(ThoughtHandler thoughts)
        {
            if (SkipNext.TryGetValue(thoughts, out _))
            {
                SkipNext.Remove(thoughts);
            }
            else
            {
                thoughts.situational.SituationalThoughtInterval();
                if (PerformanceUtility.FastSpeed)
                {
                    SkipNext.Add(thoughts, Marker);
                }
            }

            thoughts.memories.MemoryThoughtInterval();
        }
    }

    // 할 일 없는 신체 부위 상실 틱 건너뛰기: 생체공학 팔다리 아래 손가락·발가락 같은 상실 부위는 매 틱 돌지만,
    // 치료 시간이 남아 있지 않으면 하는 일이 없다. 그런 것만 틱 호출을 건너뛴다(결과는 같다).
    [HarmonyPatchCategory(Features.IdleMissingParts)]
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTick))]
    internal static class Pawn_HealthTracker_HealthTick_IdleMissingParts_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo tick = AccessTools.Method(typeof(Hediff), nameof(Hediff.Tick));
            MethodInfo postTick = AccessTools.Method(typeof(Hediff), nameof(Hediff.PostTick));
            MethodInfo tickReplacement = AccessTools.Method(typeof(Pawn_HealthTracker_HealthTick_IdleMissingParts_Patch), nameof(Tick));
            MethodInfo postTickReplacement = AccessTools.Method(typeof(Pawn_HealthTracker_HealthTick_IdleMissingParts_Patch), nameof(PostTick));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(tick))
                {
                    yield return new CodeInstruction(OpCodes.Call, tickReplacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                if (instruction.Calls(postTick))
                {
                    yield return new CodeInstruction(OpCodes.Call, postTickReplacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 2)
            {
                throw new InvalidOperationException("Hediff.Tick/PostTick calls not found in Pawn_HealthTracker.HealthTick");
            }
        }

        // Hediff_MissingPart는 Tick을 덮어쓰지 않으므로(빈 메서드) 그대로 건너뛴다.
        private static void Tick(Hediff hediff)
        {
            if (hediff.GetType() != typeof(Hediff_MissingPart))
            {
                hediff.Tick();
            }
        }

        private static void PostTick(Hediff hediff)
        {
            if (hediff.GetType() == typeof(Hediff_MissingPart) && IsIdle((Hediff_MissingPart)hediff))
            {
                return;
            }

            hediff.PostTick();
        }

        // 효과가 치료 시간뿐이고 남은 치료 시간이 없으면 PostTick은 아무 일도 하지 않는다.
        private static bool IsIdle(Hediff_MissingPart hediff)
        {
            List<HediffComp> comps = hediff.comps;
            if (comps == null)
            {
                return true;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                if (!(comps[i] is HediffComp_TendDuration tend) || tend.GetType() != typeof(HediffComp_TendDuration) || (tend.tendTicksLeft > 0 && !tend.TProps.TendIsPermanent))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // 식물 흔들림 프레임마다 갱신: 바닐라는 보고 있는 맵의 모든 식물 재질에 흔들림 값을 매 틱 넣는다.
    // 화면은 프레임마다 마지막 값만 그리므로 틱에서는 빼고, 프레임 갱신(MapUpdate)에서 한 번만 넣는다. 보이는 결과는 같다.
    [HarmonyPatchCategory(Features.PlantSwayPerFrame)]
    [HarmonyPatch]
    internal static class PlantSwayPerFrame_Patch
    {
        private static readonly AccessTools.FieldRef<WindManager, float> PlantSwayHead = AccessTools.FieldRefAccess<WindManager, float>("plantSwayHead");
        private static readonly List<Material> PlantMaterials = (List<Material>)AccessTools.Field(typeof(WindManager), "plantMaterials").GetValue(null);

        private static Map lastMap;
        private static float lastHead = float.NaN;
        private static int lastCount = -1;

        // 틱 안의 "보고 있는 맵이면 재질 갱신" 조건을 늘 거짓으로 만든다.
        [HarmonyPatch(typeof(WindManager), nameof(WindManager.WindManagerTick))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> WindManagerTick_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo currentMap = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(currentMap))
                {
                    yield return new CodeInstruction(OpCodes.Ldnull).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException("Find.CurrentMap call not found in WindManager.WindManagerTick");
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        [HarmonyPostfix]
        private static void MapUpdate_Postfix(Map __instance)
        {
            if (__instance.Disposed || Find.CurrentMap != __instance)
            {
                return;
            }

            float head = PlantSwayHead(__instance.windManager);
            if (__instance == lastMap && head == lastHead && PlantMaterials.Count == lastCount)
            {
                return;
            }

            for (int i = 0; i < PlantMaterials.Count; i++)
            {
                PlantMaterials[i].SetFloat(ShaderPropertyIDs.SwayHead, head);
            }

            lastMap = __instance;
            lastHead = head;
            lastCount = PlantMaterials.Count;
        }
    }

    // 얼음 없는 칸 판정 건너뛰기: 오디세이는 매 틱 맵의 여러 칸을 돌며 물이 얼지, 얼음이 녹을지 온도와 방을 조회한다.
    // 얼 수 있는 지형(물)이 아니고 맵에 얇은 얼음이 하나도 없으면 아무 일도 일어나지 않으므로 그런 칸은 건너뛴다.
    // 얼음이 있는지는 얼음이 생길 때 바로 표시하고, 2500틱마다 다시 확인한다.
    [HarmonyPatchCategory(Features.IdleIceChecks)]
    [HarmonyPatch]
    internal static class IdleIceChecks_Patch
    {
        private const int RescanInterval = 2500;

        private static readonly AccessTools.FieldRef<TerrainGrid, Map> TerrainGridMap = AccessTools.FieldRefAccess<TerrainGrid, Map>("map");
        private static readonly AccessTools.FieldRef<TerrainGrid, TerrainDef[]> TempGrid = AccessTools.FieldRefAccess<TerrainGrid, TerrainDef[]>("tempGrid");
        private static readonly ConditionalWeakTable<Map, IceState> States = new ConditionalWeakTable<Map, IceState>();

        private class IceState
        {
            public bool hasIce;
            public int nextScanTick = int.MinValue;
        }

        private static readonly AccessTools.FieldRef<TempTerrainManager, Map> ManagerMap = AccessTools.FieldRefAccess<TempTerrainManager, Map>("map");
        private static readonly MethodInfo DoCellSteadyEffectsMethod = AccessTools.Method(typeof(TempTerrainManager), "DoCellSteadyEffects");
        private static readonly Action<TempTerrainManager, IntVec3> OriginalDoCellSteadyEffects =
            AccessTools.MethodDelegate<Action<TempTerrainManager, IntVec3>>(DoCellSteadyEffectsMethod);

        // 칸마다 부르는 작은 메서드는 인라인될 수 있어서, 반복문이 있는 Tick 안의 호출을 바꾼다.
        [HarmonyPatch(typeof(TempTerrainManager), nameof(TempTerrainManager.Tick))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Tick_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(typeof(IdleIceChecks_Patch), nameof(DoCellSteadyEffects));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(DoCellSteadyEffectsMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException("DoCellSteadyEffects call not found in TempTerrainManager.Tick");
            }
        }

        private static void DoCellSteadyEffects(TempTerrainManager manager, IntVec3 c)
        {
            Map map = ManagerMap(manager);
            if (TerrainDefOf.ThinIce == null || c.GetTerrain(map).canFreeze || HasIce(map))
            {
                OriginalDoCellSteadyEffects(manager, c);
            }
        }

        [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTempTerrain))]
        [HarmonyPostfix]
        private static void SetTempTerrain_Postfix(TerrainGrid __instance, TerrainDef newTerr)
        {
            if (newTerr != null && newTerr == TerrainDefOf.ThinIce)
            {
                States.GetValue(TerrainGridMap(__instance), _ => new IceState()).hasIce = true;
            }
        }

        private static bool HasIce(Map map)
        {
            IceState state = States.GetValue(map, _ => new IceState());
            int now = Find.TickManager.TicksGame;
            if (now >= state.nextScanTick)
            {
                state.nextScanTick = now + RescanInterval;
                state.hasIce = Array.IndexOf(TempGrid(map.terrainGrid), TerrainDefOf.ThinIce) >= 0;
            }

            return state.hasIce;
        }
    }
}
