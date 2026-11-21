using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    [DefOf]
    public static class EnemyGravshipDefOf
    {
        public static PawnsArrivalModeDef Yuudong2_GravshipLanding;

        static EnemyGravshipDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(EnemyGravshipDefOf));
        }
    }

    public class ShipThing
    {
        public ThingDef def;
        public ThingDef stuff;
        public IntVec3 pos;
        public Rot4 rot;
    }

    // 설계도에서 잘라낸 배 한 척. 좌표는 배를 감싸는 사각형의 왼쪽 아래 구석이 (0,0)이다.
    public class ShipDesign
    {
        public string source;
        public IntVec2 size;
        public List<IntVec3> cells = new List<IntVec3>();
        public Dictionary<IntVec3, TerrainDef> floors = new Dictionary<IntVec3, TerrainDef>();
        public HashSet<IntVec3> roofs = new HashSet<IntVec3>();
        public List<ShipThing> things = new List<ShipThing>();
    }

    // Real Ruins가 내려받아 둔 다른 플레이어의 기지 설계도에서 중력선을 찾아 잘라낸다.
    public static class ShipLibrary
    {
        public const int MinCells = 60;
        private const int MaxFloorPatch = 400;

        private static readonly object Gate = new object();
        private static readonly byte[] EngineMark = Encoding.ASCII.GetBytes("def=\"GravEngine\"");
        private static readonly byte[] SubstructureMark = Encoding.ASCII.GetBytes("def=\"Substructure\"");
        private static Task scan;
        private static volatile List<string> ships;

        public static string Folder => Path.Combine(GenFilePaths.SaveDataFolderPath, "RealRuins");

        private static string IndexPath => Path.Combine(GenFilePaths.ConfigFolderPath, "Yuudong2Mod_GravshipIndex.txt");

        public static bool Ready => ships != null;

        public static int Count => ships?.Count ?? 0;

        public static IReadOnlyList<string> Files => ships ?? new List<string>();

        public static void EnsureScanStarted()
        {
            lock (Gate)
            {
                if (scan == null && Directory.Exists(Folder))
                {
                    string folder = Folder;
                    string index = IndexPath;
                    scan = Task.Run(() => Scan(folder, index));
                }
            }
        }

        public static bool WaitForScan(int milliseconds)
        {
            EnsureScanStarted();
            return scan != null && scan.Wait(milliseconds) && Ready;
        }

        // 설계도는 수천 개라서 한 번 훑은 결과(파일 이름, 크기, 하부 구조 칸 수)를 적어 두고 새 파일만 다시 읽는다.
        private static void Scan(string folder, string indexPath)
        {
            try
            {
                Dictionary<string, (long length, int cells)> known = new Dictionary<string, (long, int)>();
                if (File.Exists(indexPath))
                {
                    foreach (string line in File.ReadAllLines(indexPath))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length == 3 && long.TryParse(parts[1], out long length) && int.TryParse(parts[2], out int cells))
                        {
                            known[parts[0]] = (length, cells);
                        }
                    }
                }

                List<string> found = new List<string>();
                StringBuilder sb = new StringBuilder();
                bool changed = false;
                foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*.bp"))
                {
                    int cells;
                    if (known.TryGetValue(file.Name, out (long length, int cells) entry) && entry.length == file.Length)
                    {
                        cells = entry.cells;
                    }
                    else
                    {
                        cells = CountShipCells(file.FullName);
                        changed = true;
                    }

                    sb.Append(file.Name).Append('\t').Append(file.Length).Append('\t').Append(cells).Append('\n');
                    if (cells >= MinCells)
                    {
                        found.Add(file.Name);
                    }
                }

                if (changed)
                {
                    File.WriteAllText(indexPath, sb.ToString());
                }

                ships = found;
            }
            catch (Exception)
            {
                ships = new List<string>();
            }
        }

        // 중력 엔진이 없으면 0, 있으면 하부 구조 칸 수.
        private static int CountShipCells(string path)
        {
            try
            {
                byte[] data = ReadAll(path);
                ReadOnlySpan<byte> span = data;
                if (span.IndexOf(EngineMark) < 0)
                {
                    return 0;
                }

                int count = 0;
                while (true)
                {
                    int at = span.IndexOf(SubstructureMark);
                    if (at < 0)
                    {
                        return count;
                    }

                    count++;
                    span = span.Slice(at + SubstructureMark.Length);
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static byte[] ReadAll(string path)
        {
            using (FileStream file = File.OpenRead(path))
            using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
            using (MemoryStream memory = new MemoryStream())
            {
                gzip.CopyTo(memory);
                return memory.ToArray();
            }
        }

        private class RawCell
        {
            public string terrain;
            public bool roof;
            public List<(string def, string stuff, int rot)> items;
        }

        private static readonly Dictionary<string, ShipDesign> loaded = new Dictionary<string, ShipDesign>();

        // 한 번 읽은 설계도는 기억해 둔다. 배가 안 나오는 파일은 null로 남는다.
        public static ShipDesign TryLoad(string fileName)
        {
            if (loaded.TryGetValue(fileName, out ShipDesign known))
            {
                return known;
            }

            ShipDesign design = null;
            try
            {
                design = Extract(fileName, ReadAll(Path.Combine(Folder, fileName)));
            }
            catch (Exception e)
            {
                Log.Warning($"[Yuudong2Mod] gravship design {fileName} could not be read: {e.Message}");
            }

            loaded[fileName] = design;
            return design;
        }

        private static ShipDesign Extract(string fileName, byte[] data)
        {
            Dictionary<IntVec3, RawCell> raw = new Dictionary<IntVec3, RawCell>();
            bool truncated = false;
            int lastZ = 0;
            RawCell current = null;
            // 올라온 설계도는 중간에 잘린 것이 많다. 읽힌 데까지만 쓰고, 잘린 줄에 걸친 배는 버린다.
            try
            {
                using (XmlReader reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { IgnoreWhitespace = true }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (reader.Depth == 1 && reader.Name == "cell")
                        {
                            int x = int.Parse(reader.GetAttribute("x"));
                            lastZ = int.Parse(reader.GetAttribute("z"));
                            current = new RawCell();
                            raw[new IntVec3(x, 0, lastZ)] = current;
                        }
                        else if (reader.Depth == 2 && current != null)
                        {
                            switch (reader.Name)
                            {
                                case "terrain":
                                    current.terrain = reader.GetAttribute("def");
                                    break;
                                case "roof":
                                    current.roof = true;
                                    break;
                                case "item":
                                    int.TryParse(reader.GetAttribute("rot"), out int rot);
                                    (current.items ?? (current.items = new List<(string, string, int)>())).Add((reader.GetAttribute("def"), reader.GetAttribute("stuffDef"), rot));
                                    break;
                            }
                        }
                    }
                }
            }
            catch (XmlException)
            {
                truncated = true;
            }

            IntVec3 enginePos = IntVec3.Invalid;
            foreach (KeyValuePair<IntVec3, RawCell> pair in raw)
            {
                if (pair.Value.items != null && pair.Value.items.Any(i => i.def == "GravEngine"))
                {
                    enginePos = pair.Key;
                    break;
                }
            }

            if (!enginePos.IsValid)
            {
                return null;
            }

            bool IsSubstructure(IntVec3 c) => raw.TryGetValue(c, out RawCell cell) && cell.terrain == "Substructure";
            bool IsFloor(IntVec3 c) => raw.TryGetValue(c, out RawCell cell) && cell.terrain != null && cell.terrain != "Substructure";

            HashSet<IntVec3> hull = new HashSet<IntVec3>();
            Queue<IntVec3> open = new Queue<IntVec3>();
            foreach (IntVec3 c in CellRect.CenteredOn(enginePos, 1))
            {
                if (IsSubstructure(c) && hull.Add(c))
                {
                    open.Enqueue(c);
                }
            }

            while (open.Count > 0)
            {
                IntVec3 c = open.Dequeue();
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 n = c + dir;
                    if (IsSubstructure(n) && hull.Add(n))
                    {
                        open.Enqueue(n);
                    }
                }
            }

            // 하부 구조 위에 바닥을 깐 칸은 설계도에 바닥으로만 적힌다. 선체로 완전히 둘러싸인 바닥 덩어리만 배의 일부로 본다.
            Dictionary<IntVec3, string> floored = new Dictionary<IntVec3, string>();
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            foreach (IntVec3 start in hull.ToList())
            {
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 first = start + dir;
                    if (hull.Contains(first) || !IsFloor(first) || !seen.Add(first))
                    {
                        continue;
                    }

                    List<IntVec3> patch = new List<IntVec3> { first };
                    bool enclosed = true;
                    for (int i = 0; i < patch.Count && enclosed; i++)
                    {
                        foreach (IntVec3 d in GenAdj.CardinalDirections)
                        {
                            IntVec3 n = patch[i] + d;
                            if (hull.Contains(n))
                            {
                                continue;
                            }

                            if (!IsFloor(n) || patch.Count > MaxFloorPatch)
                            {
                                enclosed = false;
                                break;
                            }

                            if (seen.Add(n))
                            {
                                patch.Add(n);
                            }
                        }
                    }

                    if (enclosed)
                    {
                        foreach (IntVec3 c in patch)
                        {
                            floored[c] = raw[c].terrain;
                        }
                    }
                }
            }

            foreach (IntVec3 c in floored.Keys)
            {
                hull.Add(c);
            }

            if (hull.Count < MinCells || (truncated && hull.Any(c => c.z >= lastZ)))
            {
                return null;
            }

            CellRect bounds = CellRect.FromCellList(hull);
            IntVec3 origin = bounds.Min;
            ShipDesign design = new ShipDesign { source = fileName, size = new IntVec2(bounds.Width, bounds.Height) };
            bool hasEngine = false;
            foreach (IntVec3 c in hull)
            {
                IntVec3 local = c - origin;
                RawCell cell = raw[c];
                design.cells.Add(local);
                if (cell.roof)
                {
                    design.roofs.Add(local);
                }

                if (floored.TryGetValue(c, out string floorName))
                {
                    TerrainDef floor = DefDatabase<TerrainDef>.GetNamedSilentFail(floorName);
                    if (floor != null && !floor.isFoundation && !floor.temporary && floor.BuildableByPlayer)
                    {
                        design.floors[local] = floor;
                    }
                }

                if (cell.items == null)
                {
                    continue;
                }

                foreach ((string defName, string stuffName, int rot) in cell.items)
                {
                    ThingDef def = defName == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (!Usable(def) || (def == ThingDefOf.GravEngine && hasEngine))
                    {
                        continue;
                    }

                    ThingDef stuff = null;
                    if (def.MadeFromStuff)
                    {
                        stuff = stuffName == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
                        if (stuff == null || !stuff.IsStuff || !def.stuffCategories.Any(cat => stuff.stuffProps.categories.Contains(cat)))
                        {
                            stuff = GenStuff.DefaultStuffFor(def);
                        }
                    }

                    hasEngine |= def == ThingDefOf.GravEngine;
                    design.things.Add(new ShipThing { def = def, stuff = stuff, pos = local, rot = def.rotatable ? new Rot4(rot) : def.defaultPlacingRot });
                }
            }

            return hasEngine ? design : null;
        }

        // 플레이어가 지을 수 있는 건물과 중력 엔진만 옮긴다. 물건, 시체, 폰, 유적 전용 구조물은 뺀다.
        private static bool Usable(ThingDef def)
        {
            return def != null
                && def.category == ThingCategory.Building
                && def.building != null
                && !def.building.isNaturalRock
                && !def.IsFrame
                && !def.IsBlueprint
                && def.destroyable
                && (def.BuildableByPlayer || def == ThingDefOf.GravEngine);
        }
    }

    public static class EnemyGravshipUtility
    {
        private const int SiteStride = 2;
        private const int EdgeMargin = 8;
        private const int SubstructureGap = 3;
        private const float Oversize = 1.25f;
        private const float PointsPerCell = 5f;
        private const int VarietyPool = 10;
        private const int RepeatRetries = 300;
        private const float VarietySpread = 0.3f;

        private static readonly MethodInfo ResolveFaction = AccessTools.Method(typeof(IncidentWorker_Raid), "TryResolveRaidFaction");
        private static readonly MethodInfo ResolvePoints = AccessTools.Method(typeof(IncidentWorker_Raid), "ResolveRaidPoints");
        private static readonly MethodInfo ExecuteWorker = AccessTools.Method(typeof(IncidentWorker), "TryExecuteWorker");

        private static bool[] freeCells;
        private static Map freeMap;
        private static int freeTick;

        public static bool executingRaid;
        public static List<Thing> lastShipThings = new List<Thing>();
        public static List<IntVec3> lastShipCells = new List<IntVec3>();
        public static string lastReport = "";
        public static string lastSource;

        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.EnemyGravship) && ModsConfig.OdysseyActive;

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.EnemyGravship.Chance".Translate(s.enemyGravshipChance), -1f, "Yuudong2.EnemyGravship.Chance.Desc".Translate());
            s.enemyGravshipChance = Mathf.RoundToInt(listing.Slider(s.enemyGravshipChance, 0f, 100f));
            listing.CheckboxLabeled("Yuudong2.EnemyGravship.Cutscene".Translate(), ref s.enemyGravshipCutscene, "Yuudong2.EnemyGravship.Cutscene.Desc".Translate());
            listing.Gap();
            listing.Label(ShipLibrary.Ready
                ? "Yuudong2.EnemyGravship.Found".Translate(ShipLibrary.Count)
                : "Yuudong2.EnemyGravship.Scanning".Translate());
            listing.End();
        }

        // 습격 포인트 1,000이면 200칸쯤 되는 배를 노린다.
        public static int TargetCells(float points) => Mathf.Max(ShipLibrary.MinCells, Mathf.RoundToInt(points / PointsPerCell));

        // 목표 크기에 가까운 배부터 놓을 자리를 찾는다. 목표보다 많이 큰 배는 빼고, 자리가 없으면 점점 작은 배로 내려간다.
        public static bool TryPlan(Map map, float points, out ShipDesign design, out IntVec3 root)
        {
            design = null;
            root = IntVec3.Invalid;
            if (!ShipLibrary.Ready || ShipLibrary.Count == 0)
            {
                return false;
            }

            int target = TargetCells(points);
            List<ShipDesign> designs = ShipLibrary.Files.Select(ShipLibrary.TryLoad).Where(d => d != null).ToList();
            List<ShipDesign> fit = designs
                .Where(d => d.cells.Count <= target * Oversize)
                .OrderBy(d => Mathf.Abs(d.cells.Count - target) + Rand.Range(0f, target * 0.15f))
                .ToList();
            // 목표에 가까운 배 몇 척을 섞어서, 큰 목표에서도 같은 배만 계속 오지 않게 한다.
            int pool = 0;
            if (fit.Count > 0)
            {
                float limit = Mathf.Abs(fit[0].cells.Count - target) + target * VarietySpread;
                pool = fit.Take(VarietyPool).Count(d => Mathf.Abs(d.cells.Count - target) <= limit);
            }

            List<ShipDesign> head = fit.Take(pool).InRandomOrder().ToList();
            IEnumerable<ShipDesign> order = head
                .Concat(fit.Skip(pool))
                .Concat(designs.Where(d => d.cells.Count > target * Oversize).OrderBy(d => d.cells.Count));
            // 직전에 온 배가 다시 걸리면, 놓을 수 있는 다른 배를 조금 더 찾아본다.
            ShipDesign fallback = null;
            IntVec3 fallbackRoot = IntVec3.Invalid;
            int extraTries = 0;
            foreach (ShipDesign candidate in order)
            {
                if (fallback != null && extraTries++ >= RepeatRetries)
                {
                    break;
                }

                if (!TryFindSite(map, candidate, out IntVec3 site))
                {
                    continue;
                }

                if (candidate.source == lastSource && fallback == null)
                {
                    fallback = candidate;
                    fallbackRoot = site;
                    continue;
                }

                design = candidate;
                root = site;
                lastSource = candidate.source;
                return true;
            }

            if (fallback != null)
            {
                design = fallback;
                root = fallbackRoot;
                return true;
            }

            root = IntVec3.Invalid;
            lastReport = $"no landing site: none of {designs.Count} designs fits on the map";
            return false;
        }

        // 맵 전체를 두 칸 간격으로 훑어, 배의 모든 칸이 비어 있는 자리 중 정착지 중심에 가장 가까운 곳을 고른다.
        public static bool TryFindSite(Map map, ShipDesign design, out IntVec3 root)
        {
            root = IntVec3.Invalid;
            int maxX = map.Size.x - EdgeMargin - design.size.x;
            int maxZ = map.Size.z - EdgeMargin - design.size.z;
            if (maxX <= EdgeMargin || maxZ <= EdgeMargin)
            {
                return false;
            }

            bool[] free = FreeCells(map);
            int width = map.Size.x;
            int[] offsets = new int[design.cells.Count];
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = design.cells[i].z * width + design.cells[i].x;
            }

            IntVec3 colony = ColonyCenter(map);
            int halfX = design.size.x / 2;
            int halfZ = design.size.z / 2;
            float best = float.MaxValue;
            for (int z = EdgeMargin + Rand.Range(0, SiteStride); z <= maxZ; z += SiteStride)
            {
                for (int x = EdgeMargin + Rand.Range(0, SiteStride); x <= maxX; x += SiteStride)
                {
                    float dx = x + halfX - colony.x;
                    float dz = z + halfZ - colony.z;
                    float distance = dx * dx + dz * dz;
                    if (distance >= best)
                    {
                        continue;
                    }

                    int origin = z * width + x;
                    bool fits = true;
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        if (!free[origin + offsets[i]])
                        {
                            fits = false;
                            break;
                        }
                    }

                    if (fits)
                    {
                        best = distance;
                        root = new IntVec3(x, 0, z);
                    }
                }
            }

            return root.IsValid;
        }

        private static IntVec3 ColonyCenter(Map map)
        {
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            if (buildings.Count == 0)
            {
                return map.Center;
            }

            long x = 0;
            long z = 0;
            foreach (Building building in buildings)
            {
                x += building.Position.x;
                z += building.Position.z;
            }

            return new IntVec3((int)(x / buildings.Count), 0, (int)(z / buildings.Count));
        }

        // 배의 칸이 놓일 수 있는 맵 칸. 같은 틱 안에서는 한 번만 계산한다.
        private static bool[] FreeCells(Map map)
        {
            int tick = Find.TickManager.TicksGame;
            if (freeCells != null && freeMap == map && freeTick == tick)
            {
                return freeCells;
            }

            TerrainAffordanceDef needed = TerrainDefOf.Substructure.terrainAffordanceNeeded;
            bool[] free = new bool[map.cellIndices.NumGridCells];
            List<IntVec3> substructure = new List<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                TerrainDef foundation = map.terrainGrid.FoundationAt(c);
                if (foundation != null)
                {
                    if (foundation.IsSubstructure)
                    {
                        substructure.Add(c);
                    }

                    continue;
                }

                if (c.Fogged(map) || c.Roofed(map) || map.terrainGrid.UnderTerrainAt(c) != null)
                {
                    continue;
                }

                if (needed != null && !map.terrainGrid.TerrainAt(c).affordances.Contains(needed))
                {
                    continue;
                }

                bool clear = true;
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    ThingCategory category = things[i].def.category;
                    if (category != ThingCategory.Plant && category != ThingCategory.Filth && category != ThingCategory.Mote && category != ThingCategory.Ethereal && category != ThingCategory.Item)
                    {
                        clear = false;
                        break;
                    }
                }

                free[map.cellIndices.CellToIndex(c)] = clear;
            }

            // 이미 있는 하부 구조와 붙으면 두 배가 한 척으로 이어진다.
            foreach (IntVec3 c in substructure)
            {
                foreach (IntVec3 near in CellRect.CenteredOn(c, SubstructureGap).ClipInsideMap(map))
                {
                    free[map.cellIndices.CellToIndex(near)] = false;
                }
            }

            freeCells = free;
            freeMap = map;
            freeTick = tick;
            return free;
        }

        public static Building_GravEngine Place(Map map, ShipDesign design, IntVec3 root, Faction faction)
        {
            freeCells = null;
            HashSet<IntVec3> occupied = new HashSet<IntVec3>(design.cells.Select(local => root + local));
            CellRect outer = CellRect.FromCellList(occupied).ExpandedBy(1).ClipInsideMap(map);
            foreach (IntVec3 local in design.cells)
            {
                IntVec3 c = root + local;
                map.zoneManager.ZoneAt(c)?.RemoveCell(c);
                List<Thing> things = map.thingGrid.ThingsListAt(c);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    Thing thing = things[i];
                    if (thing.def.category == ThingCategory.Plant || thing.def.category == ThingCategory.Filth)
                    {
                        thing.Destroy();
                    }
                    else if (thing.def.category == ThingCategory.Item)
                    {
                        // 바닥에 놓인 물건과 돌덩이는 배 밖 가장 가까운 곳으로 밀어낸다.
                        thing.DeSpawn();
                        if (!GenPlace.TryPlaceThing(thing, NearestEdge(outer, c), map, ThingPlaceMode.Near, null, cell => !occupied.Contains(cell)))
                        {
                            GenSpawn.Spawn(thing, c, map);
                        }
                    }
                }

                map.snowGrid.SetDepth(c, 0f);
                map.terrainGrid.SetFoundation(c, TerrainDefOf.Substructure);
                if (design.floors.TryGetValue(local, out TerrainDef floor))
                {
                    map.terrainGrid.SetTerrain(c, floor);
                }
            }

            Building_GravEngine engine = null;
            lastShipThings = new List<Thing>();
            foreach (ShipThing entry in design.things.OrderBy(SpawnOrder))
            {
                IntVec3 pos = root + entry.pos;
                CellRect rect = GenAdj.OccupiedRect(pos, entry.rot, entry.def.size);
                if (!rect.Cells.All(occupied.Contains) || (entry.def.IsEdifice() && rect.Cells.Any(c => c.GetEdifice(map) != null)))
                {
                    continue;
                }

                try
                {
                    Thing thing = ThingMaker.MakeThing(entry.def, entry.stuff);
                    if (entry.def.CanHaveFaction)
                    {
                        thing.SetFactionDirect(faction);
                    }

                    thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
                    Thing spawned = GenSpawn.Spawn(thing, pos, map, entry.rot);
                    lastShipThings.Add(spawned);
                    if (spawned is Building_GravEngine made)
                    {
                        engine = made;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[Yuudong2Mod] gravship part {entry.def.defName} could not be placed: {e.Message}");
                }
            }

            foreach (IntVec3 local in design.roofs)
            {
                map.roofGrid.SetRoof(root + local, RoofDefOf.RoofConstructed);
            }

            map.substructureGrid.MarkDirty();
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            // 마스크 레이어가 다시 그려지는 도중에 하부 구조 계산이 시작되면 레이어가 겹쳐 그려진다. 여기서 먼저 계산해 둔다.
            if (engine != null)
            {
                engine.ForceSubstructureDirty();
                _ = engine.ValidSubstructureNoRegen;
            }
            lastShipCells = occupied.ToList();
            return engine;
        }

        public static void GiveShipTo(Faction faction)
        {
            foreach (Thing thing in lastShipThings)
            {
                if (thing.Spawned && thing.def.CanHaveFaction && thing.Faction != faction)
                {
                    thing.SetFaction(faction);
                }
            }
        }

        private static IntVec3 NearestEdge(CellRect rect, IntVec3 c)
        {
            int left = c.x - rect.minX;
            int right = rect.maxX - c.x;
            int bottom = c.z - rect.minZ;
            int top = rect.maxZ - c.z;
            int nearest = Mathf.Min(Mathf.Min(left, right), Mathf.Min(bottom, top));
            if (nearest == left)
            {
                return new IntVec3(rect.minX, 0, c.z);
            }

            if (nearest == right)
            {
                return new IntVec3(rect.maxX, 0, c.z);
            }

            return nearest == bottom ? new IntVec3(c.x, 0, rect.minZ) : new IntVec3(c.x, 0, rect.maxZ);
        }

        private static int SpawnOrder(ShipThing entry)
        {
            if (entry.def.building.isAttachment)
            {
                return 2;
            }

            return entry.def.passability == Traversability.Impassable ? 0 : 1;
        }

        // 배 안에서 설 수 있고 문을 지나 밖으로 나갈 수 있는 칸.
        public static List<IntVec3> BoardingCells(Map map, List<IntVec3> shipCells)
        {
            HashSet<IntVec3> ship = new HashSet<IntVec3>(shipCells);
            List<IntVec3> standable = shipCells.Where(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null).ToList();
            if (standable.Count == 0)
            {
                return standable;
            }

            CellRect bounds = CellRect.FromCellList(shipCells).ExpandedBy(4).ClipInsideMap(map);
            IntVec3 outside = bounds.EdgeCells.Where(c => !ship.Contains(c) && c.Standable(map)).RandomElementWithFallback(IntVec3.Invalid);
            if (!outside.IsValid)
            {
                return standable;
            }

            TraverseParms traverse = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            List<IntVec3> reachable = standable.Where(c => map.reachability.CanReach(c, outside, PathEndMode.OnCell, traverse)).ToList();
            return reachable.Count > 0 ? reachable : standable;
        }

        public static bool Eligible(IncidentWorker_Raid worker, IncidentParms parms)
        {
            if (!Active || executingRaid || EnemyGravshipLanding.Active)
            {
                return false;
            }

            if (!(parms.target is Map map) || !map.IsPlayerHome || parms.quest != null || parms.raidArrivalMode != null || parms.raidStrategy != null || parms.pawnGroups != null)
            {
                return false;
            }

            ShipLibrary.EnsureScanStarted();
            return ShipLibrary.Ready && ShipLibrary.Count > 0;
        }

        // 습격을 중력선 습격으로 바꿀 수 있으면 착륙을 시작하고 true를 돌려준다. 병력은 착륙이 끝난 뒤 바닐라 습격 절차로 만든다.
        public static bool TryStartRaid(IncidentWorker_Raid worker, IncidentParms parms, bool cutscene)
        {
            Map map = (Map)parms.target;
            ResolvePoints.Invoke(worker, new object[] { parms });
            if (parms.faction == null && !(bool)ResolveFaction.Invoke(worker, new object[] { parms }))
            {
                return false;
            }

            Faction faction = parms.faction;
            if (faction == null || !faction.def.humanlikeFaction || (int)faction.def.techLevel < (int)TechLevel.Industrial)
            {
                lastReport = $"faction {faction?.Name ?? "none"} does not fly gravships";
                return false;
            }

            if (!TryPlan(map, parms.points, out ShipDesign design, out IntVec3 root))
            {
                return false;
            }

            EnemyGravshipLanding.Begin(map, design, root, faction, cutscene, engine =>
            {
                parms.raidArrivalMode = EnemyGravshipDefOf.Yuudong2_GravshipLanding;
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                parms.spawnCenter = CellRect.FromCellList(lastShipCells).CenterCell;
                parms.spawnRotation = Rot4.Random;
                executingRaid = true;
                try
                {
                    bool done = (bool)ExecuteWorker.Invoke(worker, new object[] { parms });
                    lastReport = $"{design.source} {design.cells.Count} cells (aimed at {TargetCells(parms.points)}) at {root}, engine {(engine != null ? "placed" : "missing")}, raid {(done ? "arrived" : "failed")}";
                    Log.Message($"[Yuudong2Mod] enemy gravship: {lastReport}, landing {EnemyGravshipLanding.lastMode}");
                    if (!done)
                    {
                        Log.Warning("[Yuudong2Mod] enemy gravship landed but the raid could not be generated: " + parms);
                    }
                }
                finally
                {
                    executingRaid = false;
                }
            });
            return true;
        }

        [DebugAction("Yuudong2", "Enemy gravship raid", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugRaid()
        {
            Map map = Find.CurrentMap;
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
            if (!ShipLibrary.WaitForScan(60000) || !TryStartRaid((IncidentWorker_Raid)IncidentDefOf.RaidEnemy.Worker, parms, Yuudong2Settings.Current.enemyGravshipCutscene))
            {
                Messages.Message("Yuudong2.EnemyGravship.NoSite".Translate(), MessageTypeDefOf.RejectInput, false);
            }
        }
    }

    public class PawnsArrivalModeWorker_GravshipLanding : PawnsArrivalModeWorker
    {
        public override void Arrive(List<Pawn> pawns, IncidentParms parms)
        {
            Map map = (Map)parms.target;
            List<IntVec3> cells = EnemyGravshipUtility.BoardingCells(map, EnemyGravshipUtility.lastShipCells);
            foreach (Pawn pawn in pawns)
            {
                IntVec3 cell = cells.Count > 0 ? cells.RandomElement() : CellFinder.RandomClosewalkCellNear(parms.spawnCenter, map, 8);
                GenSpawn.Spawn(pawn, cell, map, parms.spawnRotation);
            }
        }

        public override bool TryResolveRaidSpawnCenter(IncidentParms parms)
        {
            return parms.spawnCenter.IsValid;
        }
    }

    // 바닐라 중력선 착륙 컷신을 적의 배에 그대로 쓴다. 바닐라 컨트롤러의 캡처·렌더러·소리를 빌려 쓰고, 끝날 때만 우리 마무리로 바꾼다.
    public static class EnemyGravshipLanding
    {
        private const float TimeoutSeconds = 60f;

        private static readonly Type Controller = typeof(WorldComponent_GravshipController);
        private static readonly FieldInfo CutsceneFlag = AccessTools.Field(Controller, "cutsceneInProgress");
        private static readonly MethodInfo RegenerateMask = AccessTools.Method(Controller, "RegenerateGravshipMask");
        private static readonly MethodInfo ResetCutscene = AccessTools.Method(Controller, "ResetCutscene");
        private static readonly int BackgroundTex = Shader.PropertyToID("_BackgroundTex");

        private static Map map;
        private static ShipDesign design;
        private static IntVec3 root;
        private static Faction faction;
        private static Action<Building_GravEngine> onLanded;
        private static Building_GravEngine engine;
        private static Building_GravEngine previousMaskEngine;
        private static bool placed;
        private static float startedAt;

        public static string lastMode = "";
        public static string stage = "";

        private static Building_GravEngine wholeShipEngine;

        public static bool Active { get; private set; }

        private static Traverse Fields => Traverse.Create(Find.GravshipController);

        private static string CutsceneBlocker()
        {
            WorldComponent_GravshipController controller = Find.GravshipController;
            if (!Prefs.GravshipCutscenes)
            {
                return "gravship cutscenes are off in the game options";
            }
            if (!UnityData.IsInMainThread || Current.ProgramState != ProgramState.Playing || controller == null)
            {
                return "game not in play";
            }
            if (WorldComponent_GravshipController.CutsceneInProgress || controller.IsGravshipTravelling || controller.LandingAreaConfirmationInProgress)
            {
                return "the player's gravship is flying or landing";
            }
            if (LongEventHandler.AnyEventNowOrWaiting)
            {
                return "a loading screen is up";
            }
            if (CutsceneFlag == null || RegenerateMask == null || ResetCutscene == null)
            {
                return "the game's landing code changed";
            }
            return null;
        }

        public static bool HoldsSubstructure(Building_GravEngine candidate) => wholeShipEngine != null && candidate == wholeShipEngine;

        // 엔진이 받칠 수 있는 칸보다 큰 배도 통째로 찍히게, 컷신 동안만 이어진 하부 구조 전체를 유효한 것으로 둔다.
        private static void HoldWholeShip()
        {
            HashSet<IntVec3> all = engine.AllConnectedSubstructureNoRegen;
            HashSet<IntVec3> valid = engine.ValidSubstructureNoRegen;
            if (valid.Count < all.Count)
            {
                valid.UnionWith(all);
                wholeShipEngine = engine;
            }
        }

        private static void ReleaseWholeShip()
        {
            Building_GravEngine held = wholeShipEngine;
            wholeShipEngine = null;
            held?.ForceSubstructureDirty();
        }

        public static void Begin(Map target, ShipDesign ship, IntVec3 at, Faction owner, bool cutscene, Action<Building_GravEngine> landed)
        {
            map = target;
            design = ship;
            root = at;
            faction = owner;
            onLanded = landed;
            engine = null;
            placed = false;
            string blocker = cutscene ? CutsceneBlocker() : "turned off in the mod settings";
            if (blocker != null)
            {
                lastMode = $"instant ({blocker})";
                PlaceNow();
                Complete();
                return;
            }

            lastMode = "cutscene";
            stage = "start";
            Active = true;
            startedAt = Time.realtimeSinceStartup;
            Guard(StartCutscene);
        }

        // 중력장 확장기와 추진기는 플레이어 엔진에만 반응한다. 컷신 동안은 배를 플레이어 것으로 지어 바닐라 계산을 그대로 쓰고, 끝날 때 원래 세력에게 넘긴다. 그동안 틱은 멈춰 있다.
        private static void PlaceNow()
        {
            if (!placed)
            {
                placed = true;
                engine = EnemyGravshipUtility.Place(map, design, root, Active ? Faction.OfPlayer : faction);
            }
        }

        private static void Complete()
        {
            Action<Building_GravEngine> landed = onLanded;
            Building_GravEngine made = engine;
            EnemyGravshipUtility.GiveShipTo(faction);
            MultiGravshipUtility.Relink(map);
            onLanded = null;
            map = null;
            design = null;
            faction = null;
            engine = null;
            landed?.Invoke(made);
        }

        private static void Guard(Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Log.Error("[Yuudong2Mod] enemy gravship cutscene failed, landing without it: " + e);
                lastMode = "instant (cutscene failed)";
                Abort();
            }
        }

        private static bool Transparent(BiomeDef biome) => biome == BiomeDefOf.Space || biome == BiomeDefOf.Orbit;

        private static Material ControllerMaterial(string name) => (Material)AccessTools.Field(Controller, name).GetValue(null);

        private static void StartCutscene()
        {
            Traverse fields = Fields;
            List<IntVec3> cells = design.cells.Select(c => root + c).ToList();
            CellRect shipRect = CellRect.FromCellList(cells);
            CellRect curtain = shipRect.ExpandedBy(15);
            fields.Field("map").SetValue(map);
            fields.Field("gravship").SetValue(null);
            fields.Field("isTakeoff").SetValue(false);
            fields.Field("isPollutedLanding").SetValue(false);
            fields.Field("terrainCurtainBounds").SetValue(curtain);
            CutsceneFlag.SetValue(null, true);
            CameraJumper.TryJump(new GlobalTargetInfo(shipRect.CenterCell, map), CameraJumper.MovementMode.Cut);
            Find.ScreenshotModeHandler.Active = true;
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (curtain.Contains(pawn.Position))
                {
                    pawn.pather.ResetToCurrentPosition();
                }
            }

            WorldComponent_GravshipController.DestroyTreesAroundSubstructure(map, new HashSet<IntVec3>(cells));
            if (Transparent(map.Biome))
            {
                GravshipCapturer.CaptureWorldSkybox(skybox => Guard(() =>
                {
                    GravshipRenderer.MatTerrainCurtain.SetTexture(BackgroundTex, skybox);
                    PanThenCapture(curtain);
                }));
            }
            else
            {
                PanThenCapture(curtain);
            }
        }

        private static void PanThenCapture(CellRect curtain)
        {
            CameraDriver camera = Find.CameraDriver;
            if (camera.config.gravshipPanOnCutsceneStart)
            {
                camera.PanToMapLocAndSize(curtain.CenterVector3, camera.config.sizeRange.max, 1f, () => Guard(() => CaptureTerrain(curtain)));
            }
            else
            {
                CaptureTerrain(curtain);
            }
        }

        private static void CaptureTerrain(CellRect curtain)
        {
            stage = "terrain capture";
            Traverse fields = Fields;
            Material shadowMaterial = ControllerMaterial("GravshipShadowMaskTerrainCurtain");
            fields.Field("terrainCurtainIndoorMask").SetValue(SectionLayer_IndoorMask.BakeGravshipIndoorMesh(map, curtain, curtain.Area, ControllerMaterial("IndoorMaskTerrainCurtain"), curtain.CenterVector3));
            LayerSubMesh shadowMask = Transparent(map.Biome)
                ? SectionLayer_GravshipMask.BakeGravshipShadowMask(map, shadowMaterial, curtain)
                : SectionLayer_GravshipMask.BakeDummyShadowMask(shadowMaterial, curtain.CenterVector3 + Vector3.up * AltitudeLayer.MetaOverlays.AltitudeFor(), curtain.Width, curtain.Height);
            GravshipCapturer capturer = fields.Field("gravshipCapturer").GetValue<GravshipCapturer>();
            capturer.BeginTerrainRender(curtain, terrainCapture => Guard(() =>
            {
                Fields.Field("terrainCapture").SetValue(terrainCapture);
                Fields.Field("forceDrawTerrainCurtain").SetValue(true);
                LongEventHandler.QueueLongEvent(() => Guard(PlaceForCapture), "PreparingForLanding", false, null, true, false, () => Guard(() => CaptureShip(shadowMask)));
            }));
        }

        private static void PlaceForCapture()
        {
            stage = "placing";
            previousMaskEngine = SectionLayer_GravshipMask.Engine;
            SectionLayer_GravshipMask.OverrideMode = SectionLayer_GravshipMask.MaskOverrideMode.Gravship;
            PlaceNow();
            if (engine == null)
            {
                return;
            }

            HoldWholeShip();
            SectionLayer_GravshipMask.Engine = engine;
            RegenerateMask.Invoke(Find.GravshipController, null);
        }

        private static void CaptureShip(LayerSubMesh shadowMask)
        {
            if (!Active)
            {
                return;
            }

            if (engine == null)
            {
                lastMode = "instant (the ship's engine could not be placed)";
                Abort();
                return;
            }

            stage = "ship capture";
            Building_GravEngine made = engine;
            Map landingMap = map;
            Traverse fields = Fields;
            Gravship ship = MakeRenderShip(made);
            Find.ScreenshotModeHandler.Active = true;
            fields.Field("gravship").SetValue(ship);
            fields.Field("gravshipRenderer").GetValue<GravshipRenderer>().Init(landingMap);
            fields.Field("gravshipCapturer").GetValue<GravshipCapturer>().BeginGravshipRender(made, capture => Guard(() =>
            {
                Traverse now = Fields;
                HashSet<IntVec3> valid = capture.engine.ValidSubstructure;
                ship.capture = capture;
                ship.bakedIndoorMasks.Clear();
                ship.bakedIndoorMasks.Add(SectionLayer_IndoorMask.BakeGravshipIndoorMesh(landingMap, valid, valid.Count, ControllerMaterial("IndoorMaskGravship"), capture.captureCenter));
                ship.bakedIndoorMasks.AddRange(SectionLayer_GravshipHull.BakeGravshipIndoorMesh(landingMap, capture.captureBounds, capture.captureCenter));
                now.Field("terrainCurtainShadowMask").SetValue(shadowMask);
                SectionLayer_GravshipMask.OverrideMode = SectionLayer_GravshipMask.MaskOverrideMode.None;
                RegenerateMask.Invoke(Find.GravshipController, null);
                now.Field("forceDrawTerrainCurtain").SetValue(false);
                stage = "descending";
                now.Field("timeLeft").SetValue(10f);
                Find.CameraDriver.shaker.DoShake(0.2f, 120);
                Vector3 enginePos = made.Position.ToVector3();
                now.Field("gravshipRenderer").GetValue<GravshipRenderer>().BeginCutscene(ship, enginePos + ship.engineToCenter, enginePos, Rot4.North);
                now.Field("gravshipAudio").GetValue<GravshipAudio>().BeginLanding();
            }));
        }

        // 렌더러가 읽는 값만 채운 Gravship. 세계 지도에 올리지 않고 컷신이 끝나면 버린다.
        private static Gravship MakeRenderShip(Building_GravEngine made)
        {
            Gravship ship = (Gravship)Activator.CreateInstance(typeof(Gravship), true);
            Traverse fields = Traverse.Create(ship);
            Dictionary<Thing, PositionData> thrusters = fields.Field("thrusters").GetValue<Dictionary<Thing, PositionData>>();
            List<IntVec3> extenders = fields.Field("gravFieldExtenderPositions").GetValue<List<IntVec3>>();
            fields.Field("engine").SetValue(made);
            HashSet<IntVec3> valid = made.ValidSubstructure;
            Building console = null;
            foreach (IntVec3 c in valid)
            {
                foreach (Thing thing in made.Map.thingGrid.ThingsListAtFast(c))
                {
                    if (thing.Position != c)
                    {
                        continue;
                    }

                    IntVec3 offset = c - made.Position;
                    if (thing.TryGetComp(out CompGravshipThruster thruster) && thruster.CanBeActive && !thrusters.ContainsKey(thing))
                    {
                        thrusters.Add(thing, new PositionData(offset, thing.Rotation));
                        ship.launchDirection += thing.Rotation.AsIntVec3 * thruster.Props.directionInfluence;
                    }

                    if (thing.def == ThingDefOf.GravFieldExtender)
                    {
                        extenders.Add(offset);
                    }

                    if (thing.def == ThingDefOf.PilotConsole)
                    {
                        console = (Building)thing;
                    }
                }
            }

            if (ship.launchDirection == IntVec3.Zero)
            {
                ship.launchDirection = console != null ? console.Rotation.AsIntVec3 : IntVec3.North;
            }

            ship.originalPosition = made.Position;
            ship.engineToCenter = CellRect.FromCellList(valid).CenterVector3 - made.Position.ToVector3();
            return ship;
        }

        // 바닐라 LandingEnded 대신 부른다. 플레이어 배의 착륙 뒤처리(착륙 사고, 시나리오 알림, 여행 중인 배 지우기)는 하지 않는다.
        public static void Finish()
        {
            WorldComponent_GravshipController controller = Find.GravshipController;
            Traverse fields = Fields;
            if (engine != null && engine.Spawned)
            {
                SectionLayer_GravshipMask.Engine = engine;
                SectionLayer_GravshipMask.OverrideMode = SectionLayer_GravshipMask.MaskOverrideMode.Gravship;
                RegenerateMask.Invoke(controller, null);
            }

            SectionLayer_GravshipMask.OverrideMode = SectionLayer_GravshipMask.MaskOverrideMode.None;
            SectionLayer_GravshipMask.Engine = previousMaskEngine;
            previousMaskEngine = null;
            fields.Field("terrainCapture").SetValue(null);
            fields.Field("gravship").SetValue(null);
            fields.Field("terrainCurtainIndoorMask").SetValue(null);
            fields.Field("terrainCurtainShadowMask").SetValue(null);
            fields.Field("forceDrawTerrainCurtain").SetValue(false);
            fields.Field("map").SetValue(null);
            Find.CameraDriver.shaker.StopAllShaking();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            ResetCutscene.Invoke(controller, null);
            stage = stage == "descending" ? "finished" : "stopped at " + stage;
            Active = false;
            ReleaseWholeShip();
            Complete();
        }

        private static void Abort()
        {
            if (!Active)
            {
                return;
            }

            PlaceNow();
            Finish();
        }

        public static void Update()
        {
            if (Active && Time.realtimeSinceStartup - startedAt > TimeoutSeconds)
            {
                Log.Error("[Yuudong2Mod] enemy gravship cutscene did not finish in time, landing without it.");
                lastMode = "instant (cutscene timed out)";
                Abort();
            }
        }
    }

    public class GameComponent_EnemyGravship : GameComponent
    {
        public GameComponent_EnemyGravship(Game game)
        {
        }

        public override void FinalizeInit()
        {
            if (EnemyGravshipUtility.Active)
            {
                ShipLibrary.EnsureScanStarted();
                MultiGravshipUtility.RelinkAllMaps();
            }
        }

        public override void GameComponentUpdate()
        {
            EnemyGravshipLanding.Update();
        }
    }

    // 맵에 중력 엔진이 둘 이상이면 한 엔진의 하부 구조를 계산하다가 확장기를 거쳐 다른 엔진의 계산이 끼어들고, 바닐라가 "Nested FloodFill" 오류를 낸다.
    // 끼어든 쪽은 이번에는 이전 결과를 쓰고, 다음에 따로 계산한다.
    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(Building_GravEngine), "UpdateSubstructureIfNeeded")]
    internal static class Building_GravEngine_UpdateSubstructureIfNeeded_EnemyGravship_Patch
    {
        private static int depth;

        private static bool Prefix(Building_GravEngine __instance, out bool __state)
        {
            __state = false;
            if (EnemyGravshipLanding.HoldsSubstructure(__instance))
            {
                return false;
            }

            if (depth > 0 && __instance.substructureDirty)
            {
                return false;
            }

            __state = true;
            depth++;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state)
            {
                depth--;
            }
        }
    }

    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(WorldComponent_GravshipController), "LandingEnded")]
    internal static class WorldComponent_GravshipController_LandingEnded_EnemyGravship_Patch
    {
        private static bool Prefix()
        {
            if (!EnemyGravshipLanding.Active)
            {
                return true;
            }

            EnemyGravshipLanding.Finish();
            return false;
        }
    }

    [HarmonyPatchCategory(Features.EnemyGravship)]
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "TryExecuteWorker")]
    internal static class IncidentWorker_RaidEnemy_TryExecuteWorker_EnemyGravship_Patch
    {
        private static bool Prefix(IncidentWorker_RaidEnemy __instance, IncidentParms parms, ref bool __result)
        {
            Yuudong2Settings settings = Yuudong2Settings.Current;
            if (!EnemyGravshipUtility.Eligible(__instance, parms) || !Rand.Chance(settings.enemyGravshipChance / 100f))
            {
                return true;
            }

            if (!EnemyGravshipUtility.TryStartRaid(__instance, parms, settings.enemyGravshipCutscene))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }
}
