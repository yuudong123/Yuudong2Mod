using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 적이 떨군 무기·옷처럼 금지 상태로 저장 구역 밖에 놓인 장비는 지붕과 상관없이 며칠 안에 닳아 없어진다.
    public static class LitterDecayUtility
    {
        // 바닐라 SteadyEnvironmentEffects가 칸 하나를 하루에 들르는 횟수.
        public const float VisitsPerDay = 36f;

        public static int Days => Mathf.Max(1, Yuudong2Settings.Current.litterDecayDays);

        public static bool IsLitter(Thing t)
        {
            ThingDef def = t.def;
            bool weapon = def.equipmentType == EquipmentType.Primary && def.stackLimit == 1;
            if (def.category != ThingCategory.Item || !def.useHitPoints || !(weapon || def.IsApparel))
            {
                return false;
            }
            if (!t.Spawned || !t.IsForbidden(Faction.OfPlayer))
            {
                return false;
            }
            Map map = t.Map;
            if (!map.IsPlayerHome || t.Position.Fogged(map) || t.IsInAnyStorage())
            {
                return false;
            }
            if (t.TryGetQuality(out QualityCategory quality) && quality >= QualityCategory.Masterwork)
            {
                return false;
            }
            return !weapon || !KeptForColonist(t, map);
        }

        // 쓰러지면서 떨군 무기(일어나면 다시 든다)와 유대 무기는 정착민 것이라 버려진 것이 아니다.
        private static bool KeptForColonist(Thing weapon, Map map)
        {
            List<Pawn> colonists = map.mapPawns.FreeColonists;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn pawn = colonists[i];
                if (pawn.mindState?.droppedWeapon == weapon || pawn.equipment?.bondedWeapon == weapon)
                {
                    return true;
                }
            }
            return false;
        }

        public static float DamagePerDay(Thing t)
        {
            return (float)t.MaxHitPoints / Days;
        }

        public static void Decay(Thing t)
        {
            int damage = GenMath.RoundRandom(DamagePerDay(t) / VisitsPerDay);
            if (damage <= 0)
            {
                return;
            }
            if (t.HitPoints <= damage)
            {
                t.Destroy();
                return;
            }
            t.HitPoints -= damage;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.LitterDecay.Days".Translate(s.litterDecayDays), -1f, "Yuudong2.LitterDecay.Days.Desc".Translate());
            s.litterDecayDays = Mathf.RoundToInt(listing.Slider(s.litterDecayDays, 1f, 15f));
            listing.End();
        }
    }

    [HarmonyPatchCategory(Features.LitterDecay)]
    [HarmonyPatch(typeof(SteadyEnvironmentEffects), "TryDoDeteriorate")]
    internal static class SteadyEnvironmentEffects_TryDoDeteriorate_LitterDecay_Patch
    {
        private static bool Prefix(Thing t)
        {
            if (!LitterDecayUtility.IsLitter(t))
            {
                return true;
            }
            LitterDecayUtility.Decay(t);
            return false;
        }
    }

    // 살펴보기 창의 "부패 중" 줄에 실제 속도와 이유를 보여 준다.
    [HarmonyPatchCategory(Features.LitterDecay)]
    [HarmonyPatch(typeof(SteadyEnvironmentEffects), nameof(SteadyEnvironmentEffects.FinalDeteriorationRate),
        new[] { typeof(Thing), typeof(bool), typeof(bool), typeof(TerrainDef), typeof(List<string>) })]
    internal static class SteadyEnvironmentEffects_FinalDeteriorationRate_LitterDecay_Patch
    {
        private static void Postfix(Thing t, List<string> reasons, ref float __result)
        {
            if (!LitterDecayUtility.IsLitter(t))
            {
                return;
            }
            __result = LitterDecayUtility.DamagePerDay(t);
            if (reasons != null)
            {
                reasons.Clear();
                reasons.Add("Yuudong2.LitterDecay.Reason".Translate());
            }
        }
    }
}
