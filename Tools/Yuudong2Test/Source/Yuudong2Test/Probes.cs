using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
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
            ("no floor infestation", FloorInfestation),
            ("egg sac targeting", EggSacTargeting),
            ("bench apparel repair", BenchApparelRepair),
            ("litter decay", LitterDecay),
            ("multi haul", MultiHaul),
            ("enemy bases", EnemyBases),
            ("faction war", FactionWar),
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

        // 이륙 준비(GenerateGravship) 도중에 기판 판정을 다시 계산하게 만드는 호출을 남긴다. 그때 바닐라가 "Collection was modified"로 죽는다.
        private static bool watchSubstructureDirty;
        private static readonly List<string> substructureDirtyStacks = new List<string>();

        private static void OnSubstructureDirty()
        {
            if (watchSubstructureDirty && substructureDirtyStacks.Count < 3)
            {
                substructureDirtyStacks.Add(string.Join(Environment.NewLine, Environment.StackTrace.Split('\n').Skip(2).Take(10)));
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
            new Harmony("yuudong2.yuudong2test.gravship").Patch(AccessTools.Method(typeof(Building_GravEngine), nameof(Building_GravEngine.ForceSubstructureDirty)),
                prefix: new HarmonyMethod(typeof(Probes), nameof(OnSubstructureDirty)));

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

                // 게임에서는 매 틱 구역과 방이 갱신된다. 갱신이 밀린 채로 이륙을 흉내 내면 수집 도중에 시설 연결이 다시 계산되어 기판 목록이 바뀐다.
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                engine.ForceSubstructureDirty();
                _ = engine.ValidSubstructure;
                Gravship gravship;
                substructureDirtyStacks.Clear();
                watchSubstructureDirty = true;
                try
                {
                    gravship = GravshipUtility.GenerateGravship(engine);
                }
                catch (InvalidOperationException)
                {
                    // 생성자 첫머리의 정상 호출 하나를 뺀 나머지가 범인이다.
                    foreach (string stack in substructureDirtyStacks)
                    {
                        sb.AppendLine("substructure dirtied during capture by:" + Environment.NewLine + stack);
                    }

                    throw;
                }
                finally
                {
                    watchSubstructureDirty = false;
                }

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
        private static void FloorInfestation(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.NoFloorInfestation))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            MethodInfo score = AccessTools.Method(typeof(InfestationCellFinder), "GetScoreAt");
            MethodInfo canSpawn = AccessTools.Method(typeof(CompSpawnerHives), "CanSpawnHiveAt");
            int bad = 0;
            foreach (Map map in Find.Maps)
            {
                List<IntVec3> floors = map.AllCells.Where(c => FloorInfestationUtility.HasFloor(c, map)).ToList();

                // TryFindCell이 이 맵의 점수용 격자를 계산해 두므로 먼저 부른다.
                int picks = 0;
                int picksOnFloor = 0;
                for (int i = 0; i < 30; i++)
                {
                    if (InfestationCellFinder.TryFindCell(out IntVec3 cell, map))
                    {
                        picks++;
                        picksOnFloor += FloorInfestationUtility.HasFloor(cell, map) ? 1 : 0;
                    }
                }

                int scored = floors.Count(c => (float)score.Invoke(null, new object[] { c, map }) > 0f);
                int hiveAllowed = floors.Count(c => (bool)canSpawn.Invoke(null, new object[] { c, map, c, ThingDefOf.Hive, 0f, true }));
                int mapBad = picksOnFloor + scored + hiveAllowed;
                bad += mapBad;
                sb.AppendLine($"{(mapBad == 0 ? "ok  " : "FAIL")} {map}: floor cells={floors.Count}, scored floor cells={scored}, hive allowed on floor={hiveAllowed}, infestation picks={picks} (on floor {picksOnFloor})");
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        private static void EggSacTargeting(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.EggSacTargeting))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("EggSac");
            Map map = Find.CurrentMap ?? Find.Maps.FirstOrDefault();
            if (def == null || map == null || Faction.OfInsects == null)
            {
                sb.AppendLine($"skipped: egg sac def={def != null}, map={map != null}, insects={Faction.OfInsects != null}");
                return;
            }

            int bad = 0;
            bool classOk = def.thingClass == typeof(Building_EggSac);
            bad += classOk ? 0 : 1;
            sb.AppendLine($"{(classOk ? "ok  " : "FAIL")} thingClass={def.thingClass}");

            if (!CellFinder.TryFindRandomCell(map, c => c.Standable(map) && c.GetEdifice(map) == null && !c.Fogged(map), out IntVec3 spot))
            {
                sb.AppendLine("FAIL: no free cell");
                return;
            }

            // 알주머니 투사체처럼 놓은 뒤 던진 쪽 세력을 준다. 지울 때는 유충이 나오지 않게 DeSpawn만 한다.
            foreach (Faction faction in new[] { Faction.OfInsects, null })
            {
                Thing sac = GenSpawn.Spawn(ThingMaker.MakeThing(def), spot, map);
                if (faction != null)
                {
                    sac.SetFaction(faction);
                }

                bool targeted = sac is Verse.AI.IAttackTarget target && map.attackTargetsCache.TargetsHostileToColony.Contains(target);
                bool ok = targeted == (faction != null);
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} faction={faction?.Name ?? "none"}: hostile target for colony={targeted}");
                sac.DeSpawn();
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 재봉대 종류에 컴프가 붙었는지 보고, 정착민 한 명의 옷을 깎아 잡 발급부터 맡기기, 수선, 다시 입기까지 돌려 본다.
        private static void BenchApparelRepair(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.BenchApparelRepair))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            int bad = 0;
            void Check(bool ok, string text)
            {
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {text}");
            }

            List<ThingDef> benchDefs = ApparelRepairUtility.BenchDefs;
            sb.AppendLine($"bench defs ({benchDefs.Count}): {string.Join(", ", benchDefs.Select(d => d.defName))}");
            foreach (string name in new[] { "HandTailoringBench", "ElectricTailoringBench" })
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                Check(def != null && benchDefs.Contains(def) && def.HasComp(typeof(CompApparelRepair)), $"{name} has the repair comp");
            }
            foreach (string name in new[] { "CraftingSpot", "FueledSmithy", "ElectricSmithy", "TableMachining", "FabricationBench", "TableButcher", "TableStonecutter" })
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                Check(def != null && !benchDefs.Contains(def), $"{name} is not a tailoring bench");
            }

            ThinkTreeDef tree = DefDatabase<ThinkTreeDef>.GetNamedSilentFail("MainColonistBehaviorCore");
            Check(tree != null && tree.thinkRoot.ThisAndChildrenRecursive.Any(n => n is JobGiver_RepairApparel), "job giver is in MainColonistBehaviorCore");
            Check(ApparelRepairDefOf.Yuudong2_RepairApparel != null, "job def exists");

            // 테스트 틱 동안 정착민이 스스로 쓰고 있던 재봉대는 여기서 상태가 보인다.
            foreach (Map m in Find.Maps.Where(x => x.IsPlayerHome))
            {
                foreach (Thing bench in benchDefs.SelectMany(d => m.listerThings.ThingsOfDef(d)))
                {
                    CompApparelRepair c = bench.TryGetComp<CompApparelRepair>();
                    Pawn worker = m.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.CurJobDef == ApparelRepairDefOf.Yuudong2_RepairApparel && x.CurJob.targetA.Thing == bench);
                    sb.AppendLine($"{m}: {bench.def.defName} state={c?.State.ToString() ?? "no comp"} user={c?.User?.LabelShort ?? "none"} progress={c?.Progress.ToStringPercent()} holds={c?.GetDirectlyHeldThings().ContentsString}; at work: {(worker == null ? "nobody" : worker.LabelShort + " (" + worker.jobs.curDriver.GetReport() + ")")}");
                    if (c != null && worker == null && c.State == ApparelRepairState.Idle && c.GetDirectlyHeldThings().Count > 0)
                    {
                        Check(false, "materials left in an idle bench that nobody is using");
                    }
                }
            }

            CompApparelRepair anyBench = null;
            Thing tempBench = null;
            Pawn pawn = null;
            Apparel target = null;
            foreach (Pawn candidate in Colonists)
            {
                // 이미 재봉대에 옷을 맡긴 정착민은 그 재봉대로 돌아가는 잡을 받으므로 고르지 않는다.
                if (!candidate.Spawned || candidate.Downed || candidate.apparel == null || ApparelRepairUtility.BenchHolding(candidate) != null)
                {
                    continue;
                }
                CompApparelRepair bench = benchDefs.SelectMany(d => candidate.Map.listerThings.ThingsOfDef(d))
                    .Where(t => t.Faction == Faction.OfPlayer)
                    .Select(t => t.TryGetComp<CompApparelRepair>())
                    .FirstOrDefault(c => c != null && c.State == ApparelRepairState.Idle && c.GetDirectlyHeldThings().Count == 0);
                Apparel worn = candidate.apparel.WornApparel.FirstOrDefault(a => a.def.useHitPoints && a.MaxHitPoints >= 10 && !candidate.apparel.IsLocked(a)
                    && a.def.CostListAdjusted(a.Stuff, false).Any(c => c.thingDef != null && c.count > 0 && !ApparelRepairUtility.IsExcludedMaterial(c.thingDef)));
                // 재봉대가 모두 쓰이는 중이면 임시 수동 재봉대를 놓고 돌린다.
                ThingDef handDef = DefDatabase<ThingDef>.GetNamedSilentFail("HandTailoringBench");
                if (bench == null && worn != null && handDef != null)
                {
                    Map benchMap = candidate.Map;
                    if (CellFinder.TryFindRandomCellNear(candidate.Position, benchMap, 40, c => GenAdj.OccupiedRect(c, Rot4.North, handDef.size).ExpandedBy(1).Cells
                        .All(x => x.InBounds(benchMap) && x.Standable(benchMap) && x.GetEdifice(benchMap) == null), out IntVec3 benchSpot))
                    {
                        tempBench = GenSpawn.Spawn(ThingMaker.MakeThing(handDef, GenStuff.DefaultStuffFor(handDef)), benchSpot, benchMap, Rot4.North);
                        tempBench.SetFaction(Faction.OfPlayer);
                        bench = tempBench.TryGetComp<CompApparelRepair>();
                        sb.AppendLine($"info: all benches busy, temporary HandTailoringBench placed at {benchSpot}");
                    }
                }
                if (bench != null && worn != null)
                {
                    anyBench = bench;
                    pawn = candidate;
                    target = worn;
                    break;
                }
            }
            if (pawn == null)
            {
                foreach (Map m in Find.Maps.Where(x => x.IsPlayerHome))
                {
                    var tables = m.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().GroupBy(t => t.def)
                        .Select(g => $"{g.Key.defName} x{g.Count()} (apparel recipes {g.Key.AllRecipes.Count(r => r.ProducedThingDef != null && r.ProducedThingDef.IsApparel)}/{g.Key.AllRecipes.Count(r => r.ProducedThingDef != null)})");
                    sb.AppendLine($"{m}: work tables: {string.Join(", ", tables)}");
                    int repairable = m.mapPawns.FreeColonistsSpawned.Count(c => c.apparel != null && c.apparel.WornApparel.Any(a => a.def.useHitPoints && !c.apparel.IsLocked(a)
                        && a.def.CostListAdjusted(a.Stuff, false).Any(x => x.thingDef != null && x.count > 0 && !ApparelRepairUtility.IsExcludedMaterial(x.thingDef))));
                    sb.AppendLine($"{m}: colonists {m.mapPawns.FreeColonistsSpawnedCount}, with repairable apparel {repairable}, worn apparel defs: {string.Join(", ", m.mapPawns.FreeColonistsSpawned.SelectMany(c => c.apparel.WornApparel).Select(a => a.def.defName).Distinct().Take(30))}");
                }
                sb.AppendLine("skipped run: no colonist with repairable apparel on a map with an idle tailoring bench");
                sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
                return;
            }

            Map map = pawn.Map;
            JobGiver_RepairApparel giver = new JobGiver_RepairApparel();
            target.HitPoints = Mathf.Max(1, Mathf.FloorToInt(target.MaxHitPoints * 0.85f));
            bool above = !ApparelRepairUtility.NeedsRepair(pawn, target);
            target.HitPoints = Mathf.Max(1, target.MaxHitPoints / 2);
            Check(above && ApparelRepairUtility.NeedsRepair(pawn, target), $"{pawn.LabelShort}: {target.LabelShort} is left alone at 85% and needs repair at 50%");
            Check(giver.GetPriority(pawn) > 0f, "job giver priority is above zero");

            Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
            List<Apparel> plan = ApparelRepairUtility.PlanRepair(pawn, _ => int.MaxValue / 2, need);
            Check(plan.Contains(target), $"plan has {plan.Count} apparel, materials: {string.Join(", ", need.Select(n => n.Key.defName + " x" + n.Value))}");

            List<Thing> materials = new List<Thing>();
            foreach (KeyValuePair<ThingDef, int> entry in need.ToList())
            {
                int left = entry.Value;
                while (left > 0)
                {
                    Thing stack = ThingMaker.MakeThing(entry.Key);
                    stack.stackCount = Mathf.Min(left, entry.Key.stackLimit);
                    left -= stack.stackCount;
                    if (GenPlace.TryPlaceThing(stack, pawn.Position, map, ThingPlaceMode.Near, out Thing placed))
                    {
                        materials.Add(placed);
                    }
                }
            }

            CompApparelRepair free = ApparelRepairUtility.FindFreeBench(pawn);
            if (free == null)
            {
                sb.AppendLine("info: no bench the colonist can reserve and reach right now, job check skipped");
            }
            else
            {
                Job job = (Job)AccessTools.Method(typeof(JobGiver_RepairApparel), "TryGiveJob").Invoke(giver, new object[] { pawn });
                Check(job != null && job.def == ApparelRepairDefOf.Yuudong2_RepairApparel && job.targetA.Thing == free.parent && !job.GetTargetQueue(TargetIndex.B).NullOrEmpty(),
                    $"job issued for {free.parent.def.defName} with {job?.GetTargetQueue(TargetIndex.B)?.Count ?? 0} material stacks queued");
            }

            CompApparelRepair comp = free ?? anyBench;
            foreach (Thing stack in materials.Where(m => m.Spawned).ToList())
            {
                stack.DeSpawn();
                comp.GetDirectlyHeldThings().TryAdd(stack);
            }

            bool started = comp.TryStartRepair(pawn);
            Check(started && comp.State == ApparelRepairState.Working && !pawn.apparel.WornApparel.Contains(target) && comp.GetDirectlyHeldThings().Contains(target)
                && ApparelRepairUtility.BenchHolding(pawn) == comp, $"apparel handed over, bench holds {comp.GetDirectlyHeldThings().Count} things, usable={comp.Usable}");

            SkillRecord crafting = pawn.skills?.GetSkill(SkillDefOf.Crafting);
            int levelBefore = crafting?.Level ?? 0;
            float xpBefore = crafting?.xpSinceLastLevel ?? 0f;
            comp.DoWork(100, pawn);
            bool partial = comp.State == ApparelRepairState.Working || !comp.Usable;
            if (crafting != null && !crafting.TotallyDisabled && comp.Usable)
            {
                Check(crafting.Level > levelBefore || crafting.xpSinceLastLevel > xpBefore,
                    $"crafting xp gained in 100 ticks of repair: level {levelBefore} -> {crafting.Level}, xp in level {xpBefore:F1} -> {crafting.xpSinceLastLevel:F1}");
            }
            comp.DoWork(10000000);
            if (!comp.Usable)
            {
                comp.FinishRepair();
            }
            Check(partial && comp.State == ApparelRepairState.Done && target.HitPoints == target.MaxHitPoints, $"repair finished, hit points {target.HitPoints}/{target.MaxHitPoints}");

            comp.RetrieveFor(pawn);
            Check(comp.State == ApparelRepairState.Idle && comp.GetDirectlyHeldThings().Count == 0 && pawn.apparel.WornApparel.Contains(target)
                && ApparelRepairUtility.BenchHolding(pawn) == null, "apparel worn again and bench empty");

            foreach (Thing stack in materials.Where(m => !m.Destroyed))
            {
                stack.Destroy();
            }
            if (tempBench != null && !tempBench.Destroyed)
            {
                tempBench.Destroy();
            }

            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 세력 전쟁: 출병과 전투 결과, 새 세력, 내 정착지 습격과 스토리텔러 습격 건너뛰기, 하루 요약 편지를 직접 돌려 본다.
        private static void FactionWar(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.FactionWar))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            int bad = 0;
            void Check(bool ok, string text)
            {
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {text}");
            }

            WorldComponent_FactionWar comp = FactionWarUtility.Comp;
            Check(comp != null && FactionWarDefOf.Yuudong2_WarArmy != null, "world component and army def exist");
            if (comp == null)
            {
                sb.AppendLine("FAIL: 1");
                return;
            }

            int errorsBefore = TestRunner.TickErrors.Count;
            List<Faction> factions = comp.SyncFactions();
            int SettlementCount(Faction f) => Find.WorldObjects.Settlements.Count(x => x.Faction == f);
            sb.AppendLine($"war factions ({factions.Count}): " + string.Join(", ", factions.OrderByDescending(comp.PowerOf)
                .Select(f => $"{f.Name}[{f.def.defName}] power {comp.PowerOf(f):F0}, settlements {SettlementCount(f)}{(f.HostileTo(Faction.OfPlayer) ? ", hostile to player" : "")}")));
            sb.AppendLine($"armies already on the map: {comp.Armies.Count()}");

            bool declared = comp.TryDeclareWar(factions);
            sb.AppendLine($"info: war declared between two non-hostile factions: {declared}");

            int battles = 0;
            foreach (Faction attacker in factions.OrderByDescending(comp.PowerOf).ToList())
            {
                if (battles >= 4)
                {
                    break;
                }
                Settlement target = Find.WorldObjects.Settlements.Where(x => x.Faction != null && x.Faction != attacker && FactionWarUtility.IsWarFaction(x.Faction)
                    && attacker.HostileTo(x.Faction) && !x.HasMap && x.questTags.NullOrEmpty() && FactionWarUtility.OnSurface(x)).InRandomOrder().FirstOrDefault();
                if (target == null || SettlementCount(attacker) == 0)
                {
                    continue;
                }

                Faction defender = target.Faction;
                PlanetTile tile = target.Tile;
                string place = target.Label;
                float attackerPower = comp.PowerOf(attacker);
                WorldObject_WarArmy army = comp.TryLaunchArmy(attacker, target);
                if (army == null)
                {
                    sb.AppendLine($"info: {attacker.Name} cannot reach {place}");
                    continue;
                }

                Check(army.Spawned && army.path.Count >= 2 && army.path[0] == army.Tile && army.path[army.path.Count - 1] == tile && army.strength > 0f
                    && comp.PowerOf(attacker) < attackerPower, $"{attacker.Name} -> {place} ({defender.Name}): path {army.path.Count} tiles, strength {army.strength:F0}, arrives in {army.TicksToArrival / 60000f:F1} days");
                if (battles == 0)
                {
                    // 첫 군대는 틱을 실제로 돌려 경로를 따라 걷게 한다.
                    int pathTiles = army.path.Count;
                    int calls = 0;
                    int limit = pathTiles * WorldObject_WarArmy.TicksPerTile + 5000;
                    bool movedMidway = false;
                    while (!army.Destroyed && calls < limit)
                    {
                        army.DoTick();
                        calls++;
                        if (!movedMidway && army.pathIndex >= 1 && !army.AtDestination)
                        {
                            Vector3 pos = army.DrawPos;
                            movedMidway = army.Tile == army.path[army.pathIndex] && !float.IsNaN(pos.x) && pos != Vector3.zero;
                        }
                    }
                    Check(army.Destroyed && (movedMidway || pathTiles <= 2) && calls >= (pathTiles - 1) * WorldObject_WarArmy.TicksPerTile * 0.9f,
                        $"army walked {pathTiles} tiles by ticking and arrived after {calls} ticks ({calls / 60000f:F1} days)");
                }
                else
                {
                    army.pathIndex = army.path.Count - 1;
                    army.Tile = tile;
                    comp.ArmyArrived(army);
                }

                string outcome = target.Destroyed && Find.WorldObjects.DestroyedSettlementAt(tile) != null ? "razed"
                    : !target.Destroyed && target.Faction == attacker ? "captured"
                    : !target.Destroyed && target.Faction == defender ? "repelled" : null;
                Check(army.Destroyed && outcome != null, $"battle resolved: {outcome ?? "inconsistent state"}; {defender.Name} now has {SettlementCount(defender)} settlements, defeated={defender.defeated}");
                battles++;
            }
            if (battles == 0)
            {
                sb.AppendLine("info: no hostile pair of factions within reach, battle check skipped");
            }

            int factionsBefore = Find.FactionManager.AllFactionsListForReading.Count;
            Faction added = comp.TryAddFaction();
            Check(added != null && Find.FactionManager.AllFactionsListForReading.Count == factionsBefore + 1 && SettlementCount(added) >= 1
                && Find.FactionManager.AllFactionsListForReading.All(f => f == added || added.RelationWith(f, true) != null),
                $"new faction: {added?.Name} [{added?.def.defName}], settlements {(added == null ? 0 : SettlementCount(added))}, hostile to player={added?.HostileTo(Faction.OfPlayer)}");

            factions = comp.SyncFactions();
            sb.AppendLine($"info: peace made between two hostile factions: {comp.TryMakePeace(factions)}");

            Settlement home = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == Faction.OfPlayer && x.HasMap && x.Map.IsPlayerHome && FactionWarUtility.OnSurface(x));
            WorldObject_WarArmy raidArmy = null;
            if (home != null)
            {
                foreach (Faction enemy in factions.Where(f => f.HostileTo(Faction.OfPlayer) && comp.PowerOf(f) > 0f)
                    .OrderBy(f => Find.WorldObjects.Settlements.Where(x => x.Faction == f).Min(x => Find.WorldGrid.ApproxDistanceInTiles(x.Tile, home.Tile))))
                {
                    raidArmy = comp.TryLaunchArmy(enemy, home);
                    if (raidArmy != null)
                    {
                        break;
                    }
                }
            }
            if (raidArmy == null)
            {
                sb.AppendLine($"info: no hostile faction can reach a player settlement (home found={home != null}), raid check skipped");
            }
            else
            {
                Map map = home.Map;
                Faction enemy = raidArmy.Faction;
                HashSet<int> thingsBefore = new HashSet<int>(map.listerThings.AllThings.Select(t => t.thingIDNumber));
                int creditBefore = comp.RaidCredit;
                sb.AppendLine($"{enemy.Name} -> {home.Label}: path {raidArmy.path.Count} tiles, arrives in {raidArmy.TicksToArrival / 60000f:F1} days");
                raidArmy.pathIndex = raidArmy.path.Count - 1;
                raidArmy.Tile = home.Tile;
                comp.ArmyArrived(raidArmy);

                List<Thing> arrived = map.listerThings.AllThings.Where(t => !thingsBefore.Contains(t.thingIDNumber) && ((t is Pawn p && p.Faction == enemy) || t is Skyfaller)).ToList();
                int expectedCredit = Yuudong2Settings.Current.factionWarReplaceRaids ? Mathf.Min(creditBefore + 1, 3) : creditBefore;
                Check(raidArmy.Destroyed && arrived.Count > 0 && comp.RaidCredit == expectedCredit,
                    $"raid on the player settlement: {arrived.Count(t => t is Pawn)} raiders spawned, {arrived.Count(t => t is Skyfaller)} incoming pods, raid credit {creditBefore} -> {comp.RaidCredit}");

                if (comp.RaidCredit > 0)
                {
                    int credit = comp.RaidCredit;
                    int thingsMid = map.listerThings.AllThings.Count;
                    FiringIncident fi = new FiringIncident(IncidentDefOf.RaidEnemy, Find.Storyteller.storytellerComps.First(), StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map));
                    bool fired = Find.Storyteller.TryFire(fi);
                    Check(!fired && comp.RaidCredit == credit - 1 && map.listerThings.AllThings.Count == thingsMid, $"storyteller raid skipped once: fired={fired}, raid credit {credit} -> {comp.RaidCredit}");
                }

                foreach (Thing thing in arrived.Where(t => !t.Destroyed))
                {
                    thing.Destroy();
                }
            }

            // 동맹 합세: 한 세력을 동맹으로 만들고, 적대 정착지 맵을 만든 뒤 동맹 군대를 도착시킨다.
            Faction ally = factions.Where(f => !f.HostileTo(Faction.OfPlayer) && Faction.OfPlayer.CanChangeGoodwillFor(f, 1) && SettlementCount(f) > 0)
                .OrderByDescending(comp.PowerOf).FirstOrDefault();
            WorldObject_WarArmy allyArmy = null;
            Settlement enemyBase = null;
            if (ally != null)
            {
                Faction.OfPlayer.TryAffectGoodwillWith(ally, 200, canSendMessage: false, canSendHostilityLetter: false);
                List<Settlement> allySettlements = Find.WorldObjects.Settlements.Where(x => x.Faction == ally).ToList();
                foreach (Settlement candidate in Find.WorldObjects.Settlements.Where(x => FactionWarUtility.IsWarFaction(x.Faction) && x.Faction.HostileTo(Faction.OfPlayer)
                    && ally.HostileTo(x.Faction) && !x.HasMap && x.questTags.NullOrEmpty() && FactionWarUtility.OnSurface(x))
                    .OrderBy(x => allySettlements.Min(o => Find.WorldGrid.ApproxDistanceInTiles(o.Tile, x.Tile))).Take(6))
                {
                    allyArmy = comp.TryLaunchArmy(ally, candidate);
                    if (allyArmy != null)
                    {
                        enemyBase = candidate;
                        break;
                    }
                }
            }
            if (allyArmy == null)
            {
                sb.AppendLine($"info: no ally with a reachable enemy of the player (ally candidate={ally?.Name ?? "none"}), join check skipped");
            }
            else
            {
                Faction defender = enemyBase.Faction;
                PlanetTile baseTile = enemyBase.Tile;
                Check(ally.PlayerRelationKind == FactionRelationKind.Ally && FactionWarUtility.CanJoinPlayer(ally, defender)
                    && allyArmy.sizeFactor >= 0.39f && allyArmy.sizeFactor <= 1.01f,
                    $"ally {ally.Name} marches on {enemyBase.Label} ({defender.Name}): strength {allyArmy.strength:F0}, size x{allyArmy.sizeFactor:F2} of a garrison");
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                Map battleMap = GetOrGenerateMapUtility.GetOrGenerateMap(baseTile, null);
                long mapMs = watch.ElapsedMilliseconds;
                watch.Restart();
                float sizeFactor = allyArmy.sizeFactor;
                float allyPoints = comp.AllyPoints(allyArmy, defender, battleMap);
                allyArmy.pathIndex = allyArmy.path.Count - 1;
                allyArmy.Tile = baseTile;
                comp.ArmyArrived(allyArmy);
                int allies = battleMap.mapPawns.SpawnedPawnsInFaction(ally).Count;
                int defenders = battleMap.mapPawns.SpawnedPawnsInFaction(defender).Count(x => x.RaceProps.Humanlike);
                float headRatio = defenders > 0 ? (float)allies / defenders : 0f;
                // 머릿수 비율이 군대 크기 배수에서 크게 벗어나지 않아야 한다(병사 값 추정 오차 허용).
                Check(allyArmy.Destroyed && allies > 0 && headRatio <= sizeFactor * 1.4f + 0.05f && headRatio >= sizeFactor * 0.5f,
                    $"allied army joined the battle: {allies} allied pawns against {defenders} defenders (x{headRatio:F2}, target x{sizeFactor:F2}; {allyPoints:F0} points, average raid pawn cost {WorldComponent_FactionWar.AverageRaidPawnCost(ally):F0}; map {mapMs} ms, allies {watch.ElapsedMilliseconds} ms)");

                SettlementDefeatUtility.CheckDefeated(enemyBase);
                Check(!enemyBase.Destroyed && enemyBase.Faction == defender, "settlement still held while defenders stand");

                foreach (Pawn pawn in battleMap.mapPawns.SpawnedPawnsInFaction(defender).ToList())
                {
                    pawn.Destroy();
                }
                int lettersAtCapture = Find.LetterStack.LettersListForReading.Count;
                SettlementDefeatUtility.CheckDefeated(enemyBase);
                if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.SettlementConquest))
                {
                    Check(!enemyBase.Destroyed && enemyBase.Faction == ally && Find.WorldObjects.DestroyedSettlementAt(baseTile) == null
                        && Find.LetterStack.LettersListForReading.Count == lettersAtCapture + 1, $"defenders gone: settlement captured by the ally, owner now {enemyBase.Faction?.Name}");
                    SettlementDefeatUtility.CheckDefeated(enemyBase);
                    Check(!enemyBase.Destroyed && enemyBase.Faction == ally && Find.LetterStack.LettersListForReading.Count == lettersAtCapture + 1, "captured settlement is left alone afterwards");
                }
                else
                {
                    ChoiceLetter_SettlementDefeated letter = Find.LetterStack.LettersListForReading.LastOrDefault() as ChoiceLetter_SettlementDefeated;
                    Check(enemyBase.Destroyed && battleMap.Parent is DestroyedSettlement && letter != null && letter.ally == ally
                        && Find.LetterStack.LettersListForReading.Count == lettersAtCapture + 1, "defenders gone: settlement destroyed, one choice letter with the ally option");
                    if (letter != null)
                    {
                        sb.AppendLine($"--- {letter.Label}");
                        sb.AppendLine(letter.Text.ToString());
                        sb.AppendLine("options: " + string.Join(" | ", letter.Choices.Select(DescribeOption)));
                        sb.AppendLine("---");
                        Settlement given = SettlementConquestUtility.GiveToAlly(letter);
                        Check(given != null && given.Faction == ally && battleMap.Parent == given && Find.WorldObjects.DestroyedSettlementAt(baseTile) == null
                            && !Find.LetterStack.LettersListForReading.Contains(letter), $"handed over: {given?.Label} now belongs to {given?.Faction?.Name}");
                        if (given != null)
                        {
                            SettlementDefeatUtility.CheckDefeated(given);
                            Check(!given.Destroyed && given.Faction == ally, "handed-over settlement is left alone afterwards");
                        }
                    }
                }
                Current.Game.DeinitAndRemoveMap(battleMap, false);
            }

            PlanetTile ruinTile = TileFinder.RandomSettlementTileFor(Find.WorldGrid.Surface, null);
            if (ruinTile.Valid)
            {
                DestroyedSettlement oldRuins = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(ruinTile.LayerDef.DestroyedSettlementWorldObjectDef);
                oldRuins.Tile = ruinTile;
                Find.WorldObjects.Add(oldRuins);
                int ruinDays = Mathf.Max(1, Yuudong2Settings.Current.factionWarRuinDays);
                oldRuins.creationGameTicks = Find.TickManager.TicksGame - ruinDays * GenDate.TicksPerDay + 10;
                int removedEarly = comp.RemoveOldRuins(ruinDays);
                Check(!oldRuins.Destroyed, $"destroyed settlement younger than {ruinDays} days stays ({removedEarly} older ones removed)");
                oldRuins.creationGameTicks -= 10;
                comp.RemoveOldRuins(ruinDays);
                Check(oldRuins.Destroyed && !Find.WorldObjects.DestroyedSettlements.Contains(oldRuins), $"destroyed settlement is removed after {ruinDays} days");

                AbandonedSettlement abandoned = (AbandonedSettlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.AbandonedSettlement);
                abandoned.Tile = ruinTile;
                abandoned.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(abandoned);
                abandoned.creationGameTicks = Find.TickManager.TicksGame - ruinDays * GenDate.TicksPerDay + 10;
                comp.RemoveOldRuins(ruinDays);
                bool keptYoung = !abandoned.Destroyed;
                abandoned.creationGameTicks -= 10;
                comp.RemoveOldRuins(ruinDays);
                Check(keptYoung && abandoned.Destroyed, $"abandoned settlement stays before {ruinDays} days and is removed after");
                if (!abandoned.Destroyed)
                {
                    abandoned.Destroy();
                }
                if (!oldRuins.Destroyed)
                {
                    oldRuins.Destroy();
                }
            }
            else
            {
                sb.AppendLine("no free tile for the destroyed settlement check");
            }

            int lettersBefore = Find.LetterStack.LettersListForReading.Count;
            comp.DailyUpdate();
            Letter last = Find.LetterStack.LettersListForReading.LastOrDefault();
            Check(Find.LetterStack.LettersListForReading.Count > lettersBefore && last is ChoiceLetter, "daily summary letter sent");
            if (last is ChoiceLetter summary)
            {
                sb.AppendLine($"--- {summary.Label}");
                sb.AppendLine(summary.Text.ToString());
                sb.AppendLine("---");
            }
            sb.AppendLine($"armies on the map after the daily update: {comp.Armies.Count()}");

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            foreach (IGrouping<string, string> group in errors.GroupBy(m => m.Split('\n')[0]).Take(15))
            {
                sb.AppendLine($"error x{group.Count()}: {group.Key}");
            }
            bad += errors.Count;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 금지된 장비를 저장 구역 밖에 놓고, 바닐라 부패 처리를 하루 치씩 돌려 본다.
        private static void LitterDecay(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.LitterDecay))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            int bad = 0;
            void Check(bool ok, string text)
            {
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {text}");
            }

            Map map = Find.AnyPlayerHomeMap;
            MethodInfo tryDeteriorate = AccessTools.Method(typeof(SteadyEnvironmentEffects), "TryDoDeteriorate");
            Check(map != null && tryDeteriorate != null, "home map and vanilla deterioration method exist");
            if (map == null || tryDeteriorate == null)
            {
                sb.AppendLine($"FAIL: {bad}");
                return;
            }

            List<Thing> spawned = new List<Thing>();
            Thing Place(string defName, QualityCategory quality, bool forbidden)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def == null || !CellFinder.TryFindRandomCellNear(map.Center, map, 60,
                    c => c.Standable(map) && !c.Fogged(map) && c.GetFirstItem(map) == null && map.haulDestinationManager.SlotGroupAt(c) == null, out IntVec3 cell))
                {
                    return null;
                }
                Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                thing.TryGetComp<CompQuality>()?.SetQuality(quality, null);
                thing = GenSpawn.Spawn(thing, cell, map);
                thing.SetForbidden(forbidden, false);
                spawned.Add(thing);
                return thing;
            }
            void Visit(Thing thing, float days)
            {
                int visits = Mathf.CeilToInt(days * LitterDecayUtility.VisitsPerDay);
                for (int i = 0; i < visits && !thing.Destroyed; i++)
                {
                    tryDeteriorate.Invoke(map.steadyEnvironmentEffects, new object[] { thing, thing.Position.Roofed(map), false, thing.Position.GetTerrain(map) });
                }
            }

            int days = LitterDecayUtility.Days;
            Thing rifle = Place("Gun_BoltActionRifle", QualityCategory.Normal, true);
            Thing parka = Place("Apparel_Parka", QualityCategory.Normal, true);
            Thing allowed = Place("Gun_BoltActionRifle", QualityCategory.Normal, false);
            Thing masterwork = Place("Gun_BoltActionRifle", QualityCategory.Masterwork, true);
            Thing dropped = Place("Gun_BoltActionRifle", QualityCategory.Normal, true);
            if (rifle == null || parka == null || allowed == null || masterwork == null || dropped == null)
            {
                Check(false, "test gear placed outside storage");
            }
            else
            {
                Check(spawned.All(x => !x.IsInAnyStorage()), "test gear placed outside storage");
                float rate = SteadyEnvironmentEffects.FinalDeteriorationRate(rifle);
                Check(LitterDecayUtility.IsLitter(rifle) && Mathf.Approximately(rate, (float)rifle.MaxHitPoints / days),
                    $"forbidden rifle outside storage counts as abandoned: {rate:F1} hit points a day of {rifle.MaxHitPoints} (roofed: {rifle.Position.Roofed(map)})");
                string inspect = rifle.GetInspectStringLowPriority();
                Check(!inspect.NullOrEmpty() && inspect.Contains("Yuudong2.LitterDecay.Reason".Translate()), $"inspect line: {inspect}");

                Visit(rifle, days * 0.5f);
                float left = rifle.Destroyed ? 0f : (float)rifle.HitPoints / rifle.MaxHitPoints;
                Check(left > 0.35f && left < 0.65f, $"rifle after {days * 0.5f:F1} days: {left:P0} hit points left");
                Visit(rifle, days * 0.75f);
                Check(rifle.Destroyed, $"rifle is gone within {days * 1.25f:F2} days");

                Check(LitterDecayUtility.IsLitter(parka), "forbidden parka outside storage counts as abandoned");
                Visit(parka, days * 1.25f);
                Check(parka.Destroyed, $"parka is gone within {days * 1.25f:F2} days");

                Check(!LitterDecayUtility.IsLitter(allowed), "allowed rifle is not abandoned");
                if (Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.EquipmentDurability))
                {
                    Visit(allowed, days * 2f);
                    Check(!allowed.Destroyed && allowed.HitPoints == allowed.MaxHitPoints, "allowed rifle keeps its hit points");
                }

                Check(!LitterDecayUtility.IsLitter(masterwork), "forbidden masterwork rifle is spared");

                Pawn colonist = map.mapPawns.FreeColonists.FirstOrDefault(p => p.mindState != null && p.mindState.droppedWeapon == null);
                if (colonist != null)
                {
                    Check(LitterDecayUtility.IsLitter(dropped), "forbidden rifle nobody owns counts as abandoned");
                    colonist.mindState.droppedWeapon = dropped;
                    Check(!LitterDecayUtility.IsLitter(dropped), "weapon dropped by a downed colonist is spared");
                    colonist.mindState.droppedWeapon = null;
                }
                else
                {
                    sb.AppendLine("no colonist without a remembered weapon: dropped weapon check skipped");
                }

                dropped.SetForbidden(true, false);
                ISlotGroupParent storage = map.haulDestinationManager.AllGroupsListForReading.Select(g => g.parent).FirstOrDefault(p => p.AllSlotCells().Any(c => c.GetFirstItem(map) == null && c.Standable(map)));
                if (storage != null)
                {
                    IntVec3 slot = storage.AllSlotCells().First(c => c.GetFirstItem(map) == null && c.Standable(map));
                    dropped.Position = slot;
                    Check(dropped.IsInAnyStorage() && !LitterDecayUtility.IsLitter(dropped), "forbidden rifle inside storage is not abandoned");
                }
                else
                {
                    sb.AppendLine("no free storage cell: storage check skipped");
                }
            }

            foreach (Thing thing in spawned)
            {
                if (!thing.Destroyed)
                {
                    thing.Destroy();
                }
            }
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 임시 저장 구역과 소총 다섯 자루를 놓고, 정착민 한 명에게 운반을 시켜 게임 틱으로 끝까지 돌려 본다.
        private static void MultiHaul(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.MultiHaul))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            int bad = 0;
            void Check(bool ok, string text)
            {
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {text}");
            }

            Check(MultiHaulDefOf.Yuudong2_HaulBatch != null && MultiHaulDefOf.Yuudong2_UnloadHauled != null, "job defs exist");
            ThingDef rifleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_BoltActionRifle");
            Map map = Find.AnyPlayerHomeMap;
            Pawn pawn = map?.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && p.Awake() && !p.InMentalState && MultiHaulUtility.CanBatch(p)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                .OrderByDescending(p => MassUtility.FreeSpace(p)).FirstOrDefault();
            if (rifleDef == null || pawn == null)
            {
                sb.AppendLine("skipped run: no rifle def or no free colonist");
                sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
                return;
            }

            const int Rifles = 5;
            bool Free(IntVec3 c)
            {
                return c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && c.GetFirstItem(map) == null && map.zoneManager.ZoneAt(c) == null
                    && map.haulDestinationManager.SlotGroupAt(c) == null && c.GetEdifice(map) == null;
            }
            bool GoodCorner(IntVec3 c)
            {
                Room room = c.GetRoom(map);
                if (room == null || !pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                {
                    return false;
                }
                foreach (IntVec3 cell in new CellRect(c.x, c.z, Rifles, 4))
                {
                    if (!Free(cell) || cell.GetRoom(map) != room)
                    {
                        return false;
                    }
                }
                return true;
            }
            if (!CellFinder.TryFindRandomCellNear(pawn.Position, map, 40, GoodCorner, out IntVec3 corner))
            {
                sb.AppendLine("skipped run: no free 5x4 area near the colonist");
                sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
                return;
            }

            int errorsBefore = TestRunner.TickErrors.Count;
            Zone_Stockpile zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            zone.settings.filter.SetDisallowAll();
            zone.settings.filter.SetAllow(ThingCategoryDefOf.Weapons, true);
            zone.settings.Priority = StoragePriority.Critical;
            for (int i = 0; i < Rifles; i++)
            {
                zone.AddCell(new IntVec3(corner.x + i, 0, corner.z));
            }

            List<Thing> rifles = new List<Thing>();
            for (int i = 0; i < Rifles; i++)
            {
                Thing rifle = ThingMaker.MakeThing(rifleDef);
                rifle.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, null);
                rifles.Add(GenSpawn.Spawn(rifle, new IntVec3(corner.x + i, 0, corner.z + 3), map));
                rifle.SetForbidden(false, false);
            }

            float free = MassUtility.FreeSpace(pawn);
            float rifleMass = rifles[0].GetStatValue(StatDefOf.Mass);
            int expectedExtras = Mathf.Min(Rifles - 1, Mathf.FloorToInt(free / rifleMass));
            sb.AppendLine($"{pawn.LabelShort}: free carry mass {free:F1} kg of {MassUtility.Capacity(pawn):F1}, rifle {rifleMass:F1} kg, expecting {expectedExtras} in the inventory plus 1 in hand");

            pawn.jobs.StopAll();
            pawn.Position = new IntVec3(corner.x, 0, corner.z + 2);
            pawn.Notify_Teleported();
            Job job = HaulAIUtility.HaulToStorageJob(pawn, rifles[0], false);
            Check(job != null && job.def == MultiHaulDefOf.Yuudong2_HaulBatch, $"haul job is the batch job: {job?.def?.defName ?? "none"}");
            if (job != null)
            {
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                int peak = 0, ticks = 0, tripTicks = -1;
                bool unloadSeen = false;
                bool AllStored() => rifles.All(r => r.Spawned && zone.ContainsCell(r.Position));
                for (; ticks < 3000 && !AllStored(); ticks++)
                {
                    Find.TickManager.DoSingleTick();
                    peak = Mathf.Max(peak, rifles.Count(r => pawn.inventory.innerContainer.Contains(r)));
                    unloadSeen |= pawn.CurJobDef == MultiHaulDefOf.Yuudong2_UnloadHauled;
                    if (tripTicks < 0 && rifles.Any(r => r.Spawned && zone.ContainsCell(r.Position)))
                    {
                        tripTicks = ticks;
                    }
                }
                Check(peak == expectedExtras, $"rifles in the inventory at once: {peak} (expected {expectedExtras})");
                Check(AllStored(), $"all {Rifles} rifles stored after {ticks} ticks (first one after {tripTicks}): {rifles.Count(r => r.Spawned && zone.ContainsCell(r.Position))} in the stockpile");
                Check(expectedExtras == 0 || unloadSeen, "unload job ran after the trip");
                Check(!rifles.Any(r => pawn.inventory.innerContainer.Contains(r)) && !pawn.inventory.UnloadEverything, "nothing left in the inventory");
            }

            foreach (Thing rifle in rifles)
            {
                if (pawn.inventory.innerContainer.Contains(rifle))
                {
                    pawn.inventory.innerContainer.Remove(rifle);
                }
                if (!rifle.Destroyed)
                {
                    rifle.Destroy();
                }
            }
            zone.Delete();

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            foreach (IGrouping<string, string> group in errors.GroupBy(m => m.Split('\n')[0]).Take(10))
            {
                sb.AppendLine($"error x{group.Count()}: {group.Key}");
            }
            bad += errors.Count;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        private static string DescribeOption(DiaOption option)
        {
            Traverse traverse = Traverse.Create(option);
            string text = traverse.Field("text").GetValue<string>();
            return traverse.Field("disabled").GetValue<bool>() ? $"{text} (disabled: {traverse.Field("disabledReason").GetValue<string>()})" : text;
        }

        // 적대 정착지 맵을 실제로 하나 만들어 보고 지운다.
        private static void EnemyBases(StringBuilder sb)
        {
            bool scaling = Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.EnemyBaseScaling);
            bool ruins = Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.RealRuinsBases);
            if (!scaling && !ruins)
            {
                sb.AppendLine("skipped: features not applied");
                return;
            }

            Settlement settlement = Find.WorldObjects.Settlements
                .Where(s => s.Faction != null && !s.Faction.IsPlayer && s.Faction.def.humanlikeFaction && s.Faction.HostileTo(Faction.OfPlayer) && !s.HasMap)
                .OrderBy(s => s.ID)
                .FirstOrDefault();
            if (settlement == null)
            {
                sb.AppendLine("skipped: no hostile settlement on this world");
                return;
            }

            float points = EnemyBaseUtility.PlayerThreatPoints();
            bool expectRuins = ruins && RealRuinsBridge.Available && RealRuinsBridge.BlueprintFiles().Length > 0;
            sb.AppendLine($"settlement {settlement.Label} ({settlement.Faction.Name}, {settlement.Faction.def.techLevel}), raid points now {points:F0}, player wealth {EnemyBaseUtility.PlayerWealth():F0}, real ruins available={RealRuinsBridge.Available}");

            int errorsBefore = TestRunner.TickErrors.Count, warningsBefore = TestRunner.TickWarnings.Count;
            string placedBefore = RealRuinsBridge.LastPlacedFile;
            int skippedBefore = RealRuinsBridge.SkippedRuinScatters;
            Settlement conquered = null;
            TestRunner.ticking = true;
            Map map = null;
            int bad = 0;
            try
            {
                DateTime start = DateTime.UtcNow;
                map = GetOrGenerateMapUtility.GetOrGenerateMap(settlement.Tile, null);
                sb.AppendLine($"map {map.Size.x}x{map.Size.z} generated in {(DateTime.UtcNow - start).TotalSeconds:F1}s");
                if (expectRuins)
                {
                    sb.AppendLine($"real ruins bases: {RealRuinsBridge.BlueprintFiles().Length} downloaded, {RealRuinsBridge.LargeBaseCount(map)} in the large pool for this map size");
                }

                List<Pawn> defenders = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == settlement.Faction).ToList();
                float defenderPoints = defenders.Sum(p => p.kindDef.combatPower);
                List<Thing> owned = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Building && t.Faction == settlement.Faction).ToList();
                List<Building_Turret> allTurrets = map.listerThings.AllThings.OfType<Building_Turret>().ToList();
                int mannable = owned.Count(t => t.TryGetComp<CompMannable>() != null);
                int turretOperators = map.lordManager.lords.Where(l => l.LordJob is LordJob_ManTurrets).Sum(l => l.ownedPawns.Count);
                sb.AppendLine($"defenders {defenders.Count} worth {defenderPoints:F0} combat power, faction buildings {owned.Count}, turrets {allTurrets.Count} (owned by the faction {allTurrets.Count(t => t.Faction == settlement.Faction)}), mannable {mannable}, turret operators {turretOperators}");
                sb.AppendLine($"powered buildings on: {owned.Count(t => t.TryGetComp<CompPowerTrader>()?.PowerOn == true)} of {owned.Count(t => t.TryGetComp<CompPowerTrader>() != null)}, doors {owned.Count(t => t is Building_Door)}, beds {owned.Count(t => t is Building_Bed)}");

                if (scaling)
                {
                    // 무리 생성기는 포인트를 다 쓰지 못할 수 있다. 바닐라 고정값(최대 1600)보다 확실히 많은지만 본다.
                    bool ok = points <= 3200f || defenderPoints >= points * 0.5f;
                    bad += ok ? 0 : 1;
                    sb.AppendLine($"{(ok ? "ok  " : "FAIL")} defenders scaled: {defenderPoints:F0} of {points:F0} points");
                }

                if (expectRuins)
                {
                    bool placed = RealRuinsBridge.LastPlacedFile != null && (placedBefore == null || owned.Count > 0);
                    CellRect rect = RealRuinsBridge.LastPlacedRect;
                    bool ok = placed && owned.Count > 0;
                    bad += ok ? 0 : 1;
                    sb.AppendLine($"{(ok ? "ok  " : "FAIL")} real ruins base: {RealRuinsBridge.LastPlacedFile ?? "none"} at {rect} ({rect.Width}x{rect.Height}), market value of items in it {rect.Cells.Where(c => c.InBounds(map)).SelectMany(c => c.GetThingList(map)).Where(t => t.def.category == ThingCategory.Item).Distinct().Sum(t => t.MarketValue * t.stackCount):F0}");
                }
                else if (ruins)
                {
                    sb.AppendLine("--   real ruins base not expected: mod inactive or no downloaded blueprints");
                }

                if (ruins && RealRuinsBridge.Available)
                {
                    bool ok = RealRuinsBridge.SkippedRuinScatters == skippedBefore + 1;
                    bad += ok ? 0 : 1;
                    CellRect baseRect = MapGenerator.TryGetVar(GenStep_Settlement.RectVarName, out CellRect settlementRect) ? settlementRect : RealRuinsBridge.LastPlacedRect;
                    CellRect around = baseRect.ExpandedBy(5);
                    int outside = map.listerThings.AllThings.Count(t => t.def.category == ThingCategory.Building && t.def.building != null && !t.def.building.isNaturalRock
                        && t.def.IsEdifice() && !around.Contains(t.Position));
                    sb.AppendLine($"{(ok ? "ok  " : "FAIL")} real ruins scatter skipped on the settlement map ({RealRuinsBridge.SkippedRuinScatters - skippedBefore} time), built edifices outside the base area: {outside}");
                }
            }
            catch (Exception e)
            {
                bad++;
                sb.AppendLine("FAIL: " + e);
            }

            try
            {
                if (map != null && Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.SettlementConquest))
                {
                    Faction defender = settlement.Faction;
                    foreach (Pawn pawn in map.mapPawns.SpawnedPawnsInFaction(defender).ToList())
                    {
                        pawn.Destroy();
                    }
                    int lettersBefore = Find.LetterStack.LettersListForReading.Count;
                    // 바닐라는 함락 때 그 맵의 정착민 한 명으로 이야기를 기록한다. 이 맵에는 정착민이 없어서 나는 오류는 세지 않는다.
                    TestRunner.ticking = false;
                    SettlementDefeatUtility.CheckDefeated(settlement);
                    TestRunner.ticking = true;
                    ChoiceLetter_SettlementDefeated letter = Find.LetterStack.LettersListForReading.LastOrDefault() as ChoiceLetter_SettlementDefeated;
                    bool ok = settlement.Destroyed && map.Parent is DestroyedSettlement && letter != null && letter.ally == null
                        && Find.LetterStack.LettersListForReading.Count == lettersBefore + 1;
                    bad += ok ? 0 : 1;
                    sb.AppendLine($"{(ok ? "ok  " : "FAIL")} settlement defeated: one choice letter instead of the vanilla one");
                    if (letter != null)
                    {
                        sb.AppendLine($"--- {letter.Label}");
                        sb.AppendLine(letter.Text.ToString());
                        sb.AppendLine("options: " + string.Join(" | ", letter.Choices.Select(DescribeOption)));
                        sb.AppendLine("---");
                        int before = Find.WorldObjects.Settlements.Count(s => s.Faction == Faction.OfPlayer);
                        conquered = SettlementConquestUtility.Settle(letter);
                        int after = Find.WorldObjects.Settlements.Count(s => s.Faction == Faction.OfPlayer);
                        ok = conquered != null && conquered.Faction == Faction.OfPlayer && map.Parent == conquered && map.IsPlayerHome && after == before + 1
                            && !Find.LetterStack.LettersListForReading.Contains(letter);
                        bad += ok ? 0 : 1;
                        sb.AppendLine($"{(ok ? "ok  " : "FAIL")} settled in the defeated settlement: player settlements {before} -> {after} with a game limit of {Prefs.MaxNumberOfPlayerSettlements}");
                    }
                }
            }
            catch (Exception e)
            {
                bad++;
                sb.AppendLine("FAIL: " + e);
            }
            finally
            {
                TestRunner.ticking = false;
                if (map != null)
                {
                    Current.Game.DeinitAndRemoveMap(map, false);
                }
                if (conquered != null && !conquered.Destroyed)
                {
                    conquered.Destroy();
                }
            }

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            foreach (IGrouping<string, string> group in errors.Concat(TestRunner.TickWarnings.Skip(warningsBefore)).GroupBy(m => m.Split('\n')[0]).Take(15))
            {
                sb.AppendLine($"log x{group.Count()}: {group.Key}");
            }

            bad += errors.Count;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

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
