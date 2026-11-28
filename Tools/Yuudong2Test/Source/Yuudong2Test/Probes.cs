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
using Yuudong2Mod.OrderTools;
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
            ("order tools", OrderToolsProbe),
            ("gravship no range", GravshipNoRange),
            ("sleep limits", Sleep),
            ("installed parts count", InstalledParts),
            ("roofed scanning", RoofedScanning),
            ("no floor infestation", FloorInfestation),
            ("egg sac targeting", EggSacTargeting),
            ("bench apparel repair", BenchApparelRepair),
            ("litter decay", LitterDecay),
            ("multi haul", MultiHaul),
            ("multi load", MultiLoad),
            ("repair bill count", RepairBillCount),
            ("weapon pair", WeaponPair),
            ("ratkin shield", RatkinShield),
            ("dragonborn genes", DragonbornGenes),
            ("enemy bases", EnemyBases),
            ("enemy gravship", EnemyGravship),
            ("two gravships", TwoGravships),
            ("no beacon alert", NoBeaconAlert),
            ("no emp brain shock", NoEmpBrainShock),
            ("land over wall lamps", LandOverWallLamps),
            ("stack limit", StackLimit),
            ("policy weapons", PolicyWeapons),
            ("pulse minigun", PulseMinigun),
            ("weapon auto equip", WeaponAutoEquip),
            ("trader request cooldown", TraderRequestCooldownProbe),
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

            // 행군 중인 적 군대 기습: 전투 맵을 열고, 물러나면 군대가 줄고, 다 물리치면 군대가 사라진다.
            Faction ambushed = factions.FirstOrDefault(f => f.HostileTo(Faction.OfPlayer) && comp.PowerOf(f) > 0f && f.def.humanlikeFaction);
            Settlement ambushTarget = ambushed == null ? null : Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction != ambushed && !x.Faction.IsPlayer && x.Faction.HostileTo(ambushed) && FactionWarUtility.OnSurface(x));
            WorldObject_WarArmy marching = ambushed == null || ambushTarget == null ? null : comp.TryLaunchArmy(ambushed, ambushTarget);
            if (marching == null)
            {
                sb.AppendLine("info: no hostile army could be launched, ambush check skipped");
            }
            else
            {
                Check(!ArmyAttackUtility.CanAttack(marching, marching.Tile).Accepted && ArmyAttackUtility.StartBattle(marching, marching.Tile) == null, "no battle opens on the settlement tile the army starts from");
                if (marching.path.Count > 2)
                {
                    marching.pathIndex = 1;
                    marching.Tile = marching.path[1];
                }
                Gizmo command = marching.GetGizmos().OfType<Command_Action>().FirstOrDefault(g => g.icon == ArmyAttackUtility.AttackTex);
                Check(command != null && command.Disabled && ArmyAttackUtility.CanAttack(marching, marching.Tile).Accepted,
                    $"ambush command on {marching.Label}: {(command == null ? "missing" : command.Disabled ? "disabled - " + command.disabledReason : "enabled")}");
                float strength = marching.strength;
                PlanetTile at = marching.Tile;
                WorldObject_ArmyBattle fight = ArmyAttackUtility.StartBattle(marching, at);
                TestRunner.ticking = false;
                Map field = GetOrGenerateMapUtility.GetOrGenerateMap(fight.Tile, ArmyAttackUtility.MapSize, null);
                TestRunner.ticking = true;
                List<Pawn> soldiers = field.mapPawns.SpawnedPawnsInFaction(ambushed).ToList();
                Check(field.Parent == fight && soldiers.Count == Mathf.RoundToInt(strength) && fight.initialEnemies == soldiers.Count && soldiers.All(p => Verse.AI.Group.LordUtility.GetLord(p) != null),
                    $"battle map {field.Size.x}x{field.Size.z}: {soldiers.Count} soldiers for an army showing strength {Mathf.RoundToInt(strength)}, lord {(soldiers.Count > 0 ? Verse.AI.Group.LordUtility.GetLord(soldiers[0]) : null)?.LordJob.GetType().Name}");
                Pawn scout = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(scout, CellFinder.RandomEdgeCell(field), field);
                // 병사들이 정찰병을 잡아 전투가 먼저 끝나면 군대가 다시 움직여서, 이 확인 동안은 병사들을 묶어 둔다.
                foreach (Pawn soldier in soldiers)
                {
                    soldier.stances.stunner.StunFor(WorldObject_WarArmy.TicksPerTile + 200, null, false, false);
                }
                int tileBefore = marching.pathIndex;
                for (int i = 0; i < WorldObject_WarArmy.TicksPerTile + 100; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                Check(marching.Engaged && marching.pathIndex == tileBefore && !fight.won, $"army holds still during the battle (path index {marching.pathIndex}), battle not won while {soldiers.Count(p => !p.Downed && !p.Dead)} soldiers stand");
                int half = soldiers.Count / 2;
                foreach (Pawn p in soldiers.Take(half))
                {
                    p.Destroy();
                }
                foreach (Pawn p in soldiers.Skip(half))
                {
                    p.jobs.StopAll();
                }
                scout.Destroy();
                for (int i = 0; i < 300 && !fight.Destroyed; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                Check(fight.Destroyed && !marching.Destroyed && marching.battle == null && marching.strength < strength && marching.strength > 0f,
                    $"left without winning with {soldiers.Count - half} of {soldiers.Count} standing: battle removed {fight.Destroyed}, army strength {strength:F0} -> {marching.strength:F0}");

                if (!marching.Destroyed)
                {
                    WorldObject_ArmyBattle second = ArmyAttackUtility.StartBattle(marching, marching.Tile);
                    TestRunner.ticking = false;
                    Map field2 = GetOrGenerateMapUtility.GetOrGenerateMap(second.Tile, ArmyAttackUtility.MapSize, null);
                    TestRunner.ticking = true;
                    Pawn witness = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    GenSpawn.Spawn(witness, CellFinder.RandomEdgeCell(field2), field2);
                    foreach (Pawn p in field2.mapPawns.SpawnedPawnsInFaction(ambushed).ToList())
                    {
                        p.Destroy();
                    }
                    for (int i = 0; i < 120; i++)
                    {
                        Find.TickManager.DoSingleTick();
                    }
                    Check(second.won && marching.Destroyed, $"all soldiers gone with a colonist on the map: battle won {second.won}, army destroyed {marching.Destroyed}");
                    witness.Destroy();
                    for (int i = 0; i < 300 && !second.Destroyed; i++)
                    {
                        Find.TickManager.DoSingleTick();
                    }
                    sb.AppendLine($"info: battle site removed after the last colonist left: {second.Destroyed}");
                }
            }

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

            // 앞선 검사가 남긴 정착지 맵에는 정착민이 없어서, 틱을 돌리면 바닐라 함락 판정이 이야기 기록에 쓸 정착민을 못 찾고 이 오류를 낸다.
            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).Where(e => !e.StartsWith("Failed to create tale object CaravanAssaultSuccessful")).ToList();
            foreach (IGrouping<string, string> group in errors.GroupBy(m => m.Split('\n')[0]).Take(15))
            {
                sb.AppendLine($"error x{group.Count()}: {group.Key}");
            }
            bad += errors.Count;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 금지된 장비를 저장 구역 밖에 놓고, 바닐라 부패 처리를 하루 치씩 돌려 본다.
        public static string cutsceneReport = "not run";
        private static DateTime cutsceneStart;

        private static Faction GravshipRaider()
        {
            return Find.FactionManager.AllFactionsVisible
                .Where(f => !f.IsPlayer && !f.defeated && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction && (int)f.def.techLevel >= (int)TechLevel.Industrial)
                .RandomElementWithFallback();
        }

        private static IncidentParms GravshipRaidParms(Map map, Faction faction)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            parms.faction = faction;
            parms.points = 1500f;
            return parms;
        }

        // 컷신은 여러 프레임에 걸쳐 진행되므로 러너가 시작과 끝을 따로 부른다.
        public static bool BeginGravshipCutscene()
        {
            if (!EnemyGravshipUtility.Active)
            {
                cutsceneReport = "skipped: feature not applied";
                return false;
            }

            if (GenCommandLine.CommandLineArgPassed("headless"))
            {
                cutsceneReport = "skipped: nothing renders without a window, run GameTest.ps1 -Windowed";
                return false;
            }

            Map map = Find.AnyPlayerHomeMap;
            Faction faction = GravshipRaider();
            if (map == null || faction == null || !ShipLibrary.WaitForScan(180000))
            {
                cutsceneReport = $"skipped: home map {map != null}, hostile industrial faction {faction != null}, designs {ShipLibrary.Count}";
                return false;
            }

            DebugViewSettings.saveGravshipRenders = true;
            cutsceneStart = DateTime.UtcNow;
            bool started = EnemyGravshipUtility.TryStartRaid((IncidentWorker_Raid)IncidentDefOf.RaidEnemy.Worker, GravshipRaidParms(map, faction), true);
            cutsceneReport = started ? "started as " + EnemyGravshipLanding.lastMode + "; " + EnemyGravshipUtility.lastReport : "FAIL: could not start";
            return started && EnemyGravshipLanding.Active;
        }

        public static void EndGravshipCutscene(bool timedOut)
        {
            DebugViewSettings.saveGravshipRenders = false;
            cutsceneReport = $"{(timedOut ? "FAIL: still running; " : "")}mode {EnemyGravshipLanding.lastMode}, stage {EnemyGravshipLanding.stage}, took {(DateTime.UtcNow - cutsceneStart).TotalSeconds:F1}s, "
                + $"cutscene flag {WorldComponent_GravshipController.CutsceneInProgress}, screenshot mode {Find.ScreenshotModeHandler.Active}, travelling {Find.GravshipController.IsGravshipTravelling}; {EnemyGravshipUtility.lastReport}";
        }

        private static void EnemyGravship(StringBuilder sb)
        {
            if (!EnemyGravshipUtility.Active)
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

            Check(!cutsceneReport.StartsWith("FAIL") && !WorldComponent_GravshipController.CutsceneInProgress && !Find.ScreenshotModeHandler.Active, "cutscene landing: " + cutsceneReport);
            Check(ShipLibrary.WaitForScan(180000), $"design scan finished: {ShipLibrary.Count} files hold a gravship");
            int sampled = 0;
            System.Diagnostics.Stopwatch loadTimer = System.Diagnostics.Stopwatch.StartNew();
            List<string> shapes = new List<string>();
            foreach (string file in ShipLibrary.Files.Take(40))
            {
                sampled++;
                ShipDesign d = ShipLibrary.TryLoad(file);
                if (d != null)
                {
                    shapes.Add($"{d.size.x}x{d.size.z} {d.cells.Count}c {d.things.Count}t {d.floors.Count}f");
                }
            }
            Check(shapes.Count > 0, $"{shapes.Count} of {sampled} sampled files give a whole ship (read in {loadTimer.ElapsedMilliseconds} ms): {string.Join(", ", shapes.Take(14))}");

            Map map = Find.AnyPlayerHomeMap;
            Faction faction = GravshipRaider();
            if (map == null || faction == null)
            {
                sb.AppendLine("info: no home map or no hostile industrial faction, raid check skipped");
                sb.AppendLine($"FAIL: {bad}");
                return;
            }

            foreach (float points in new[] { 300f, 1500f, 5000f, 10000f })
            {
                bool planned = EnemyGravshipUtility.TryPlan(map, points, out ShipDesign plan, out IntVec3 at);
                sb.AppendLine($"info: {points} points aims at {EnemyGravshipUtility.TargetCells(points)} cells -> {(planned ? $"{plan.size.x}x{plan.size.z} {plan.cells.Count} cells at {at}" : EnemyGravshipUtility.lastReport)}");
            }

            List<string> picks = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                if (EnemyGravshipUtility.TryPlan(map, 20000f, out ShipDesign pick, out _))
                {
                    picks.Add(pick.source);
                }
            }

            bool repeated = Enumerable.Range(1, Math.Max(0, picks.Count - 1)).Any(i => picks[i] == picks[i - 1]);
            Check(picks.Count < 2 || (!repeated && picks.Distinct().Count() > 2), $"8 plans at 20000 points pick {picks.Distinct().Count()} different ships, same ship twice in a row: {repeated}");

            int errorsBefore = TestRunner.TickErrors.Count;
            TestRunner.ticking = true;
            HashSet<Pawn> before = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int lordsBefore = map.lordManager.lords.Count;
            IncidentParms parms = GravshipRaidParms(map, faction);
            bool started = EnemyGravshipUtility.TryStartRaid((IncidentWorker_Raid)IncidentDefOf.RaidEnemy.Worker, parms, false);
            Check(started && EnemyGravshipLanding.lastMode.StartsWith("instant"), $"raid by {faction.Name} started without cutscene: {EnemyGravshipUtility.lastReport}");
            if (started)
            {
                List<IntVec3> cells = EnemyGravshipUtility.lastShipCells;
                HashSet<IntVec3> ship = new HashSet<IntVec3>(cells);
                Building_GravEngine engine = cells.Select(c => c.GetEdifice(map)).OfType<Building_GravEngine>().FirstOrDefault();
                Check(engine != null && engine.Faction == faction, $"grav engine aboard, owned by {engine?.Faction?.Name ?? "nobody"}");
                Check(cells.All(c => map.terrainGrid.FoundationAt(c)?.IsSubstructure == true), $"all {cells.Count} ship cells have substructure");
                List<Thing> buildings = cells.SelectMany(c => c.GetThingList(map)).Where(t => t is Building).Distinct().ToList();
                Check(buildings.Count > 10 && buildings.Where(b => b.def.CanHaveFaction).All(b => b.Faction == faction),
                    $"{buildings.Count} buildings aboard: {string.Join(", ", buildings.GroupBy(b => b.def.defName).OrderByDescending(g => g.Count()).Take(10).Select(g => g.Key + " x" + g.Count()))}");
                int aboardItems = cells.Count(c => c.GetFirstItem(map) != null);
                Check(aboardItems == 0, $"items left under the ship: {aboardItems}");
                if (engine != null)
                {
                    sb.AppendLine($"info: engine supports {engine.ValidSubstructure.Count} of {cells.Count} cells, claimable by player: {engine.ClaimableBy(Faction.OfPlayer)}");
                }

                List<Pawn> raiders = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p) && p.Faction == faction).ToList();
                Check(raiders.Count > 0 && raiders.All(p => ship.Contains(p.Position)), $"{raiders.Count} raiders spawned, {raiders.Count(p => ship.Contains(p.Position))} of them aboard");
                Check(parms.raidArrivalMode == EnemyGravshipDefOf.Yuudong2_GravshipLanding && map.lordManager.lords.Count > lordsBefore,
                    $"arrival mode {parms.raidArrivalMode?.defName}, lord {map.lordManager.lords.LastOrDefault()?.LordJob?.GetType().Name}");
                for (int i = 0; i < 900; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
                int outside = raiders.Count(p => p.Spawned && !ship.Contains(p.Position));
                Check(outside > 0, $"after 900 ticks {outside} of {raiders.Count} raiders have left the ship ({raiders.Count(p => p.Dead)} dead)");
            }

            TestRunner.ticking = false;
            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            Check(errors.Count == 0, $"errors during the raid: {errors.Count}" + (errors.Count > 0 ? " - " + string.Join(" | ", errors.Take(4).Select(e => e.Split('\n')[0])) : ""));
            sb.AppendLine($"FAIL: {bad}");
        }

        // 플레이어 소유 배 두 척을 한 맵에 놓고, 시설이 자기 배의 엔진에만 연결되고 켜지는지 본다.
        private static void NoBeaconAlert(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            bool patched = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(Alert_ShuttleLandingBeaconUnusable), "GetReport"))?.Prefixes.Any(x => x.owner.Contains("uudong")) == true;
            bool active = new Alert_ShuttleLandingBeaconUnusable().GetReport().active;
            Check(patched && !active, $"unusable beacon alert: patched {patched}, active {active}");
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // EMP에 반응하는 뇌 이식물을 가진 정착민에게 EMP 반응을 일으켜, 뇌충격류 상태가 붙지 않는지 본다.
        private static void NoEmpBrainShock(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            Map map = Find.CurrentMap;
            HediffComp_ReactOnDamage comp = null;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                comp = p.health.hediffSet.hediffs.Select(h => h.TryGetComp<HediffComp_ReactOnDamage>()).FirstOrDefault(c => c != null && c.Props.damageDefIncoming == DamageDefOf.EMP && Yuudong2Mod.HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage_Patch.IsBrainShock(c.Props.createHediff));
                if (comp != null)
                {
                    break;
                }
            }
            if (comp == null)
            {
                Pawn p = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                HediffDef def = DefDatabase<HediffDef>.AllDefs.FirstOrDefault(d => d.comps != null && d.comps.OfType<HediffCompProperties_ReactOnDamage>().Any(c => c.damageDefIncoming == DamageDefOf.EMP && Yuudong2Mod.HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage_Patch.IsBrainShock(c.createHediff)));
                BodyPartRecord brain = p?.health.hediffSet.GetBrain();
                if (p != null && def != null && brain != null)
                {
                    comp = p.health.AddHediff(def, brain).TryGetComp<HediffComp_ReactOnDamage>();
                }
            }
            if (comp == null)
            {
                sb.AppendLine("skipped: no EMP-reacting implant");
                return;
            }

            Pawn pawn = comp.parent.pawn;
            HediffDef made = comp.Props.createHediff;
            int before = pawn.health.hediffSet.hediffs.Count(h => h.def == made);
            comp.Notify_PawnPostApplyDamage(new DamageInfo(DamageDefOf.EMP, 10f), 10f);
            int after = pawn.health.hediffSet.hediffs.Count(h => h.def == made);
            Check(after == before, $"{pawn.LabelShort} {comp.parent.def.defName} hit by EMP: {made.defName} {before} -> {after}");
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 벽 앞에 벽등을 달고, 그 칸을 덮는 자리에 왕복선 착륙이 허용되는지, 실제로 놓았을 때 벽등이 남는지 본다.
        private static void LandOverWallLamps(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            Map map = Find.CurrentMap;
            ThingDef shuttleDef = DefDatabase<ThingDef>.GetNamedSilentFail("PassengerShuttle") ?? ThingDefOf.Shuttle;
            ThingDef lampDef = DefDatabase<ThingDef>.GetNamed("WallLamp");
            Rot4 rot = shuttleDef.defaultPlacingRot;
            IntVec3 found = IntVec3.Invalid;
            foreach (IntVec3 c in map.AllCells.InRandomOrder().Take(30000))
            {
                if (!(c + IntVec3.North * 2).InBounds(map) || RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(c, map, shuttleDef, rot).Accepted == false)
                {
                    continue;
                }
                CellRect rect = GenAdj.OccupiedRect(c, rot, shuttleDef.size);
                if (rect.Cells.All(x => !x.Roofed(map)) && rect.Contains(c))
                {
                    found = c;
                    break;
                }
            }
            if (!found.IsValid)
            {
                sb.AppendLine("skipped: no landing spot");
                return;
            }

            CellRect occupied = GenAdj.OccupiedRect(found, rot, shuttleDef.size);
            IntVec3 lampCell = new IntVec3(occupied.minX, 0, occupied.maxZ);
            IntVec3 wallCell = lampCell + IntVec3.North;
            Thing wall = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.Steel), wallCell, map, WipeMode.Vanish);
            Thing lamp = GenSpawn.Spawn(ThingMaker.MakeThing(lampDef), lampCell, map, Rot4.North, WipeMode.Vanish);
            AcceptanceReport report = RoyalTitlePermitWorker_CallShuttle.ShuttleCanLandHere(found, map, shuttleDef, rot);
            Check(report.Accepted, $"shuttle landing over a wall lamp at {lampCell}: {(report.Accepted ? "accepted" : report.Reason)}");

            ShipLandingArea area = new ShipLandingArea(occupied, map);
            area.beacons.Add(new CompShipLandingBeacon { props = new CompProperties_ShipLandingBeacon() });
            area.RecalculateBlockingThing();
            Check(area.FirstBlockingThing != lamp, $"beacon landing area blocked by: {area.FirstBlockingThing?.LabelShort ?? "nothing"}");

            Thing shuttle = GenSpawn.Spawn(ThingMaker.MakeThing(shuttleDef), found, map, rot, WipeMode.Vanish);
            Check(!lamp.Destroyed && lamp.Spawned, $"wall lamp after the shuttle spawned: {(lamp.Destroyed ? "destroyed" : "kept")}");
            shuttle.Destroy();
            if (!lamp.Destroyed)
            {
                lamp.Destroy();
            }
            if (!wall.Destroyed)
            {
                wall.Destroy();
            }
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 배수를 3으로 바꿔 다시 곱하고, 강철·식사는 3배, 무기는 그대로인지, 실제로 한 칸에 그만큼 쌓이는지 본다.
        private static void StackLimit(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.StackLimit))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }
            int saved = Yuudong2Settings.Current.stackLimitMultiplier;
            ThingDef rifle = DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            Yuudong2Settings.Current.stackLimitMultiplier = 3;
            StackLimitUtility.Apply();
            int steelOriginal = StackLimitUtility.OriginalLimit(ThingDefOf.Steel);
            Check(ThingDefOf.Steel.stackLimit == steelOriginal * 3, $"steel x3: {steelOriginal} -> {ThingDefOf.Steel.stackLimit}");
            Check(ThingDefOf.MealSimple.stackLimit == StackLimitUtility.OriginalLimit(ThingDefOf.MealSimple) * 3, $"simple meal x3: {ThingDefOf.MealSimple.stackLimit}");
            Check(rifle.stackLimit == 1, $"assault rifle stays {rifle.stackLimit}");

            Thing a = ThingMaker.MakeThing(ThingDefOf.Steel);
            a.stackCount = steelOriginal * 2;
            Thing b = ThingMaker.MakeThing(ThingDefOf.Steel);
            b.stackCount = steelOriginal;
            bool absorbed = a.TryAbsorbStack(b, true);
            Check(absorbed && a.stackCount == steelOriginal * 3, $"one stack holds {a.stackCount} steel after merging");

            Yuudong2Settings.Current.stackLimitMultiplier = 1;
            StackLimitUtility.Apply();
            Check(ThingDefOf.Steel.stackLimit == steelOriginal, $"back to x1: steel {ThingDefOf.Steel.stackLimit}");
            Yuudong2Settings.Current.stackLimitMultiplier = saved;
            StackLimitUtility.Apply();
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 불러온 정책이 무기를 허용하는지, 무기를 뺀 정책을 주면 내려놓는 일을 주는지, 직접 장착시킨 무기는 두는지 본다.
        private static void PolicyWeapons(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            if (!PolicyWeaponsUtility.Active)
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }
            ThingFilter global = (ThingFilter)HarmonyLib.AccessTools.Property(typeof(Dialog_ManageApparelPolicies), "ApparelGlobalFilter").GetValue(null);
            ThingDef rifle = DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            Check(global.Allows(rifle), "policy window lists weapons");
            List<ApparelPolicy> policies = Current.Game.outfitDatabase.AllOutfits;
            sb.AppendLine($"info: policies allowing the assault rifle: {policies.Count(p => p.filter.Allows(rifle))}/{policies.Count}");

            Pawn pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.equipment?.Primary != null && !p.Drafted && !p.InMentalState);
            if (pawn == null)
            {
                sb.AppendLine("skipped: no armed colonist");
                return;
            }
            ThingWithComps weapon = pawn.equipment.Primary;
            ApparelPolicy before = pawn.outfits.CurrentApparelPolicy;
            ApparelPolicy test = Current.Game.outfitDatabase.MakeNewOutfit();
            Check(test.filter.Allows(weapon.def), "a new policy allows weapons");
            test.filter.SetAllow(weapon.def, false);
            pawn.outfits.CurrentApparelPolicy = test;
            bool wasForced = GameComponent_PolicyWeapons.Instance.IsForced(weapon);
            GameComponent_PolicyWeapons.Instance.SetForced(weapon, false);
            Verse.AI.Job job = PolicyWeaponsUtility.DropJob(pawn);
            Check(job != null && job.def == JobDefOf.DropEquipment && job.targetA.Thing == weapon, $"{pawn.LabelShort} drops {weapon.LabelShort} left out of the policy: {job?.def.defName ?? "no job"}");
            GameComponent_PolicyWeapons.Instance.SetForced(weapon, true);
            job = PolicyWeaponsUtility.DropJob(pawn);
            Check(job == null, $"a weapon equipped by order is kept: {job?.def.defName ?? "no job"}");
            Check(!PolicyWeaponsUtility.AllowsPickup(pawn, weapon), "the weapon is not picked up on its own");

            GameComponent_PolicyWeapons.Instance.SetForced(weapon, wasForced);
            pawn.outfits.CurrentApparelPolicy = before;
            Current.Game.outfitDatabase.TryDelete(test);
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 랫킨 모드가 있을 때 파동 미니건과 광폭쥐 프로젝트가 읽혔는지, 연결과 수치, 랫킨 정착민이 장착해 쏠 수 있는지 본다.
        private static void PulseMinigun(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            ThingDef gun = DefDatabase<ThingDef>.GetNamedSilentFail("Y2_PulseMinigun");
            if (!ModsConfig.IsActive("Solaris.RatkinRaceMod"))
            {
                Check(gun == null, "no ratkin mod, no pulse minigun");
                sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
                return;
            }
            ResearchProjectDef project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Y2_Research_BerserkRatProject");
            Check(gun != null && project != null, $"defs loaded: gun {gun != null}, research {project != null}");
            if (gun == null || project == null)
            {
                sb.AppendLine($"FAIL: {bad}");
                return;
            }
            Check(project.prerequisites != null && project.prerequisites.Any(p => p.defName == "RK_Research_AngryRatProject"), $"{project.LabelCap} follows the angry rat project, tab {project.tab?.defName}");
            RecipeDef recipe = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(r => r.ProducedThingDef == gun);
            Check(recipe != null && recipe.researchPrerequisite == project && recipe.AllRecipeUsers.Any(u => u.defName == "FabricationBench"), $"recipe {recipe?.defName} at the fabrication bench after {recipe?.researchPrerequisite?.defName}");

            ThingDef bulletDef = gun.Verbs[0].defaultProjectile;
            string gunTex = gun.graphic?.MatSingle?.mainTexture?.name;
            string bulletTex = bulletDef.graphic?.MatSingle?.mainTexture?.name;
            Check(gunTex == "PulseMinigun" && bulletTex == "PulseMinigunBullet", $"own textures loaded: gun {gunTex}, bullet {bulletTex}");
            ThingDef rifle = DefDatabase<ThingDef>.GetNamed("RK_PrototypePulseRifle");
            ThingDef minigun = DefDatabase<ThingDef>.GetNamed("Gun_Minigun");
            string Line(ThingDef d)
            {
                VerbProperties v = d.Verbs[0];
                ProjectileProperties p = v.defaultProjectile.projectile;
                float cycle = v.warmupTime + d.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown) + (v.burstShotCount - 1) * v.ticksBetweenBurstShots / 60f;
                float dps = v.burstShotCount * p.GetDamageAmount(null) / cycle;
                return $"{d.defName}: {v.burstShotCount}x{p.GetDamageAmount(null)} AP {p.GetArmorPenetration(null):0.00}, cooldown {d.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown)}, raw dps {dps:0.0}, accuracy medium {d.GetStatValueAbstract(StatDefOf.AccuracyMedium):0.00}";
            }
            sb.AppendLine("info: " + Line(rifle));
            sb.AppendLine("info: " + Line(minigun));
            sb.AppendLine("info: " + Line(gun));
            VerbProperties gv = gun.Verbs[0];
            Check(gv.defaultProjectile.projectile.GetArmorPenetration(null) >= 0.5f && gun.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown) < 1f && gv.burstShotCount >= 25, "pierces like the pulse rifle, fires like the minigun, short cooldown");

            Pawn ratkin = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.def.defName.Contains("Ratkin") && p.equipment != null);
            if (ratkin == null)
            {
                sb.AppendLine("info: no ratkin colonist, equip check skipped");
            }
            else
            {
                ThingWithComps made = (ThingWithComps)ThingMaker.MakeThing(gun);
                bool can = EquipmentUtility.CanEquip(made, ratkin, out string reason);
                Check(can, $"{ratkin.LabelShort} ({ratkin.def.defName}) can equip it" + (can ? "" : ": " + reason));
                made.Destroy();
            }
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 정착민마다 무기 상태와, 알아서 보조 무기를 찾지 않는 이유를 적는다.
        private static void WeaponAutoEquip(StringBuilder sb)
        {
            Map map = Find.CurrentMap;
            GameComponent_WeaponPair tracker = GameComponent_WeaponPair.Instance;
            List<Thing> weapons = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).Where(t => t.Spawned).ToList();
            sb.AppendLine($"info: weapons lying on the map: {weapons.Count} (ranged {weapons.Count(w => w.def.IsRangedWeapon)}, melee {weapons.Count(w => w.def.IsMeleeWeapon)}), forbidden {weapons.Count(w => w.IsForbidden(Faction.OfPlayer))}");
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                ThingWithComps primary = pawn.equipment?.Primary;
                string stowed = string.Join("/", pawn.inventory.innerContainer.Where(t => WeaponPairUtility.IsWeapon(t)).Select(t => t.LabelShort + (tracker.IsSidearmOf(pawn, t) ? "(sidearm)" : "")));
                List<string> why = new List<string>();
                if (primary == null) why.Add($"unarmed: allowed by policy and equippable - ranged {weapons.Count(w => w.def.IsRangedWeapon && !w.IsForbidden(pawn) && PolicyWeaponsUtility.AllowsPickup(pawn, w) && EquipmentUtility.CanEquip(w, pawn))}, melee {weapons.Count(w => w.def.IsMeleeWeapon && !w.IsForbidden(pawn) && PolicyWeaponsUtility.AllowsPickup(pawn, w) && EquipmentUtility.CanEquip(w, pawn))}");
                if (!tracker.AutoSidearmAllowed(pawn)) why.Add("auto sidearm stopped (dropped from gear tab)");
                if (pawn.WorkTagIsDisabled(WorkTags.Violent)) why.Add("incapable of violence");
                if (primary != null)
                {
                    bool wantRanged = !primary.def.IsRangedWeapon;
                    if (WeaponPairUtility.Stowed(pawn, wantRanged) != null) why.Add("already has the other kind");
                    if (wantRanged && pawn.WorkTagIsDisabled(WorkTags.Shooting)) why.Add("cannot shoot");
                    if (wantRanged && pawn.story != null && pawn.story.traits.HasTrait(TraitDefOf.Brawler)) why.Add("brawler");
                    List<Thing> kind = weapons.Where(w => w.def.IsRangedWeapon == wantRanged && WeaponPairUtility.IsWeapon(w) && !w.IsForbidden(pawn)).ToList();
                    int policyOk = kind.Count(w => PolicyWeaponsUtility.AllowsPickup(pawn, w));
                    int equipOk = kind.Count(w => EquipmentUtility.CanEquip(w, pawn, out string _, false));
                    int reach = kind.Count(w => pawn.CanReserveAndReach(w, Verse.AI.PathEndMode.ClosestTouch, pawn.NormalMaxDanger()));
                    why.Add($"{(wantRanged ? "ranged" : "melee")} candidates {kind.Count}: policy ok {policyOk}, can equip {equipOk}, reachable {reach}");
                }
                Verse.AI.Job job = WeaponPairUtility.AutoArmJob(pawn);
                sb.AppendLine($"info: {pawn.LabelShort} [{pawn.def.defName}] holds {primary?.LabelShort ?? "nothing"}; inventory weapons {(stowed.Length > 0 ? stowed : "none")}; policy {pawn.outfits?.CurrentApparelPolicy?.label}; job now: {job?.targetA.Thing?.LabelShort ?? "none"} | {string.Join("; ", why)}");
            }
        }

        // 동맹 세력에게 상단·궤도 상선을 부르는 메뉴를 만들어, 줄어든 대기 시간 안팎에서 켜지고 꺼지는지, 마지막 요청 시각이 그대로인지 본다.
        private static void TraderRequestCooldownProbe(StringBuilder sb)
        {
            int bad = 0;
            void Check(bool ok, string text)
            {
                sb.AppendLine((ok ? "ok   " : "FAIL ") + text);
                if (!ok)
                {
                    bad++;
                }
            }

            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.OrbitalTraderFrequency))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }
            Map map = Find.AnyPlayerHomeMap;
            Pawn negotiator = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            Faction ally = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => !f.IsPlayer && f.PlayerRelationKind == FactionRelationKind.Ally && f.def.canRequestTraders
                && f.def.allowedArrivalTemperatureRange.ExpandedBy(-4f).Includes(map.mapTemperature.SeasonalTemp));
            if (ally == null || negotiator == null)
            {
                sb.AppendLine("skipped: no ally that can send traders");
                return;
            }
            int now = Find.TickManager.TicksGame;
            DiaOption Make(string method)
            {
                return (DiaOption)HarmonyLib.AccessTools.Method(typeof(FactionDialogMaker), method).Invoke(null, new object[] { map, ally, negotiator });
            }
            int savedCaravan = ally.lastTraderRequestTick;
            int savedOrbital = ally.lastOrbitalTraderRequestTick;

            ally.lastTraderRequestTick = now - 70000;
            bool caravanOpen = !Make("RequestTraderOption").disabled;
            ally.lastTraderRequestTick = now - 50000;
            DiaOption caravanWait = Make("RequestTraderOption");
            Check(caravanOpen && caravanWait.disabled && ally.lastTraderRequestTick == now - 50000, $"{ally.Name}: caravan can be called again after 1 day (1.2 days ago: open {caravanOpen}; 0.8 days ago: {caravanWait.disabledReason})");

            ally.lastOrbitalTraderRequestTick = now - 230000;
            bool orbitalOpen = !Make("RequestOrbitalTraderOption").disabled;
            ally.lastOrbitalTraderRequestTick = now - 200000;
            DiaOption orbitalWait = Make("RequestOrbitalTraderOption");
            Check(orbitalOpen && orbitalWait.disabled && ally.lastOrbitalTraderRequestTick == now - 200000, $"orbital trader can be called again after 3.75 days (3.8 days ago: open {orbitalOpen}; 3.3 days ago: {orbitalWait.disabledReason})");

            ally.lastTraderRequestTick = savedCaravan;
            ally.lastOrbitalTraderRequestTick = savedOrbital;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        private static void TwoGravships(StringBuilder sb)
        {
            if (!EnemyGravshipUtility.Active)
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
            if (map == null || !ShipLibrary.WaitForScan(60000))
            {
                sb.AppendLine("skipped: no home map or no ship designs");
                return;
            }

            int errorsBefore = TestRunner.TickErrors.Count;
            List<Building_GravEngine> engines = new List<Building_GravEngine>();
            foreach (float points in new[] { 2500f, 1500f })
            {
                if (EnemyGravshipUtility.TryPlan(map, points, out ShipDesign design, out IntVec3 root))
                {
                    Building_GravEngine placed = EnemyGravshipUtility.Place(map, design, root, Faction.OfPlayer);
                    if (placed != null)
                    {
                        engines.Add(placed);
                    }
                }
            }
            if (engines.Count < 2)
            {
                sb.AppendLine($"skipped: only {engines.Count} ship(s) fit on the map ({EnemyGravshipUtility.lastReport})");
                return;
            }
            MultiGravshipUtility.Relink(map);
            for (int i = 0; i < 5; i++)
            {
                Find.TickManager.DoSingleTick();
            }

            int total = map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).Count;
            sb.AppendLine($"info: {total} grav engines on the map, two placed {engines[0].Position.DistanceTo(engines[1].Position):F0} cells apart");
            foreach (Building_GravEngine engine in engines)
            {
                HashSet<IntVec3> own = engine.AllConnectedSubstructure;
                List<Thing> linked = engine.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading;
                int strangers = linked.Count(f => !own.Contains(f.Position) && engines.Any(e => e != engine && e.AllConnectedSubstructure.Contains(f.Position)));
                List<CompGravshipFacility> aboard = own.SelectMany(c => c.GetThingList(map)).Distinct().Select(t => t.TryGetComp<CompGravshipFacility>()).Where(c => c != null).ToList();
                bool Powered(CompGravshipFacility c) => c.parent.TryGetComp<CompPowerTrader>()?.PowerOn ?? true;
                bool Supported(CompGravshipFacility c) => c.parent.Position.InHorDistOf(engine.Position, c.Props.maxDistance)
                    && (c.Props.onlyRequiresLooseConnection ? engine.LooselyConnectedToGravEngine(c.parent) : engine.OnValidSubstructure(c.parent));
                bool CapReached(CompGravshipFacility c) => linked.Count(l => l.def == c.parent.def) >= c.parent.def.GetCompProperties<CompProperties_Facility>().maxSimultaneous;
                int supported = aboard.Count(c => Powered(c) && Supported(c) && (linked.Contains(c.parent) || (c.CanLink() && !CapReached(c))));
                int active = aboard.Count(c => c.CanBeActive);
                foreach (CompGravshipFacility c in aboard.Where(c => !c.CanBeActive || !linked.Contains(c.parent)))
                {
                    sb.AppendLine($"info: {c.parent.def.defName} at {c.parent.Position}: active {c.CanBeActive}, powered {Powered(c)}, supported {Supported(c)}, linked {linked.Contains(c.parent)}, can link {c.CanLink()}, in vanilla link range {CompAffectedByFacilities.CanPotentiallyLinkTo_Static(c.parent, engine.def, engine.Position, engine.Rotation, map)}, same kind linked {linked.Count(l => l.def == c.parent.def)} of {c.parent.def.GetCompProperties<CompProperties_Facility>().maxSimultaneous}");
                }
                int unlinked = aboard.Count(c => !linked.Contains(c.parent) && c.CanLink());
                Check(strangers == 0, $"engine at {engine.Position}: {linked.Count} linked facilities, {strangers} of them stand on the other ship");
                Check(active == supported, $"engine at {engine.Position}: {aboard.Count} gravship facilities aboard, {supported} on supported substructure, {active} active, {unlinked} not linked");
                sb.AppendLine($"info: engine at {engine.Position} supports {engine.ValidSubstructure.Count} of {own.Count} cells (stat {engine.GetStatValue(StatDefOf.SubstructureSupport):F0})");
            }

            Building_GravEngine found = GravshipUtility.GetPlayerGravEngine_NewTemp(map);
            Check(found != null && found.Faction == Faction.OfPlayer, $"player engine lookup returns a player engine: {found?.Position.ToString() ?? "none"}");
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            Building_GravEngine last = map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).OfType<Building_GravEngine>().Last();
            if (pawn != null)
            {
                last.pawnsToBoard = new HashSet<Pawn> { pawn };
                Find.TickManager.DoSingleTick();
                Check(MultiGravshipUtility.EngineFor(map.listerThings.ThingsOfDef(ThingDefOf.GravEngine), pawn) == last, "boarding follows the engine that listed the colonist");
                Check(GravshipUtility.GetPlayerGravEngine_NewTemp(map) == last, "player engine lookup prefers the engine that is boarding");
                last.pawnsToBoard = null;
            }

            Faction hostile = GravshipRaider();
            if (hostile != null)
            {
                Building_GravEngine taken = engines[1];
                taken.SetFaction(hostile);
                Find.TickManager.DoSingleTick();
                Check(GravshipUtility.GetPlayerGravEngine_NewTemp(map) != taken, "an engine held by a hostile faction is not the player's engine");
                taken.SetFaction(Faction.OfPlayer);
            }

            // 한 배에 엔진을 하나 더 올려 한 척으로 합쳐지는지 본다.
            Building_GravEngine first = engines[0];
            HashSet<IntVec3> deck = first.AllConnectedSubstructure;
            IntVec3 spot = deck.FirstOrDefault(c => c.DistanceTo(first.Position) > 4f && CellRect.CenteredOn(c, 1).Cells.All(x => deck.Contains(x) && x.GetEdifice(map) == null && x.GetFirstItem(map) == null));
            if (spot.IsValid && spot != default(IntVec3))
            {
                float alone = first.GetStatValue(StatDefOf.SubstructureSupport);
                float rangeAlone = first.GetStatValue(StatDefOf.GravshipRange);
                float apartBefore = engines[1].GetStatValue(StatDefOf.SubstructureSupport);
                int componentsAlone = first.GravshipComponents.Count;
                Building_GravEngine second = (Building_GravEngine)GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.GravEngine), spot, map);
                second.SetFaction(Faction.OfPlayer);
                MultiGravshipUtility.Relink(map);
                Find.TickManager.DoSingleTick();
                float a = first.GetStatValue(StatDefOf.SubstructureSupport);
                float b = second.GetStatValue(StatDefOf.SubstructureSupport);
                List<Thing> linkedA = first.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading;
                List<Thing> linkedB = second.GetComp<CompAffectedByFacilities>().LinkedFacilitiesListForReading;
                Check(Mathf.Approximately(a, b) && Mathf.Approximately(a, alone + 500f), $"two engines on one ship: support {alone:F0} alone -> {a:F0} and {b:F0}");
                Check(!linkedA.Any(f => linkedB.Contains(f)), $"no facility is linked to both engines ({linkedA.Count} + {linkedB.Count})");
                Check(first.GravshipComponents.Count == second.GravshipComponents.Count && first.GravshipComponents.Count >= componentsAlone,
                    $"both engines see the same {first.GravshipComponents.Count} ship components (one engine alone saw {componentsAlone})");
                Check(Mathf.Approximately(first.GetStatValue(StatDefOf.GravshipRange), second.GetStatValue(StatDefOf.GravshipRange)) && first.GetStatValue(StatDefOf.GravshipRange) >= rangeAlone,
                    $"launch range {rangeAlone:F0} alone -> {first.GetStatValue(StatDefOf.GravshipRange):F0} and {second.GetStatValue(StatDefOf.GravshipRange):F0}");
                Check(Mathf.Approximately(first.TotalFuel, second.TotalFuel) && first.ValidSubstructure.Count == second.ValidSubstructure.Count,
                    $"fuel {first.TotalFuel:F0}/{second.TotalFuel:F0}, valid substructure {first.ValidSubstructure.Count}/{second.ValidSubstructure.Count} of {deck.Count}");
                Building_GravEngine apart = engines[1];
                Check(!EngineGroupUtility.SameShip(first, apart) && Mathf.Approximately(apart.GetStatValue(StatDefOf.SubstructureSupport), apartBefore), $"the separate ship keeps its own support: {apartBefore:F0} -> {apart.GetStatValue(StatDefOf.SubstructureSupport):F0}");
            }
            else
            {
                sb.AppendLine("info: no free 3x3 spot aboard for a second engine, engine group check skipped");
            }

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            Check(errors.Count == 0, $"errors: {errors.Count}" + (errors.Count > 0 ? " - " + string.Join(" | ", errors.Take(4).Select(e => e.Split('\n')[0])) : ""));
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        private static void WeaponPair(StringBuilder sb)
        {
            if (!WeaponPairUtility.Active)
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
            Pawn pawn = map?.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.Drafted && !p.WorkTagIsDisabled(WorkTags.Violent)
                && WeaponPairUtility.Stowed(p, true) == null && WeaponPairUtility.Stowed(p, false) == null);
            ThingDef rifleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_BoltActionRifle");
            ThingDef swordDef = DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_LongSword");
            if (pawn == null || rifleDef == null || swordDef == null)
            {
                sb.AppendLine("skipped: no free colonist or test weapons");
                return;
            }

            // 세이브의 의류 정책이 시험용 무기를 막지 않게 모두 허용하는 정책을 잠시 준다.
            ApparelPolicy savedPolicy = pawn.outfits?.CurrentApparelPolicy;
            ApparelPolicy openPolicy = PolicyWeaponsUtility.Active ? Current.Game.outfitDatabase.MakeNewOutfit() : null;
            if (openPolicy != null)
            {
                pawn.outfits.CurrentApparelPolicy = openPolicy;
            }

            int errorsBefore = TestRunner.TickErrors.Count;
            TestRunner.ticking = true;
            ThingWithComps original = pawn.equipment.Primary;
            if (original != null)
            {
                pawn.equipment.Remove(original);
            }

            ThingWithComps rifle = (ThingWithComps)ThingMaker.MakeThing(rifleDef);
            ThingWithComps sword = (ThingWithComps)ThingMaker.MakeThing(swordDef, ThingDefOf.Steel);
            ThingWithComps cargo = (ThingWithComps)ThingMaker.MakeThing(rifleDef);
            pawn.equipment.AddEquipment(sword);
            pawn.inventory.innerContainer.TryAdd(cargo, false);

            Thing spare = GenSpawn.Spawn(ThingMaker.MakeThing(rifleDef), pawn.Position, map);
            spare.SetForbidden(false, false);
            bool canShoot = !pawn.WorkTagIsDisabled(WorkTags.Shooting) && !pawn.story.traits.HasTrait(TraitDefOf.Brawler);
            GameComponent_WeaponPair.Instance.SearchSidearmNow(pawn);
            Job fetch = WeaponPairUtility.AutoArmJob(pawn);
            Check(canShoot ? fetch != null && fetch.def == JobDefOf.Equip && fetch.targetA.Thing.def.IsRangedWeapon : fetch == null,
                $"holding only a sword: {(canShoot ? "goes to equip a ranged weapon" : "cannot shoot, fetches nothing")} ({fetch?.targetA.Thing?.LabelShort ?? "no job"})");
            Check(WeaponPairUtility.AutoArmJob(pawn) == null, "does not search again right away");
            GameComponent_WeaponPair.Instance.StopAutoSidearm(pawn);
            bool stopped = !GameComponent_WeaponPair.Instance.AutoSidearmAllowed(pawn);
            GameComponent_WeaponPair.Instance.MarkSidearm(pawn, spare);
            Check(stopped && GameComponent_WeaponPair.Instance.AutoSidearmAllowed(pawn), "dropping the sidearm by hand stops the search, stowing one by hand resumes it");

            // 맨손에 소지품의 근접 무기만 있을 때: 근접 무기를 꺼내 들고, 총을 찾으러 간다.
            ThingWithComps held = pawn.equipment.Primary;
            pawn.equipment.TryTransferEquipmentToContainer(held, pawn.inventory.innerContainer);
            GameComponent_WeaponPair.Instance.MarkSidearm(pawn, held);
            GameComponent_WeaponPair.Instance.SearchSidearmNow(pawn);
            Job arm = WeaponPairUtility.AutoArmJob(pawn);
            Check(pawn.equipment.Primary == held && (canShoot ? arm != null && arm.targetA.Thing.def.IsRangedWeapon : arm == null),
                $"unarmed with a stowed sword: draws {pawn.equipment.Primary?.LabelShort ?? "nothing"}, then fetches {arm?.targetA.Thing?.LabelShort ?? "nothing"}");
            // 꺼내 들 때 걸린 바꿔 들기 대기 시간을 지워, 뒤의 바꿔 들기 검사에 영향을 주지 않게 한다.
            ((Dictionary<int, int>)HarmonyLib.AccessTools.Field(typeof(WeaponPairUtility), "lastSwapTick").GetValue(null)).Clear();
            spare.Destroy();

            TraitDef nimble = DefDatabase<TraitDef>.GetNamedSilentFail("Nimble");
            if (nimble != null)
            {
                Trait broken = new Trait(nimble, 2);
                Check(TraitDegreeFix.Fix(broken) && broken.Degree == nimble.degreeDatas[0].degree && !TraitDegreeFix.Fix(new Trait(nimble, nimble.degreeDatas[0].degree)),
                    "a trait with an undefined degree falls back to the first defined degree");
            }
            Check(!WeaponPairUtility.DrawRanged(pawn, null) && pawn.equipment.Primary == sword && pawn.inventory.innerContainer.Contains(cargo),
                $"a rifle carried as cargo is not drawn: in hand {pawn.equipment.Primary?.def.defName}");
            pawn.inventory.innerContainer.Remove(cargo);
            cargo.Destroy();
            pawn.equipment.Remove(sword);
            pawn.equipment.AddEquipment(rifle);
            pawn.equipment.MakeRoomFor(sword);
            pawn.equipment.AddEquipment(sword);
            Check(pawn.equipment.Primary == sword && pawn.inventory.innerContainer.Contains(rifle) && !rifle.Spawned,
                $"{pawn.LabelShort} equips a sword while holding a rifle: in hand {pawn.equipment.Primary?.def.defName}, rifle stowed {pawn.inventory.innerContainer.Contains(rifle)}, dropped on the ground {rifle.Spawned}");

            void Wait()
            {
                for (int i = 0; i < 35; i++)
                {
                    Find.TickManager.DoSingleTick();
                }
            }

            pawn.jobs.StopAll();
            Check(WeaponPairUtility.DrawRanged(pawn, null) && pawn.equipment.Primary == rifle && pawn.inventory.innerContainer.Contains(sword), "no enemy adjacent: draws the rifle, sword stowed");
            Check(!WeaponPairUtility.DrawMelee(pawn) && pawn.equipment.Primary == rifle, "no second switch within the cooldown");
            List<Thing> shownEquipment = WeaponPairUtility.EquipmentForGearTab(pawn.equipment);
            List<Thing> shownInventory = WeaponPairUtility.InventoryForGearTab(pawn.inventory);
            Check(shownEquipment.Contains(rifle) && shownEquipment.Contains(sword) && !shownInventory.Contains(sword),
                "gear tab lists the stowed sword under equipment, not inventory");
            Check(HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(typeof(ITab_Pawn_Gear), "FillTab"))?.Transpilers.Any(x => x.owner.Contains("uudong")) == true,
                "gear tab patch applied");
            Wait();

            pawn.inventory.UnloadEverything = true;
            Thing unload = pawn.inventory.FirstUnloadableThing.Thing;
            pawn.inventory.UnloadEverything = false;
            Check(unload != sword, $"the stowed sword is not offered for unloading (offered: {unload?.def.defName ?? "nothing"})");

            Faction hostile = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
            IntVec3 beside = GenAdj.CellsAdjacent8Way(pawn).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null);
            if (hostile != null && beside.IsValid && pawn.Spawned)
            {
                Pawn enemy = PawnGenerator.GeneratePawn(hostile.RandomPawnKind(), hostile);
                GenSpawn.Spawn(enemy, beside, map);
                pawn.stances.CancelBusyStanceHard();
                string meleeState = $"enemy {enemy.kindDef?.defName} spawned {enemy.Spawned} dead {enemy.Dead} at {enemy.PositionHeld}, pawn at {pawn.Position} downed {pawn.Downed} busy {pawn.stances.FullBodyBusy} stance {pawn.stances.curStance?.GetType().Name} canMelee {pawn.kindDef.canMeleeAttack}";
                // 자세를 풀자마자 대기 작업이 먼저 공격해 버리는 경우가 있다. 그때는 이미 근접 공격 후딜 중이다.
                bool autoAttacked = pawn.stances.curStance is Stance_Cooldown cooldown && cooldown.verb != null && cooldown.verb.IsMeleeAttack;
                bool attacked = pawn.meleeVerbs.TryMeleeAttack(enemy) || autoAttacked;
                meleeState += $", after: busy {pawn.stances.FullBodyBusy} stance {pawn.stances.curStance?.GetType().Name} verb {pawn.meleeVerbs.TryGetMeleeVerb(enemy)?.ToString() ?? "none"}";
                if (attacked || !enemy.Downed)
                {
                    Check(attacked && pawn.equipment.Primary == sword && pawn.inventory.innerContainer.Contains(rifle), $"melee attack on an adjacent enemy: attacked {attacked}, in hand {pawn.equipment.Primary?.def.defName}; {meleeState}");
                }
                else
                {
                    sb.AppendLine("info: the test enemy arrived downed, melee attack check skipped");
                }
                enemy.stances?.stunner?.StunFor(600, null, false, false);
                Wait();
                if (enemy.Spawned && !enemy.Downed && !enemy.Position.AdjacentTo8Way(pawn.Position))
                {
                    enemy.Position = beside;
                    enemy.Notify_Teleported();
                }
                pawn.stances.CancelBusyStanceHard();
                pawn.TryGetAttackVerb(enemy);
                if (enemy.Spawned && !enemy.Downed)
                {
                    Check(pawn.equipment.Primary == sword, $"enemy still adjacent: keeps the sword (in hand {pawn.equipment.Primary?.def.defName})");
                }
                else
                {
                    sb.AppendLine($"info: the test enemy went down (dead {enemy.Dead}, downed {enemy.Downed}), adjacent-enemy check skipped");
                }
                if (!enemy.Destroyed)
                {
                    enemy.Destroy();
                }
                pawn.stances.CancelBusyStanceHard();
                Verb verb = pawn.TryGetAttackVerb(null);
                Check(pawn.equipment.Primary == rifle && verb != null && !verb.IsMeleeAttack, $"enemy gone: back to the rifle (in hand {pawn.equipment.Primary?.def.defName})");
            }
            else
            {
                sb.AppendLine("info: no hostile faction or free cell beside the colonist, combat check skipped");
            }

            Wait();
            pawn.stances.CancelBusyStanceHard();
            WeaponPairUtility.SetMode(pawn, WeaponPairMode.Melee);
            Check(pawn.equipment.Primary == sword && !WeaponPairUtility.DrawRanged(pawn, null) && pawn.equipment.Primary == sword, "locked to melee: holds the sword and does not draw the rifle");
            WeaponPairUtility.SetMode(pawn, WeaponPairMode.Ranged);
            Check(pawn.equipment.Primary == rifle && !WeaponPairUtility.DrawMelee(pawn) && pawn.equipment.Primary == rifle, "locked to ranged: holds the rifle and does not draw the sword");
            List<Gizmo> gizmos = WeaponPairUtility.Gizmos(pawn).ToList();
            Check(gizmos.Count == 1, $"lock button shown: {(gizmos.Count == 1 ? ((Command)gizmos[0]).defaultLabel : "none")}");
            if (gizmos.Count == 1 && gizmos[0] is Command_WeaponPair merged)
            {
                Command_WeaponPair another = new Command_WeaponPair { pawns = new List<Pawn> { map.mapPawns.FreeColonistsSpawned.First(p => p != pawn) } };
                bool groups = merged.GroupsWith(another);
                merged.MergeWith(another);
                Check(groups && merged.pawns.Count == 2, $"buttons of two colonists merge into one ({merged.pawns.Count} colonists, label {merged.Label})");
            }
            WeaponPairUtility.SetMode(pawn, WeaponPairMode.Auto);

            if (pawn.equipment.Primary != null)
            {
                pawn.equipment.Remove(pawn.equipment.Primary);
            }
            pawn.inventory.innerContainer.Remove(rifle);
            pawn.inventory.innerContainer.Remove(sword);
            if (!rifle.Destroyed)
            {
                rifle.Destroy();
            }
            if (!sword.Destroyed)
            {
                sword.Destroy();
            }
            if (original != null)
            {
                pawn.equipment.AddEquipment(original);
            }

            TestRunner.ticking = false;
            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            Check(errors.Count == 0, $"errors: {errors.Count}" + (errors.Count > 0 ? " - " + string.Join(" | ", errors.Take(4).Select(e => e.Split('\n')[0])) : ""));
            if (openPolicy != null)
            {
                pawn.outfits.CurrentApparelPolicy = savedPolicy;
                Current.Game.outfitDatabase.TryDelete(openPolicy);
            }
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        private static void DragonbornGenes(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.DragonianGenes))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            int bad = 0;
            foreach (string defName in new[] { "Dragonian_Dragonborn", "Dragonian_Dragonborn_Black" })
            {
                GeneDef def = DefDatabase<GeneDef>.GetNamedSilentFail(defName);
                if (def == null)
                {
                    sb.AppendLine($"info: {defName} not loaded");
                    continue;
                }
                bool free = def.exclusionTags.NullOrEmpty();
                List<Pawn> carriers = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.Where(p => p.genes != null && p.genes.GenesListForReading.Any(g => g.def == def)).ToList();
                List<string> suppressed = carriers.SelectMany(p => p.genes.GenesListForReading.Where(g => g.overriddenByGene != null && g.overriddenByGene.def != g.def && (g.def == def || g.overriddenByGene.def == def))
                    .Select(g => $"{p.LabelShort}: {g.def.defName} by {g.overriddenByGene.def.defName}")).ToList();
                bool ok = free && suppressed.Count == 0;
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {defName}: exclusion tags {(free ? "none" : string.Join(",", def.exclusionTags))}, {carriers.Count} carriers, suppressions involving it: {suppressed.Count}{(suppressed.Count > 0 ? " - " + string.Join(" | ", suppressed.Take(5)) : "")}");
            }
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        private static void RatkinShield(StringBuilder sb)
        {
            Type filter = AccessTools.TypeByName("NewRatkin.CompGripTypeFilter");
            if (filter == null || !Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.RatkinShieldAnyWeapon))
            {
                sb.AppendLine("skipped: NewRatkinPlus not loaded or feature not applied");
                return;
            }

            MethodInfo allowed = AccessTools.Method(filter, "TryIsWeaponAllowed");
            List<ThingDef> shields = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.comps != null && d.comps.Any(c => c.compClass != null && filter.IsAssignableFrom(c.compClass))).ToList();
            List<ThingDef> weapons = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsWeapon && d.equipmentType == EquipmentType.Primary).ToList();
            int refused = 0;
            foreach (ThingDef shieldDef in shields)
            {
                ThingWithComps shield = (ThingWithComps)ThingMaker.MakeThing(shieldDef, shieldDef.MadeFromStuff ? GenStuff.DefaultStuffFor(shieldDef) : null);
                ThingComp comp = shield.AllComps.First(c => filter.IsInstanceOfType(c));
                foreach (ThingDef weapon in weapons)
                {
                    if (!(bool)allowed.Invoke(comp, new object[] { weapon, null }))
                    {
                        refused++;
                    }
                }
                shield.Destroy();
            }

            bool ok = shields.Count > 0 && refused == 0;
            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {shields.Count} grip-limited apparel ({string.Join(", ", shields.Take(6).Select(d => d.defName))}) x {weapons.Count} weapons: {refused} refused");
            sb.AppendLine(ok ? "PASS" : "FAIL: 1");
        }

        private static void RepairBillCount(StringBuilder sb)
        {
            if (!Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.BenchApparelRepair))
            {
                sb.AppendLine("skipped: feature not applied");
                return;
            }

            // 세이브에 실제로 수선 중인 재봉대가 있으면 그 옷으로 확인한다.
            foreach (Map map in Find.Maps.Where(m => m.IsPlayerHome))
            {
                foreach (ThingDef benchDef in ApparelRepairUtility.BenchDefs)
                {
                    foreach (Thing bench in map.listerThings.ThingsOfDef(benchDef))
                    {
                        CompApparelRepair comp = bench.TryGetComp<CompApparelRepair>();
                        if (comp == null || comp.State == ApparelRepairState.Idle || !(bench is IBillGiver giver))
                        {
                            continue;
                        }

                        foreach (Apparel apparel in comp.Repairing.ToList())
                        {
                            RecipeDef recipe = DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(r => r.products != null && r.products.Count == 1 && r.products[0].thingDef == apparel.def && r.specialProducts == null);
                            if (recipe == null)
                            {
                                continue;
                            }

                            Bill_Production bill = new Bill_Production(recipe) { includeEquipped = true, includeTainted = true, hpRange = FloatRange.ZeroToOne, qualityRange = QualityRange.All };
                            bill.billStack = giver.BillStack;
                            int counted = recipe.WorkerCounter.CountProducts(bill);
                            List<Apparel> list = Traverse.Create(comp).Field("repairing").GetValue<List<Apparel>>();
                            int index = list.IndexOf(apparel);
                            list.RemoveAt(index);
                            int without = recipe.WorkerCounter.CountProducts(bill);
                            list.Insert(index, apparel);
                            bool ok = counted == without + 1;
                            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {apparel.def.defName} being repaired for {comp.User?.LabelShort} at {bench.def.defName}: a bill that counts worn apparel sees {counted} (would see {without} if the repaired one were left out)");
                            sb.AppendLine(ok ? "PASS" : "FAIL: 1");
                            return;
                        }
                    }
                }
            }

            foreach (Map map in Find.Maps.Where(m => m.IsPlayerHome))
            {
                foreach (ThingDef benchDef in ApparelRepairUtility.BenchDefs)
                {
                    foreach (Thing bench in map.listerThings.ThingsOfDef(benchDef))
                    {
                        CompApparelRepair comp = bench.TryGetComp<CompApparelRepair>();
                        if (comp == null || comp.State != ApparelRepairState.Idle || !(bench is IBillGiver giver))
                        {
                            continue;
                        }

                        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                        {
                            foreach (Apparel apparel in pawn.apparel.WornApparel.ToList())
                            {
                                RecipeDef recipe = DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(r => r.products != null && r.products.Count == 1 && r.products[0].thingDef == apparel.def && r.specialProducts == null);
                                if (recipe == null)
                                {
                                    continue;
                                }

                                Bill_Production bill = new Bill_Production(recipe) { includeEquipped = true, includeTainted = true, hpRange = FloatRange.ZeroToOne, qualityRange = QualityRange.All };
                                bill.billStack = giver.BillStack;
                                int worn = recipe.WorkerCounter.CountProducts(bill);
                                Traverse fields = Traverse.Create(comp);
                                pawn.apparel.Remove(apparel);
                                comp.GetDirectlyHeldThings().TryAdd(apparel, false);
                                fields.Field("state").SetValue(ApparelRepairState.Working);
                                fields.Field("user").SetValue(pawn);
                                fields.Field("repairing").GetValue<List<Apparel>>().Add(apparel);
                                int repairing = recipe.WorkerCounter.CountProducts(bill);
                                fields.Field("repairing").GetValue<List<Apparel>>().Remove(apparel);
                                fields.Field("user").SetValue(null);
                                fields.Field("state").SetValue(ApparelRepairState.Idle);
                                comp.GetDirectlyHeldThings().Remove(apparel);
                                pawn.apparel.Wear(apparel, false);
                                bool ok = worn > 0 && repairing == worn;
                                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} bill counting worn {apparel.def.defName}: {worn} while {pawn.LabelShort} wears it, {repairing} while it is in {bench.def.defName} for repair");
                                sb.AppendLine(ok ? "PASS" : "FAIL: 1");
                                return;
                            }
                        }
                    }
                }
            }

            sb.AppendLine("skipped: no idle repair bench with a colonist wearing craftable apparel");
        }

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
                .OrderByDescending(p => MultiHaulUtility.FreeMass(p)).FirstOrDefault();
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

            List<Pawn> held = HoldOthers(map, pawn);
            List<Thing> rifles = new List<Thing>();
            for (int i = 0; i < Rifles; i++)
            {
                Thing rifle = ThingMaker.MakeThing(rifleDef);
                rifle.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, null);
                rifles.Add(GenSpawn.Spawn(rifle, new IntVec3(corner.x + i, 0, corner.z + 3), map));
                rifle.SetForbidden(false, false);
            }

            float free = MultiHaulUtility.FreeMass(pawn);
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
                foreach (Thing r in rifles.Where(r => !(r.Spawned && zone.ContainsCell(r.Position))))
                {
                    sb.AppendLine($"info: not stored: {r} spawned {r.Spawned} destroyed {r.Destroyed} at {r.PositionHeld} holder {r.ParentHolder} forbidden {(r.Spawned ? r.IsForbidden(pawn) : false)} hp {r.HitPoints}/{r.MaxHitPoints} reserved by {(r.Spawned ? map.reservationManager.FirstRespectedReserver(r, pawn)?.LabelShort : null) ?? "nobody"} haulable {(r.Spawned && map.listerHaulables.ThingsPotentiallyNeedingHauling().Contains(r))}, corner {corner}, pawn at {pawn.Position} doing {pawn.CurJobDef?.defName}, same room as pawn start {(r.Spawned && r.GetRoom() == new IntVec3(corner.x, 0, corner.z + 2).GetRoom(map))}, zone cells free {zone.Cells.Count(c => c.GetFirstItem(map) == null)}");
                }
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
            ReleaseOthers(held);

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            foreach (IGrouping<string, string> group in errors.GroupBy(m => m.Split('\n')[0]).Take(10))
            {
                sb.AppendLine($"error x{group.Count()}: {group.Key}");
            }
            bad += errors.Count;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 수송 포드 하나와 서로 다른 물건 네 더미를 놓고, 정착민 한 명에게 적재를 시켜 게임 틱으로 끝까지 돌려 본다.
        private static void MultiLoad(StringBuilder sb)
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

            Check(JobDefOf.HaulToTransporter.driverClass == typeof(JobDriver_LoadBatchTransporter), $"transporter loading driver: {JobDefOf.HaulToTransporter.driverClass}");
            Check(JobDefOf.HaulToPortal == null || JobDefOf.HaulToPortal.driverClass == typeof(JobDriver_LoadBatchPortal), $"portal loading driver: {JobDefOf.HaulToPortal?.driverClass}");
            Map map = Find.AnyPlayerHomeMap;
            List<Pawn> pawns = map?.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && p.Awake() && !p.InMentalState && MultiHaulUtility.CanBatch(p)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                .OrderByDescending(p => MultiHaulUtility.FreeMass(p)).ToList();
            Pawn pawn = pawns?.FirstOrDefault();
            sb.AppendLine("free carry mass (vanilla -> batch): " + string.Join(", ", pawns.Take(8).Select(p => $"{p.LabelShort} {MassUtility.FreeSpace(p):F1}->{MultiHaulUtility.FreeMass(p):F1}")));
            if (pawn == null)
            {
                sb.AppendLine("skipped run: no free colonist");
                sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
                return;
            }

            bool GoodCorner(IntVec3 c)
            {
                Room room = c.GetRoom(map);
                if (room == null || !pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                {
                    return false;
                }
                foreach (IntVec3 cell in new CellRect(c.x, c.z, 5, 5))
                {
                    if (!cell.InBounds(map) || !cell.Standable(map) || cell.Fogged(map) || cell.GetFirstItem(map) != null || cell.GetEdifice(map) != null
                        || map.haulDestinationManager.SlotGroupAt(cell) != null || cell.GetRoom(map) != room)
                    {
                        return false;
                    }
                }
                return true;
            }
            if (!CellFinder.TryFindRandomCellNear(pawn.Position, map, 40, GoodCorner, out IntVec3 corner))
            {
                sb.AppendLine("skipped run: no free 5x5 area near the colonist");
                sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
                return;
            }

            int errorsBefore = TestRunner.TickErrors.Count;
            List<Pawn> held = HoldOthers(map, pawn);
            Thing pod = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.TransportPod), new IntVec3(corner.x + 2, 0, corner.z), map);
            pod.SetFaction(Faction.OfPlayer);
            CompTransporter comp = pod.TryGetComp<CompTransporter>();
            comp.groupID = TransporterUtility.InitiateLoading(new[] { comp });
            (ThingDef def, int count)[] loads = { (ThingDefOf.Steel, 10), (ThingDefOf.WoodLog, 10), (ThingDefOf.Silver, 100), (ThingDefOf.ComponentIndustrial, 3) };
            List<Thing> items = new List<Thing>();
            float extraMass = 0f;
            for (int i = 0; i < loads.Length; i++)
            {
                Thing item = ThingMaker.MakeThing(loads[i].def);
                item.stackCount = loads[i].count;
                items.Add(GenSpawn.Spawn(item, new IntVec3(corner.x + i, 0, corner.z + 4), map));
                item.SetForbidden(false, false);
                TransferableOneWay transferable = new TransferableOneWay();
                transferable.things.Add(item);
                comp.AddToTheToLoadList(transferable, item.stackCount);
                extraMass += i == 0 ? 0f : item.stackCount * item.GetStatValue(StatDefOf.Mass);
            }

            pawn.jobs.StopAll();
            pawn.Position = new IntVec3(corner.x, 0, corner.z + 3);
            pawn.Notify_Teleported();
            float free = MultiHaulUtility.FreeMass(pawn);
            sb.AppendLine($"{pawn.LabelShort}: free carry mass {free:F1} kg, the three extra stacks weigh {extraMass:F1} kg");
            Check(LoadTransportersJobUtility.HasJobOnTransporter(pawn, comp), "colonist has a loading job on the pod");
            pawn.jobs.StartJob(LoadTransportersJobUtility.JobOnTransporter(pawn, comp), JobCondition.InterruptForced);
            LoadBatch batch = (pawn.jobs.curDriver as ILoadBatchDriver)?.Batch;
            Check(batch != null, $"loading runs on the batch driver: {pawn.jobs.curDriver?.GetType().Name ?? "none"}");

            int peak = 0, ticks = 0, jobs = 1;
            bool othersChecked = false;
            Job last = pawn.CurJob;
            for (; ticks < 3000 && comp.AnythingLeftToLoad; ticks++)
            {
                Find.TickManager.DoSingleTick();
                if (pawn.CurJob != last)
                {
                    last = pawn.CurJob;
                    jobs += last != null && last.def == JobDefOf.HaulToTransporter ? 1 : 0;
                }
                if (batch != null)
                {
                    peak = Mathf.Max(peak, batch.TakenCount);
                    if (!othersChecked && batch.TakenCount > 0 && batch.Running && pawns.Count > 1 && free > extraMass + 1f)
                    {
                        othersChecked = true;
                        ThingCount other = LoadTransportersJobUtility.FindThingToLoad(pawns[1], comp);
                        Check(other.Thing == null, $"another colonist finds nothing left to fetch while the first carries it all: {other.Thing?.LabelCap ?? "nothing"}");
                    }
                }
            }
            ThingOwner loaded = comp.GetDirectlyHeldThings();
            bool allIn = loads.All(l => loaded.Where(t => t.def == l.def).Sum(t => t.stackCount) == l.count);
            Check(!comp.AnythingLeftToLoad && allIn, $"everything loaded after {ticks} ticks in {jobs} loading job(s): {string.Join(", ", loaded.Select(t => t.LabelCap))}");
            Check(free <= extraMass + 1f || (peak == 3 && jobs == 1), $"stacks carried in the inventory at once: {peak}");
            Check(!loads.Any(l => pawn.inventory.innerContainer.Any(t => t.def == l.def && items.Contains(t))) && !pawn.inventory.UnloadEverything, "nothing left in the inventory");

            pawn.jobs.StopAll();
            foreach (Thing item in items)
            {
                if (pawn.inventory.innerContainer.Contains(item))
                {
                    pawn.inventory.innerContainer.Remove(item);
                }
                if (!item.Destroyed)
                {
                    item.Destroy();
                }
            }
            loaded.ClearAndDestroyContents();
            comp.CancelLoad(map);
            ReleaseOthers(held);
            if (!pod.Destroyed)
            {
                pod.Destroy();
            }

            ShuttleManyStacks(sb, map, pawn, corner, Check);
            SavedShuttleTrip(sb, map, pawns);

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            foreach (IGrouping<string, string> group in errors.GroupBy(m => m.Split('\n')[0]).Take(10))
            {
                sb.AppendLine($"error x{group.Count()}: {group.Key}");
            }
            bad += errors.Count;
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
        }

        // 세이브에 이미 적재 중인 운반선이 있으면, 정착민 한 명에게 한 번 나르게 해서 한 번에 몇 개를 싣는지 본다.
        private static void SavedShuttleTrip(StringBuilder sb, Map map, List<Pawn> pawns)
        {
            CompTransporter comp = map.listerThings.AllThings.Select(t => t.TryGetComp<CompTransporter>()).FirstOrDefault(c => c != null && c.AnythingLeftToLoad && c.leftToLoad.Any(tr => tr.CountToTransfer > 0 && tr.AnyThing?.def.category == ThingCategory.Item));
            if (comp == null)
            {
                sb.AppendLine("info: no transporter is loading items in the save");
                return;
            }

            foreach (TransferableOneWay tr in comp.leftToLoad.Where(tr => tr.CountToTransfer > 0))
            {
                sb.AppendLine($"info: saved {comp.parent.def.defName} at {comp.parent.Position} wants {tr.CountToTransfer} {tr.ThingDef?.defName}: {tr.things.Count} things listed, {tr.things.Count(t => t.Spawned && t.Map == map)} spawned here, {tr.things.Count(t => t.Destroyed)} destroyed");
            }

            foreach (Pawn pawn in pawns.Take(3))
            {
                ThingCount first = LoadTransportersJobUtility.FindThingToLoad(pawn, comp);
                if (first.Thing == null || !LoadTransportersJobUtility.HasJobOnTransporter(pawn, comp))
                {
                    sb.AppendLine($"info: {pawn.LabelShort} has no loading job on it (first pick {first.Thing?.LabelCap ?? "none"})");
                    continue;
                }

                List<Pawn> held = HoldOthers(map, pawn);
                ThingDef def = first.Thing.def;
                int before = comp.innerContainer.Where(t => t.def == def).Sum(t => t.stackCount);
                pawn.jobs.StopAll();
                Job job = LoadTransportersJobUtility.JobOnTransporter(pawn, comp);
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                LoadBatch batch = (pawn.jobs.curDriver as ILoadBatchDriver)?.Batch;
                int peak = 0, ticks = 0;
                for (; ticks < 6000 && pawn.CurJob == job; ticks++)
                {
                    Find.TickManager.DoSingleTick();
                    peak = Mathf.Max(peak, pawn.inventory.innerContainer.Where(t => t.def == def).Sum(t => t.stackCount));
                }
                int after = comp.innerContainer.Where(t => t.def == def).Sum(t => t.stackCount);
                sb.AppendLine($"info: {pawn.LabelShort} (free {MultiHaulUtility.FreeMass(pawn):F1} kg, vanilla {MassUtility.FreeSpace(pawn):F1}) first picks {first.Count} {def.defName} at {first.Thing.Position}; one trip loaded {after - before} in {ticks} ticks, up to {peak} in the inventory, batch {(batch != null)}");
                pawn.jobs.StopAll();
                ReleaseOthers(held);
            }
        }

        // 실제 상황처럼 여객 왕복선에 같은 물건 여러 묶음(플라스틸 75개 x 6)을 실어 본다.
        private static void ShuttleManyStacks(StringBuilder sb, Map map, Pawn pawn, IntVec3 corner, Action<bool, string> Check)
        {
            ThingDef shuttleDef = ModsConfig.OdysseyActive ? ThingDefOf.PassengerShuttle : null;
            if (shuttleDef == null || !pawn.Spawned)
            {
                sb.AppendLine("skipped shuttle run: Odyssey missing");
                return;
            }

            Thing shuttle = GenSpawn.Spawn(ThingMaker.MakeThing(shuttleDef), new IntVec3(corner.x + 2, 0, corner.z + 1), map);
            shuttle.SetFaction(Faction.OfPlayer);
            CompTransporter comp = shuttle.TryGetComp<CompTransporter>();
            comp.groupID = TransporterUtility.InitiateLoading(new[] { comp });
            List<Thing> stacks = new List<Thing>();
            TransferableOneWay transferable = new TransferableOneWay();
            for (int i = 0; i < 6; i++)
            {
                Thing item = ThingMaker.MakeThing(ThingDefOf.Plasteel);
                item.stackCount = 75;
                IntVec3 cell = new IntVec3(corner.x + i % 5, 0, corner.z + 4 - i / 5);
                stacks.Add(GenSpawn.Spawn(item, cell, map));
                item.SetForbidden(false, false);
                transferable.things.Add(item);
            }
            comp.AddToTheToLoadList(transferable, 450);

            List<Pawn> held = HoldOthers(map, pawn);
            pawn.jobs.StopAll();
            pawn.Position = new IntVec3(corner.x, 0, corner.z + 3);
            pawn.Notify_Teleported();
            int peakInventory = 0, jobs = 0, ticks = 0;
            Job last = null;
            for (; ticks < 4000 && comp.AnythingLeftToLoad; ticks++)
            {
                if (pawn.CurJob == null || pawn.CurJob.def != JobDefOf.HaulToTransporter)
                {
                    if (LoadTransportersJobUtility.HasJobOnTransporter(pawn, comp))
                    {
                        pawn.jobs.StartJob(LoadTransportersJobUtility.JobOnTransporter(pawn, comp), JobCondition.InterruptForced);
                    }
                }
                if (pawn.CurJob != last)
                {
                    last = pawn.CurJob;
                    jobs += last != null && last.def == JobDefOf.HaulToTransporter ? 1 : 0;
                }
                Find.TickManager.DoSingleTick();
                peakInventory = Mathf.Max(peakInventory, pawn.inventory.innerContainer.Where(t => t.def == ThingDefOf.Plasteel).Sum(t => t.stackCount));
            }
            int inShuttle = comp.innerContainer.Where(t => t.def == ThingDefOf.Plasteel).Sum(t => t.stackCount);
            sb.AppendLine($"shuttle: driver {JobDefOf.HaulToTransporter.driverClass.Name}, free {MultiHaulUtility.FreeMass(pawn):F1} kg");
            Check(inShuttle == 450 && peakInventory >= 150, $"passenger shuttle, 6 stacks of 75 plasteel: {inShuttle} loaded in {jobs} job(s) over {ticks} ticks, up to {peakInventory} carried in the inventory at once");

            pawn.jobs.StopAll();
            foreach (Thing t in pawn.inventory.innerContainer.Where(t => t.def == ThingDefOf.Plasteel).ToList())
            {
                pawn.inventory.innerContainer.Remove(t);
                t.Destroy();
            }
            foreach (Thing t in stacks.Where(t => !t.Destroyed))
            {
                t.Destroy();
            }
            comp.innerContainer.ClearAndDestroyContents();
            comp.CancelLoad(map);
            ReleaseOthers(held);
            if (!shuttle.Destroyed)
            {
                shuttle.Destroy();
            }
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

        private static void GravshipNoRange(StringBuilder sb)
        {
            if (!global::Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.GravshipNoRange) || !ModsConfig.OdysseyActive)
            {
                sb.AppendLine("skipped: feature not applied or Odyssey missing");
                return;
            }

            Map map = Find.AnyPlayerHomeMap;
            float link = DefDatabase<ThingDef>.GetNamed("GravFieldExtender").GetCompProperties<CompProperties_GravshipFacility>().maxDistance;
            bool anywhere = map.AllCells.Where((c, i) => i % 997 == 0).All(c => GravshipUtility.InsideFootprint(c, map));
            bool ok = link >= GravshipNoRangeUtility.LinkDistance && anywhere;
            sb.AppendLine($"{(ok ? "ok  " : "FAIL")} extender link distance {link}, every sampled cell counts as inside the footprint: {anywhere}");
            foreach (Building_GravEngine engine in map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).OfType<Building_GravEngine>())
            {
                sb.AppendLine($"info: engine at {engine.Position}: valid substructure {engine.ValidSubstructure.Count} of {engine.AllConnectedSubstructure.Count} connected, support {engine.GetStatValue(StatDefOf.SubstructureSupport)}");
            }

            sb.AppendLine(ok ? "PASS" : "FAIL: 1");
        }

        private static void OrderToolsProbe(StringBuilder sb)
        {
            if (!OrderToolsUtility.Active)
            {
                sb.AppendLine(ModsConfig.IsActive(OrderToolsUtility.AllowToolPackageId) ? "skipped: AllowTool is active (run with -DropMods unlimitedhugs.allowtool)" : "skipped: feature not applied");
                return;
            }

            int bad = 0;
            void Check(bool ok, string text)
            {
                bad += ok ? 0 : 1;
                sb.AppendLine($"{(ok ? "ok  " : "FAIL")} {text}");
            }

            Map map = Find.AnyPlayerHomeMap;
            int errorsBefore = TestRunner.TickErrors.Count;
            List<Designator> orders = DefDatabase<DesignationCategoryDef>.GetNamed("Orders").AllResolvedDesignators.ToList();
            Check(orders.Any(d => d is Designator_HaulUrgently) && orders.Any(d => d is Designator_FinishOff) && orders.Any(d => d is Designator_SelectSimilar) && orders.Any(d => d is Designator_StripMine),
                "orders menu has haul urgently, finish off, select similar and strip mine");
            Check(new Designator_Forbid().icon?.name == "forbid" && new Designator_Unforbid().icon?.name == "allow", "forbid and allow tools use the replaced icons");
            List<Designator> reverse = Find.ReverseDesignatorDatabase.AllDesignators;
            Check(reverse.Any(d => d is Designator_HaulUrgently) && reverse.Any(d => d is Designator_FinishOff) && reverse.Any(d => d is Designator_SelectSimilarReverse), "selected-thing buttons include the three tools");

            int kept = Find.Maps.Sum(m => m.designationManager.SpawnedDesignationsOfDef(OrderToolsUtility.HaulUrgently).Count());
            sb.AppendLine($"info: urgent-haul designations carried over from the save: {kept}");
            Pawn worker = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.Drafted && p.Awake() && !p.InMentalState && !p.WorkTagIsDisabled(WorkTags.Violent)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
            if (worker == null)
            {
                sb.AppendLine("skipped the rest: no free colonist");
                return;
            }

            WorkTypeDef haulPlus = DefDatabase<WorkTypeDef>.GetNamed("HaulingUrgent");
            sb.AppendLine($"info: {worker.LabelShort} work priorities: " + string.Join(", ", DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(d => d.index >= DefDatabase<WorkTypeDef>.DefCount - 6)
                .Select(d => $"{d.defName}={worker.workSettings.GetPriority(d)}")));
            List<string> fakeMods = LoadedModManager.RunningModsListForReading.Select(m => m.PackageId).ToList();
            fakeMods.Insert(Math.Min(3, fakeMods.Count), OrderToolsUtility.AllowToolPackageId);
            List<WorkTypeDef> order = WorkOrderMigration.OldOrder(fakeMods);
            DefMap<WorkTypeDef, int> fake = new DefMap<WorkTypeDef, int>();
            for (int i = 0; i < order.Count; i++)
            {
                fake[DefDatabase<WorkTypeDef>.AllDefsListForReading[i]] = i;
            }

            WorkOrderMigration.Remap(fake, order);
            bool remapped = true;
            for (int i = 0; i < order.Count; i++)
            {
                remapped &= fake[order[i]] == i;
            }

            int haulOld = order.IndexOf(haulPlus);
            Check(remapped && haulOld < haulPlus.index, $"work priorities saved with AllowTool move to the new order (Haul+ was slot {haulOld}, is now {haulPlus.index} of {order.Count})");
            Check(WorkOrderMigration.OldOrder(LoadedModManager.RunningModsListForReading.Select(m => m.PackageId).ToList()) == null, "a save made without AllowTool is left alone");

            IntVec3 spot = CellFinder.RandomClosewalkCellNear(worker.Position, map, 8, c => c.Standable(map) && c.GetFirstItem(map) == null && !c.Fogged(map) && map.haulDestinationManager.SlotGroupAt(c) == null);
            Thing steel = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), spot, map);
            steel.stackCount = 5;
            steel.SetForbidden(true, false);
            Designator_HaulUrgently haul = new Designator_HaulUrgently();
            bool can = haul.CanDesignateThing(steel).Accepted;
            haul.DesignateThing(steel);
            List<Thing> urgent = GameComponent_OrderTools.Instance.UrgentHaulables(map);
            Check(can && OrderToolsUtility.Has(steel, OrderToolsUtility.HaulUrgently) && !steel.IsForbidden(Faction.OfPlayer) && urgent.Contains(steel), "haul urgently designates, unforbids and lists the thing");
            WorkGiver_Scanner hauler = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("HaulUrgently").Worker;
            Job haulJob = hauler.ShouldSkip(worker) ? null : hauler.JobOnThing(worker, steel);
            sb.AppendLine($"info: urgent haul job for {worker.LabelShort}: {haulJob?.def.defName ?? "none (no storage space for steel?)"}");
            map.designationManager.TryRemoveDesignationOn(steel, OrderToolsUtility.HaulUrgently);
            GameComponent_OrderTools.Instance.ForgetUrgent(map);

            Thing steel2 = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), spot + new IntVec3(1, 0, 0), map, WipeMode.VanishOrMoveAside);
            try
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(steel, false, false);
                Designator_SelectSimilar similar = new Designator_SelectSimilar();
                similar.Reindex();
                similar.DesignateMultiCell(CellRect.CenteredOn(steel.Position, 3).Cells.Where(c => c.InBounds(map)));
                List<object> picked = Find.Selector.SelectedObjectsListForReading;
                Check(picked.Contains(steel2) && picked.All(o => o is Thing t && t.def == ThingDefOf.Steel), $"select similar picks only steel in the dragged area ({picked.Count} selected)");
                Find.Selector.ClearSelection();
            }
            catch (Exception e)
            {
                Check(false, "select similar threw: " + e.Message);
            }

            steel.Destroy();
            if (!steel2.Destroyed)
            {
                steel2.Destroy();
            }

            IntVec3 rock = map.AllCells.FirstOrDefault(c => !c.Fogged(map) && c.GetFirstMineable(map) != null && CellRect.CenteredOn(c, 3).Cells.All(x => x.InBounds(map) && x.GetFirstMineable(map) != null
                && map.designationManager.DesignationAt(x, DesignationDefOf.Mine) == null));
            if (rock.IsValid && rock != default)
            {
                GameComponent_OrderTools.Instance.stripSpacingX = 2;
                GameComponent_OrderTools.Instance.stripSpacingZ = 2;
                CellRect area = CellRect.CenteredOn(rock, 3);
                new Designator_StripMine().DesignateMultiCell(area.Cells);
                int marked = area.Cells.Count(c => map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null);
                int expected = area.Cells.Count(c => (c.x - area.minX) % 3 == 0 || (c.z - area.minZ) % 3 == 0);
                Check(marked == expected && marked < area.Area, $"strip mine marks the grid lines only: {marked} of {area.Area} cells (expected {expected})");
                foreach (IntVec3 c in area.Cells)
                {
                    map.designationManager.TryRemoveDesignation(c, DesignationDefOf.Mine);
                }

                GameComponent_OrderTools.Instance.stripSpacingX = 5;
                GameComponent_OrderTools.Instance.stripSpacingZ = 5;
            }
            else
            {
                sb.AppendLine("info: no solid 7x7 rock area, strip mine check skipped");
            }

            TestRunner.ticking = true;
            List<Pawn> held = HoldOthers(map, worker);
            Pawn animal = PawnGenerator.GeneratePawn(PawnKindDefOf.Thrumbo, null);
            GenSpawn.Spawn(animal, CellFinder.RandomClosewalkCellNear(worker.Position, map, 6), map);
            HealthUtility.DamageUntilDowned(animal);
            if (animal.Dead || !animal.Downed)
            {
                sb.AppendLine("info: test animal did not end up downed, finish off check skipped");
            }
            else
            {
                Designator_FinishOff finish = new Designator_FinishOff();
                Check(finish.CanDesignateThing(animal).Accepted, "a downed wild animal can be marked");
                finish.DesignateThing(animal);
                Job job = WorkGiver_FinishOff.Create().JobOnThing(worker, animal);
                Check(job != null && job.def == OrderToolsUtility.FinishOffJob, "a colonist gets the finish off job");
                if (job != null)
                {
                    worker.jobs.StopAll();
                    worker.jobs.StartJob(job, JobCondition.InterruptForced);
                    int ticks = 0;
                    for (; ticks < 1500 && !animal.Dead; ticks++)
                    {
                        Find.TickManager.DoSingleTick();
                    }

                    Check(animal.Dead && animal.Corpse != null && !animal.Corpse.IsForbidden(Faction.OfPlayer), $"the animal is dead after {ticks} ticks and its corpse is allowed");
                }
            }

            if (!animal.Dead && !animal.Destroyed)
            {
                animal.Destroy();
            }
            else if (animal.Corpse != null && !animal.Corpse.Destroyed)
            {
                animal.Corpse.Destroy();
            }

            worker.jobs.StopAll();
            worker.drafter.Drafted = true;
            GameComponent_OrderTools settings = GameComponent_OrderTools.Instance;
            bool gizmo = worker.GetGizmos().Any(g => g is Command_PartyHunt);
            settings.SetPartyHunting(worker, true);
            bool on = settings.IsPartyHunting(worker);
            worker.drafter.Drafted = false;
            Check(gizmo && on && !settings.IsPartyHunting(worker), "drafted hunt button shows while drafted and turns off on undraft");
            ReleaseOthers(held);
            TestRunner.ticking = false;

            List<string> errors = TestRunner.TickErrors.Skip(errorsBefore).ToList();
            Check(errors.Count == 0, $"errors: {errors.Count}{(errors.Count > 0 ? " - " + string.Join(" | ", errors.Take(3).Select(e => e.Split('\n')[0])) : "")}");
            sb.AppendLine(bad == 0 ? "PASS" : $"FAIL: {bad}");
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

            foreach (Pawn pawn in Colonists)
            {
                HealthSummary health = HealthSummary.Build(pawn);
                if (health.Any)
                {
                    sb.AppendLine($"health {pawn.LabelShort}: " + string.Join(" | ", Enum.GetValues(typeof(HealthColumn)).Cast<HealthColumn>()
                        .Where(c => health[c].Count > 0).Select(c => $"{c}: {string.Join(", ", health[c].Select(l => l.text))}")));
                }
            }

            Pawn patient = Colonists.FirstOrDefault(p => !p.health.hediffSet.HasHediff(DefDatabase<HediffDef>.GetNamed("Flu")));
            if (patient != null)
            {
                Hediff flu = patient.health.AddHediff(DefDatabase<HediffDef>.GetNamed("Flu"));
                flu.Severity = 0.4f;
                BodyPartRecord lung = patient.health.hediffSet.GetNotMissingParts().FirstOrDefault(x => x.def == BodyPartDefOf.Lung);
                Hediff cancer = lung == null ? null : patient.health.AddHediff(HediffDefOf.Carcinoma, lung);
                HealthSummary sick = HealthSummary.Build(patient);
                bool fluShown = sick[HealthColumn.Illness].Any(l => l.text.Contains(flu.LabelCap) && l.text.Contains("40%"));
                bool cancerShown = cancer == null || sick[HealthColumn.Illness].Any(l => l.text.Contains(lung.Label));
                sb.AppendLine($"{(fluShown && cancerShown ? "ok  " : "FAIL")} health overview lists flu progress with immunity and carcinoma with its part: {string.Join(", ", sick[HealthColumn.Illness].Select(l => l.text))}");
                patient.health.RemoveHediff(flu);
                if (cancer != null)
                {
                    patient.health.RemoveHediff(cancer);
                }
            }

            int tabBefore = plans.openTab;
            plans.openTab = 1;
            sb.AppendLine($"{(plans.openTab == 1 ? "ok  " : "FAIL")} the open tab is kept in the saved surgery plans");
            plans.openTab = tabBefore;
        }

        // 살아 있는 세이브에서는 다른 정착민이 시험용 물건을 먼저 집어 갈 수 있다. 시험하는 동안 소집해서 일을 못 하게 한다.
        private static List<Pawn> HoldOthers(Map map, Pawn tested)
        {
            List<Pawn> held = map.mapPawns.FreeColonistsSpawned.Where(p => p != tested && !p.Drafted && !p.Downed && p.drafter != null && p.Awake() && !p.InMentalState).ToList();
            foreach (Pawn pawn in held)
            {
                pawn.drafter.Drafted = true;
            }

            return held;
        }

        private static void ReleaseOthers(List<Pawn> held)
        {
            foreach (Pawn pawn in held)
            {
                if (pawn.drafter != null && !pawn.Dead)
                {
                    pawn.drafter.Drafted = false;
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
