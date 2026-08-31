using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 分帧地图生成进度提示（2026-08 用户需求："地图加载过程中应该让左上角显示进度"）。
    /// <see cref="IncrementalMapGenerator"/> 分帧生成期间不暂停游戏、无进度画面——玩家只看到
    /// 接缝对侧长时间空白。本 GameComponent 在左上角常驻显示"地图生成中 {步数/步骤}"，
    /// 生成结束后自动消失。GameComponent 子类由 GameComponentUtility 自动实例化（无需注册）。
    ///
    /// 同步重操作覆盖（2026-08-31）：快照精简重生成（<see cref="SeamlessSnapshotRegenerator"/>）、
    /// 原版同步生成逃生通道与 POI 原生生成（GenerateTileMap 两分支）都是**单帧冻结**——冻结帧内
    /// OnGUI 无从重绘，标签必须在入队时（<see cref="SeamlessTilePreloader.QueuePreload"/>，重操作
    /// 前一帧）就点亮，冻结期间屏幕保持的正是带标签的上一帧画面。显示优先级：
    /// ActiveSyncOpLabel（同步操作 try/finally 维护的具体文案）> 分帧步数 > 队列占位文案；
    /// QueuedGeneration 在"队列空 && 无分帧生成 && 无同步操作"时自动熄灭（GameComponentTick）。
    /// </summary>
    public class MapGenerationProgressUI : GameComponent
    {
        /// <summary>有预加载请求已入队（QueuePreload 时置位）：从入队到全部生成结束显示进度占位。</summary>
        internal static bool QueuedGeneration;

        /// <summary>当前同步重操作的具体文案（BeginSyncOp/EndSyncOp 成对维护，null = 无）。</summary>
        internal static string ActiveSyncOpLabel;

        /// <summary>QueuePreload 入队时调用：点亮进度提示（早于重操作至少一帧渲染）。</summary>
        internal static void NotifyQueued() => QueuedGeneration = true;

        /// <summary>同步重操作开始（try 与 EndSyncOp 的 finally 成对；label 为已翻译文案）。</summary>
        internal static void BeginSyncOp(string label) => ActiveSyncOpLabel = label;

        /// <summary>同步重操作结束（finally 调用，无条件清除）。</summary>
        internal static void EndSyncOp() => ActiveSyncOpLabel = null;

        public MapGenerationProgressUI(Game game) { }

        public override void GameComponentTick()
        {
            if (QueuedGeneration && !SeamlessTilePreloader.HasPendingRequests
                && !IncrementalMapGenerator.IsAnyGenerating && ActiveSyncOpLabel == null)
            {
                QueuedGeneration = false;
            }
        }

        public override void GameComponentOnGUI()
        {
            if (ActiveSyncOpLabel != null)
            {
                Draw(ActiveSyncOpLabel);
                return;
            }
            if (!QueuedGeneration) return;
            if (IncrementalMapGenerator.IsAnyGenerating)
            {
                var progress = IncrementalMapGenerator.GenerationProgressDescription;
                if (progress != null) Draw("RimExodus_MapGeneratingProgress".Translate(progress));
            }
            else
            {
                Draw("RimExodus_MapGeneratingPending".Translate());
            }
        }

        private static void Draw(string label)
        {
            var rect = new Rect(10f, 10f, 460f, 26f);
            GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.8f);
            GUI.DrawTexture(rect, BaseContent.WhiteTex);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect.ContractedBy(3f), label);
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
