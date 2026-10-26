using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 무역 송신기 리워크. 전원이 들어온 송신기가 맵에 하나라도 있으면 반경 대신 맵 전체의 정착지 창고에 있는 물건을 거래에 쓴다.
    // "정착지 창고"는 비축 구역, 플레이어 소유 저장 건물(선반 등), 책장·의상 거치대 같은 보관함, 유전자 은행이다.
    // 바닥에 떨어진 물건(적이 떨어뜨린 무기, 바위 등)과 금지된 물건은 쓰지 않는다. 대신 건설 비용이 10배다.
    internal static class MapWideTradeBeacon
    {
        public const int CostMultiplier = 10;

        public static void ApplyDefChanges()
        {
            ThingDef beacon = ThingDefOf.OrbitalTradeBeacon;
            if (beacon == null)
            {
                return;
            }

            if (beacon.costList != null)
            {
                foreach (ThingDefCountClass cost in beacon.costList)
                {
                    cost.count *= CostMultiplier;
                }
            }

            beacon.description += "\n\n" + "Yuudong2.TradeBeacon.DescNote".Translate();
        }

        public static bool HasPoweredBeacon(Map map)
        {
            return map != null && Building_OrbitalTradeBeacon.AllPowered(map).Any();
        }

        private static bool Usable(Thing thing, ITrader trader)
        {
            return thing != null
                && thing.def.category == ThingCategory.Item
                && !thing.IsForbidden(Faction.OfPlayer)
                && TradeUtility.PlayerSellableNow(thing, trader);
        }

        private static bool OwnedByPlayer(object holder)
        {
            // 비축 구역은 항상 플레이어 것이다. 건물이면 플레이어 소유이고 금지되지 않아야 한다.
            return !(holder is Thing building) || (building.Faction == Faction.OfPlayer && !building.IsForbidden(Faction.OfPlayer));
        }

        public static IEnumerable<Thing> StoredThings(Map map, ITrader trader)
        {
            HashSet<Thing> seen = new HashSet<Thing>();

            foreach (SlotGroup group in map.haulDestinationManager.AllGroupsListForReading)
            {
                if (!OwnedByPlayer(group.parent))
                {
                    continue;
                }

                foreach (Thing thing in group.HeldThings)
                {
                    if (Usable(thing, trader) && seen.Add(thing))
                    {
                        yield return thing;
                    }
                }
            }

            // 책장, 의상 거치대 같은 보관함
            foreach (IHaulSource source in map.haulDestinationManager.AllHaulSourcesListForReading)
            {
                if (!OwnedByPlayer(source))
                {
                    continue;
                }

                foreach (Thing thing in source.GetDirectlyHeldThings())
                {
                    if (Usable(thing, trader) && seen.Add(thing))
                    {
                        yield return thing;
                    }
                }
            }

            // 유전자 은행의 유전자팩 (바닐라 송신기와 같이 포함, 유전자 도우미의 "유전자 은행에서 팔지 않기" 설정은 뒤에서 걸러진다)
            if (ModsConfig.BiotechActive)
            {
                foreach (Building building in map.listerBuildings.allBuildingsColonist)
                {
                    if (building.def != ThingDefOf.GeneBank || building.IsForbidden(Faction.OfPlayer))
                    {
                        continue;
                    }

                    CompGenepackContainer container = building.TryGetComp<CompGenepackContainer>();
                    if (container == null)
                    {
                        continue;
                    }

                    foreach (Genepack genepack in container.ContainedGenepacks)
                    {
                        if (TradeUtility.PlayerSellableNow(building, trader) && seen.Add(genepack))
                        {
                            yield return genepack;
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatchCategory(Features.MapWideTradeBeacon)]
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.AllLaunchableThingsForTrade))]
    internal static class TradeUtility_AllLaunchableThingsForTrade_MapWide_Patch
    {
        private static bool Prefix(Map map, ITrader trader, ref IEnumerable<Thing> __result)
        {
            if (!MapWideTradeBeacon.HasPoweredBeacon(map))
            {
                return true;
            }

            __result = MapWideTradeBeacon.StoredThings(map, trader).ToList();
            return false;
        }
    }

    // 궤도 상선에 은을 낼 때도 같은 범위에서 꺼낸다.
    [HarmonyPatchCategory(Features.MapWideTradeBeacon)]
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.LaunchThingsOfType))]
    internal static class TradeUtility_LaunchThingsOfType_MapWide_Patch
    {
        private static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (!MapWideTradeBeacon.HasPoweredBeacon(map))
            {
                return true;
            }

            while (debt > 0)
            {
                Thing thing = MapWideTradeBeacon.StoredThings(map, trader).FirstOrDefault(t => t.def == resDef);
                if (thing == null)
                {
                    Log.Error("[Yuudong2Mod] 거래 대금으로 낼 " + resDef + "을(를) 정착지 창고에서 찾지 못했습니다.");
                    break;
                }

                int count = System.Math.Min(debt, thing.stackCount);
                if (trader != null)
                {
                    trader.GiveSoldThingToTrader(thing, count, TradeSession.playerNegotiator);
                }
                else
                {
                    thing.SplitOff(count).Destroy();
                }

                debt -= count;
            }

            return false;
        }
    }

    // 송신기 반경 표시(설치 미리보기, 선택 시 원)를 그리지 않는다.
    [HarmonyPatchCategory(Features.MapWideTradeBeacon)]
    [HarmonyPatch(typeof(PlaceWorker_ShowTradeBeaconRadius), nameof(PlaceWorker_ShowTradeBeaconRadius.DrawGhost))]
    internal static class PlaceWorker_ShowTradeBeaconRadius_DrawGhost_Patch
    {
        private static bool Prefix()
        {
            return false;
        }
    }

    // 반경 안에 비축 구역을 만드는 버튼은 의미가 없어 숨긴다.
    [HarmonyPatchCategory(Features.MapWideTradeBeacon)]
    [HarmonyPatch(typeof(Building_OrbitalTradeBeacon), nameof(Building_OrbitalTradeBeacon.GetGizmos))]
    internal static class Building_OrbitalTradeBeacon_GetGizmos_Patch
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos)
        {
            string hidden = "CommandMakeBeaconStockpileLabel".Translate();
            foreach (Gizmo gizmo in gizmos)
            {
                if (gizmo is Command command && command.defaultLabel == hidden)
                {
                    continue;
                }

                yield return gizmo;
            }
        }
    }
}
