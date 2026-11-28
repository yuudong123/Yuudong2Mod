using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 중력 닻 없이 떠나면 바닐라는 그 타일에 "중력선 발진지" 표시를 영구히 남기고, 그 표시 때문에 다시 착륙할 수 없다.
    // 표시가 정한 날짜보다 오래되면 지워서 그 타일에 다시 내릴 수 있게 한다.
    public static class GravshipLaunchSiteUtility
    {
        public const int MaxDays = 30;
        private const int SweepInterval = 2500;

        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.GravshipLaunchSiteExpiry);

        public static int Days => Mathf.Clamp(Yuudong2Settings.Current.gravshipLaunchSiteDays, 0, MaxDays);

        private static readonly List<WorldObject> expired = new List<WorldObject>();

        public static bool Expired(WorldObject launch)
        {
            return Find.TickManager.TicksGame - launch.creationGameTicks >= Days * GenDate.TicksPerDay;
        }

        public static void Sweep()
        {
            if (!Active || !ModsConfig.OdysseyActive || WorldObjectDefOf.GravshipLaunch == null)
            {
                return;
            }
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].def == WorldObjectDefOf.GravshipLaunch && Expired(all[i]))
                {
                    expired.Add(all[i]);
                }
            }
            for (int i = 0; i < expired.Count; i++)
            {
                Find.WorldObjects.Remove(expired[i]);
            }
            expired.Clear();
        }

        public static void SweepIfDue()
        {
            if (Find.TickManager.TicksGame % SweepInterval == 0)
            {
                Sweep();
            }
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.GravshipLaunchSiteExpiry.Days".Translate(s.gravshipLaunchSiteDays), -1f, "Yuudong2.GravshipLaunchSiteExpiry.Days.Desc".Translate());
            s.gravshipLaunchSiteDays = Mathf.RoundToInt(listing.Slider(s.gravshipLaunchSiteDays, 0f, MaxDays));
            listing.End();
        }
    }

    public class WorldComponent_GravshipLaunchSites : WorldComponent
    {
        public WorldComponent_GravshipLaunchSites(World world) : base(world)
        {
        }

        public override void WorldComponentTick()
        {
            GravshipLaunchSiteUtility.SweepIfDue();
        }
    }

    // 목적지를 고르기 직전에도 지워서 시간이 막 지난 타일을 바로 고를 수 있게 한다.
    [HarmonyPatchCategory(Features.GravshipLaunchSiteExpiry)]
    [HarmonyPatch(typeof(CompPilotConsole), nameof(CompPilotConsole.StartChoosingDestination_NewTemp))]
    internal static class CompPilotConsole_StartChoosingDestination_LaunchSite_Patch
    {
        private static void Prefix()
        {
            GravshipLaunchSiteUtility.Sweep();
        }
    }

    // 바닐라는 중력 닻이 있으면 떠난 맵을 그대로 남겨서 계속 틱이 돈다. 닻은 그 자리에 바로 다시 내려오려고 짓는 것이므로,
    // 남는 정착민도 다른 중력 엔진도 없으면 닻 없이 떠날 때처럼 맵을 버리되 발진지 표시는 남기지 않아 바로 다시 착륙할 수 있게 한다.
    // 같은 타일 안에서 자리만 옮길 때는 그 맵에 다시 내리므로 그대로 둔다.
    public static class AnchorDepartureUtility
    {
        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, Map> MapRef =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, Map>("map");

        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, bool> AnchorRef =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, bool>("mapHasGravAnchor");

        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, PlanetTile> TakeoffTileRef =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, PlanetTile>("takeoffTile");

        private static readonly AccessTools.FieldRef<WorldComponent_GravshipController, PlanetTile> LandingTileRef =
            AccessTools.FieldRefAccess<WorldComponent_GravshipController, PlanetTile>("landingTile");

        public static bool ShouldLeaveMap(Map map, PlanetTile takeoffTile, PlanetTile landingTile)
        {
            return map != null && takeoffTile != landingTile && !map.mapPawns.AnyColonistSpawned
                && !map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).Any(e => e.Spawned && e.Faction == Faction.OfPlayer);
        }

        // 버릴 맵이면 닻이 없던 것으로 돌리고 그 타일을 돌려준다. 바닐라가 맵을 버린 뒤 그 타일의 발진지 표시를 지운다.
        public static PlanetTile PrepareTakeoffEnd(WorldComponent_GravshipController controller)
        {
            if (!ModsConfig.OdysseyActive || !AnchorRef(controller))
            {
                return PlanetTile.Invalid;
            }
            PlanetTile takeoff = TakeoffTileRef(controller);
            if (!ShouldLeaveMap(MapRef(controller), takeoff, LandingTileRef(controller)))
            {
                return PlanetTile.Invalid;
            }
            AnchorRef(controller) = false;
            return takeoff;
        }

        public static void RemoveLaunchMarker(PlanetTile tile)
        {
            if (!tile.Valid || WorldObjectDefOf.GravshipLaunch == null)
            {
                return;
            }
            List<WorldObject> here = Find.WorldObjects.ObjectsAt(tile).Where(o => o.def == WorldObjectDefOf.GravshipLaunch).ToList();
            for (int i = 0; i < here.Count; i++)
            {
                Find.WorldObjects.Remove(here[i]);
            }
        }
    }

    [HarmonyPatchCategory(Features.AnchorDeparture)]
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "TakeoffEnded")]
    internal static class WorldComponent_GravshipController_TakeoffEnded_AnchorDeparture_Patch
    {
        private static void Prefix(WorldComponent_GravshipController __instance, out PlanetTile __state)
        {
            __state = AnchorDepartureUtility.PrepareTakeoffEnd(__instance);
        }

        private static void Postfix(PlanetTile __state)
        {
            AnchorDepartureUtility.RemoveLaunchMarker(__state);
        }
    }
}
