using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图公共函数层（2026-08 架构重构 Phase B，用户定夺"点击重放拿原生选项和命令 +
    /// patch 距离判断等公共函数使其跨图工作"，VMF 同构）：点击重放（Patches_ClickReplay）产出
    /// 原生选项后，命令的执行在这四个公共 chokepoint 跨图化——
    /// ① <see cref="Patch_RCellFinder_BestOrderedGotoDestNear_CrossMap"/>：队形/站位选位在
    ///    真邻图上算（虚拟传送括号），root 框架歧义用"邻图界内且近命令格"启发式消解；
    /// ② <see cref="Patch_DraftedMove_PawnGotoAction_CrossMap"/>：征召移动下令点——命令目标
    ///    在邻图 → 桥接（gotoLoc = 各自队形格，队形跨缝保留），并消灭误置 exitMapOnArrival；
    /// ③ <see cref="Patch_MultiPawnGotoController_CrossMap"/>：拖拽队形控制器的锚点归一
    ///    （StartInteraction 锚点回宿主系）与预览绘制偏移（Draw/OnGUI）——原生挂起-释放
    ///    流程不再需要任何拦截，框架一致性由 ① 保证；
    /// ④ <see cref="Patch_Pawn_PathFollower_StartPath_CrossMap"/>：一切玩家下令 job 的通用
    ///    跨图包装——目标在邻图（Thing 帧无歧义 / Cell 匹配命令登记格）→ 以 NextJob=curJob
    ///    桥接，传送后续原 job（近战/开采/砍伐/搬运/驯服等全选项通用；范围 = 仅 playerForced）。
    /// 可达性真实答案在 Patches_ReachabilityCrossMap。
    /// </summary>
    public static class Patches_CrossMapCommon
    {
    }

    // =====================================================================================
    // ① 队形/站位选位：在真邻图上算
    // =====================================================================================

    /// <summary>
    /// RCellFinder.BestOrderedGotoDestNear 的跨图括号（Prefix 虚拟传送 → 原版在邻图跑 → Postfix
    /// 恢复 + 登记）。root 框架消解：pawn 带指向邻图的命令登记时——
    /// root 已是邻图框架（DraftedMove 单选 action 的 curLoc ≈ 命令格，或重放帧内的 context 派生格）
    /// ⟺ root 在邻图界内且距命令格 ≤5；否则视为宿主框架（控制器插值根点，StartInteraction 已归一）
    /// 转 −offset。结果 = 邻图框架队形格，写回命令登记（供 PawnGotoAction 桥接取用）。
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.BestOrderedGotoDestNear))]
    public static class Patch_RCellFinder_BestOrderedGotoDestNear_CrossMap
    {
        private static SeamlessVirtualTeleporter teleport;
        private static Map engagedTarget;

        public static void Prefix(ref IntVec3 root, Pawn searcher)
        {
            engagedTarget = null;
            if (searcher?.Map == null) return;
            if (!SeamlessCommandTargets.TryGet(searcher, out var ct) || ct.map == searcher.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(searcher.Map, ct.map, out var link)) return;

            // root 框架消解（见类头）：优先"已是邻图框架"，否则按宿主框架转邻图局部。
            var rootTarget = root.InBounds(ct.map) && root.DistanceTo(ct.cell) <= 5
                ? root
                : root - link.offset;
            if (!rootTarget.InBounds(ct.map)) return;

            teleport = new SeamlessVirtualTeleporter(searcher, ct.map, rootTarget);
            engagedTarget = ct.map;
            root = rootTarget;
        }

        public static void Postfix(Pawn searcher, IntVec3 __result)
        {
            if (engagedTarget == null) return;
            teleport.Dispose();
            if (__result.IsValid)
            {
                // 队形格（邻图框架）覆盖命令登记格——PawnGotoAction 桥接时用它当各自终点。
                SeamlessCommandTargets.Set(searcher, engagedTarget, __result);
            }
            engagedTarget = null;
        }
    }

    // =====================================================================================
    // ② 征召移动下令点：命令目标在邻图 → 桥接（队形逐 pawn 保留）
    // =====================================================================================

    /// <summary>
    /// PawnGotoAction 跨图版（替代旧 NoteCrossMapClick 单槽检测）：pawn 的命令登记指向邻图
    /// → 以 gotoLoc（邻图框架的队形格，由 ① 算好登记）为终点桥接，跳过原生（原生会在本图
    /// 坐标上发 job + 误读 IsExitCell 置 exitMapOnArrival）。非跨图下令主体吞掉。
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
    public static class Patch_DraftedMove_PawnGotoAction_CrossMap
    {
        public static bool Prefix(IntVec3 clickCell, Pawn pawn, IntVec3 gotoLoc)
        {
            if (pawn?.Map == null) return true;
            if (!SeamlessCommandTargets.TryGet(pawn, out var ct) || ct.map == pawn.Map) return true;
            if (!SeamlessViewProjection.TryProject(ct.map, gotoLoc, pawn.Map, out _)) return true;

            // 不可跨图下令的主体：吞掉（对齐原版"不可对其下令移动"，不给错误的本图同坐标 job）。
            if (!SeamlessBoundaryRules.IsCrossMapOrderable(pawn)) return false;

            if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                Log.Message($"[RimExodus] Cross-map drafted goto: {pawn.LabelShort} -> {gotoLoc} on map {ct.map.uniqueID} (formation dest).");

            if (SeamlessCrossMapOrders.TryBridgeJob(pawn, ct.map, gotoLoc))
            {
                // 对齐原生 PawnGotoAction 成功路径的下令反馈（DraftedMove.cs:119 FeedbackGoto）
                // ——反馈归目标图管理，复合视图绘制阶段统一平移（与攻击的 FeedbackShoot 区分）。
                FleckMaker.Static(gotoLoc, ct.map, FleckDefOf.FeedbackGoto);
            }
            return false;
        }
    }

    // =====================================================================================
    // ③ 拖拽队形控制器：锚点归一 + 预览绘制偏移
    // =====================================================================================

    /// <summary>
    /// MultiPawnGotoController 的跨图适配（原生挂起-释放流程不拦截，只修框架与绘制）：
    /// StartInteraction Postfix——任一选中 pawn 的命令登记指向邻图时，锚点（邻图框架的
    /// StandableCellNear 结果）+offset 归一回宿主系，使 ProcessInputEvents 的 end（宿主框架
    /// 鼠标格）与插值根点框架一致；Draw/OnGUI Prefix——各 pawn 的 dests 是邻图框架队形格，
    /// +offset 回宿主屏幕坐标绘制，迷雾按归属图判。
    /// </summary>
    [HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.StartInteraction))]
    public static class Patch_MultiPawnGotoController_StartInteraction
    {
        private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> StartField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("start");

        private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> EndField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("end");

        public static void Postfix(MultiPawnGotoController __instance, IntVec3 mouseCell)
        {
            var hostMap = Find.CurrentMap;
            if (hostMap == null) return;
            foreach (var pawn in Find.Selector.SelectedPawns)
            {
                if (!SeamlessCommandTargets.TryGet(pawn, out var ct) || ct.map == hostMap) continue;
                if (!SeamlessViewProjection.TryProject(ct.map, mouseCell, hostMap, out var anchor)) continue;

                // mouseCell 是邻图框架锚点 → 宿主系（hostLocal = targetLocal + offset）。
                // 复合视图坐标允许落在宿主地图矩形外；控制器只把它用作插值/绘制锚点。
                StartField(__instance) = anchor;
                EndField(__instance) = anchor;
                return;
            }
        }
    }

    [HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.Draw))]
    [StaticConstructorOnStartup] // 静态 Material 字段（惰性反射赋值）——加特性消 Verse 启动分析器警告
    public static class Patch_MultiPawnGotoController_Draw
    {
        private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<Pawn>> PawnsField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, List<Pawn>>("pawns");

        private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<IntVec3>> DestsField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, List<IntVec3>>("dests");

        private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> StartField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("start");

        private static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> EndField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("end");

        private static Material gotoCircleMaterial;
        private static Material gotoBetweenLineMaterial;

        /// <summary>
        /// 材质惰性获取（勿在 Prepare/静态构造期反射——会过早触发 MultiPawnGotoController 的
        /// StaticConstructorOnStartup 链（ShaderDatabase/MaterialPool），PatchAll 期未就绪；
        /// 首次 Draw 时游戏已完全启动，原类静态字段早已初始化）。
        /// </summary>
        private static bool TryGetMaterials()
        {
            if (gotoCircleMaterial == null)
            {
                gotoCircleMaterial = AccessTools.Field(typeof(MultiPawnGotoController), "GotoCircleMaterial")?.GetValue(null) as Material;
                gotoBetweenLineMaterial = AccessTools.Field(typeof(MultiPawnGotoController), "GotoBetweenLineMaterial")?.GetValue(null) as Material;
            }
            return gotoCircleMaterial != null && gotoBetweenLineMaterial != null;
        }

        /// <summary>
        /// 原生 Draw 体复刻：任一 pawn 的命令登记指向邻图时接管——邻图框架 dest +offset 绘制、
        /// 迷雾按归属图判；同图 pawn 按原生坐标（混合选中共存）。起终点连线用控制器自身锚点
        /// （StartInteraction 已归一宿主系）原生绘制。
        /// </summary>
        public static bool Prefix(MultiPawnGotoController __instance)
        {
            if (!__instance.Active) return true;
            var hostMap = Find.CurrentMap;
            if (hostMap == null) return true;
            var pawns = PawnsField(__instance);
            var dests = DestsField(__instance);
            if (pawns == null || dests == null) return true;
            if (!Patch_MultiPawnGotoController_OnGUI.AnyCrossMap(pawns, hostMap)) return true; // 同图队形：原生
            if (!TryGetMaterials()) return true; // 材质未就绪：退原生（仅预览视觉，非功能）

            var s = new Vector3(1.7f, 1f, 1.7f);
            float num = Altitudes.AltitudeFor(AltitudeLayer.MetaOverlays);
            var addedAltitude = num + 0.03658537f;
            var addedAltitude2 = num - 0.03658537f;
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                var c = dests[i];
                if (!c.IsValid || pawn == null || !pawn.Spawned) continue;

                IntVec3 drawCell;
                if (SeamlessCommandTargets.TryGet(pawn, out var ct) && ct.map != hostMap
                    && SeamlessViewProjection.TryProject(ct.map, c, hostMap, out var projected))
                {
                    if (c.Fogged(ct.map)) continue; // 迷雾按归属图（本地格）判
                    drawCell = projected;
                }
                else
                {
                    if (c.Fogged(pawn.Map)) continue;
                    drawCell = c;
                }
                Vector3 drawLoc = drawCell.ToVector3ShiftedWithAltitude(num);
                pawn.Drawer.renderer.RenderPawnAt(drawLoc, Rot4.South);
                Vector3 pos = drawCell.ToVector3ShiftedWithAltitude(addedAltitude);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, s), gotoCircleMaterial, 0);
            }
            Vector3 a = StartField(__instance).ToVector3ShiftedWithAltitude(addedAltitude2);
            Vector3 b = EndField(__instance).ToVector3ShiftedWithAltitude(addedAltitude2);
            GenDraw.DrawLineBetween(a, b, gotoBetweenLineMaterial, 0.9f);
            return false;
        }
    }

    [HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.OnGUI))]
    public static class Patch_MultiPawnGotoController_OnGUI
    {
        private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<Pawn>> PawnsField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, List<Pawn>>("pawns");

        private static readonly AccessTools.FieldRef<MultiPawnGotoController, List<IntVec3>> DestsField =
            AccessTools.FieldRefAccess<MultiPawnGotoController, List<IntVec3>>("dests");

        /// <summary>原生 OnGUI 体复刻：标签画在（邻图框架 dest +offset）的屏幕位置；同图队形放行原生。</summary>
        public static bool Prefix(MultiPawnGotoController __instance)
        {
            if (!__instance.Active) return true;
            var hostMap = Find.CurrentMap;
            if (hostMap == null) return true;
            var pawns = PawnsField(__instance);
            var dests = DestsField(__instance);
            if (pawns == null || dests == null) return true;
            if (!AnyCrossMap(pawns, hostMap)) return true; // 同图队形：原生（勿在此绘制，防双画）

            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                var c = dests[i];
                if (!c.IsValid || pawn == null || !pawn.Spawned) continue;
                var drawCell = c;
                if (SeamlessCommandTargets.TryGet(pawn, out var ct) && ct.map != hostMap
                    && SeamlessViewProjection.TryProject(ct.map, c, hostMap, out var projected))
                {
                    drawCell = projected;
                }
                Rect rect = drawCell.ToUIRect();
                Vector2 pos = new Vector2(rect.center.x, rect.yMax + 5f);
                GenMapUI.DrawPawnLabel(pawn, pos, 0.5f);
            }
            return false;
        }

        internal static bool AnyCrossMap(List<Pawn> pawns, Map hostMap)
        {
            foreach (var pawn in pawns)
            {
                if (pawn != null && SeamlessCommandTargets.TryGet(pawn, out var ct) && ct.map != hostMap) return true;
            }
            return false;
        }
    }

    // =====================================================================================
    // ④ 玩家下令 job 的通用跨图包装
    // =====================================================================================

    /// <summary>
    /// Pawn_PathFollower.StartPath 的跨图包装（VMF Patch_Pawn_PathFollower_StartPath 同构）：
    /// playerForced job 的路径目标在邻图（Thing 目标帧无歧义；Cell 目标匹配命令登记格），或
    /// NPC 战斗 job（AttackMelee/AttackStatic/跨图推进 Goto）的目标在邻图 → 以 NextJob=curJob
    /// 桥接（Grant 携带原 job，传送后 ContinueBridgeMove 续跑），跳过本次 StartPath。桥接不可达
    /// （无 spot）放行原生（原生在本图坐标上失败，对齐不可达表现）。普通 NPC/自动 job 的跨图
    /// 目标仍保持原生（其评估侧 CanReach 已安静 false，正常情况下走不到这里；追击/撤离走 Grant 链）。
    /// </summary>
    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    public static class Patch_Pawn_PathFollower_StartPath_CrossMap
    {
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_PathFollower, Pawn>("pawn");

        public static bool Prefix(Pawn_PathFollower __instance, LocalTargetInfo dest)
        {
            var pawn = PawnRef(__instance);
            if (pawn?.Map == null || pawn.Destroyed) return true;
            var job = pawn.jobs?.curJob;
            if (job == null) return true;

            // 桥接资格（2026-08 扩 NPC 战斗 job，原版两层模型：推进/接战 job 的寻路跨图化）：
            // ① playerForced（玩家点击下令，原有）；② AttackMelee/AttackStatic（战斗 job 目标跨图——
            // 近战追击直桥）；③ 跨图推进 Goto（GotoNearestHostile 跨图版下发）。
            // ③ 不能用 dutyTag 自标识（2026-08 实证教训：ThinkNode_Duty.TryIssueJobPackage 会把
            // think tree 产出的 job.dutyTag 无条件覆写为 duty.tag（AssaultColony 等 duty 无 tag →
            // null），RimExodus.NpcApproach 标记在 StartJob 前就被抹掉——旧判据恒 false，推进 job
            // 从未被桥接过。job.dutyTag 不能作为 think-tree job 的自标识通道）。改结构判据：非
            // playerForced 的 Goto 且 targetA 是邻图上的 Thing——原生 giver 的 CanReach 对跨图 NPC
            // 安静 false，这样的 job 只可能来自我们的跨图推进 patch。
            // NPC 战斗 job 不查 IsCrossMapOrderable（敌对 NPC 本就不是"可下令主体"，但战斗推进合法）。
            var npcCombatJob = job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic
                || IsNpcApproachGoto(job, pawn);
            if (!job.playerForced && !npcCombatJob) return true;
            if (job.playerForced && !npcCombatJob && !SeamlessBoundaryRules.IsCrossMapOrderable(pawn)) return true;

            Map targetMap = null;
            var finalCell = IntVec3.Invalid;
            if (dest.HasThing && dest.Thing != null)
            {
                targetMap = dest.Thing.Map;
                finalCell = dest.Thing.Position;
            }
            else if (dest.Cell.IsValid
                && job.playerForced
                && SeamlessCommandTargets.TryGet(pawn, out var ct)
                && ct.map != pawn.Map
                && dest.Cell == ct.cell)
            {
                targetMap = ct.map;
                finalCell = ct.cell;
            }
            if (targetMap == null || targetMap == pawn.Map) return true;
            if (!SeamlessCombatCoords.TryGetCombatLink(pawn.Map, targetMap, out _)) return true;

            // TransitTag 的桥接 job 自身目标是本图 spot——不会进到这里（同图）。
            if (SeamlessCrossMapOrders.TryBridgeJob(pawn, targetMap, finalCell, job))
            {
                if (RimExodusLog.Enabled(RimExodusLogModule.Transfer))
                    Log.Message($"[RimExodus] Cross-map job wrap: {pawn.LabelShort} {job.def.defName} -> map {targetMap.uniqueID}; resume after transfer.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// NPC 跨图推进 Goto 的结构判据（GotoNearestHostile 跨图版下发的 job 经 think tree 冒泡后的
        /// 存活识别——dutyTag 已被 ThinkNode_Duty 抹掉，见 Prefix 内教训注）：非 playerForced 的 Goto
        /// 且 targetA 是 Thing 且在别的图上。targetA 在 pawn 当前图上时返回 false（同图走原生）。
        /// </summary>
        private static bool IsNpcApproachGoto(Job job, Pawn pawn)
        {
            return job.def == JobDefOf.Goto && !job.playerForced
                && job.targetA.HasThing && job.targetA.Thing != null
                && job.targetA.Thing.Map != null && job.targetA.Thing.Map != pawn.Map;
        }
    }
}
