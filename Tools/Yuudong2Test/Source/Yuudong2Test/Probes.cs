using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Yuudong2Mod;
using Yuudong2Mod.SurgeryManager;

namespace Yuudong2Test
{
    // 기능별 검사. 새 기능을 넣으면 여기에 항목을 더한다. 각 항목은 보고서의 한 절이 된다.
    public static class Probes
    {
        public static readonly List<(string, Action<StringBuilder>)> All = new List<(string, Action<StringBuilder>)>
        {
            ("features", Features_),
            ("surgery manager", Surgery),
            ("sleep limits", Sleep),
            ("installed parts count", InstalledParts),
        };

        // "장착 중인 것 포함" 지시서는 어느 맵·캐러밴·중력부양선에 있든 모든 정착민의 무기·옷·소지품과 몸의 부품을 세야 한다.
        private static void InstalledParts(StringBuilder sb)
        {
            List<Pawn> colonists = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists;
            sb.AppendLine("colonists: " + string.Join(", ", colonists.GroupBy(p => p.MapHeld?.ToString() ?? (p.IsCaravanMember() ? "caravan" : "other")).Select(g => $"{g.Key}={g.Count()}")));
            int bad = 0;
            foreach (Map map in Find.Maps)
            {
                foreach (Bill_Production bill in map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver).OfType<IBillGiver>()
                    .SelectMany(g => g.BillStack.Bills).OfType<Bill_Production>())
                {
                    ThingDef product = bill.recipe.products.NullOrEmpty() ? null : bill.recipe.products[0].thingDef;
                    if (!bill.includeEquipped || bill.repeatMode != BillRepeatModeDefOf.TargetCount || product == null)
                    {
                        continue;
                    }

                    RecipeWorkerCounter counter = bill.recipe.WorkerCounter;
                    int installed = colonists.Sum(p => p.health.hediffSet.hediffs.Count(h => h.def.spawnThingOnRemoved == product));
                    int carriedElsewhere = colonists.Where(p => !(p.Spawned && p.Map == map))
                        .SelectMany(p => p.equipment.AllEquipmentListForReading.Cast<Thing>().Concat(p.apparel.WornApparel).Concat(p.inventory.innerContainer))
                        .Where(t => counter.CountValidThing(t, bill, product)).Sum(t => t.stackCount);
                    if (installed == 0 && carriedElsewhere == 0)
                    {
                        continue;
                    }

                    int counted = counter.CountProducts(bill);
                    bool ok = counted >= installed + carriedElsewhere;
                    bad += ok ? 0 : 1;
                    sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {map} {product.defName}: counted {counted}, installed on all colonists {installed}, carried by colonists elsewhere {carriedElsewhere}");
                }
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad} bills undercount installed parts");
        }

        private static IEnumerable<Pawn> Colonists => PawnsFinder.AllMaps_FreeColonists.OrderBy(p => p.LabelShort);

        private static void Features_(StringBuilder sb)
        {
            List<string> all = Features.Groups.SelectMany(g => g.features).ToList();
            List<string> notApplied = all.Where(f => Features.Enabled(f) && !Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(f)).ToList();
            sb.AppendLine($"applied {Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Count} / enabled {all.Count(Features.Enabled)} / total {all.Count}");
            sb.AppendLine("enabled but NOT applied: " + (notApplied.Count == 0 ? "none" : string.Join(", ", notApplied)));
            sb.AppendLine("disabled: " + string.Join(", ", all.Where(f => !Features.Enabled(f))));
        }

        private static void Surgery(StringBuilder sb)
        {
            GameComponent_SurgeryPlans plans = GameComponent_SurgeryPlans.Instance;
            if (plans == null)
            {
                sb.AppendLine("FAIL: GameComponent_SurgeryPlans missing");
                return;
            }

            sb.AppendLine($"presets: {string.Join(", ", plans.presets.Select(p => $"{p.label}({p.entries.Count})"))}");
            foreach (SurgeryPreset preset in plans.presets)
            {
                List<string> noIcon = preset.entries.Where(e => SurgeryPlanner.IconFor(e.hediff) == null).Select(e => e.hediff.defName).ToList();
                if (noIcon.Count > 0)
                {
                    sb.AppendLine($"WARN no icon in '{preset.label}': {string.Join(", ", noIcon)}");
                }
            }

            foreach (Pawn pawn in Colonists)
            {
                SurgeryPreset preset = plans.PresetFor(pawn);
                List<PlanStatus> statuses = SurgeryPlanner.Evaluate(pawn, preset);
                string counts = string.Join(" ", Enum.GetValues(typeof(PlanState)).Cast<PlanState>()
                    .Select(s => $"{s}={statuses.Count(x => x.state == s)}"));
                sb.AppendLine($"{pawn.LabelShort} [{pawn.def.defName}] plan={preset?.label ?? "-"} {counts}");
                foreach (IGrouping<string, PlanStatus> blocked in statuses.Where(s => s.state == PlanState.Blocked).GroupBy(s => s.reason))
                {
                    sb.AppendLine($"    blocked ({blocked.Key}): {string.Join(", ", blocked.Select(s => s.entry.hediff.defName + "@" + s.part.Label + PartHediffs(pawn, s.part)).Distinct())}");
                }
            }
        }

        private static string PartHediffs(Pawn pawn, BodyPartRecord part)
        {
            List<string> names = pawn.health.hediffSet.hediffs.Where(h => h.Part == part).Select(h => h.def.defName).ToList();
            return names.Count == 0 ? "" : "{" + string.Join("+", names) + "}";
        }

        // 수면 효율은 상한(종족+유전자) 이하, 피로 누적 속도는 하한(종족+유전자+생체리듬 이식물) 이상, 상태 이상 피로 배율은 1 이상이어야 한다.
        private static void Sleep(StringBuilder sb)
        {
            Func<Pawn, StatDef, bool, float> limit = AccessTools.MethodDelegate<Func<Pawn, StatDef, bool, float>>(
                AccessTools.Method(AccessTools.TypeByName("Yuudong2Mod.SleepLimitsUtility"), "BaseAndGenes"));
            int bad = 0;
            foreach (Pawn pawn in Colonists)
            {
                float restRate = pawn.GetStatValue(StatDefOf.RestRateMultiplier);
                float fallFactor = pawn.GetStatValue(StatDefOf.RestFallRateFactor);
                float hediffFall = pawn.health.hediffSet.RestFallFactor;
                float rateCap = limit(pawn, StatDefOf.RestRateMultiplier, false);
                float fallFloor = limit(pawn, StatDefOf.RestFallRateFactor, true);
                bool ok = restRate <= rateCap + 0.001f && fallFactor >= fallFloor - 0.001f && hediffFall >= 0.999f;
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {pawn.LabelShort} [{pawn.def.defName}] restRate={restRate:F2} (cap {rateCap:F2}) fallFactor={fallFactor:F2} (floor {fallFloor:F2}) hediffFall={hediffFall:F2}");
                if (!ok && bad == 1)
                {
                    StatDef stat = restRate > rateCap + 0.001f ? StatDefOf.RestRateMultiplier : StatDefOf.RestFallRateFactor;
                    sb.AppendLine($"    parts: {string.Join(", ", stat.parts?.Select(p => p.GetType().Name + "(" + p.priority + ")") ?? Enumerable.Empty<string>())}");
                    sb.AppendLine("    " + stat.Worker.GetExplanationFull(StatRequest.For(pawn), stat.toStringNumberSense, stat.Worker.GetValue(pawn)).Replace("\n", "\n    "));
                }
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad} pawns out of limits");
        }
    }
}
