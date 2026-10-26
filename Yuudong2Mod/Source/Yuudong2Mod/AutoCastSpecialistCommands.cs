using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace Yuudong2Mod
{
    // Auto-Cast Specialist Commands 포팅. 전문가 역할 명령을 해당 작업을 시작할 때 자동으로 사용한다.
    // 게임 안에서 명령 버튼을 오른쪽 클릭하면 자동 사용을 켜고 끈다.
    [StaticConstructorOnStartup]
    internal static class AutoCastUtility
    {
        public static readonly Texture2D AutoCastIcon = ContentFinder<Texture2D>.Get("UI/CheckAuto", false) ?? ContentFinder<Texture2D>.Get("UI/CheckAutoFallback");

        private static AbilityDef farming;
        private static AbilityDef mining;
        private static AbilityDef production;
        private static AbilityDef research;
        private static AbilityDef cooking;
        private static AbilityDef voidCommand;
        private static AbilityDef majordomo;
        private static bool resolved;

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            farming = DefDatabase<AbilityDef>.GetNamedSilentFail("FarmingCommand");
            mining = DefDatabase<AbilityDef>.GetNamedSilentFail("MiningCommand");
            production = DefDatabase<AbilityDef>.GetNamedSilentFail("ProductionCommand");
            research = DefDatabase<AbilityDef>.GetNamedSilentFail("ResearchCommand");
            cooking = DefDatabase<AbilityDef>.GetNamedSilentFail("SO_ButcherCommand");
            voidCommand = DefDatabase<AbilityDef>.GetNamedSilentFail("SO_VoidCommand");
            majordomo = DefDatabase<AbilityDef>.GetNamedSilentFail("VME_WorkCommand");
            resolved = true;
        }

        public static AbilityDef Farming { get { Resolve(); return farming; } }
        public static AbilityDef Mining { get { Resolve(); return mining; } }
        public static AbilityDef Production { get { Resolve(); return production; } }
        public static AbilityDef Research { get { Resolve(); return research; } }
        public static AbilityDef Cooking { get { Resolve(); return cooking; } }
        public static AbilityDef VoidCommand { get { Resolve(); return voidCommand; } }
        public static AbilityDef Majordomo { get { Resolve(); return majordomo; } }

        // 자동 사용 설정이 있는 명령이면 그 설정값을 돌려준다.
        public static bool TryGetSetting(AbilityDef def, out bool enabled)
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            enabled = false;
            if (def == null)
            {
                return false;
            }

            if (def == Farming) { enabled = s.autoCastFarming; return true; }
            if (def == Mining) { enabled = s.autoCastMining; return true; }
            if (def == Production) { enabled = s.autoCastProduction; return true; }
            if (def == Research) { enabled = s.autoCastResearch; return true; }
            if (def == Cooking) { enabled = s.autoCastCooking; return true; }
            if (def == VoidCommand) { enabled = s.autoCastVoid; return true; }
            if (def == Majordomo) { enabled = s.autoCastMajordomo; return true; }
            return false;
        }

        public static bool Toggle(AbilityDef def)
        {
            Yuudong2Settings s = Yuudong2Settings.Current;
            if (def == null) return false;
            if (def == Farming) s.autoCastFarming = !s.autoCastFarming;
            else if (def == Mining) s.autoCastMining = !s.autoCastMining;
            else if (def == Production) s.autoCastProduction = !s.autoCastProduction;
            else if (def == Research) s.autoCastResearch = !s.autoCastResearch;
            else if (def == Cooking) s.autoCastCooking = !s.autoCastCooking;
            else if (def == VoidCommand) s.autoCastVoid = !s.autoCastVoid;
            else if (def == Majordomo) s.autoCastMajordomo = !s.autoCastMajordomo;
            else return false;
            s.Write();
            return true;
        }

        public static Ability FindAbility(Pawn pawn, AbilityDef def)
        {
            if (pawn == null || def == null)
            {
                return null;
            }

            Ability ability = pawn.abilities?.GetAbility(def);
            if (ability != null)
            {
                return ability;
            }

            List<Ability> roleAbilities = pawn.Ideo?.GetRole(pawn)?.AbilitiesFor(pawn);
            if (roleAbilities == null)
            {
                return null;
            }

            foreach (Ability roleAbility in roleAbilities)
            {
                if (roleAbility.def == def && (def.requiredMemes == null || def.requiredMemes.All(m => pawn.Ideo?.memes?.Contains(m) == true)))
                {
                    return roleAbility;
                }
            }

            return null;
        }

        public static void Inject(JobDriver driver, ref IEnumerable<Toil> toils, AbilityDef def, bool enabled)
        {
            Pawn pawn = driver?.pawn;
            if (!enabled || pawn == null || def == null)
            {
                return;
            }

            Ability ability = FindAbility(pawn, def);
            if (ability?.verb == null || !ability.CanCast)
            {
                return;
            }

            Toil castToil = new Toil
            {
                initAction = () =>
                {
                    if (ability.CanCast)
                    {
                        ability.verb.TryStartCastOn(pawn, false, true, false, false);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant,
            };

            List<Toil> list = (toils ?? Array.Empty<Toil>()).ToList();
            int index = list.FindIndex(t => t.defaultCompleteMode == ToilCompleteMode.Never);
            list.Insert(index < 0 ? 0 : index, castToil);
            toils = list;
        }

        public static void InjectWithMajordomo(JobDriver driver, ref IEnumerable<Toil> toils, AbilityDef def, bool enabled)
        {
            Inject(driver, ref toils, def, enabled);
            Inject(driver, ref toils, Majordomo, Yuudong2Settings.Current.autoCastMajordomo);
        }

        public static void PatchResearchReinvented(Harmony harmony)
        {
            if (ModLister.GetActiveModWithIdentifier("PeteTimesSix.ResearchReinvented", true) == null)
            {
                return;
            }

            HarmonyMethod postfix = new HarmonyMethod(typeof(JobDriver_Research_MakeNewToils_AutoCast_Patch), "Postfix");
            string[] drivers = { "JobDriver_Analyse", "JobDriver_AnalyseInPlace", "JobDriver_AnalyseTerrain", "JobDriver_InterrogatePrisoner", "JobDriver_LearnRemotely", "JobDriver_ResearchRR" };
            foreach (string driver in drivers)
            {
                MethodInfo method = AccessTools.Method(AccessTools.TypeByName("PeteTimesSix.ResearchReinvented.Rimworld.JobDrivers." + driver), "MakeNewToils");
                if (method != null)
                {
                    harmony.Patch(method, postfix: postfix);
                }
            }
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.AutoCast.Tip".Translate());
            listing.CheckboxLabeled("Yuudong2.AutoCast.Farming".Translate(), ref s.autoCastFarming);
            listing.CheckboxLabeled("Yuudong2.AutoCast.Mining".Translate(), ref s.autoCastMining);
            listing.CheckboxLabeled("Yuudong2.AutoCast.Production".Translate(), ref s.autoCastProduction);
            listing.CheckboxLabeled("Yuudong2.AutoCast.Research".Translate(), ref s.autoCastResearch);
            if (Cooking != null)
            {
                listing.CheckboxLabeled("Yuudong2.AutoCast.Cooking".Translate(), ref s.autoCastCooking);
            }

            if (VoidCommand != null && AccessTools.TypeByName("RimWorld.JobDriver_StudyInteract") != null)
            {
                listing.CheckboxLabeled("Yuudong2.AutoCast.Void".Translate(), ref s.autoCastVoid);
            }

            if (Majordomo != null)
            {
                listing.CheckboxLabeled("Yuudong2.AutoCast.Majordomo".Translate(), ref s.autoCastMajordomo);
            }

            listing.End();
        }
    }

    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch(typeof(JobDriver_DoBill), "MakeNewToils")]
    internal static class JobDriver_DoBill_MakeNewToils_AutoCast_Patch
    {
        private static void Postfix(JobDriver_DoBill __instance, ref IEnumerable<Toil> __result)
        {
            RecipeDef recipe = __instance.job?.bill?.recipe;
            if (recipe == null)
            {
                return;
            }

            Yuudong2Settings s = Yuudong2Settings.Current;
            if (recipe.workSkill == SkillDefOf.Crafting)
            {
                AutoCastUtility.Inject(__instance, ref __result, AutoCastUtility.Production, s.autoCastProduction);
            }

            if (recipe.workSkill == SkillDefOf.Cooking)
            {
                AutoCastUtility.Inject(__instance, ref __result, AutoCastUtility.Cooking, s.autoCastCooking);
            }

            AutoCastUtility.Inject(__instance, ref __result, AutoCastUtility.Majordomo, s.autoCastMajordomo);
        }
    }

    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch(typeof(JobDriver_Mine), "MakeNewToils")]
    internal static class JobDriver_Mine_MakeNewToils_AutoCast_Patch
    {
        private static void Postfix(JobDriver_Mine __instance, ref IEnumerable<Toil> __result)
        {
            AutoCastUtility.InjectWithMajordomo(__instance, ref __result, AutoCastUtility.Mining, Yuudong2Settings.Current.autoCastMining);
        }
    }

    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch(typeof(JobDriver_PlantWork), "MakeNewToils")]
    internal static class JobDriver_PlantWork_MakeNewToils_AutoCast_Patch
    {
        private static void Postfix(JobDriver_PlantWork __instance, ref IEnumerable<Toil> __result)
        {
            JobDef job = __instance.job?.def;
            if (job == JobDefOf.PlantSeed || job == JobDefOf.Harvest || job == JobDefOf.HarvestDesignated || job == JobDefOf.CutPlant || job == JobDefOf.CutPlantDesignated)
            {
                AutoCastUtility.InjectWithMajordomo(__instance, ref __result, AutoCastUtility.Farming, Yuudong2Settings.Current.autoCastFarming);
            }
        }
    }

    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch(typeof(JobDriver_Research), "MakeNewToils")]
    internal static class JobDriver_Research_MakeNewToils_AutoCast_Patch
    {
        public static void Postfix(JobDriver __instance, ref IEnumerable<Toil> __result)
        {
            AutoCastUtility.InjectWithMajordomo(__instance, ref __result, AutoCastUtility.Research, Yuudong2Settings.Current.autoCastResearch);
        }
    }

    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch]
    internal static class JobDriver_StudyInteract_MakeNewToils_AutoCast_Patch
    {
        private static bool Prepare()
        {
            return AccessTools.TypeByName("RimWorld.JobDriver_StudyInteract") != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("RimWorld.JobDriver_StudyInteract"), "MakeNewToils");
        }

        private static void Postfix(JobDriver __instance, ref IEnumerable<Toil> __result)
        {
            if (AutoCastUtility.VoidCommand != null)
            {
                AutoCastUtility.InjectWithMajordomo(__instance, ref __result, AutoCastUtility.VoidCommand, Yuudong2Settings.Current.autoCastVoid);
            }
        }
    }

    // 오른쪽 클릭은 명령 사용 대신 자동 사용 토글로 쓴다.
    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch(typeof(Command_Ability), nameof(Command_Ability.ProcessInput))]
    internal static class Command_Ability_ProcessInput_AutoCast_Patch
    {
        private static bool Prefix(Command_Ability __instance, Event ev)
        {
            if (!AutoCastUtility.TryGetSetting(__instance?.Ability?.def, out bool _))
            {
                return true;
            }

            return !(ev?.button == 1 || Event.current?.button == 1);
        }
    }

    [HarmonyPatchCategory(Features.AutoCastSpecialistCommands)]
    [HarmonyPatch(typeof(Command), "GizmoOnGUIInt")]
    internal static class Command_GizmoOnGUIInt_AutoCast_Patch
    {
        private static bool Prefix(Command __instance, Rect butRect, ref GizmoResult __result)
        {
            if (!(__instance is Command_Ability command) || !Mouse.IsOver(butRect) || Event.current == null
                || Event.current.button != 1 || Event.current.type != EventType.MouseDown)
            {
                return true;
            }

            if (!AutoCastUtility.Toggle(command.Ability?.def))
            {
                return true;
            }

            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            Event.current.Use();
            __result = new GizmoResult(GizmoState.Clear);
            return false;
        }

        private static void Postfix(Command __instance, Rect butRect, GizmoRenderParms parms)
        {
            if (__instance.GetType() != typeof(Command_Ability))
            {
                return;
            }

            if (AutoCastUtility.TryGetSetting(((Command_Ability)__instance).Ability?.def, out bool enabled) && enabled)
            {
                float size = parms.shrunk ? 12f : 24f;
                GUI.DrawTexture(new Rect(butRect.x + butRect.width - size, butRect.y, size, size), AutoCastUtility.AutoCastIcon);
            }
        }
    }
}
