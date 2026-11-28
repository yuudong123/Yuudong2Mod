using System.Collections.Generic;
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

    // 중력 닻을 두고 떠난 맵은 바닐라에서 계속 정착지(플레이어 거점)로 남아 습격·사건을 받고 부와 위협 점수에 들어간다.
    // 닻만 있고 정착민도 중력 엔진도 없으면 거점으로 보지 않는다. 정착민이 가거나 배가 돌아오면 다시 거점이 된다.
    public static class UnattendedAnchorUtility
    {
        private static readonly Dictionary<Map, int> checkedTick = new Dictionary<Map, int>();
        private static readonly Dictionary<Map, bool> unattended = new Dictionary<Map, bool>();

        public static bool Unattended(Map map)
        {
            if (!ModsConfig.OdysseyActive || !map.listerThings.AnyThingWithDef(ThingDefOf.GravAnchor))
            {
                return false;
            }
            int tick = Find.TickManager.TicksGame;
            if (checkedTick.TryGetValue(map, out int last) && last == tick && unattended.TryGetValue(map, out bool cached))
            {
                return cached;
            }
            bool value = !map.mapPawns.AnyColonistSpawned && !GravshipUtility.PlayerHasGravEngine(map);
            checkedTick[map] = tick;
            unattended[map] = value;
            return value;
        }

        public static void Forget(Map map)
        {
            checkedTick.Remove(map);
            unattended.Remove(map);
        }
    }

    [HarmonyPatchCategory(Features.UnattendedAnchorMap)]
    [HarmonyPatch(typeof(Map), nameof(Map.IsPlayerHome), MethodType.Getter)]
    internal static class Map_IsPlayerHome_UnattendedAnchor_Patch
    {
        private static void Postfix(Map __instance, ref bool __result)
        {
            if (__result && UnattendedAnchorUtility.Unattended(__instance))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatchCategory(Features.UnattendedAnchorMap)]
    [HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
    internal static class Game_DeinitAndRemoveMap_UnattendedAnchor_Patch
    {
        private static void Postfix(Map map)
        {
            UnattendedAnchorUtility.Forget(map);
        }
    }
}
