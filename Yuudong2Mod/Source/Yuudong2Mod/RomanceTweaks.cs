using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Yuudong2Mod
{
    // Romance Tweaks 포팅. 연애 시도·성공·이별 확률에 배율을 곱한다.
    // 기본값은 기존에 쓰던 설정(바람·다른 세력 연애·이별 0배)이다.
    internal static class RomanceTweaksUtility
    {
        public static bool IsRomanticRelation(PawnRelationDef def)
        {
            return def == PawnRelationDefOf.Lover || def == PawnRelationDefOf.Fiance || def == PawnRelationDefOf.Spouse;
        }

        public static Pawn FindLover(Pawn pawn)
        {
            return pawn.relations?.DirectRelations.Find(r => IsRomanticRelation(r.def) && !r.otherPawn.Dead)?.otherPawn;
        }

        public static void DebugLog(string label, Pawn initiator, Pawn recipient, float before, float after, string tags = null)
        {
            if (Yuudong2Settings.Current.romanceDebug)
            {
                Log.Message($"[Yuudong2Mod] {label} {initiator.Name?.ToStringShort} -> {recipient.Name?.ToStringShort} : {before} -> {after}" + (tags == null ? "" : $" [{tags}]"));
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> buffers = new System.Collections.Generic.Dictionary<string, string>();

        private static void NumberRow(Listing_Standard listing, string key, ref float value)
        {
            Rect row = listing.GetRect(30f);
            Rect labelRect = row.LeftPart(0.5f).Rounded();
            Rect right = row.RightPart(0.5f).Rounded();
            Rect fieldRect = right.LeftPart(0.25f).Rounded();
            Rect sliderRect = right.RightPart(0.7f).Rounded();
            TooltipHandler.TipRegion(row, ("Yuudong2.Romance." + key + ".Desc").Translate());
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, ("Yuudong2.Romance." + key).Translate());
            buffers.TryGetValue(key, out string buffer);
            Widgets.TextFieldNumeric(fieldRect, ref value, ref buffer, 0f, 10f);
            Text.Anchor = TextAnchor.UpperLeft;
            float slider = Widgets.HorizontalSlider(sliderRect, value, 0f, 10f, true);
            if (slider != value)
            {
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                value = slider;
                buffer = null;
            }

            buffers[key] = buffer;

            listing.Gap(2f);
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Yuudong2.Romance.Debug".Translate(), ref s.romanceDebug);
            NumberRow(listing, "Chance", ref s.romanceChanceMult);
            NumberRow(listing, "Single", ref s.romanceSingleMult);
            NumberRow(listing, "Cheating", ref s.romanceCheatingMult);
            NumberRow(listing, "Faction", ref s.romanceFactionMult);
            NumberRow(listing, "Success", ref s.romanceSuccessMult);
            NumberRow(listing, "Breakup", ref s.breakupChanceMult);
            listing.End();
        }
    }

    [HarmonyPatchCategory(Features.RomanceTweaks)]
    [HarmonyPatch(typeof(InteractionWorker_RomanceAttempt), nameof(InteractionWorker_RomanceAttempt.RandomSelectionWeight))]
    [HarmonyAfter("HugsLib.Psychology")]
    internal static class InteractionWorker_RomanceAttempt_RandomSelectionWeight_RomanceTweaks_Patch
    {
        private static void Postfix(ref float __result, Pawn initiator, Pawn recipient)
        {
            if (__result == 0f)
            {
                return;
            }

            Yuudong2Settings s = Yuudong2Settings.Current;
            float mult = s.romanceChanceMult;
            string tags = "General";
            Pawn initiatorLover = RomanceTweaksUtility.FindLover(initiator);
            if (initiatorLover == null && RomanceTweaksUtility.FindLover(recipient) == null)
            {
                mult *= s.romanceSingleMult;
                tags += ", Singles";
            }
            else if (initiatorLover != recipient)
            {
                mult *= s.romanceCheatingMult;
                tags += ", Cheating";
            }

            if (initiator.Faction != recipient.Faction)
            {
                mult *= s.romanceFactionMult;
                tags += ", CrossFaction";
            }

            RomanceTweaksUtility.DebugLog("Romance Chance", initiator, recipient, __result, __result * mult, tags);
            __result *= mult;
        }
    }

    [HarmonyPatchCategory(Features.RomanceTweaks)]
    [HarmonyPatch(typeof(InteractionWorker_RomanceAttempt), nameof(InteractionWorker_RomanceAttempt.SuccessChance))]
    [HarmonyAfter("HugsLib.Psychology")]
    internal static class InteractionWorker_RomanceAttempt_SuccessChance_RomanceTweaks_Patch
    {
        private static void Postfix(ref float __result, Pawn initiator, Pawn recipient)
        {
            if (__result == 0f)
            {
                return;
            }

            float mult = Yuudong2Settings.Current.romanceSuccessMult;
            RomanceTweaksUtility.DebugLog("Romance Success", initiator, recipient, __result, __result * mult);
            __result *= mult;
        }
    }

    [HarmonyPatchCategory(Features.RomanceTweaks)]
    [HarmonyPatch(typeof(InteractionWorker_Breakup), nameof(InteractionWorker_Breakup.RandomSelectionWeight))]
    internal static class InteractionWorker_Breakup_RandomSelectionWeight_RomanceTweaks_Patch
    {
        private static void Postfix(ref float __result, Pawn initiator, Pawn recipient)
        {
            if (__result == 0f)
            {
                return;
            }

            float mult = Yuudong2Settings.Current.breakupChanceMult;
            RomanceTweaksUtility.DebugLog("Breakup Chance", initiator, recipient, __result, __result * mult);
            __result *= mult;
        }
    }

    // Roofed Scanning 포팅. 지하 자원 스캐너와 장거리 광물 스캐너를 지붕 아래에서도 쓸 수 있다.
    [HarmonyPatchCategory(Features.RoofedScanning)]
    [HarmonyPatch(typeof(CompScanner), nameof(CompScanner.CanUseNow), MethodType.Getter)]
    internal static class CompScanner_CanUseNow_RoofedScanning_Patch
    {
        private static bool Prefix(CompScanner __instance, CompPowerTrader ___powerComp, CompForbiddable ___forbiddable, ref AcceptanceReport __result)
        {
            ThingWithComps parent = __instance.parent;
            __result = parent.Spawned
                && (___powerComp == null || ___powerComp.PowerOn)
                && (___forbiddable == null || !___forbiddable.Forbidden)
                && parent.Faction == Faction.OfPlayer;
            return false;
        }
    }
}
