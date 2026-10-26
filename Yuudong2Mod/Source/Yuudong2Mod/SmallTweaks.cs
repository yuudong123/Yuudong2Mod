using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // No Random Relations 포팅. 새로 생성되는 폰에 무작위 가족·연인 관계를 만들지 않는다.
    [HarmonyPatchCategory(Features.NoRandomRelations)]
    [HarmonyPatch(typeof(PawnGenerationRequest), nameof(PawnGenerationRequest.CanGeneratePawnRelations), MethodType.Getter)]
    internal static class PawnGenerationRequest_CanGeneratePawnRelations_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    // Empire -100 goodwill in scenarios Fix 포팅.
    // 제국이 다른 세력에게 영구 적대인 설정에서 플레이어 세력을 예외 목록에 넣어, 시나리오 시작 시 호감도 -100을 막는다.
    internal static class EmpireGoodwillFixUtility
    {
        public static void ApplyDefPatches()
        {
            if (!ModsConfig.RoyaltyActive)
            {
                return;
            }

            FactionDef empire = DefDatabase<FactionDef>.GetNamedSilentFail("Empire");
            if (empire == null || empire.permanentEnemyToEveryoneExcept.NullOrEmpty())
            {
                return;
            }

            foreach (FactionDef faction in DefDatabase<FactionDef>.AllDefsListForReading)
            {
                if (faction.isPlayer && !empire.permanentEnemyToEveryoneExcept.Contains(faction))
                {
                    empire.permanentEnemyToEveryoneExcept.Add(faction);
                }
            }
        }
    }

    // Gloomy Furniture Fix의 코드 부분 포팅. 벽걸이 가구가 있는 벽 칸에도 다른 청사진을 놓을 수 있게 한다.
    [HarmonyPatchCategory(Features.GloomyFurnitureFix)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintOver))]
    [HarmonyBefore("com.Gloomylynx.rimworld.mod")]
    internal static class GenConstruct_CanPlaceBlueprintOver_GloomyWallLamp_Patch
    {
        private const string AttachedToWallPlaceWorker = "Placeworker_AttachedToWall";

        private static bool Prefix(ThingDef oldDef, ref bool __result)
        {
            List<Type> placeWorkers = oldDef?.placeWorkers;
            if (placeWorkers == null)
            {
                return true;
            }

            for (int i = 0; i < placeWorkers.Count; i++)
            {
                if (placeWorkers[i].Name == AttachedToWallPlaceWorker)
                {
                    __result = true;
                    return false;
                }
            }

            return true;
        }
    }

    // Room Food 포팅. 정착민이 밥을 먹을 때 근처 식탁이 있는 방 안의 음식을 먼저 고른다.
    [HarmonyPatchCategory(Features.RoomFood)]
    [HarmonyPatch(typeof(FoodUtility), "SpawnedFoodSearchInnerScan")]
    [HarmonyPriority(Priority.Last)]
    internal static class FoodUtility_SpawnedFoodSearchInnerScan_RoomFood_Patch
    {
        private static void Postfix(ref Thing __result, Pawn eater, IntVec3 root, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator)
        {
            Thing roomFood = RoomFoodUtility.FindRoomFood(__result, eater, root, traverseParams, maxDistance, validator);
            if (roomFood != null)
            {
                __result = roomFood;
            }
        }
    }

    internal static class RoomFoodUtility
    {
        public static Thing FindRoomFood(Thing vanillaFood, Pawn eater, IntVec3 root, TraverseParms traverseParams, float maxDistance, Predicate<Thing> foodValidator)
        {
            Pawn getter = traverseParams.pawn ?? eater;
            if (eater == null || getter == null || !getter.IsFreeColonist || !eater.RaceProps.Humanlike)
            {
                return null;
            }

            Map map = eater.Map;
            District district;
            if (getter != eater)
            {
                district = eater.GetDistrict();
                if (district?.Room == null || district.Room.IsHuge)
                {
                    return null;
                }
            }
            else
            {
                float searchDistance = Yuudong2Settings.Current.roomFoodTableDistance;
                if (vanillaFood != null)
                {
                    searchDistance = Mathf.Max(searchDistance, (root - vanillaFood.Position).LengthHorizontal);
                }

                Thing table = GenClosest.ClosestThingReachable(root, map, ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial), PathEndMode.OnCell, traverseParams, searchDistance,
                    t => IsTableWithFood(t, getter, map));
                if (table == null)
                {
                    return null;
                }

                district = table.GetDistrict();
            }

            List<Thing> candidates = new List<Thing>();
            foreach (Region region in district.Regions)
            {
                candidates.AddRange(region.ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree));
            }

            return GenClosest.ClosestThing_Global(eater.Position, candidates, maxDistance, foodValidator,
                f => FoodUtility.FoodOptimality(eater, f, FoodUtility.GetFinalIngestibleDef(f), (root - f.Position).LengthManhattan));
        }

        private static bool IsTableWithFood(Thing thing, Pawn getter, Map map)
        {
            if (!(thing is Building building) || building.def.surfaceType != SurfaceType.Eat || building.Position.GetDangerFor(getter, map) != Danger.None)
            {
                return false;
            }

            Room room = building.GetRoom();
            if (room == null || room.IsHuge || room.IsPrisonCell)
            {
                return false;
            }

            District district = building.GetDistrict();
            if (district == null)
            {
                return false;
            }

            foreach (Region region in district.Regions)
            {
                if (!region.ListerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).NullOrEmpty())
                {
                    return true;
                }
            }

            return false;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            settings.roomFoodTableDistance = Mathf.RoundToInt(listing.SliderLabeled(
                "Yuudong2.RoomFood.Distance".Translate(settings.roomFoodTableDistance), settings.roomFoodTableDistance, 1f, 200f, 0.5f,
                "Yuudong2.RoomFood.Distance.Desc".Translate()));
            listing.End();
        }
    }
}
