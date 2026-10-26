using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace Yuudong2Mod
{
    internal static class EpoeCompatibilityUtility
    {
        private const string EpoePackageId = "vat.epoeforked";
        private const string AiPersonaCoreHediffDefName = "AIPersonaCore";

        private static readonly string[] AiIssueHediffDefNames =
        {
            "EPIA_AIPersonaCore_GlitchMinor",
            "EPIA_AIPersonaCore_GlitchMajor",
            "EPIA_AIPersonaCore_GlitchCritical",
            "EPIA_AIPersonaCore_EMPed"
        };

        private static readonly List<Hediff> TmpHediffsToRemove = new List<Hediff>();

        private static bool initialized;
        private static bool epoeActive;
        private static HediffDef aiPersonaCoreDef;
        private static HediffDef[] aiIssueHediffDefs;

        public static bool EpoeForkedActive
        {
            get
            {
                EnsureInitialized();
                return epoeActive;
            }
        }

        public static void ApplyDefPatches()
        {
            EnsureInitialized();
            if (!epoeActive)
            {
                return;
            }

            if (aiPersonaCoreDef != null)
            {
                aiPersonaCoreDef.tendable = false;
                aiPersonaCoreDef.initialSeverity = aiPersonaCoreDef.maxSeverity;
                RemoveCoreMaintenanceComps(aiPersonaCoreDef);
                RemoveStageHediffGivers(aiPersonaCoreDef);
            }

            for (int i = 0; i < aiIssueHediffDefs.Length; i++)
            {
                HediffDef def = aiIssueHediffDefs[i];
                if (def == null)
                {
                    continue;
                }

                def.tendable = false;
                RemoveStageHediffGivers(def);
            }
        }

        public static void CleanPawn(Pawn pawn)
        {
            EnsureInitialized();
            if (!epoeActive || pawn == null || pawn.Destroyed || pawn.health?.hediffSet == null)
            {
                return;
            }

            if (aiPersonaCoreDef != null)
            {
                Hediff aiCore = pawn.health.hediffSet.GetFirstHediffOfDef(aiPersonaCoreDef);
                if (aiCore != null && aiCore.Severity < aiPersonaCoreDef.maxSeverity)
                {
                    aiCore.Severity = aiPersonaCoreDef.maxSeverity;
                }
            }

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff hediff = hediffs[i];
                if (hediff != null && IsAiIssueHediff(hediff.def))
                {
                    TmpHediffsToRemove.Add(hediff);
                }
            }

            for (int i = 0; i < TmpHediffsToRemove.Count; i++)
            {
                pawn.health.RemoveHediff(TmpHediffsToRemove[i]);
            }

            TmpHediffsToRemove.Clear();
        }

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            epoeActive = ModsConfig.IsActive(EpoePackageId);
            if (!epoeActive)
            {
                aiIssueHediffDefs = new HediffDef[0];
                return;
            }

            aiPersonaCoreDef = DefDatabase<HediffDef>.GetNamedSilentFail(AiPersonaCoreHediffDefName);

            List<HediffDef> issueDefs = new List<HediffDef>();
            for (int i = 0; i < AiIssueHediffDefNames.Length; i++)
            {
                HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(AiIssueHediffDefNames[i]);
                if (def != null)
                {
                    issueDefs.Add(def);
                }
            }

            aiIssueHediffDefs = issueDefs.ToArray();
        }

        private static void RemoveCoreMaintenanceComps(HediffDef def)
        {
            if (def.comps == null)
            {
                return;
            }

            def.comps.RemoveAll(IsCoreMaintenanceComp);
        }

        private static bool IsCoreMaintenanceComp(HediffCompProperties comp)
        {
            if (comp == null)
            {
                return false;
            }

            if (comp is HediffCompProperties_TendDuration || comp is HediffCompProperties_SeverityPerDay)
            {
                return true;
            }

            return comp.GetType().Name == "HediffCompProperties_ReactOnDamage";
        }

        private static void RemoveStageHediffGivers(HediffDef def)
        {
            if (def.stages == null)
            {
                return;
            }

            for (int i = 0; i < def.stages.Count; i++)
            {
                HediffStage stage = def.stages[i];
                if (stage?.hediffGivers != null)
                {
                    stage.hediffGivers.Clear();
                }
            }
        }

        private static bool IsAiIssueHediff(HediffDef def)
        {
            if (def == null || aiIssueHediffDefs == null)
            {
                return false;
            }

            for (int i = 0; i < aiIssueHediffDefs.Length; i++)
            {
                if (def == aiIssueHediffDefs[i])
                {
                    return true;
                }
            }

            return false;
        }
    }

    [HarmonyPatchCategory(Features.EpoeAiCore)]
    [HarmonyPatch(typeof(Pawn), "Tick")]
    internal static class Pawn_Tick_EpoeAiCorePatch
    {
        private static void Postfix(Pawn __instance)
        {
            if (__instance != null && __instance.IsHashIntervalTick(250))
            {
                EpoeCompatibilityUtility.CleanPawn(__instance);
            }
        }
    }
}
