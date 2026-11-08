using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Yuudong2Mod.SurgeryManager
{
    public enum PlanState
    {
        Done,
        InProgress,
        Queued,
        Missing,
        Blocked,
    }

    // 계획 항목 하나를 실제 부위 하나에 대응시킨 결과.
    public class PlanStatus
    {
        public SurgeryPlanEntry entry;
        public BodyPartRecord part;
        public PlanState state;
        public RecipeDef recipe;
        public string reason;
        public float daysLeft;
    }

    public static class SurgeryPlanner
    {
        private static readonly Dictionary<(HediffDef, BodyPartDef), List<RecipeDef>> RecipeCache = new Dictionary<(HediffDef, BodyPartDef), List<RecipeDef>>();
        private static List<RecipeDef> installRecipes;

        // 부품·임플란트를 다는 수술 전부(자연 장기 이식처럼 상태 이상을 남기지 않는 것은 뺀다).
        public static List<RecipeDef> InstallRecipes
        {
            get
            {
                if (installRecipes == null)
                {
                    installRecipes = DefDatabase<RecipeDef>.AllDefsListForReading
                        .Where(r => r.addsHediff != null && !r.appliedOnFixedBodyParts.NullOrEmpty() && IsInstallWorker(r.workerClass))
                        .ToList();
                }

                return installRecipes;
            }
        }

        private static bool IsInstallWorker(Type worker)
        {
            return worker != null && (typeof(Recipe_InstallImplant).IsAssignableFrom(worker) || typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(worker));
        }

        public static List<RecipeDef> RecipesFor(HediffDef hediff, BodyPartDef part)
        {
            if (!RecipeCache.TryGetValue((hediff, part), out List<RecipeDef> list))
            {
                list = InstallRecipes.Where(r => r.addsHediff == hediff && r.appliedOnFixedBodyParts.Contains(part)).ToList();
                RecipeCache[(hediff, part)] = list;
            }

            return list;
        }

        public static bool IsReplacement(RecipeDef recipe)
        {
            return typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(recipe.workerClass);
        }

        // 최적화 나노봇처럼 며칠에 걸쳐 단계가 올라가는 임플란트.
        public static bool IsGradual(HediffDef hediff)
        {
            return hediff.stages != null && hediff.stages.Count > 1
                && hediff.comps != null && hediff.comps.Any(c => c is HediffCompProperties_SeverityPerDay s && s.severityPerDay > 0f);
        }

        public static List<PlanStatus> Evaluate(Pawn pawn, SurgeryPreset preset)
        {
            List<PlanStatus> result = new List<PlanStatus>();
            if (preset == null || pawn.health?.hediffSet == null)
            {
                return result;
            }

            HediffSet hediffSet = pawn.health.hediffSet;
            List<Bill> bills = pawn.health.surgeryBills?.Bills;
            foreach (SurgeryPlanEntry entry in preset.entries)
            {
                List<BodyPartRecord> parts = pawn.RaceProps.body.GetPartsWithDef(entry.part);
                foreach (BodyPartRecord part in parts)
                {
                    PlanStatus status = new PlanStatus { entry = entry, part = part };
                    result.Add(status);
                    Hediff installed = hediffSet.hediffs.FirstOrDefault(h => h.def == entry.hediff && h.Part == part);
                    if (installed != null)
                    {
                        status.state = PlanState.Done;
                        if (IsGradual(entry.hediff) && installed.CurStageIndex < entry.hediff.stages.Count - 1)
                        {
                            float perDay = entry.hediff.CompProps<HediffCompProperties_SeverityPerDay>().severityPerDay;
                            float target = entry.hediff.stages[entry.hediff.stages.Count - 1].minSeverity;
                            status.state = PlanState.InProgress;
                            status.daysLeft = perDay > 0f ? Math.Max(0f, (target - installed.Severity) / perDay) : 0f;
                        }

                        continue;
                    }

                    RecipeDef recipe = RecipesFor(entry.hediff, entry.part).FirstOrDefault(r => pawn.def.AllRecipes.Contains(r));
                    status.recipe = recipe;
                    if (recipe == null)
                    {
                        status.state = PlanState.Blocked;
                        status.reason = "Yuudong2.Surgery.Reason.Race".Translate();
                        continue;
                    }

                    if (bills != null && bills.Any(b => b is Bill_Medical m && m.recipe == recipe && m.Part == part))
                    {
                        status.state = PlanState.Queued;
                        continue;
                    }

                    if (!recipe.AvailableNow)
                    {
                        status.state = PlanState.Blocked;
                        ResearchProjectDef research = recipe.researchPrerequisite ?? recipe.researchPrerequisites?.FirstOrDefault(r => !r.IsFinished);
                        status.reason = "Yuudong2.Surgery.Reason.Research".Translate(research?.LabelCap ?? "?");
                        continue;
                    }

                    if (!recipe.Worker.GetPartsToApplyOn(pawn, recipe).Contains(part))
                    {
                        status.state = PlanState.Blocked;
                        status.reason = "Yuudong2.Surgery.Reason.NotNow".Translate();
                        continue;
                    }

                    status.state = PlanState.Missing;
                }
            }

            return result;
        }

        // 수술의 주재료(부품·임플란트 아이템). 약처럼 여러 종류를 받는 재료는 뺀다.
        public static ThingDef MainItem(RecipeDef recipe)
        {
            foreach (IngredientCount ingredient in recipe.ingredients)
            {
                if (ingredient.IsFixedIngredient && !ingredient.FixedIngredient.IsMedicine)
                {
                    return ingredient.FixedIngredient;
                }
            }

            return null;
        }

        public static int CountOnMap(Map map, ThingDef def)
        {
            int count = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                if (!things[i].IsForbidden(Faction.OfPlayer))
                {
                    count += things[i].stackCount;
                }
            }

            return count;
        }

        private static bool HasIngredients(Map map, RecipeDef recipe, Dictionary<ThingDef, int> used)
        {
            foreach (IngredientCount ingredient in recipe.ingredients)
            {
                int need = Math.Max(1, (int)Math.Ceiling(ingredient.GetBaseCount()));
                int have = 0;
                foreach (ThingDef def in ingredient.filter.AllowedThingDefs)
                {
                    if (recipe.fixedIngredientFilter != null && !recipe.fixedIngredientFilter.Allows(def))
                    {
                        continue;
                    }

                    have += CountOnMap(map, def) - (used.TryGetValue(def, out int u) ? u : 0);
                    if (have >= need)
                    {
                        break;
                    }
                }

                if (have < need)
                {
                    return false;
                }
            }

            foreach (IngredientCount ingredient in recipe.ingredients)
            {
                if (ingredient.IsFixedIngredient)
                {
                    ThingDef def = ingredient.FixedIngredient;
                    used[def] = (used.TryGetValue(def, out int u) ? u : 0) + Math.Max(1, (int)Math.Ceiling(ingredient.GetBaseCount()));
                }
            }

            return true;
        }

        public class QueueResult
        {
            public int queued;
            public int noMaterials;
        }

        // 부족한 수술을 예약한다. 부품 교체를 먼저, 그 위에 올라가는 임플란트를 나중에 넣는다.
        public static QueueResult QueueMissing(Pawn pawn, SurgeryPreset preset, GameComponent_SurgeryPlans options, Dictionary<ThingDef, int> used)
        {
            QueueResult result = new QueueResult();
            Map map = pawn.MapHeld;
            if (map == null)
            {
                return result;
            }

            List<PlanStatus> statuses = Evaluate(pawn, preset);
            bool gradualBusy = statuses.Any(s => (s.state == PlanState.InProgress || s.state == PlanState.Queued) && IsGradual(s.entry.hediff));
            IEnumerable<PlanStatus> missing = statuses.Where(s => s.state == PlanState.Missing).OrderBy(s => IsReplacement(s.recipe) ? 0 : 1);
            foreach (PlanStatus status in missing)
            {
                bool gradual = IsGradual(status.entry.hediff);
                if (gradual && options.nanobotsOneAtATime && gradualBusy)
                {
                    continue;
                }

                if (options.onlyWithMaterials && !HasIngredients(map, status.recipe, used))
                {
                    result.noMaterials++;
                    continue;
                }

                HealthCardUtility.CreateSurgeryBill(pawn, status.recipe, status.part, null, sendMessages: false);
                result.queued++;
                gradualBusy |= gradual;
            }

            return result;
        }
    }
}
