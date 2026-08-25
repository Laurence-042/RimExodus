using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 【实验分支】分帧生成期间，generating map 不参与主线程 tick/渲染。
    /// patch Map.MapPreTick/MapPostTick/MapUpdate 三个入口，对 generating map 早退。
    /// 这样玩家可继续操作其他 map，generating map 的 grid/region 在分帧 genStep 中逐步构建，不被 tick 干扰。
    ///
    /// 注意：全局 TickList（TickManager）仍可能 tick 到 generating map 上 spawn 的 Thing
    /// （RegisterAllTickAbilityFor 是全局的）。但生成期间 spawn 的 Thing 主要是岩石/植物/动物，
    /// 短暂 tick 影响极小（岩石/植物 tick 只生长，动物 AI 在 region 未建时寻路失败但不崩）。
    /// 若实测有问题，再 patch TickList 过滤 generating map 的 Thing。
    /// </summary>
    public static class Patches_IncrementalMapGen
    {
        [HarmonyPatch(typeof(Map), nameof(Map.MapPreTick))]
        static class Patch_Map_MapPreTick
        {
            static bool Prefix(Map __instance)
            {
                return !IncrementalMapGenerator.IsGenerating(__instance); // generating map 跳过 tick。
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapPostTick))]
        static class Patch_Map_MapPostTick
        {
            static bool Prefix(Map __instance)
            {
                return !IncrementalMapGenerator.IsGenerating(__instance);
            }
        }

        [HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
        static class Patch_Map_MapUpdate
        {
            static bool Prefix(Map __instance)
            {
                return !IncrementalMapGenerator.IsGenerating(__instance); // generating map 跳过渲染更新。
            }
        }

        /// <summary>
        /// 手动存档在对话框层拦截（2026-08-25 补）：`Dialog_SaveFileList_Save.DoFileInteraction` 在
        /// 调用 SaveGame 后**无条件**弹 "SavedAs" 左上角消息并 Close——只在 SaveGame 深处拦截会出现
        /// "显示存档成功、实际未存"的矛盾反馈。此处对存档对话框（`is Dialog_SaveFileList_Save`，
        /// 方法声明在基类、勿影响加载/蓝图等其他子类对话框）整方法跳过：不出假成功消息、不关
        /// 对话框，玩家等几秒生成完成后原地重按即可。自动存档不经过此对话框，仍由 SaveGame Prefix
        /// 兜底（拒绝后下一周期自然重试）。
        /// </summary>
        [HarmonyPatch(typeof(Dialog_SaveFileList_Save), "DoFileInteraction", new[] { typeof(string) })] // protected override 声明在此类上，字符串名+显式参数类型绑定（只影响存档对话框）
        static class Patch_SaveDialog_DoFileInteraction_BlockDuringGeneration
        {
            static bool Prefix(Dialog_SaveFileList_Save __instance, string mapName)
            {
                if (!IncrementalMapGenerator.IsAnyGenerating) return true;
                Log.Warning($"[RimExodus] Manual save '{mapName}' rejected at save dialog: incremental map generation in progress " +
                            $"({IncrementalMapGenerator.GenerationProgressDescription}).");
                SendRejectionLetter();
                return false;
            }
        }

        private static void SendRejectionLetter()
        {
            Find.LetterStack.ReceiveLetter(LetterMaker.MakeLetter(
                "RimExodus.SaveBlockedDuringGenerationLabel".Translate(),
                "RimExodus.SaveBlockedDuringGeneration".Translate(),
                LetterDefOf.ThreatBig));
        }

        /// <summary>
        /// 分帧生成期间拒绝存档（2026-08-25 修复"生成中途存档 → 读档后半成品图雪崩"）。
        ///
        /// 根因：半成品地图会被整体存进档（Game.ExposeData 序列化 Find.Maps 全列表），但其
        /// RimExodus_SeamlessTileMap parent 在 onComplete（生成完成）才 WorldObjects.Add——存档时
        /// 即警告 "WorldObject_... is referenced (xml node: parent) but is not deep-saved"；读档后
        /// 该图 parent == null → Map.TileInfo → WorldGrid[invalid tile] 越界，VacuumComponent/温度/
        /// 植物 tick/FinalizeLoading 全线异常，且新进程里增量生成 static 为空、无人恢复也无人丢弃这张图。
        ///
        /// 为什么不是"读档后续跑/修复"而是拒绝存档：genStep 间的共享数据（Elevation/Fertility 等
        /// GenGenFloatGrid）挂在 MapGenerator 进程级 static `data` 字典里、刻意不序列化，"恢复生成"
        /// 结构性不可行；把 parent 提前登记也救不了（图内容仍是半成品：fog 未揭、plants 半铺、
        /// FinalizeInit 未跑）。唯一正确语义 = 生成期间不可存档。手动/自动存档都汇于
        /// GameDataSaveLoader.SaveGame 单一入口（自动存档被拒后下一周期自然重试，生成 wall
        /// 约 10s ≪ 自动存档间隔，无饿死风险）。用户可见提示用 ThreatBig 信件（左上角消息条
        /// 不显眼被用户否决；手动存档另有对话框层拦截，防止 "SavedAs" 假成功消息）。
        /// </summary>
        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
        static class Patch_SaveGame_BlockDuringIncrementalGeneration
        {
            static bool Prefix(string fileName)
            {
                if (!IncrementalMapGenerator.IsAnyGenerating) return true;
                Log.Warning($"[RimExodus] Save '{fileName}' rejected: incremental map generation in progress " +
                            $"({IncrementalMapGenerator.GenerationProgressDescription}). Retry after generation completes.");
                SendRejectionLetter();
                return false;
            }
        }
    }
}
