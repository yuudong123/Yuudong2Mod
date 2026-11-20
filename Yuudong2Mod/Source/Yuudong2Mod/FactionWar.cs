using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    [DefOf]
    public static class FactionWarDefOf
    {
        public static WorldObjectDef Yuudong2_WarArmy;

        static FactionWarDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(FactionWarDefOf));
        }
    }

    public static class FactionWarUtility
    {
        public static bool Active => Yuudong2Mod.AppliedFeatures.Contains(Features.FactionWar);

        public static WorldComponent_FactionWar Comp => Find.World?.GetComponent<WorldComponent_FactionWar>();

        // 전쟁에 끼는 세력: 정착지를 가질 수 있는 사람 세력. 숨은 세력(기계, 곤충)과 퀘스트용 임시 세력은 뺀다.
        public static bool IsWarFaction(Faction faction)
        {
            return faction != null && !faction.IsPlayer && !faction.Hidden && !faction.temporary && !faction.defeated && !faction.deactivated
                && faction.def.humanlikeFaction;
        }

        public static float TechFactor(Faction faction)
        {
            return 0.6f + 0.2f * Mathf.Clamp((int)faction.def.techLevel, 1, 7);
        }

        // 우리와 동맹이고, 우리와 적대인 세력을 치는 군대만 우리 전투에 합세한다.
        public static bool CanJoinPlayer(Faction attacker, Faction defender)
        {
            return attacker != null && defender != null && !attacker.IsPlayer && !defender.IsPlayer
                && attacker.PlayerRelationKind == FactionRelationKind.Ally && defender.HostileTo(Faction.OfPlayer);
        }

        public static bool OnSurface(WorldObject worldObject)
        {
            return worldObject.Tile.Valid && worldObject.Tile.Layer.IsRootSurface;
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.FactionWar.Intensity".Translate(s.factionWarIntensity.ToString("0.##")), -1f, "Yuudong2.FactionWar.Intensity.Desc".Translate());
            s.factionWarIntensity = Mathf.Round(listing.Slider(s.factionWarIntensity, 0.25f, 4f) * 4f) / 4f;
            listing.Label("Yuudong2.FactionWar.MaxArmies".Translate(s.factionWarMaxArmies), -1f, "Yuudong2.FactionWar.MaxArmies.Desc".Translate());
            s.factionWarMaxArmies = Mathf.RoundToInt(listing.Slider(s.factionWarMaxArmies, 1f, 20f));
            listing.Label("Yuudong2.FactionWar.MaxFactions".Translate(s.factionWarMaxFactions), -1f, "Yuudong2.FactionWar.MaxFactions.Desc".Translate());
            s.factionWarMaxFactions = Mathf.RoundToInt(listing.Slider(s.factionWarMaxFactions, 0f, 40f));
            listing.Label("Yuudong2.FactionWar.RuinDays".Translate(s.factionWarRuinDays), -1f, "Yuudong2.FactionWar.RuinDays.Desc".Translate());
            s.factionWarRuinDays = Mathf.RoundToInt(listing.Slider(s.factionWarRuinDays, 0f, 60f));
            listing.Gap();
            listing.CheckboxLabeled("Yuudong2.FactionWar.AttackPlayer".Translate(), ref s.factionWarAttackPlayer, "Yuudong2.FactionWar.AttackPlayer.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.FactionWar.ReplaceRaids".Translate(), ref s.factionWarReplaceRaids, "Yuudong2.FactionWar.ReplaceRaids.Desc".Translate());
            listing.End();
        }
    }

    // 세계 지도 위를 걸어가는 군대. 경로는 출발할 때 한 번만 구하고, 일정 틱마다 다음 타일로 옮긴다.
    public class WorldObject_WarArmy : WorldObject
    {
        public const int TicksPerTile = 3000;

        // 목표 맵에 플레이어가 들어가 있으면 이 간격으로 다시 확인하며 기다린다.
        public const int ArrivalRetryTicks = 2500;

        public const int MaxWaitTicks = GenDate.TicksPerDay * 3;

        public WorldObject target;

        public List<PlanetTile> path = new List<PlanetTile>();

        public int pathIndex;

        public int ticksOnTile;

        public int waitedTicks;

        public float strength;

        // 출발할 때 자기 세력의 정착지 한 곳 몫에 견준 군대 크기. 내 전투에 합세할 때 병력 규모가 된다.
        public float sizeFactor = DefaultSizeFactor;

        // 내가 기습해서 열린 전투. 전투가 끝날 때까지 군대는 멈춰 있다(Source/ArmyAttack.cs).
        public WorldObject_ArmyBattle battle;

        // 중력선이 이 군대를 노리고 날아오는 동안 붙잡아 두는 시한.
        public int heldUntilTick;

        public bool Engaged => (battle != null && !battle.Destroyed) || Find.TickManager.TicksGame < heldUntilTick;

        private const float DefaultSizeFactor = 0.7f;

        private Material cachedMat;

        public bool AtDestination => path == null || pathIndex >= path.Count - 1;

        public int TicksToArrival => AtDestination ? 0 : (path.Count - 1 - pathIndex) * TicksPerTile - ticksOnTile;

        public override string Label => Faction != null ? "Yuudong2.FactionWar.ArmyLabel".Translate(Faction.Name) : base.Label;

        public override Material Material
        {
            get
            {
                if (cachedMat == null)
                {
                    cachedMat = MaterialPool.MatFrom(def.texture, ShaderDatabase.WorldOverlayTransparentLit, Faction?.Color ?? Color.white, 3600);
                }
                return cachedMat;
            }
        }

        public override Color ExpandingIconColor => Faction?.Color ?? Color.white;

        public override Vector3 DrawPos
        {
            get
            {
                if (AtDestination)
                {
                    return base.DrawPos;
                }
                WorldGrid grid = Find.WorldGrid;
                Vector3 from = grid.GetTileCenter(path[pathIndex]);
                Vector3 to = grid.GetTileCenter(path[pathIndex + 1]);
                return Tile.Layer.Origin + Vector3.Slerp(from, to, Mathf.Clamp01((float)ticksOnTile / TicksPerTile));
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref target, "target");
            Scribe_Collections.Look(ref path, "path", LookMode.Value);
            Scribe_Values.Look(ref pathIndex, "pathIndex", 0);
            Scribe_Values.Look(ref ticksOnTile, "ticksOnTile", 0);
            Scribe_Values.Look(ref waitedTicks, "waitedTicks", 0);
            Scribe_Values.Look(ref strength, "strength", 0f);
            Scribe_Values.Look(ref sizeFactor, "sizeFactor", DefaultSizeFactor);
            Scribe_References.Look(ref battle, "battle");
            Scribe_Values.Look(ref heldUntilTick, "heldUntilTick", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                path ??= new List<PlanetTile>();
            }
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            WorldComponent_FactionWar comp = FactionWarUtility.Active ? FactionWarUtility.Comp : null;
            if (comp == null)
            {
                Destroy();
                return;
            }

            if (Engaged)
            {
                return;
            }

            ticksOnTile += delta;
            if (!AtDestination)
            {
                while (ticksOnTile >= TicksPerTile && !AtDestination)
                {
                    ticksOnTile -= TicksPerTile;
                    pathIndex++;
                    Tile = path[pathIndex];
                }
                if (AtDestination)
                {
                    ticksOnTile = 0;
                    comp.ArmyArrived(this);
                }
                return;
            }

            if (ticksOnTile >= ArrivalRetryTicks)
            {
                waitedTicks += ticksOnTile;
                ticksOnTile = 0;
                comp.ArmyArrived(this);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            if (Faction == null || !Faction.HostileTo(Faction.OfPlayer))
            {
                yield break;
            }

            List<Caravan> caravans = ArmyAttackUtility.CaravansInReach(this);
            Command_Action attack = new Command_Action
            {
                defaultLabel = "Yuudong2.ArmyAttack.Command".Translate(),
                defaultDesc = "Yuudong2.ArmyAttack.CommandDesc".Translate(),
                icon = ArmyAttackUtility.AttackTex,
                action = () => ArmyAttackUtility.AttackWithCaravans(this, caravans),
            };
            AcceptanceReport report = ArmyAttackUtility.CanAttack(this, ArmyAttackUtility.BattleTile(this, caravans));
            if (!report.Accepted)
            {
                attack.Disable(report.Reason);
            }
            else if (caravans.Count == 0)
            {
                attack.Disable("Yuudong2.ArmyAttack.NoCaravan".Translate());
            }

            yield return attack;
        }

        public override IEnumerable<FloatMenuOption> GetTransportersFloatMenuOptions(IEnumerable<IThingHolder> pods, System.Action<PlanetTile, TransportersArrivalAction> launchAction)
        {
            foreach (FloatMenuOption option in base.GetTransportersFloatMenuOptions(pods, launchAction))
            {
                yield return option;
            }

            foreach (FloatMenuOption option in ArmyAttackUtility.TransporterOptions(this, pods, launchAction))
            {
                yield return option;
            }
        }

        public override IEnumerable<FloatMenuOption> GetShuttleFloatMenuOptions(IEnumerable<IThingHolder> pods, System.Action<PlanetTile, TransportersArrivalAction> launchAction)
        {
            foreach (FloatMenuOption option in base.GetShuttleFloatMenuOptions(pods, launchAction))
            {
                yield return option;
            }

            foreach (FloatMenuOption option in ArmyAttackUtility.TransporterOptions(this, pods, launchAction))
            {
                yield return option;
            }
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder(base.GetInspectString());
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.AppendLine("Yuudong2.FactionWar.ArmyStrength".Translate(Mathf.RoundToInt(strength)));
            if (target != null && !target.Destroyed)
            {
                sb.AppendLine("Yuudong2.FactionWar.ArmyTarget".Translate(target.Label, target.Faction?.Name ?? "-"));
                sb.Append(AtDestination
                    ? "Yuudong2.FactionWar.ArmyWaiting".Translate()
                    : "Yuudong2.FactionWar.ArmyArrival".Translate(TicksToArrival.ToStringTicksToPeriod()));
                if (FactionWarUtility.CanJoinPlayer(Faction, target.Faction))
                {
                    sb.AppendLine();
                    sb.Append("Yuudong2.FactionWar.ArmyAllyHint".Translate());
                }
            }
            return sb.ToString().TrimEndNewlines();
        }
    }

    // 세력 전쟁 전체를 하루에 한 번 계산한다. 틱마다 하는 일은 날짜가 바뀌었는지 보는 것뿐이다.
    public class WorldComponent_FactionWar : WorldComponent
    {
        private const int CheckInterval = 2000;

        private const float PowerPerSettlement = 100f;

        private const float MaxPowerPerSettlement = 300f;

        private const float DailyPowerPerSettlement = 8f;

        private const float MinPowerToAttack = 120f;

        private const float DailyAttackChance = 0.12f;

        private const float MaxTargetDistance = 60f;

        private const float DefenseBonus = 1.3f;

        private const float MaxArmyShare = 0.5f;

        // 원정군은 본거지 수비대보다 작다: 정착지 한 곳 몫의 이 배수만큼 보낸다.
        private static readonly FloatRange ArmyGarrisonFactor = new FloatRange(0.4f, 1f);

        private const float WarMtbDays = 12f;

        private const float PeaceMtbDays = 25f;

        private const float NewFactionMtbDays = 20f;

        private const int MinDaysBetweenPlayerAttacks = 5;

        private const int MaxRaidCredit = 3;

        private const int MaxNewsLines = 30;

        // Enemy Base Scaling이 꺼져 있을 때 정착지 방어 병력의 기준 포인트(바닐라 정착지의 중간값).
        private const float VanillaSettlementPoints = 1400f;

        private const float MinAllyPoints = 300f;

        private const float FallbackRaidPawnCost = 100f;

        private const int AllyCaptureGoodwill = 20;

        private Dictionary<Faction, float> power = new Dictionary<Faction, float>();

        private Dictionary<Faction, int> lastSettlements = new Dictionary<Faction, int>();

        private List<string> news = new List<string>();

        private bool newsThreat;

        private int lastDay = -1;

        private int raidCredit;

        private int lastPlayerAttackTick = -9999999;

        // 동맹 군대가 내 전투에 합세한 정착지와 그 동맹. 내가 이기면 그 동맹이 정착지를 차지한다.
        private Dictionary<WorldObject, Faction> assists = new Dictionary<WorldObject, Faction>();

        private List<WorldObject> tmpAssistKeys;

        private List<Faction> tmpAssistValues;

        private List<Faction> tmpFactionKeys;

        private List<float> tmpPowerValues;

        private List<Faction> tmpCountKeys;

        private List<int> tmpCountValues;

        private readonly List<GlobalTargetInfo> newsTargets = new List<GlobalTargetInfo>();

        private static readonly AccessTools.FieldRef<Settlement, Material> settlementMaterial = AccessTools.FieldRefAccess<Settlement, Material>("cachedMat");

        private static readonly List<PlanetTile> tmpNeighbors = new List<PlanetTile>();

        public WorldComponent_FactionWar(World world) : base(world)
        {
        }

        public int RaidCredit => raidCredit;

        public IEnumerable<WorldObject_WarArmy> Armies => Find.WorldObjects.AllWorldObjects.OfType<WorldObject_WarArmy>();

        public float PowerOf(Faction faction)
        {
            return power.TryGetValue(faction, out float value) ? value : 0f;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref power, "power", LookMode.Reference, LookMode.Value, ref tmpFactionKeys, ref tmpPowerValues);
            Scribe_Collections.Look(ref lastSettlements, "lastSettlements", LookMode.Reference, LookMode.Value, ref tmpCountKeys, ref tmpCountValues);
            Scribe_Collections.Look(ref news, "news", LookMode.Value);
            Scribe_Values.Look(ref newsThreat, "newsThreat", false);
            Scribe_References.Look(ref gravshipTarget, "gravshipTarget");
            Scribe_Values.Look(ref lastDay, "lastDay", -1);
            Scribe_Values.Look(ref raidCredit, "raidCredit", 0);
            Scribe_Values.Look(ref lastPlayerAttackTick, "lastPlayerAttackTick", -9999999);
            Scribe_Collections.Look(ref assists, "assists", LookMode.Reference, LookMode.Reference, ref tmpAssistKeys, ref tmpAssistValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                assists ??= new Dictionary<WorldObject, Faction>();
                assists.RemoveAll(pair => pair.Key == null || pair.Value == null);
                power ??= new Dictionary<Faction, float>();
                lastSettlements ??= new Dictionary<Faction, int>();
                news ??= new List<string>();
                power.RemoveAll(pair => pair.Key == null);
                lastSettlements.RemoveAll(pair => pair.Key == null);
            }
        }

        public override void WorldComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckInterval != 0 || !FactionWarUtility.Active)
            {
                return;
            }
            int day = Find.TickManager.TicksGame / GenDate.TicksPerDay;
            if (day == lastDay)
            {
                return;
            }
            bool firstRun = lastDay < 0;
            lastDay = day;
            if (firstRun)
            {
                SyncFactions();
                return;
            }
            DailyUpdate();
        }

        public void DailyUpdate()
        {
            Yuudong2Settings settings = Yuudong2Settings.Current;
            float intensity = Mathf.Max(0.01f, settings.factionWarIntensity);
            List<Faction> factions = SyncFactions();

            foreach (Faction faction in factions)
            {
                int count = lastSettlements[faction];
                float gain = DailyPowerPerSettlement * count * FactionWarUtility.TechFactor(faction);
                power[faction] = Mathf.Min(power[faction] + gain, Mathf.Max(power[faction], MaxPowerPerSettlement * count * FactionWarUtility.TechFactor(faction)));
            }

            if (Rand.Chance(intensity / WarMtbDays))
            {
                TryDeclareWar(factions);
            }
            if (Rand.Chance(1f / PeaceMtbDays))
            {
                TryMakePeace(factions);
            }
            if (factions.Count < settings.factionWarMaxFactions && Rand.Chance(intensity / NewFactionMtbDays))
            {
                TryAddFaction();
                factions = SyncFactions();
            }

            int armies = Armies.Count();
            foreach (Faction faction in factions.InRandomOrder().ToList())
            {
                if (armies >= settings.factionWarMaxArmies)
                {
                    break;
                }
                if (power[faction] >= MinPowerToAttack && Rand.Chance(DailyAttackChance * intensity) && TryLaunchArmy(faction, null) != null)
                {
                    armies++;
                }
            }

            RemoveOldRuins(settings.factionWarRuinDays);
            FlushNews();
        }

        // 맵이 열려 있지 않은 파괴된 정착지와 버려진 정착지 표시는 생긴 지 days일이 지나면 세계 지도에서 지운다. 0이면 지우지 않는다.
        public int RemoveOldRuins(int days)
        {
            if (days <= 0)
            {
                return 0;
            }
            int newest = Find.TickManager.TicksGame - days * GenDate.TicksPerDay;
            List<DestroyedSettlement> old = Find.WorldObjects.DestroyedSettlements
                .Where(x => x.GetType() == typeof(DestroyedSettlement) && !x.HasMap && x.creationGameTicks <= newest).ToList();
            foreach (DestroyedSettlement ruins in old)
            {
                ruins.Destroy();
            }
            List<AbandonedSettlement> abandoned = Find.WorldObjects.AllWorldObjects.OfType<AbandonedSettlement>()
                .Where(x => x.GetType() == typeof(AbandonedSettlement) && x.creationGameTicks <= newest).ToList();
            foreach (AbandonedSettlement site in abandoned)
            {
                site.Destroy();
            }
            return old.Count + abandoned.Count;
        }

        // 지금 전쟁에 끼는 세력을 정리하고, 처음 보는 세력에는 힘을 준다.
        // 우리가 한 일이 아닌데 정착지가 줄었으면(플레이어가 부숨) 그 비율만큼 힘을 깎는다.
        public List<Faction> SyncFactions()
        {
            Dictionary<Faction, int> counts = new Dictionary<Faction, int>();
            List<Settlement> settlements = Find.WorldObjects.Settlements;
            for (int i = 0; i < settlements.Count; i++)
            {
                Faction faction = settlements[i].Faction;
                if (FactionWarUtility.IsWarFaction(faction) && FactionWarUtility.OnSurface(settlements[i]))
                {
                    counts.TryGetValue(faction, out int count);
                    counts[faction] = count + 1;
                }
            }

            foreach (KeyValuePair<Faction, int> pair in counts)
            {
                if (!power.ContainsKey(pair.Key))
                {
                    power[pair.Key] = PowerPerSettlement * pair.Value * FactionWarUtility.TechFactor(pair.Key);
                }
                else if (lastSettlements.TryGetValue(pair.Key, out int before) && pair.Value < before)
                {
                    power[pair.Key] *= (float)pair.Value / before;
                }
            }
            foreach (Faction gone in power.Keys.Where(f => !counts.ContainsKey(f)).ToList())
            {
                power.Remove(gone);
            }
            lastSettlements = counts;
            assists.RemoveAll(pair => pair.Key.Destroyed || !(pair.Key is MapParent parent) || !parent.HasMap);
            return counts.Keys.ToList();
        }

        private static List<Settlement> SettlementsOf(Faction faction)
        {
            return Find.WorldObjects.Settlements.Where(s => s.Faction == faction && FactionWarUtility.OnSurface(s)).ToList();
        }

        private static float ClosestDistance(Faction a, Faction b)
        {
            float best = float.MaxValue;
            List<Settlement> mine = SettlementsOf(a);
            List<Settlement> theirs = SettlementsOf(b);
            for (int i = 0; i < mine.Count; i++)
            {
                for (int j = 0; j < theirs.Count; j++)
                {
                    best = Mathf.Min(best, Find.WorldGrid.ApproxDistanceInTiles(mine[i].Tile, theirs[j].Tile));
                }
            }
            return best;
        }

        public bool TryDeclareWar(List<Faction> factions)
        {
            foreach (Faction a in factions.InRandomOrder())
            {
                foreach (Faction b in factions.InRandomOrder())
                {
                    if (a == b || a.HostileTo(b) || !a.CanChangeGoodwillFor(b, -1) || ClosestDistance(a, b) > MaxTargetDistance)
                    {
                        continue;
                    }
                    a.TryAffectGoodwillWith(b, -200, canSendMessage: false, canSendHostilityLetter: false);
                    if (a.HostileTo(b))
                    {
                        AddNews("Yuudong2.FactionWar.News.War".Translate(a.Name, b.Name));
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TryMakePeace(List<Faction> factions)
        {
            foreach (Faction a in factions.InRandomOrder())
            {
                foreach (Faction b in factions.InRandomOrder())
                {
                    if (a == b || !a.HostileTo(b) || !a.CanChangeGoodwillFor(b, 1))
                    {
                        continue;
                    }
                    a.TryAffectGoodwillWith(b, -a.BaseGoodwillWith(b), canSendMessage: false, canSendHostilityLetter: false);
                    if (!a.HostileTo(b))
                    {
                        foreach (WorldObject_WarArmy army in Armies.Where(x => (x.Faction == a && x.target?.Faction == b) || (x.Faction == b && x.target?.Faction == a)).ToList())
                        {
                            Retreat(army);
                        }
                        AddNews("Yuudong2.FactionWar.News.Peace".Translate(a.Name, b.Name));
                        return true;
                    }
                }
            }
            return false;
        }

        // 새 세력: 세계 생성 화면에서 고를 수 있는 사람 세력 종류 중 하나를 정착지 두세 곳과 함께 만든다.
        public Faction TryAddFaction()
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            List<Faction> existing = Find.FactionManager.AllFactionsListForReading;
            IEnumerable<FactionDef> defs = DefDatabase<FactionDef>.AllDefsListForReading.Where(d => !d.hidden && !d.isPlayer && d.humanlikeFaction
                && d.maxConfigurableAtWorldCreation > 0 && d.displayInFactionSelection && d.settlementGenerationWeight > 0f
                && (d.layerWhitelist.NullOrEmpty() || d.layerWhitelist.Contains(surface.Def)) && (d.layerBlacklist.NullOrEmpty() || !d.layerBlacklist.Contains(surface.Def))
                && existing.Count(f => f.def == d && !f.defeated) < d.maxConfigurableAtWorldCreation
                && (d.fixedName == null || !existing.Any(f => f.def == d)));
            if (!defs.TryRandomElementByWeight(d => d.settlementGenerationWeight, out FactionDef def))
            {
                return null;
            }

            HashSet<Faction> before = new HashSet<Faction>(existing);
            FactionGenerator.CreateFactionAndAddToManager(surface, def);
            Faction faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !before.Contains(f));
            if (faction == null)
            {
                return null;
            }
            Find.IdeoManager.SortIdeos();

            Settlement first = Find.WorldObjects.Settlements.FirstOrDefault(s => s.Faction == faction);
            if (first != null)
            {
                int extra = Rand.RangeInclusive(1, 2);
                for (int i = 0; i < extra; i++)
                {
                    PlanetTile tile = FindTileNear(first.Tile);
                    if (tile.Valid)
                    {
                        AddSettlement(faction, tile);
                    }
                }
                newsTargets.Add(first);
            }
            AddNews("Yuudong2.FactionWar.News.NewFaction".Translate(faction.Name, def.LabelCap, first?.Label ?? "-", faction.HostileTo(Faction.OfPlayer)
                ? "Yuudong2.FactionWar.Hostile".Translate() : "Yuudong2.FactionWar.NotHostile".Translate()), faction.HostileTo(Faction.OfPlayer));
            return faction;
        }

        private static Settlement AddSettlement(Faction faction, PlanetTile tile)
        {
            Settlement settlement = (Settlement)WorldObjectMaker.MakeWorldObject(tile.LayerDef.SettlementWorldObjectDef);
            settlement.SetFaction(faction);
            settlement.Tile = tile;
            settlement.Name = SettlementNameGenerator.GenerateSettlementName(settlement);
            Find.WorldObjects.Add(settlement);
            return settlement;
        }

        // 가까운 빈 정착지 자리: 이웃 타일을 몇 걸음 무작위로 걸어가 본다.
        private static PlanetTile FindTileNear(PlanetTile origin)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                PlanetTile tile = origin;
                int steps = Rand.RangeInclusive(4, 10);
                for (int i = 0; i < steps; i++)
                {
                    tmpNeighbors.Clear();
                    Find.WorldGrid.GetTileNeighbors(tile, tmpNeighbors);
                    if (tmpNeighbors.Count == 0)
                    {
                        break;
                    }
                    tile = tmpNeighbors.RandomElement();
                }
                if (tile != origin && TileFinder.IsValidTileForNewSettlement(tile) && !Find.WorldObjects.AnyWorldObjectAt(tile)
                    && Find.WorldReachability.CanReach(origin, tile))
                {
                    return tile;
                }
            }
            return PlanetTile.Invalid;
        }

        private static bool ValidTarget(Faction attacker, Settlement settlement, bool allowPlayer)
        {
            Faction owner = settlement.Faction;
            if (owner == null || owner == attacker || !FactionWarUtility.OnSurface(settlement) || !attacker.HostileTo(owner) || !settlement.questTags.NullOrEmpty())
            {
                return false;
            }
            if (owner.IsPlayer)
            {
                return allowPlayer && settlement.HasMap && settlement.Map.IsPlayerHome;
            }
            return FactionWarUtility.IsWarFaction(owner);
        }

        // 적대 세력의 가까운 정착지 하나를 골라 군대를 보낸다. forcedTarget은 검사용이다.
        public WorldObject_WarArmy TryLaunchArmy(Faction faction, Settlement forcedTarget)
        {
            List<Settlement> own = SettlementsOf(faction);
            if (own.Count == 0)
            {
                return null;
            }

            Yuudong2Settings settings = Yuudong2Settings.Current;
            bool allowPlayer = settings.factionWarAttackPlayer
                && Find.TickManager.TicksGame - lastPlayerAttackTick >= MinDaysBetweenPlayerAttacks * GenDate.TicksPerDay
                && !Armies.Any(a => a.target?.Faction != null && a.target.Faction.IsPlayer);
            HashSet<WorldObject> taken = new HashSet<WorldObject>(Armies.Select(a => a.target));

            Settlement origin = null;
            Settlement target = forcedTarget;
            if (target == null)
            {
                origin = own.RandomElement();
                List<Settlement> candidates = Find.WorldObjects.Settlements.Where(s => ValidTarget(faction, s, allowPlayer) && !taken.Contains(s)
                    && Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, s.Tile) <= MaxTargetDistance && Find.WorldReachability.CanReach(origin.Tile, s.Tile)).ToList();
                if (!candidates.TryRandomElementByWeight(s => 1f / (5f + Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, s.Tile)), out target))
                {
                    return null;
                }
            }
            else
            {
                origin = own.MinBy(s => Find.WorldGrid.ApproxDistanceInTiles(s.Tile, target.Tile));
                if (!Find.WorldReachability.CanReach(origin.Tile, target.Tile))
                {
                    return null;
                }
            }

            List<PlanetTile> nodes;
            using (WorldPath worldPath = origin.Tile.Layer.Pather.FindPath(origin.Tile, target.Tile, null))
            {
                if (!worldPath.Found)
                {
                    return null;
                }
                nodes = new List<PlanetTile>(worldPath.NodesReversed);
                nodes.Reverse();
            }

            // 세력 전체 힘의 절반은 넘지 않는다.
            float garrisonShare = power[faction] / own.Count;
            float committed = Mathf.Min(power[faction] * MaxArmyShare, garrisonShare * ArmyGarrisonFactor.RandomInRange);
            power[faction] -= committed;

            WorldObject_WarArmy army = (WorldObject_WarArmy)WorldObjectMaker.MakeWorldObject(FactionWarDefOf.Yuudong2_WarArmy);
            army.SetFaction(faction);
            army.Tile = origin.Tile;
            army.target = target;
            army.path = nodes;
            army.strength = committed;
            army.sizeFactor = committed / garrisonShare;
            Find.WorldObjects.Add(army);

            bool atPlayer = target.Faction.IsPlayer;
            if (atPlayer)
            {
                lastPlayerAttackTick = Find.TickManager.TicksGame;
            }
            newsTargets.Add(army);
            string marchKey = atPlayer ? "Yuudong2.FactionWar.News.MarchPlayer"
                : FactionWarUtility.CanJoinPlayer(faction, target.Faction) ? "Yuudong2.FactionWar.News.MarchAlly" : "Yuudong2.FactionWar.News.March";
            AddNews(marchKey.Translate(faction.Name, origin.Label, target.Label, target.Faction.Name, army.TicksToArrival.ToStringTicksToPeriod()), atPlayer);
            return army;
        }

        // 중력선이 목적지로 고른 타일에 있던 적 군대. 도착하면 그 군대와 전투가 열린다.
        public WorldObject_WarArmy gravshipTarget;

        public void ArmyDefeatedByPlayer(WorldObject_WarArmy army)
        {
            AddNews("Yuudong2.FactionWar.News.ArmyDefeated".Translate(army.Faction?.Name ?? "?"));
            army.Destroy();
        }

        private void Retreat(WorldObject_WarArmy army)
        {
            if (army.Faction != null && power.ContainsKey(army.Faction))
            {
                power[army.Faction] += army.strength;
            }
            if (!army.Destroyed)
            {
                army.Destroy();
            }
        }

        public void ArmyArrived(WorldObject_WarArmy army)
        {
            Faction attacker = army.Faction;
            WorldObject target = army.target;
            Faction defender = target?.Faction;
            if (attacker == null || attacker.defeated || target == null || target.Destroyed || defender == null || defender == attacker || !attacker.HostileTo(defender))
            {
                if (attacker != null)
                {
                    AddNews("Yuudong2.FactionWar.News.Retreat".Translate(attacker.Name));
                }
                Retreat(army);
                return;
            }

            if (defender.IsPlayer)
            {
                RaidPlayer(army, attacker, target);
                return;
            }

            Settlement settlement = target as Settlement;
            if (settlement == null || !settlement.questTags.NullOrEmpty())
            {
                AddNews("Yuudong2.FactionWar.News.Retreat".Translate(attacker.Name));
                Retreat(army);
                return;
            }
            bool canJoin = FactionWarUtility.CanJoinPlayer(attacker, defender);
            if (settlement.HasMap)
            {
                // 플레이어가 그 정착지 맵에 있다. 동맹 군대는 전투에 합세하고, 다른 군대는 결과를 미룬다.
                if (canJoin && JoinPlayerBattle(army, attacker, defender, settlement))
                {
                    return;
                }
                if (army.waitedTicks >= WorldObject_WarArmy.MaxWaitTicks)
                {
                    AddNews("Yuudong2.FactionWar.News.Retreat".Translate(attacker.Name));
                    Retreat(army);
                }
                return;
            }

            // 내 상단이 같은 정착지를 치러 오는 중이면 동맹 군대는 기다린다. 한도가 지나면 혼자 공격한다.
            if (canJoin && army.waitedTicks < WorldObject_WarArmy.MaxWaitTicks && PlayerCaravanHeadingTo(settlement.Tile))
            {
                return;
            }

            ResolveBattle(army, attacker, defender, settlement);
        }

        private static bool PlayerCaravanHeadingTo(PlanetTile tile)
        {
            List<Caravan> caravans = Find.WorldObjects.Caravans;
            for (int i = 0; i < caravans.Count; i++)
            {
                Caravan caravan = caravans[i];
                if (caravan.IsPlayerControlled && (caravan.Tile == tile || (caravan.pather.Moving && caravan.pather.Destination == tile)))
                {
                    return true;
                }
            }
            return false;
        }

        public static float DefenderPoints => Yuudong2Mod.AppliedFeatures.Contains(Features.EnemyBaseScaling) ? EnemyBaseUtility.PlayerThreatPoints() : VanillaSettlementPoints;

        // 합세 병력의 머릿수가 맵에 있는 방어 병력의 (군대 크기 배수)배가 되도록 포인트를 잡는다.
        // 습격 편성은 정착지 편성보다 병사 값이 싸서, 포인트를 그대로 견주면 머릿수가 방어 병력보다 많아진다.
        public float AllyPoints(WorldObject_WarArmy army, Faction defender, Map map)
        {
            int defenders = 0;
            List<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(defender);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (pawns[i].RaceProps.Humanlike && !pawns[i].Downed)
                {
                    defenders++;
                }
            }
            float points = defenders > 0 ? defenders * army.sizeFactor * AverageRaidPawnCost(army.Faction) : DefenderPoints * army.sizeFactor;
            return Mathf.Max(MinAllyPoints, points);
        }

        public static float AverageRaidPawnCost(Faction faction)
        {
            float weight = 0f;
            float cost = 0f;
            List<PawnGroupMaker> makers = faction.def.pawnGroupMakers;
            if (makers != null)
            {
                for (int i = 0; i < makers.Count; i++)
                {
                    if (makers[i].kindDef != PawnGroupKindDefOf.Combat || makers[i].options == null)
                    {
                        continue;
                    }
                    foreach (PawnGenOption option in makers[i].options)
                    {
                        float w = option.selectionWeight * makers[i].commonality;
                        weight += w;
                        cost += w * option.kind.combatPower;
                    }
                }
            }
            return weight > 0f ? cost / weight : FallbackRaidPawnCost;
        }

        private bool JoinPlayerBattle(WorldObject_WarArmy army, Faction attacker, Faction defender, Settlement settlement)
        {
            IncidentParms parms = new IncidentParms
            {
                target = settlement.Map,
                faction = attacker,
                points = AllyPoints(army, defender, settlement.Map),
                forced = true,
                raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn,
            };
            if (!IncidentDefOf.RaidFriendly.Worker.TryExecute(parms))
            {
                return false;
            }
            assists[settlement] = attacker;
            AddNews("Yuudong2.FactionWar.News.Joined".Translate(attacker.Name, settlement.Label));
            army.Destroy();
            return true;
        }

        // 바닐라 함락 판정 대신: 동맹이 합세한 정착지의 방어 병력을 다 물리치면 정착지를 부수지 않고 그 동맹에게 넘긴다.
        // true를 돌려주면 바닐라 판정을 건너뛴다.
        public bool TryAllyCapture(Settlement settlement, bool autoCapture)
        {
            if (!assists.TryGetValue(settlement, out Faction ally))
            {
                return false;
            }
            if (settlement.Faction == ally)
            {
                return true;
            }
            if (!autoCapture)
            {
                return false;
            }
            Faction defender = settlement.Faction;
            Map map = settlement.Map;
            if (map == null || ally == null || ally.defeated || !FactionWarUtility.CanJoinPlayer(ally, defender))
            {
                assists.Remove(settlement);
                return false;
            }
            if (!SettlementDefeatUtility.IsDefeated(map, defender))
            {
                return true;
            }

            IdeoUtility.Notify_PlayerRaidedSomeone(map.mapPawns.FreeColonistsSpawned);
            string place = settlement.Label;
            int defenderCount = Mathf.Max(1, SettlementsOf(defender).Count);
            if (power.ContainsKey(defender))
            {
                power[defender] = Mathf.Max(0f, power[defender] - power[defender] / defenderCount);
            }
            CaptureSettlement(settlement, ally);

            StringBuilder sb = new StringBuilder();
            sb.Append("Yuudong2.FactionWar.AllyCaptureText".Translate(ally.Name, defender.Name, place));
            if (defenderCount <= 1)
            {
                defender.defeated = true;
                power.Remove(defender);
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("LetterFactionBaseDefeated_FactionDestroyed".Translate(defender.Name));
            }
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (other.Hidden || other.IsPlayer || other == defender || !other.HostileTo(defender))
                {
                    continue;
                }
                FactionRelationKind before = other.PlayerRelationKind;
                if (Faction.OfPlayer.TryAffectGoodwillWith(other, AllyCaptureGoodwill, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.DestroyedEnemyBase))
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.Append("RelationsWith".Translate(other.Name) + ": " + AllyCaptureGoodwill.ToStringWithSign());
                    other.TryAppendRelationKindChangedInfo(sb, before, other.PlayerRelationKind);
                }
            }
            Find.LetterStack.ReceiveLetter("Yuudong2.FactionWar.AllyCaptureLabel".Translate(), sb.ToString(), LetterDefOf.PositiveEvent, new GlobalTargetInfo(settlement.Tile), ally);
            AddNews("Yuudong2.FactionWar.News.CapturedWithUs".Translate(ally.Name, defender.Name, place));
            SyncFactions();
            return true;
        }

        // 함께 싸우고 있는 동맹. 정착지 파괴 편지의 "동맹에게 넘기기" 선택지에 쓴다.
        public Faction AssistingAlly(Settlement settlement)
        {
            return assists.TryGetValue(settlement, out Faction ally) && ally != null && !ally.defeated && settlement.Faction != ally
                && FactionWarUtility.CanJoinPlayer(ally, settlement.Faction) ? ally : null;
        }

        // 플레이어가 이미 파괴한 정착지(맵이 열려 있는 폐허)를 동맹의 새 정착지로 바꾼다.
        public Settlement GiveRuinsToAlly(MapParent ruins, Faction ally, Faction defender, string name)
        {
            Map map = ruins?.Map;
            if (map == null || ally == null || ally.defeated)
            {
                return null;
            }
            Settlement settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            settlement.SetFaction(ally);
            settlement.Tile = ruins.Tile;
            settlement.Name = name.NullOrEmpty() ? SettlementNameGenerator.GenerateSettlementName(settlement) : name;
            Find.WorldObjects.Add(settlement);
            map.info.parent = settlement;
            ruins.Destroy();
            assists[settlement] = ally;

            Faction.OfPlayer.TryAffectGoodwillWith(ally, AllyCaptureGoodwill, canSendMessage: false, canSendHostilityLetter: false, HistoryEventDefOf.DestroyedEnemyBase);
            Messages.Message("Yuudong2.Conquest.GivenToAlly".Translate(settlement.Label, ally.Name, AllyCaptureGoodwill.ToStringWithSign()), settlement, MessageTypeDefOf.PositiveEvent, false);
            AddNews("Yuudong2.FactionWar.News.CapturedWithUs".Translate(ally.Name, defender?.Name ?? "?", settlement.Label));
            SyncFactions();
            return settlement;
        }

        private static void CaptureSettlement(Settlement settlement, Faction newOwner)
        {
            settlement.SetFaction(newOwner);
            settlementMaterial(settlement) = null;
            Find.World.renderer.Notify_StaticWorldObjectPosChanged();
        }

        private void RaidPlayer(WorldObject_WarArmy army, Faction attacker, WorldObject target)
        {
            Map map = (target as MapParent)?.Map;
            bool raided = false;
            if (map != null)
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.faction = attacker;
                parms.forced = true;
                raided = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
            }

            if (raided)
            {
                lastPlayerAttackTick = Find.TickManager.TicksGame;
                if (Yuudong2Settings.Current.factionWarReplaceRaids)
                {
                    raidCredit = Mathf.Min(raidCredit + 1, MaxRaidCredit);
                }
                AddNews("Yuudong2.FactionWar.News.RaidPlayer".Translate(attacker.Name, target.Label), true);
                army.Destroy();
            }
            else
            {
                AddNews("Yuudong2.FactionWar.News.Retreat".Translate(attacker.Name));
                Retreat(army);
            }
        }

        private void ResolveBattle(WorldObject_WarArmy army, Faction attacker, Faction defender, Settlement settlement)
        {
            int defenderCount = Mathf.Max(1, SettlementsOf(defender).Count);
            float garrison = PowerOf(defender) / defenderCount;
            float defense = garrison * DefenseBonus + 30f;
            bool win = Rand.Chance(army.strength / (army.strength + defense));
            string place = settlement.Label;
            newsTargets.Add(new GlobalTargetInfo(settlement.Tile));

            if (!win)
            {
                if (power.ContainsKey(defender))
                {
                    power[defender] = Mathf.Max(0f, power[defender] - army.strength * 0.3f);
                }
                AddNews("Yuudong2.FactionWar.News.Repelled".Translate(defender.Name, place, attacker.Name));
                army.Destroy();
                return;
            }

            if (power.ContainsKey(defender))
            {
                power[defender] = Mathf.Max(0f, power[defender] - garrison);
            }
            bool raze = Rand.Chance(attacker.def.permanentEnemy ? 0.75f : 0.4f);
            if (raze)
            {
                DestroyedSettlement ruins = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(settlement.Tile.LayerDef.DestroyedSettlementWorldObjectDef);
                ruins.Tile = settlement.Tile;
                ruins.SetFaction(defender);
                Find.WorldObjects.Add(ruins);
                settlement.Destroy();
                if (power.ContainsKey(attacker))
                {
                    power[attacker] += army.strength * 0.7f;
                }
                AddNews("Yuudong2.FactionWar.News.Razed".Translate(attacker.Name, defender.Name, place));
            }
            else
            {
                CaptureSettlement(settlement, attacker);
                if (power.ContainsKey(attacker))
                {
                    power[attacker] += army.strength * 0.5f;
                }
                AddNews("Yuudong2.FactionWar.News.Captured".Translate(attacker.Name, defender.Name, place));
            }

            if (defenderCount <= 1)
            {
                defender.defeated = true;
                power.Remove(defender);
                AddNews("Yuudong2.FactionWar.News.Defeated".Translate(defender.Name));
            }
            army.Destroy();
            SyncFactions();
        }

        public bool TryConsumeRaidCredit()
        {
            if (raidCredit <= 0 || !Yuudong2Settings.Current.factionWarReplaceRaids)
            {
                return false;
            }
            raidCredit--;
            return true;
        }

        private void AddNews(string line, bool threat = false)
        {
            if (news.Count < MaxNewsLines)
            {
                news.Add(line);
            }
            newsThreat |= threat;
        }

        // 하루치 소식을 편지 한 통으로 보낸다.
        private void FlushNews()
        {
            if (news.Count == 0)
            {
                newsTargets.Clear();
                newsThreat = false;
                return;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < news.Count; i++)
            {
                sb.AppendLine("- " + news[i]);
            }
            newsTargets.RemoveAll(t => !t.IsValid || (t.HasWorldObject && t.WorldObject.Destroyed));
            Find.LetterStack.ReceiveLetter("Yuudong2.FactionWar.NewsLabel".Translate(), sb.ToString().TrimEndNewlines(),
                newsThreat ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent, newsTargets.Count > 0 ? new LookTargets(newsTargets) : null);
            news.Clear();
            newsTargets.Clear();
            newsThreat = false;
        }
    }

    // 전쟁 군대가 내 정착지를 습격한 만큼 스토리텔러가 고른 적 습격을 건너뛴다. 퀘스트 습격은 건드리지 않는다.
    [HarmonyPatchCategory(Features.FactionWar)]
    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
    internal static class Storyteller_TryFire_FactionWar_Patch
    {
        private static bool Prefix(FiringIncident fi, ref bool __result)
        {
            if (fi?.def != IncidentDefOf.RaidEnemy || fi.source == null || fi.sourceQuestPart != null)
            {
                return true;
            }
            WorldComponent_FactionWar comp = FactionWarUtility.Comp;
            if (comp == null || !comp.TryConsumeRaidCredit())
            {
                return true;
            }
            __result = false;
            return false;
        }
    }

    // 동맹이 합세한 정착지는 함락되면 파괴되지 않고 그 동맹 것이 된다(WorldComponent_FactionWar.TryAllyCapture).
    // 정착지 점령 선택 기능이 켜져 있으면 자동으로 넘기지 않고 파괴 편지에서 고르게 한다. 이미 동맹 것이 된 정착지를 바닐라 판정에서 지키는 일은 그대로 한다.
    [HarmonyPatchCategory(Features.FactionWar)]
    [HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
    internal static class SettlementDefeatUtility_CheckDefeated_FactionWar_Patch
    {
        private static bool Prefix(Settlement factionBase)
        {
            WorldComponent_FactionWar comp = FactionWarUtility.Comp;
            return comp == null || !comp.TryAllyCapture(factionBase, !Yuudong2Mod.AppliedFeatures.Contains(Features.SettlementConquest));
        }
    }

    // 세계 생성 화면의 세력 수 제한(12)을 푼다. 제한은 "세력 추가" 버튼의 검사 함수 안에 숫자로 박혀 있다.
    [HarmonyPatchCategory(Features.NoFactionLimit)]
    [HarmonyPatch]
    internal static class WorldFactionsUIUtility_NoFactionLimit_Patch
    {
        private const int VanillaLimit = 12;

        private const int NewLimit = 9999;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            List<MethodBase> found = new List<MethodBase>();
            List<System.Type> types = new List<System.Type> { typeof(WorldFactionsUIUtility) };
            types.AddRange(typeof(WorldFactionsUIUtility).GetNestedTypes(AccessTools.all));
            foreach (System.Type type in types)
            {
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method.Name.Contains("CanAddFaction"))
                    {
                        found.Add(method);
                    }
                }
            }
            if (found.Count == 0)
            {
                Log.Error("[Yuudong2Mod] NoFactionLimit: could not find the faction count check in WorldFactionsUIUtility. The game code changed; update the patch target.");
            }
            return found;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte small && small == VanillaLimit)
                    || (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int big && big == VanillaLimit))
                {
                    instruction.opcode = OpCodes.Ldc_I4;
                    instruction.operand = NewLimit;
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced == 0)
            {
                Log.Error("[Yuudong2Mod] NoFactionLimit: the faction limit constant 12 was not found in the faction count check. The game code changed; update the patch.");
            }
        }
    }
}
