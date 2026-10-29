$ErrorActionPreference = 'Stop'
# 编译实际排序代码；不加载 Unity。运行：pwsh -File tests/EvacuationRouting.Tests.ps1
$source = Get-Content (Join-Path $PSScriptRoot '../Source/Transfer/SeamlessEvacuationExitScore.cs') -Raw
Add-Type -TypeDefinition ($source + @'
namespace RimExodus
{
    public static class EvacuationRoutingTests
    {
        private static SeamlessEvacuationExitScore S(long progress, int cost, bool visited = false)
            => new SeamlessEvacuationExitScore(progress, cost, visited);

        private static void Prefer(string scenario, SeamlessEvacuationExitScore expected,
            SeamlessEvacuationExitScore other)
        {
            if (expected.CompareTo(other) >= 0 || other.CompareTo(expected) <= 0)
                throw new System.Exception(scenario);
        }

        public static void Run()
        {
            Prefer("Open ground: forward beats a cheap seam corner", S(100, 1300), S(5, 300));
            Prefer("Obstacle: useful forward detour beats expensive straight exit", S(60, 1200), S(100, 4000));
            Prefer("Any reachable forward exit precedes sideways fallback", S(1, 4000), S(0, 13));
            Prefer("No forward route: choose cheaper unvisited fallback", S(-10, 130), S(0, 1000));
            Prefer("Never revisit while an unvisited fallback exists", S(-10, 1300), S(100, 130, true));
            Prefer("Only visited exits remain: cheapest force-exit", S(-10, 130, true), S(100, 1300, true));
            Prefer("Equal efficiency: make more progress", S(100, 1300), S(10, 130));
            Prefer("No initial direction: choose by cost", S(0, 130), S(0, 260));
            Prefer("Direction scaling preserves choice", S(700, 1300), S(35, 300));
            Prefer("Cross products exceed Int32 safely", S(100000, 2000000), S(90000, 2000000));

            // 排序必须有传递性，否则 List.Sort 可能出错或受输入顺序影响。
            var scores = new[] { S(100, 1300), S(5, 300), S(60, 1200), S(-10, 130), S(0, 20), S(100, 13, true) };
            foreach (var a in scores)
            foreach (var b in scores)
            foreach (var c in scores)
                if (a.CompareTo(b) <= 0 && b.CompareTo(c) <= 0 && a.CompareTo(c) > 0)
                    throw new System.Exception("Ordering must be transitive");
        }
    }
}
'@)
[RimExodus.EvacuationRoutingTests]::Run()
Write-Output 'PASS: 10 evacuation selection scenarios and ordering transitivity.'
