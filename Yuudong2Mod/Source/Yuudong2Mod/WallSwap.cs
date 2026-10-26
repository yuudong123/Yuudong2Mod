using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 벽 교체: Gloomy 벽을 이미 설치된 벽 위에 지으면 해체하지 않고 그 자리에서 그 벽으로 바꾼다.
    // 재질 교체의 교체 프레임을 그대로 쓴다. 기존 벽을 허물지 않으므로 지붕과 방이 그대로 유지된다.
    //
    // 방향 규칙 (환풍벽):
    //  1) 설치 미리보기의 방향을 그대로 쓴다.
    //  2) 그 방향으로 양쪽(앞뒤) 칸이 막혀 있으면 90도 돌린 방향을 시도한다. 벽 줄을 따라 환풍벽을 끌어 놓아도 알맞게 선다.
    //  3) 같은 벽·같은 재질·같은 방향이면 아무것도 하지 않는다 (같은 방향 재클릭으로 엉뚱하게 돌지 않는다).
    internal static class WallSwapUtility
    {
        public static readonly HashSet<string> GloomyWalls = new HashSet<string>
        {
            "RGK_Wall", "GL_Wall", "RGK_VentWall", "GL_VentWall", "RGK_WindowWall", "GL_WindowWall",
            "GL_SterileWall", "GL_HeavyWall", "GL_HeavyWallEmb", "GL_HeavySterileWall",
            "GL_HeavyVentWall", "GL_SterileVentWall", "GL_HeavySterileVentWall",
        };

        public static bool IsGloomyWall(BuildableDef def)
        {
            return def is ThingDef thingDef && GloomyWalls.Contains(thingDef.defName) && ReplaceStuffUtility.HasFrameDefFor(thingDef);
        }

        public static bool IsReplaceableWall(ThingDef def)
        {
            if (def?.building == null || def.building.isNaturalRock || def.IsFrame || def.IsBlueprint || def.size != IntVec2.One
                || typeof(Building_Door).IsAssignableFrom(def.thingClass))
            {
                return false;
            }

            return def.IsWall || def.building.isPlaceOverableWall || (def.replaceTags != null && def.replaceTags.Contains("Wall")) || GloomyWalls.Contains(def.defName);
        }

        private static bool PlaceWorkersAllow(ThingDef def, IntVec3 cell, Rot4 rot, Map map, Thing old)
        {
            if (def.PlaceWorkers == null)
            {
                return true;
            }

            foreach (PlaceWorker worker in def.PlaceWorkers)
            {
                if (!worker.AllowsPlacing(def, cell, rot, map, old).Accepted)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TryFind(IntVec3 cell, Map map, ThingDef newDef, ThingDef stuff, Rot4 placingRot, out Thing old, out Rot4 rot)
        {
            old = null;
            rot = newDef.rotatable ? placingRot : Rot4.North;
            if (map == null || !cell.InBounds(map))
            {
                return false;
            }

            // 일반 청사진이나 건설 중인 프레임이 있으면 바닐라 규칙에 맡긴다.
            foreach (Thing thing in cell.GetThingList(map))
            {
                if (thing is Blueprint || (thing is Frame && !(thing is ReplaceFrame)))
                {
                    return false;
                }
            }

            Building wall = cell.GetEdifice(map);
            if (wall == null || wall.Position != cell || wall.Faction != Faction.OfPlayer || !IsReplaceableWall(wall.def))
            {
                return false;
            }

            ThingDef useStuff = newDef.MadeFromStuff ? stuff : null;
            if (wall.def == newDef && wall.Stuff == useStuff && (!newDef.rotatable || wall.Rotation == rot))
            {
                return false;
            }

            Rot4[] candidates = newDef.rotatable ? new[] { rot, rot.Rotated(RotationDirection.Clockwise) } : new[] { rot };
            foreach (Rot4 candidate in candidates)
            {
                if (wall.def == newDef && wall.Stuff == useStuff && wall.Rotation == candidate)
                {
                    continue;
                }

                if (!GenConstruct.CanBuildOnTerrain(newDef, cell, map, candidate, wall, useStuff) || !PlaceWorkersAllow(newDef, cell, candidate, map, wall))
                {
                    continue;
                }

                old = wall;
                rot = candidate;
                return true;
            }

            return false;
        }

        public static void DoSwap(Thing old, ThingDef newDef, ThingDef stuff, Rot4 rot)
        {
            ReplaceFrame existing = ReplaceStuffUtility.FrameFor(old);
            existing?.Destroy(DestroyMode.Cancel);
            ThingDef useStuff = newDef.MadeFromStuff ? stuff : null;
            if (DebugSettings.godMode)
            {
                SwapNow(old, newDef, useStuff, rot, null);
                return;
            }

            ReplaceStuffUtility.PlaceReplaceFrame(old, useStuff, newDef, rot);
        }

        public static void FinalizeSwap(ReplaceFrame frame, Pawn worker)
        {
            Thing old = frame.oldThing;
            ThingDef newDef = (ThingDef)frame.def.entityDefToBuild;
            ThingDef stuff = frame.Stuff;
            Rot4 rot = frame.Rotation;
            frame.resourceContainer.ClearAndDestroyContents();
            frame.Destroy();
            SwapNow(old, newDef, stuff, rot, worker);
            worker?.records.Increment(RecordDefOf.ThingsConstructed);
        }

        // 기존 벽을 해체할 때처럼 일부 재료를 돌려주고, 같은 틱에 새 벽을 세운다 (지붕이 무너지지 않는다).
        private static void SwapNow(Thing old, ThingDef newDef, ThingDef stuff, Rot4 rot, Pawn worker)
        {
            Map map = old.Map;
            IntVec3 cell = old.Position;
            if (GenLeaving.CanBuildingLeaveResources(old, DestroyMode.Deconstruct))
            {
                GenLeaving.DoLeavingsFor(old, map, DestroyMode.Deconstruct);
            }

            old.Destroy(DestroyMode.WillReplace);
            Thing built = ThingMaker.MakeThing(newDef, stuff);
            built.SetFactionDirect(Faction.OfPlayer);
            CompQuality quality = built.TryGetComp<CompQuality>();
            if (quality != null && worker != null)
            {
                quality.SetQuality(QualityUtility.GenerateQualityCreatedByPawn(worker, SkillDefOf.Construction), ArtGenerationContext.Colony);
            }

            // 돌려준 재료가 벽 칸에 떨어졌으면 옆으로 옮긴다.
            GenSpawn.Spawn(built, cell, map, rot, WipeMode.FullRefund);
        }
    }

    // Gloomy 벽을 이미 있는 벽 위에 놓을 수 있게 한다.
    [HarmonyPatchCategory(Features.GloomyWallReplace)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    internal static class GenConstruct_CanPlaceBlueprintAt_WallSwap_Patch
    {
        private static void Postfix(ref AcceptanceReport __result, BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, ThingDef stuffDef)
        {
            if (__result.Accepted || !WallSwapUtility.IsGloomyWall(entDef))
            {
                return;
            }

            if (WallSwapUtility.TryFind(center, map, (ThingDef)entDef, stuffDef, rot, out Thing _, out Rot4 _))
            {
                __result = AcceptanceReport.WasAccepted;
            }
        }
    }

    // 놓을 때 청사진 대신 벽 교체 프레임을 만든다. 재질 교체보다 먼저 판단한다.
    [HarmonyPatchCategory(Features.GloomyWallReplace)]
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
    [HarmonyPriority(Priority.High)]
    internal static class Designator_Build_DesignateSingleCell_WallSwap_Patch
    {
        private static bool Prefix(Designator_Build __instance, IntVec3 c, BuildableDef ___entDef, Rot4 ___placingRot)
        {
            if (!WallSwapUtility.IsGloomyWall(___entDef))
            {
                return true;
            }

            ThingDef def = (ThingDef)___entDef;
            ThingDef stuff = def.MadeFromStuff ? __instance.StuffDef : null;
            if (!WallSwapUtility.TryFind(c, __instance.Map, def, stuff, ___placingRot, out Thing old, out Rot4 rot))
            {
                return true;
            }

            WallSwapUtility.DoSwap(old, def, stuff, rot);
            FleckMaker.ThrowMetaPuffs(GenAdj.OccupiedRect(c, rot, def.size), __instance.Map);
            return false;
        }
    }
}
