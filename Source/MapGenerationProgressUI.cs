using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 分帧地图生成进度提示（2026-08 用户需求："地图加载过程中应该让左上角显示进度"）。
    /// <see cref="IncrementalMapGenerator"/> 分帧生成期间不暂停游戏、无进度画面——玩家只看到
    /// 接缝对侧长时间空白。本 GameComponent 在左上角常驻显示"地图生成中 {步数/步骤}"，
    /// 生成结束后自动消失。GameComponent 子类由 GameComponentUtility 自动实例化（无需注册）。
    /// 注意：仅覆盖分帧增量路径（地块图预加载）；Settlement 原生生成是同步单帧冻结，UI 无从刷新。
    /// </summary>
    public class MapGenerationProgressUI : GameComponent
    {
        public MapGenerationProgressUI(Game game) { }

        public override void GameComponentOnGUI()
        {
            if (!IncrementalMapGenerator.IsAnyGenerating) return;
            var progress = IncrementalMapGenerator.GenerationProgressDescription;
            if (progress == null) return;

            var rect = new Rect(10f, 10f, 460f, 26f);
            GUI.color = new Color(0.08f, 0.08f, 0.08f, 0.8f);
            GUI.DrawTexture(rect, BaseContent.WhiteTex);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect.ContractedBy(3f), "RimExodus_MapGeneratingProgress".Translate(progress));
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
