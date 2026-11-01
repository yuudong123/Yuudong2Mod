using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    [DefOf]
    public static class ArrivalSpotDefOf
    {
        public static ThingDef Yuudong2_CaravanArrivalSpot;
        public static ThingDef Yuudong2_TradeShipDropSpot;

        static ArrivalSpotDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ArrivalSpotDefOf));
        }
    }

    internal static class ArrivalSpotUtility
    {
        private static readonly int[] EntrySlacks = { 15, 30, 60 };
        private static readonly int[] ChillSearchRadii = { 2, 4, 8 };

        public static bool TryGetSpot(Map map, ThingDef spotDef, out IntVec3 spot)
        {
            spot = IntVec3.Invalid;
            if (map == null || spotDef == null)
            {
                return false;
            }

            List<Building> spots = map.listerBuildings.AllBuildingsColonistOfDef(spotDef);
            if (spots.Count == 0)
            {
                return false;
            }

            spot = spots[0].Position;
            return true;
        }

        // 상단 도착 지점과 가장 가까운 맵 가장자리에서 진입 셀을 고른다.
        public static bool TryFindCaravanEntryCell(Map map, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (!TryGetSpot(map, ArrivalSpotDefOf.Yuudong2_CaravanArrivalSpot, out IntVec3 spot))
            {
                return false;
            }

            int edgeDistance = Mathf.Min(
                Mathf.Min(spot.x, map.Size.x - 1 - spot.x),
                Mathf.Min(spot.z, map.Size.z - 1 - spot.z));
            TraverseParms traverseParms = TraverseParms.For(TraverseMode.PassDoors);

            foreach (int slack in EntrySlacks)
            {
                float maxDistance = edgeDistance + slack;
                if (RCellFinder.TryFindRandomPawnEntryCell(out result, map, CellFinder.EdgeRoadChance_Neutral, false, c =>
                    c.InHorDistOf(spot, maxDistance)
                    && map.reachability.CanReach(c, spot, PathEndMode.OnCell, traverseParms)))
                {
                    return true;
                }
            }

            return RCellFinder.TryFindRandomPawnEntryCell(out result, map, CellFinder.EdgeRoadChance_Neutral, false, c =>
                map.reachability.CanReach(c, spot, PathEndMode.OnCell, traverseParms));
        }

        // RCellFinder.TryFindRandomSpotJustOutsideColony와 같은 시그니처. 상단 대기 위치를 도착 지점 근처로 바꾼다.
        public static bool TryFindCaravanChillSpot(IntVec3 root, Map map, Pawn searcher, out IntVec3 result, Predicate<IntVec3> extraValidator)
        {
            if (TryGetSpot(map, ArrivalSpotDefOf.Yuudong2_CaravanArrivalSpot, out IntVec3 spot))
            {
                Predicate<IntVec3> validator = c =>
                    c.InBounds(map)
                    && c.Standable(map)
                    && !c.Fogged(map)
                    && (extraValidator == null || extraValidator(c));

                if (validator(spot))
                {
                    result = spot;
                    return true;
                }

                foreach (int radius in ChillSearchRadii)
                {
                    if (CellFinder.TryFindRandomCellNear(spot, map, radius, validator, out result))
                    {
                        return true;
                    }
                }

                // 지점에 갈 수 없으면 바닐라 위치로 가되, 조용히 넘어가지 않고 이유를 알린다.
                string reason = !spot.Standable(map) ? "Yuudong2.ArrivalSpot.Reason.Blocked"
                    : spot.Fogged(map) ? "Yuudong2.ArrivalSpot.Reason.Fogged"
                    : "Yuudong2.ArrivalSpot.Reason.NoPath";
                Messages.Message("Yuudong2.ArrivalSpot.Unreachable".Translate(reason.Translate()), new TargetInfo(spot, map), MessageTypeDefOf.CautionInput, false);
            }

            return RCellFinder.TryFindRandomSpotJustOutsideColony(root, map, searcher, out result, extraValidator);
        }

        // 정착지에 온 다른 세력(적대 아님)의 동물. 상단 짐 동물처럼 방목 동물이어도 정착지 문을 지나가게 한다.
        public static bool IsVisitorAnimal(Pawn p)
        {
            return p.RaceProps.Animal && p.Faction != null && !p.Faction.IsPlayer && !p.Faction.HostileTo(Faction.OfPlayer);
        }

        // 0보다 크면 물자 낙하 지점을 쓰지 않는다 (적 중앙 낙하 공습 등).
        [ThreadStatic]
        public static int SuppressDropSpot;

        // 물자 낙하 지점이 있으면 그 근처의 지붕 없는 낙하 셀을 고른다.
        public static bool TryFindDropSpotCell(Map map, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            return TryGetSpot(map, ArrivalSpotDefOf.Yuudong2_TradeShipDropSpot, out IntVec3 spot)
                && DropCellFinder.TryFindDropSpotNear(spot, map, out result, false, false);
        }
    }
}
