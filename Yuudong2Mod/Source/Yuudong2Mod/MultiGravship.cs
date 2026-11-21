using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // 바닐라는 한 맵에 중력 엔진이 하나라고 보고 짜여 있다. 적 배가 내려앉거나 그 배를 빼앗아 엔진이 둘 이상이 되면 어긋나는 곳을 바로잡는다.
    public static class MultiGravshipUtility
    {
        private static readonly List<Building_GravEngine> tmpEngines = new List<Building_GravEngine>();

        public static bool Several(Map map)
        {
            return map != null && map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).Count > 1;
        }

        public static bool Foreign(Building_GravEngine engine)
        {
            return engine.Faction != null && engine.Faction != Faction.OfPlayer;
        }

        // 연결 판정에만 쓰고 엔진의 계산 상태는 건드리지 않는다. 시설 연결이 바뀌는 중에는 지탱 칸 수가 아직 맞지 않기 때문이다.
        private static HashSet<IntVec3> Connected(Building_GravEngine engine)
        {
            bool dirty = engine.substructureDirty;
            HashSet<IntVec3> cells = engine.AllConnectedSubstructureNoRegen;
            engine.substructureDirty |= dirty;
            return cells;
        }

        // 다른 엔진의 하부 구조 위에 있는 시설은 그 엔진 것이다. 어느 배에도 올라가 있지 않은 시설은 바닐라대로 둔다.
        public static bool OnAnotherShip(Building_GravEngine engine, IntVec3 cell)
        {
            if (!engine.Spawned || !Several(engine.Map) || Connected(engine).Contains(cell))
            {
                return false;
            }
            List<Thing> engines = engine.Map.listerThings.ThingsOfDef(ThingDefOf.GravEngine);
            for (int i = 0; i < engines.Count; i++)
            {
                if (engines[i] != engine && engines[i] is Building_GravEngine other && other.Spawned && Connected(other).Contains(cell))
                {
                    return true;
                }
            }
            return false;
        }

        public static void Relink(Map map)
        {
            if (!Several(map))
            {
                return;
            }
            tmpEngines.Clear();
            tmpEngines.AddRange(map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).OfType<Building_GravEngine>().Where(e => e.Spawned));
            foreach (Building_GravEngine engine in tmpEngines)
            {
                engine.GetComp<CompAffectedByFacilities>()?.Notify_ThingChanged();
            }
            foreach (Building_GravEngine engine in tmpEngines)
            {
                engine.ForceSubstructureDirty();
            }
            tmpEngines.Clear();
        }

        public static void RelinkAllMaps()
        {
            foreach (Map map in Find.Maps)
            {
                Relink(map);
            }
        }

        public static Thing EngineFor(IEnumerable<Thing> engines, Pawn pawn)
        {
            Thing first = null;
            foreach (Thing thing in engines)
            {
                first = first ?? thing;
                if (thing is Building_GravEngine engine && ((engine.pawnsToBoard != null && engine.pawnsToBoard.Contains(pawn)) || (engine.pawnsToLeave != null && engine.pawnsToLeave.Contains(pawn))))
                {
                    return thing;
                }
            }
            return first;
        }
    }

    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(CompAffectedByFacilities), nameof(CompAffectedByFacilities.CanPotentiallyLinkTo), new[] { typeof(ThingDef), typeof(IntVec3), typeof(Rot4) })]
    internal static class CompAffectedByFacilities_CanPotentiallyLinkTo_MultiGravship_Patch
    {
        private static void Postfix(CompAffectedByFacilities __instance, IntVec3 facilityPos, ref bool __result)
        {
            if (__result && __instance.parent is Building_GravEngine engine && MultiGravshipUtility.OnAnotherShip(engine, facilityPos))
            {
                __result = false;
            }
        }
    }

    // 바닐라는 첫 번째 엔진에서 멀기만 해도 꺼진 것으로 본다. 범위 안에 있는 플레이어 엔진을 모두 확인한다.
    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(CompGravshipFacility), nameof(CompGravshipFacility.CanBeActive), MethodType.Getter)]
    internal static class CompGravshipFacility_CanBeActive_MultiGravship_Patch
    {
        private static bool Prefix(CompGravshipFacility __instance, ref bool __result)
        {
            Thing parent = __instance.parent;
            if (!ModsConfig.OdysseyActive || !parent.Spawned || !MultiGravshipUtility.Several(parent.Map))
            {
                return true;
            }
            __result = false;
            if (!CompFacility_CanBeActive_Original.Get(__instance))
            {
                return false;
            }
            List<Thing> engines = parent.Map.listerThings.ThingsOfDef(ThingDefOf.GravEngine);
            for (int i = 0; i < engines.Count; i++)
            {
                if (engines[i] is Building_GravEngine engine && engine.Spawned && engine.Faction == Faction.OfPlayer
                    && parent.Position.InHorDistOf(engine.Position, __instance.Props.maxDistance)
                    && ((__instance.Props.onlyRequiresLooseConnection && engine.LooselyConnectedToGravEngine(parent)) || engine.OnValidSubstructure(parent)))
                {
                    __result = true;
                    break;
                }
            }
            return false;
        }
    }

    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch]
    internal static class CompFacility_CanBeActive_Original
    {
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(CompFacility), nameof(CompFacility.CanBeActive), MethodType.Getter)]
        public static bool Get(CompFacility instance)
        {
            throw new System.NotImplementedException();
        }
    }

    // 적 세력의 엔진은 플레이어 엔진이 아니다. 플레이어 쪽 엔진이 여럿이면 탑승이 진행 중인 엔진, 그다음 플레이어 소유 엔진을 고른다.
    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(GravshipUtility), nameof(GravshipUtility.GetPlayerGravEngine_NewTemp))]
    internal static class GravshipUtility_GetPlayerGravEngine_MultiGravship_Patch
    {
        private static void Postfix(Map map, ref Building_GravEngine __result)
        {
            if (__result == null || map == null)
            {
                return;
            }
            List<Thing> engines = map.listerThings.ThingsOfDef(ThingDefOf.GravEngine);
            if (engines.Count == 0 || (engines.Count == 1 && !MultiGravshipUtility.Foreign(__result)))
            {
                return;
            }
            Building_GravEngine best = null;
            int bestScore = -1;
            for (int i = 0; i < engines.Count; i++)
            {
                if (!(engines[i] is Building_GravEngine engine) || MultiGravshipUtility.Foreign(engine))
                {
                    continue;
                }
                int score = (engine.pawnsToBoard != null || engine.pawnsToLeave != null ? 2 : 0) + (engine.Faction == Faction.OfPlayer ? 1 : 0);
                if (score > bestScore)
                {
                    best = engine;
                    bestScore = score;
                }
            }
            __result = best;
        }
    }

    // 탑승·하선 명령은 그 정착민을 명단에 올린 엔진의 것을 따른다.
    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(JobGiver_BoardOrLeaveGravship), "TryGiveJob")]
    internal static class JobGiver_BoardOrLeaveGravship_TryGiveJob_MultiGravship_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo pick = AccessTools.Method(typeof(MultiGravshipUtility), nameof(MultiGravshipUtility.EngineFor));
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!replaced && instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method && method.Name == "FirstOrDefault" && method.DeclaringType == typeof(Enumerable))
                {
                    replaced = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Call, pick);
                    continue;
                }
                yield return instruction;
            }
            if (!replaced)
            {
                Log.Error("[Yuudong2Mod] 중력부양선 여러 대: 탑승 일에서 엔진을 고르는 부분을 찾지 못했습니다. 엔진이 둘 이상인 맵에서는 탑승 명령이 첫 엔진만 봅니다. JobGiver_BoardOrLeaveGravship이 바뀌었는지 확인하세요.");
            }
        }
    }

    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(SubstructureGrid), nameof(SubstructureGrid.MarkDirty))]
    internal static class SubstructureGrid_MarkDirty_MultiGravship_Patch
    {
        private static void Postfix(Map ___map)
        {
            List<Thing> engines = ___map.listerThings.ThingsOfDef(ThingDefOf.GravEngine);
            for (int i = 0; i < engines.Count; i++)
            {
                if (engines[i] is Building_GravEngine engine)
                {
                    engine.substructureDirty = true;
                }
            }
        }
    }

    // 빼앗은 엔진을 차지하면 그 배의 시설이 켜지므로 하부 구조를 다시 계산하게 한다.
    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(Building), nameof(Building.SetFaction))]
    internal static class Building_SetFaction_MultiGravship_Patch
    {
        private static void Postfix(Building __instance)
        {
            if (__instance is Building_GravEngine engine && engine.Spawned)
            {
                engine.Map.substructureGrid?.MarkDirty();
                engine.ForceSubstructureDirty();
            }
        }
    }

    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(GravshipPlacementUtility), nameof(GravshipPlacementUtility.PlaceGravshipInMap))]
    internal static class GravshipPlacementUtility_PlaceGravshipInMap_MultiGravship_Patch
    {
        private static void Postfix(Map map)
        {
            MultiGravshipUtility.Relink(map);
        }
    }
}
