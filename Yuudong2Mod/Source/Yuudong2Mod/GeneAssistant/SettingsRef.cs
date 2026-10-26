using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod.GeneAssistant
{
    // 원본 SettingsRef와 같은 이름·멤버를 Yuudong2Mod 설정 위에 다시 만든다. 다른 원본 파일은 이 클래스를 그대로 쓴다.
    public static class SettingsRef
    {
        private const string LegacySettingsFile = "Mod_2882497271_SettingsRef.xml";

        private static HashSet<GeneDef> ignored;

        private static Yuudong2Settings S => Yuudong2Settings.Current;

        public static bool overrideSorting => S.geneOverrideSorting;
        public static bool singlesFirst => S.geneSinglesFirst;
        public static float sat => S.geneBackgroundSaturation;
        public static bool sellFromGeneBanks => S.geneSellFromGeneBanks;

        public static Color green => S.geneAlternateColors ? new Color(0f, 0f, sat, 0.6f) : new Color(0f, sat, 0f, 0.4f);
        public static Color yellow => S.geneAlternateColors ? new Color(sat * 0.4f, sat * 0.4f, sat * 0.4f, 0.6f) : new Color(sat, sat, 0f, 0.4f);
        public static Color red => S.geneAlternateColors ? new Color(0f, sat, 0f, 0.6f) : new Color(sat, 0f, 0f, 0.4f);
        public static Color gray => new Color(0.2f, 0.2f, 0.2f, 0.6f);

        public static HashSet<GeneDef> ignoredGenes => GetGeneIgnore();

        private static HashSet<string> IgnoredNames
        {
            get
            {
                if (S.geneIgnored == null)
                {
                    S.geneIgnored = ImportLegacyIgnoredGenes();
                }

                return S.geneIgnored;
            }
        }

        // 원본 모드의 설정 파일이 남아 있으면 무시 유전자 목록을 가져온다.
        public static HashSet<string> ImportLegacyIgnoredGenes()
        {
            HashSet<string> result = new HashSet<string>();
            try
            {
                string path = Path.Combine(GenFilePaths.ConfigFolderPath, LegacySettingsFile);
                if (File.Exists(path))
                {
                    XElement list = XDocument.Load(path).Root?.Element("ModSettings")?.Element("ignoredGenes");
                    if (list != null)
                    {
                        foreach (XElement li in list.Elements("li"))
                        {
                            result.Add(li.Value);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Yuudong2Mod] 이전 Gene Assistant 설정을 읽지 못했습니다: " + e.Message);
            }

            return result;
        }

        public static bool ToggleGeneIgnore(GeneDef gene)
        {
            if (GetGeneIgnore().Remove(gene))
            {
                return false;
            }

            ignored.Add(gene);
            return true;
        }

        public static void CommitGeneIgnore()
        {
            IgnoredNames.Clear();
            foreach (GeneDef gene in GetGeneIgnore())
            {
                IgnoredNames.Add(gene.defName);
            }

            S.Write();
        }

        public static void RevertGeneIgnore()
        {
            ignored = null;
            GetGeneIgnore();
        }

        public static void ResetGeneIgnore()
        {
            GetGeneIgnore().Clear();
        }

        public static void AddToGeneIgnore(HashSet<GeneDef> genes)
        {
            GetGeneIgnore().AddRange(genes);
        }

        public static void RemoveFromGeneIgnore(HashSet<GeneDef> genes)
        {
            GetGeneIgnore().RemoveWhere(genes.Contains);
        }

        public static HashSet<GeneDef> GetGeneIgnore()
        {
            if (ignored == null)
            {
                ignored = new HashSet<GeneDef>();
                foreach (GeneDef gene in GeneUtility.GenesInOrder)
                {
                    if (IgnoredNames.Contains(gene.defName))
                    {
                        ignored.Add(gene);
                    }
                }
            }

            return ignored;
        }

        public static void PatchOtherMods(Harmony harmony)
        {
            PatchTradeUIRevisited.HandlePatch(harmony);
            PatchGeneripperUI.HandlePatch(harmony);
            PatchCharacterEditor.HandlePatch(harmony);
        }

        public static void DrawSettings(Rect rect)
        {
            Yuudong2Settings s = S;
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Yuudong2.Gene.OverrideSorting".Translate(), ref s.geneOverrideSorting, "Yuudong2.Gene.OverrideSorting.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Gene.SinglesFirst".Translate(), ref s.geneSinglesFirst, "Yuudong2.Gene.SinglesFirst.Desc".Translate());
            s.geneBackgroundSaturation = listing.SliderLabeled("Yuudong2.Gene.Saturation".Translate(s.geneBackgroundSaturation.ToStringPercent()), s.geneBackgroundSaturation, 0f, 1f, 0.6f, "Yuudong2.Gene.Saturation.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Gene.AltColors".Translate(), ref s.geneAlternateColors, "Yuudong2.Gene.AltColors.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Gene.SellFromBanks".Translate(), ref s.geneSellFromGeneBanks, "Yuudong2.Gene.SellFromBanks.Desc".Translate());
            listing.Gap();
            GUI.color = Color.gray;
            listing.Label("Yuudong2.Gene.LibraryHint".Translate(IgnoredNames.Count));
            GUI.color = Color.white;
            listing.End();
        }
    }
}
