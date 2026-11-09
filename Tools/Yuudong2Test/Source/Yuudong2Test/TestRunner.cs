using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Test
{
    // 게임을 -y2test로 켰을 때만 동작한다. 세이브(autostart)를 불러오면 틱을 돌리고, 검사 결과를 보고서로 남긴 뒤 게임을 끈다.
    // 옵션: -y2ticks=<틱 수>(기본 5000), -y2report=<보고서 경로>(기본 저장 폴더/Yuudong2Test.txt)
    [StaticConstructorOnStartup]
    public static class TestRunner
    {
        public static readonly bool Active = GenCommandLine.CommandLineArgPassed("y2test");
        public static readonly List<string> LoadErrors = new List<string>();
        public static readonly List<string> TickErrors = new List<string>();
        public static readonly List<string> TickWarnings = new List<string>();
        public static bool ticking;

        static TestRunner()
        {
            if (!Active)
            {
                return;
            }

            Harmony harmony = new Harmony("yuudong2.yuudong2test");
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OnError)));
            harmony.Patch(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(TestRunner), nameof(OnWarning)));
            Log.Message("[Yuudong2Test] test mode on");
        }

        public static int TicksToRun => GenCommandLine.TryGetCommandLineArg("y2ticks", out string value) && int.TryParse(value, out int n) ? n : 5000;

        public static string ReportPath => GenCommandLine.TryGetCommandLineArg("y2report", out string value) && !value.NullOrEmpty()
            ? value
            : Path.Combine(GenFilePaths.SaveDataFolderPath, "Yuudong2Test.txt");

        private static void OnError(string text)
        {
            (ticking ? TickErrors : LoadErrors).Add(text);
        }

        private static void OnWarning(string text)
        {
            if (ticking)
            {
                TickWarnings.Add(text);
            }
        }
    }

    public class GameComponent_Yuudong2Test : GameComponent
    {
        private const int TicksPerFrame = 250;

        private bool loaded;
        private bool finished;
        private int ticksDone;
        private DateTime tickStart;

        public GameComponent_Yuudong2Test(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (TestRunner.Active)
            {
                loaded = true;
            }
        }

        public override void GameComponentUpdate()
        {
            if (!loaded || finished || LongEventHandler.AnyEventNowOrWaiting)
            {
                return;
            }

            if (ticksDone == 0)
            {
                TestRunner.ticking = true;
                tickStart = DateTime.UtcNow;
            }

            int target = TestRunner.TicksToRun;
            for (int i = 0; i < TicksPerFrame && ticksDone < target; i++)
            {
                Find.TickManager.DoSingleTick();
                ticksDone++;
            }

            if (ticksDone >= target)
            {
                finished = true;
                TestRunner.ticking = false;
                WriteReport((DateTime.UtcNow - tickStart).TotalSeconds);
                Root.Shutdown();
            }
        }

        private void WriteReport(double seconds)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Yuudong2Mod test report");
            sb.AppendLine($"ticks: {ticksDone} in {seconds:F1}s ({(seconds > 0 ? ticksDone / seconds : 0):F0} TPS), game tick now {Find.TickManager.TicksGame}");
            sb.AppendLine();
            Section(sb, "load errors", TestRunner.LoadErrors);
            Section(sb, "errors while ticking", TestRunner.TickErrors);
            Section(sb, "warnings while ticking", TestRunner.TickWarnings);
            foreach ((string name, Action<StringBuilder> probe) in Probes.All)
            {
                sb.AppendLine("## " + name);
                try
                {
                    probe(sb);
                }
                catch (Exception e)
                {
                    sb.AppendLine("PROBE FAILED: " + e);
                }

                sb.AppendLine();
            }

            string path = TestRunner.ReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log.Message("[Yuudong2Test] report written: " + path);
        }

        // 같은 메시지는 한 번만, 횟수와 함께 적는다. 스택 트레이스는 첫 줄 몇 개만 남긴다.
        private static void Section(StringBuilder sb, string title, List<string> messages)
        {
            sb.AppendLine($"## {title}: {messages.Count}");
            foreach (IGrouping<string, string> group in messages.GroupBy(m => m).OrderByDescending(g => g.Count()).Take(30))
            {
                string[] lines = group.Key.Split('\n');
                sb.AppendLine($"- x{group.Count()} {string.Join("\n    ", lines.Take(6)).TrimEnd()}");
            }

            sb.AppendLine();
        }
    }
}
