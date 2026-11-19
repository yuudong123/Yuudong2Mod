using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace Yuudong2Mod
{
    public static class EnemyBaseUtility
    {
        // 바닐라 정착지 방어 병력(1150~1600)의 가운데 값. 이 값일 때 기지 크기가 바닐라와 같다.
        private const float VanillaPoints = 1400f;
        private const float MaxSizeFactor = 2.2f;

        // 지금 만드는 정착지 맵이 조정 대상일 때만 값이 있다.
        public static float? activePoints;

        // 다른 세력의 정착지 맵만 조정한다. 퀘스트 현장이나 고대 중력선처럼 같은 생성 단계를 빌려 쓰는 곳은 건드리지 않는다.
        public static bool Eligible(GenStep_Settlement step, Map map)
        {
            return map.Parent is Settlement
                && map.ParentFaction != null
                && map.ParentFaction != Faction.OfPlayer
                && step.generatePawns
                && step.overrideFaction == null
                && step.requiredGravcoreRooms == 0
                && step.postProcessSettlementParams == null;
        }

        private static IIncidentTarget RichestPlayerTarget()
        {
            Map best = null;
            float bestWealth = -1f;
            foreach (Map map in Find.Maps)
            {
                if (map.IsPlayerHome && map.PlayerWealthForStoryteller > bestWealth)
                {
                    best = map;
                    bestWealth = map.PlayerWealthForStoryteller;
                }
            }

            return best ?? (IIncidentTarget)Find.World;
        }

        // 플레이어 정착지에 오는 습격과 같은 공식. 난이도의 위협 배율, 적응도, 경과 일수가 모두 들어간다.
        public static float PlayerThreatPoints()
        {
            return StorytellerUtility.DefaultThreatPointsNow(RichestPlayerTarget());
        }

        public static float PlayerWealth()
        {
            return RichestPlayerTarget().PlayerWealthForStoryteller;
        }

        public static int ScaleSize(int size)
        {
            if (activePoints == null)
            {
                return size;
            }

            float factor = Mathf.Clamp(Mathf.Sqrt(activePoints.Value / VanillaPoints), 1f, MaxSizeFactor);
            return Mathf.RoundToInt(size * factor);
        }
    }

    // 적 기지 강화: 다른 세력 정착지의 방어 병력을 플레이어의 습격 포인트에 맞추고, 그만큼 기지도 넓힌다.
    [HarmonyPatchCategory(Features.EnemyBaseScaling)]
    [HarmonyPatch(typeof(GenStep_Settlement), "ScatterAt")]
    internal static class GenStep_Settlement_ScatterAt_EnemyBaseScaling_Patch
    {
        private static void Prefix(GenStep_Settlement __instance, Map map)
        {
            EnemyBaseUtility.activePoints = EnemyBaseUtility.Eligible(__instance, map) ? EnemyBaseUtility.PlayerThreatPoints() : (float?)null;
        }

        private static void Finalizer()
        {
            EnemyBaseUtility.activePoints = null;
        }

        // 정착지 한 변 길이를 뽑는 두 곳 뒤에 크기 조정을 끼운다.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo randomInRange = AccessTools.PropertyGetter(typeof(IntRange), nameof(IntRange.RandomInRange));
            MethodInfo scale = AccessTools.Method(typeof(EnemyBaseUtility), nameof(EnemyBaseUtility.ScaleSize));
            int patched = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (instruction.Calls(randomInRange))
                {
                    patched++;
                    yield return new CodeInstruction(OpCodes.Call, scale);
                }
            }

            if (patched != 2)
            {
                Log.Error($"[Yuudong2Mod] EnemyBaseScaling: expected 2 settlement size rolls in GenStep_Settlement.ScatterAt, found {patched}. Check the vanilla method and update the transpiler.");
            }
        }
    }

    [HarmonyPatchCategory(Features.EnemyBaseScaling)]
    [HarmonyPatch(typeof(SymbolResolver_Settlement), nameof(SymbolResolver_Settlement.Resolve))]
    internal static class SymbolResolver_Settlement_Resolve_EnemyBaseScaling_Patch
    {
        private static void Prefix(ref ResolveParams rp)
        {
            if (EnemyBaseUtility.activePoints != null && rp.settlementPawnGroupPoints == null)
            {
                rp.settlementPawnGroupPoints = EnemyBaseUtility.activePoints;
            }
        }
    }

    // 남이 만든 기지: Real Ruins가 켜져 있으면 다른 세력 정착지를 Real Ruins가 받아 둔 다른 플레이어의 기지로 짓는다.
    // 쓸 만한 기지를 못 찾거나 Real Ruins 쪽 호출이 실패하면 바닐라 생성으로 넘어간다.
    [HarmonyPatchCategory(Features.RealRuinsBases)]
    [HarmonyPatch(typeof(GenStep_Settlement), "ScatterAt")]
    internal static class GenStep_Settlement_ScatterAt_RealRuinsBases_Patch
    {
        private const int MaxTurretOperators = 12;

        private static bool Prefix(GenStep_Settlement __instance, IntVec3 c, Map map)
        {
            if (!RealRuinsBridge.Available || !EnemyBaseUtility.Eligible(__instance, map))
            {
                return true;
            }

            Faction faction = map.ParentFaction;
            if (!RealRuinsBridge.TryPlaceBase(map, c, faction, EnemyBaseUtility.PlayerWealth(), out CellRect rect))
            {
                return true;
            }

            MapGenerator.SetVar(GenStep_Settlement.RectVarName, rect);
            SpawnDefenders(map, rect, faction);
            return false;
        }

        private static void SpawnDefenders(Map map, CellRect rect, Faction faction)
        {
            float points = Yuudong2Mod.AppliedFeatures.Contains(Features.EnemyBaseScaling)
                ? EnemyBaseUtility.PlayerThreatPoints()
                : SymbolResolver_Settlement.DefaultPawnsPoints.RandomInRange;

            BaseGen.globalSettings.map = map;
            TraverseParms traverseParms = TraverseParms.For(TraverseMode.PassDoors);
            ResolveParams group = new ResolveParams
            {
                rect = rect,
                faction = faction,
                singlePawnLord = LordMaker.MakeNewLord(faction, new LordJob_DefendBase(faction, rect.CenterCell, 25000, false), map),
                pawnGroupKindDef = PawnGroupKindDefOf.Settlement,
                singlePawnSpawnCellExtraPredicate = x => map.reachability.CanReachMapEdge(x, traverseParms),
                pawnGroupMakerParams = new PawnGroupMakerParms
                {
                    tile = map.Tile,
                    faction = faction,
                    points = points,
                    inhabitants = true,
                },
            };
            BaseGen.symbolStack.Push("pawnGroup", group);

            // 박격포처럼 사람이 잡아야 하는 포탑에는 운용 인원을 따로 붙인다.
            int mannable = 0;
            foreach (IntVec3 cell in rect)
            {
                foreach (Thing thing in cell.GetThingList(map))
                {
                    if (thing.Faction == faction && thing.Position == cell && thing.TryGetComp<CompMannable>() != null)
                    {
                        mannable++;
                    }
                }
            }

            if (mannable > 0)
            {
                Lord turretLord = LordMaker.MakeNewLord(faction, new LordJob_ManTurrets(), map);
                for (int i = 0; i < Mathf.Min(mannable, MaxTurretOperators); i++)
                {
                    ResolveParams operatorParams = group;
                    operatorParams.pawnGroupMakerParams = null;
                    operatorParams.singlePawnLord = turretLord;
                    operatorParams.singlePawnGenerationRequest = new PawnGenerationRequest(faction.RandomPawnKind(), faction, PawnGenerationContext.NonPlayer, map.Tile, mustBeCapableOfViolence: true);
                    BaseGen.symbolStack.Push("pawn", operatorParams);
                }
            }

            BaseGen.Generate();
        }
    }

    // Real Ruins의 내부 형식을 리플렉션으로 부른다. Real Ruins는 선택 모드라 컴파일할 때 참조하지 않는다.
    public static class RealRuinsBridge
    {
        public const string PackageId = "woolstrand.realruins";

        // 정착지 하나를 지을 때 끝까지 불러와 가치를 재 보는 기지 수. 기지 전체에서 고르는 범위는 LargeBases가 정한다.
        private const int Candidates = 16;
        private const float WealthBandMin = 0.5f;
        private const float WealthBandMax = 3f;
        private const int EdgeMargin = 15;
        private const int MinArea = 900;
        private const float MinCost = 5000f;

        // Real Ruins의 POIType.MilitaryBaseLarge. 폐허(Ruins)만 아니면 온전한 기지로 찍힌다.
        private const int IntactPoiType = 7;

        // 마지막으로 지은 기지의 파일 이름과 자리. 검사와 문제 추적에 쓴다.
        public static string LastPlacedFile { get; private set; }
        public static CellRect LastPlacedRect { get; private set; }

        private static bool initialized;
        private static bool ready;

        private static MethodInfo getSnapshotsFolder;
        private static MethodInfo loadBlueprint;
        private static MethodInfo updateStats;
        private static PropertyInfo blueprintWidth;
        private static PropertyInfo blueprintHeight;
        private static PropertyInfo blueprintCost;
        private static MethodInfo optionsForPoi;
        private static Type poiType;
        private static FieldInfo overridePosition;
        private static FieldInfo maxTechLevel;
        private static FieldInfo techLimitStrict;
        private static MethodInfo pipelineFromModSettings;
        private static Type contextType;
        private static FieldInfo preloadedBlueprint;
        private static FieldInfo contextState;
        private static FieldInfo placementRect;
        private static FieldInfo uncoveredCost;
        private static MethodInfo scatter;
        private static MethodInfo removeUnsupportedRoofs;

        public static bool Available
        {
            get
            {
                if (!initialized)
                {
                    initialized = true;
                    ready = ModsConfig.IsActive(PackageId) && Bind();
                }

                return ready;
            }
        }

        // 하나라도 못 찾으면 무엇이 빠졌는지 한 번 알리고 기능을 끈다.
        private static bool Bind()
        {
            List<string> missing = new List<string>();
            Type Find(string name)
            {
                Type type = AccessTools.TypeByName("RealRuins." + name);
                if (type == null)
                {
                    missing.Add(name);
                }

                return type;
            }

            T Need<T>(T member, string name) where T : class
            {
                if (member == null)
                {
                    missing.Add(name);
                }

                return member;
            }

            Type store = Find("SnapshotStoreManager");
            Type loader = Find("BlueprintLoader");
            Type blueprint = Find("Blueprint");
            Type factory = Find("ScatterOptionsFactory");
            Type options = Find("ScatterOptions");
            Type pipeline = Find("PipelineSettings");
            Type state = Find("ScatterState");
            Type scatterer = Find("RuinsScatterer");
            Type roofs = Find("RoofSupportUtility");
            poiType = Find("POIType");
            contextType = Find("ScatterContext");
            if (missing.Count == 0)
            {
                getSnapshotsFolder = Need(AccessTools.Method(store, "GetSnapshotsFolderPath"), "SnapshotStoreManager.GetSnapshotsFolderPath");
                loadBlueprint = Need(AccessTools.Method(loader, "LoadWholeBlueprintAtPath", new[] { typeof(string) }), "BlueprintLoader.LoadWholeBlueprintAtPath");
                updateStats = Need(AccessTools.Method(blueprint, "UpdateBlueprintStats", new[] { typeof(bool) }), "Blueprint.UpdateBlueprintStats");
                blueprintWidth = Need(AccessTools.Property(blueprint, "width"), "Blueprint.width");
                blueprintHeight = Need(AccessTools.Property(blueprint, "height"), "Blueprint.height");
                blueprintCost = Need(AccessTools.Property(blueprint, "totalCost"), "Blueprint.totalCost");
                optionsForPoi = Need(AccessTools.Method(factory, "ForPOI", new[] { typeof(string), blueprint, poiType, typeof(bool), typeof(bool), typeof(bool) }), "ScatterOptionsFactory.ForPOI");
                overridePosition = Need(AccessTools.Field(options, "overridePosition"), "ScatterOptions.overridePosition");
                maxTechLevel = Need(AccessTools.Field(options, "maxTechLevel"), "ScatterOptions.maxTechLevel");
                techLimitStrict = Need(AccessTools.Field(options, "techLimitStrict"), "ScatterOptions.techLimitStrict");
                pipelineFromModSettings = Need(AccessTools.Method(pipeline, "FromModSettings"), "PipelineSettings.FromModSettings");
                preloadedBlueprint = Need(AccessTools.Field(contextType, "preloadedBlueprint"), "ScatterContext.preloadedBlueprint");
                contextState = Need(AccessTools.Field(contextType, "state"), "ScatterContext.state");
                placementRect = Need(AccessTools.Field(state, "placementRect"), "ScatterState.placementRect");
                uncoveredCost = Need(AccessTools.Field(state, "uncoveredCost"), "ScatterState.uncoveredCost");
                scatter = Need(AccessTools.Method(scatterer, "Scatter"), "RuinsScatterer.Scatter");
                removeUnsupportedRoofs = Need(AccessTools.Method(roofs, "RemoveUnsupportedRoofs", new[] { typeof(Map), typeof(CellRect) }), "RoofSupportUtility.RemoveUnsupportedRoofs");
            }

            if (missing.Count > 0)
            {
                Log.Error("[Yuudong2Mod] RealRuinsBases: this Real Ruins version lacks " + string.Join(", ", missing) + ". Enemy settlements use vanilla generation until RealRuinsBridge is updated for it.");
                return false;
            }

            return true;
        }

        public static string[] BlueprintFiles()
        {
            string folder = (string)getSnapshotsFolder.Invoke(null, null);
            if (folder.NullOrEmpty() || !Directory.Exists(folder))
            {
                return Array.Empty<string>();
            }

            string[] files = Directory.GetFiles(folder);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        private struct BaseInfo
        {
            public string file;
            public int width;
            public int height;
        }

        private static readonly Regex SnapshotTag = new Regex("<snapshot\\b[^>]*>");
        private static readonly Regex WidthAttribute = new Regex("\\bwidth=\"(\\d+)\"");
        private static readonly Regex HeightAttribute = new Regex("\\bheight=\"(\\d+)\"");

        // 기지 파일 머리말에서 크기만 읽는다. 전체를 불러오는 것보다 훨씬 싸서 받아 둔 기지를 모두 훑을 수 있다.
        private static bool TryReadSize(string file, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (FileStream stream = File.OpenRead(file))
                {
                    bool gzip = stream.ReadByte() == 0x1F && stream.ReadByte() == 0x8B;
                    stream.Position = 0;
                    using (Stream source = gzip ? new GZipStream(stream, CompressionMode.Decompress) : (Stream)stream)
                    {
                        byte[] buffer = new byte[1024];
                        int read = 0;
                        int n;
                        while (read < buffer.Length && (n = source.Read(buffer, read, buffer.Length - read)) > 0)
                        {
                            read += n;
                        }

                        Match tag = SnapshotTag.Match(Encoding.UTF8.GetString(buffer, 0, read));
                        Match w = WidthAttribute.Match(tag.Value);
                        Match h = HeightAttribute.Match(tag.Value);
                        return tag.Success && w.Success && h.Success
                            && int.TryParse(w.Groups[1].Value, out width)
                            && int.TryParse(h.Groups[1].Value, out height);
                    }
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        // 맵에 들어가는 기지를 넓이 순으로 줄 세워 큰 쪽 절반만 남긴다.
        private static List<BaseInfo> LargeBases(Map map)
        {
            List<BaseInfo> fitting = new List<BaseInfo>();
            foreach (string file in BlueprintFiles())
            {
                if (TryReadSize(file, out int width, out int height)
                    && width <= map.Size.x - EdgeMargin * 2
                    && height <= map.Size.z - EdgeMargin * 2
                    && width * height >= MinArea)
                {
                    fitting.Add(new BaseInfo { file = file, width = width, height = height });
                }
            }

            fitting.Sort((a, b) => (b.width * b.height).CompareTo(a.width * a.height));
            if (fitting.Count > 1)
            {
                fitting.RemoveRange((fitting.Count + 1) / 2, fitting.Count - (fitting.Count + 1) / 2);
            }

            return fitting;
        }

        public static int LargeBaseCount(Map map)
        {
            return LargeBases(map).Count;
        }

        // 큰 기지들에서 후보를 무작위로 뽑아, 가치가 플레이어 부의 절반~3배인 것 가운데 하나를 무작위로 고른다. 그런 것이 없으면 가장 가까운 것을 쓴다.
        // 맵 생성 난수를 쓰므로 받아 둔 기지가 그대로면 같은 정착지는 같은 기지가 된다.
        private static object PickBlueprint(Map map, float targetWealth, out string file)
        {
            file = null;
            targetWealth = Mathf.Max(targetWealth, MinCost);
            List<(object blueprint, string file)> inBand = new List<(object, string)>();
            object closest = null;
            string closestFile = null;
            float closestDistance = float.MaxValue;
            foreach (BaseInfo info in LargeBases(map).InRandomOrder().Take(Candidates))
            {
                object blueprint = loadBlueprint.Invoke(null, new object[] { info.file });
                if (blueprint == null)
                {
                    continue;
                }

                updateStats.Invoke(blueprint, new object[] { true });
                float cost = (float)blueprintCost.GetValue(blueprint);
                if (cost < MinCost)
                {
                    continue;
                }

                if (cost >= targetWealth * WealthBandMin && cost <= targetWealth * WealthBandMax)
                {
                    inBand.Add((blueprint, info.file));
                }

                float distance = Mathf.Abs(Mathf.Log(cost / targetWealth));
                if (distance < closestDistance)
                {
                    closest = blueprint;
                    closestFile = info.file;
                    closestDistance = distance;
                }
            }

            if (inBand.Count > 0)
            {
                (object blueprint, string pickedFile) = inBand.RandomElement();
                file = pickedFile;
                return blueprint;
            }

            file = closestFile;
            return closest;
        }

        public static bool TryPlaceBase(Map map, IntVec3 center, Faction faction, float targetWealth, out CellRect rect)
        {
            rect = CellRect.Empty;
            try
            {
                object blueprint = PickBlueprint(map, targetWealth, out string file);
                if (blueprint == null)
                {
                    return false;
                }

                int width = (int)blueprintWidth.GetValue(blueprint);
                int height = (int)blueprintHeight.GetValue(blueprint);
                object options = optionsForPoi.Invoke(null, new[] { file, blueprint, Enum.ToObject(poiType, IntactPoiType), true, false, false });

                // 원래 맵에서의 위치 대신 정착지 자리 한가운데에 놓는다.
                overridePosition.SetValue(options, IntVec3.Zero);

                // 부족·중세 세력의 기지에는 그 시대를 넘는 물건을 놓지 않는다. Real Ruins 설정에서 시대 제한을 켰으면 그 값을 따른다.
                if (faction.def.techLevel < TechLevel.Industrial && (TechLevel)maxTechLevel.GetValue(options) == TechLevel.Undefined)
                {
                    maxTechLevel.SetValue(options, faction.def.techLevel);
                    techLimitStrict.SetValue(options, true);
                }

                object context = Activator.CreateInstance(contextType, options, pipelineFromModSettings.Invoke(null, null));
                preloadedBlueprint.SetValue(context, blueprint);

                CellRect target = new CellRect(center.x - width / 2, center.z - height / 2, width, height);
                ResolveParams rp = new ResolveParams { faction = faction, rect = target };
                BaseGen.globalSettings.map = map;
                BaseGen.globalSettings.mainRect = target;
                scatter.Invoke(null, new[] { rp, context, null, null });

                object state = contextState.GetValue(context);
                rect = (CellRect)placementRect.GetValue(state);
                rect.ClipInsideMap(map);
                ResolveParams finish = new ResolveParams { faction = faction, rect = rect };
                BaseGen.symbolStack.Push("chargeBatteries", finish);
                BaseGen.symbolStack.Push("ensureCanHoldRoof", finish);
                BaseGen.symbolStack.Push("refuel", finish);
                BaseGen.Generate();
                removeUnsupportedRoofs.Invoke(null, new object[] { map, rect });

                if ((float)uncoveredCost.GetValue(state) <= 0f)
                {
                    Log.Warning($"[Yuudong2Mod] RealRuinsBases: Real Ruins placed nothing from {Path.GetFileName(file)}; using vanilla generation for this settlement.");
                    return false;
                }

                LastPlacedFile = Path.GetFileName(file);
                LastPlacedRect = rect;
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[Yuudong2Mod] RealRuinsBases: placing a Real Ruins base failed; using vanilla generation for this settlement. " + e);
                return false;
            }
        }
    }
}
