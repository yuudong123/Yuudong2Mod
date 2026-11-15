using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            ("roofed scanning", RoofedScanning),
            // 맵을 바꾸므로 항상 마지막에 둔다.
            ("gravship replace stuff", GravshipReplace),
        };

        // 교체 프레임이 지워지면 누가 지웠는지 남긴다.
        private static readonly List<string> frameDestroyStacks = new List<string>();

        private static void OnFrameDestroy(Frame __instance, DestroyMode mode)
        {
            if (__instance is ReplaceFrame && frameDestroyStacks.Count < 2)
            {
                frameDestroyStacks.Add($"{__instance} mode={mode}" + Environment.NewLine + string.Join(Environment.NewLine, Environment.StackTrace.Split('\n').Skip(2).Take(6)));
            }
        }

        // 중력부양선 위 건물에 재질 교체를 예약하고 이륙·착륙(같은 자리)을 두 번 한 뒤 프레임과 원래 건물이 그대로 있는지 본다.
        // 착륙 때는 프레임과 건물 중 먼저 놓인 쪽을 나중 것이 지우면 안 된다(SpawningWipes가 양쪽 모두 false여야 한다).
        private static void GravshipReplace(StringBuilder sb)
        {
            Map map = Find.Maps.FirstOrDefault(m => GravshipUtility.GetPlayerGravEngine_NewTemp(m) != null);
            Building_GravEngine engine = map == null ? null : GravshipUtility.GetPlayerGravEngine_NewTemp(map);
            if (engine == null || !Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.ReplaceStuff))
            {
                sb.AppendLine("skipped: no grav engine or feature off");
                return;
            }

            Type util = AccessTools.TypeByName("Yuudong2Mod.ReplaceStuffUtility");
            MethodInfo hasFrameDef = AccessTools.Method(util, "HasFrameDefFor");
            MethodInfo place = AccessTools.Method(util, "PlaceReplaceFrame");
            MethodInfo frameFor = AccessTools.Method(util, "FrameFor");
            MethodInfo postSwap = AccessTools.Method(typeof(GravshipPlacementUtility), "PostSwapMap");
            new Harmony("yuudong2.yuudong2test.gravship").Patch(AccessTools.Method(typeof(Frame), nameof(Frame.Destroy)),
                prefix: new HarmonyMethod(typeof(Probes), nameof(OnFrameDestroy)));

            engine.ForceSubstructureDirty();
            HashSet<IntVec3> substructure = new HashSet<IntVec3>(engine.ValidSubstructure);
            List<Thing> targets = map.listerBuildings.allBuildingsColonist
                .Where(b => b.def.MadeFromStuff && b.OccupiedRect().All(substructure.Contains)
                    && (bool)hasFrameDef.Invoke(null, new object[] { b.def }) && GenStuff.AllowedStuffsFor(b.def).Any(s => s != b.Stuff))
                .OrderByDescending(b => b.def.building.isWall || b.def.Fillage == FillCategory.Full).ThenBy(b => b.Position.x).ThenBy(b => b.Position.z)
                .Take(6).Cast<Thing>().ToList();
            var pairs = new List<(Thing target, ReplaceFrame frame, IntVec3 cell)>();
            for (int i = 0; i < targets.Count; i++)
            {
                Thing target = targets[i];
                ThingDef stuff = GenStuff.AllowedStuffsFor(target.def).First(s => s != target.Stuff);
                ReplaceFrame frame = (ReplaceFrame)place.Invoke(null, new object[] { target, stuff, null, null });
                if (frame != null && i % 2 == 1)
                {
                    frame.workDone = 10f;
                }

                pairs.Add((target, frame, target.Position));
            }

            int bad = 0;
            if (pairs.Count > 0 && pairs[0].frame != null)
            {
                ThingDef targetDef = pairs[0].target.def, frameDef = pairs[0].frame.def;
                bool wipeFrame = GenSpawn.SpawningWipes(targetDef, frameDef), wipeTarget = GenSpawn.SpawningWipes(frameDef, targetDef);
                bad += wipeFrame || wipeTarget ? 1 : 0;
                sb.AppendLine($"{(wipeFrame || wipeTarget ? "FAIL" : "ok  ")} SpawningWipes(building over frame)={wipeFrame} (frame over building)={wipeTarget}");
            }

            sb.AppendLine($"engine at {engine.Position}, substructure {substructure.Count} cells, frames placed {pairs.Count(p => p.frame != null)}");
            int errorsBefore = TestRunner.TickErrors.Count, warningsBefore = TestRunner.TickWarnings.Count;
            TestRunner.ticking = true;
            for (int cycle = 1; cycle <= 2; cycle++)
            {
                // 들고 있는 물건을 내려놓는 중에 기판 판정이 다시 계산되면 바닐라가 예외를 낸다. 실제 이륙 전처럼 미리 내려놓게 한다.
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.ToList())
                {
                    if (pawn.carryTracker?.CarriedThing != null)
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing _);
                    }
                }

                engine.ForceSubstructureDirty();
                _ = engine.ValidSubstructure;
                Gravship gravship = GravshipUtility.GenerateGravship(engine);
                map.powerNetManager.UpdatePowerNetsAndConnections_First(); // 비행 중 지나가는 틱 대신 전력망 정리
                List<Thing> carried = gravship.Things.ToList();
                sb.AppendLine($"cycle {cycle}: gravship carries {carried.Count} things, frames carried {pairs.Count(p => carried.Contains(p.frame))}, targets carried {pairs.Count(p => carried.Contains(p.target))}");
                GravshipPlacementUtility.PlaceGravshipInMap(gravship, gravship.originalPosition, map, out List<Thing> spawned);
                postSwap.Invoke(null, new object[] { gravship, spawned });
                Current.Game.Gravship = null;
                foreach (string stack in frameDestroyStacks)
                {
                    sb.AppendLine("    frame destroyed: " + stack);
                }

                frameDestroyStacks.Clear();
            }

            foreach ((Thing target, ReplaceFrame frame, IntVec3 cell) in pairs)
            {
                ReplaceFrame registered = frameFor.Invoke(null, new object[] { target }) as ReplaceFrame;
                bool ok = frame != null && target.Spawned && frame.Spawned && frame.Position == target.Position && frame.oldThing == target && registered == frame;
                bad += ok ? 0 : 1;
                string others = target.Spawned ? string.Join(", ", target.Position.GetThingList(map).Select(t => t.def.defName)) : "-";
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {target.def.defName}({target.Stuff?.defName}) {cell}: target spawned={target.Spawned} pos={target.PositionHeld}; frame spawned={frame?.Spawned} pos={frame?.Position} work={frame?.workDone} registered={registered == frame}; cell now [{others}]");
            }

            TestRunner.ticking = false;
            foreach (string message in TestRunner.TickErrors.Skip(errorsBefore).Concat(TestRunner.TickWarnings.Skip(warningsBefore)))
            {
                sb.AppendLine("    log: " + message.Split('\n')[0]);
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 스캐너 세 종류는 지붕 아래에 지을 수 있고(설치 제한 없음) 지붕 경고 대상이 아니어야 한다. 지붕 아래 있는 스캐너는 쓸 수 있어야 한다.
        private static void RoofedScanning(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.RoofedScanning))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            int bad = 0;
            foreach (string defName in new[] { "GroundPenetratingScanner", "LongRangeMineralScanner", "OrbitalScanner" })
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def == null)
                {
                    sb.AppendLine($"--   {defName}: not loaded");
                    continue;
                }

                bool placeBlocked = def.placeWorkers?.Contains(typeof(PlaceWorker_NotUnderRoof)) == true;
                bool ok = def.canBeUsedUnderRoof && !placeBlocked;
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {defName}: canBeUsedUnderRoof={def.canBeUsedUnderRoof} NotUnderRoof placeWorker={placeBlocked} placeWorkers=[{string.Join(", ", def.placeWorkers?.Select(t => t.Name) ?? Enumerable.Empty<string>())}]");
            }

            foreach (Map map in Find.Maps)
            {
                foreach (ThingWithComps thing in map.listerThings.AllThings.OfType<ThingWithComps>().Where(t => t.Faction == Faction.OfPlayer && RoofUtility.IsAnyCellUnderRoof(t)))
                {
                    CompScanner scanner = thing.GetComp<CompScanner>();
                    if (scanner != null)
                    {
                        AcceptanceReport report = scanner.CanUseNow;
                        bool ok = report.Accepted || report.Reason.NullOrEmpty();
                        bad += ok ? 0 : 1;
                        sb.AppendLine($"{(ok ? "ok  " : "FAIL")} roofed {thing.def.defName} on {map}: CanUseNow={report.Accepted} {report.Reason}");
                    }
                }
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

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
