using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimExodus
{
    /// <summary>
    /// 跨图点击重放的会话上下文（2026-08 架构重构，VMF 同构，勿回退为 GetOptions 整方法接管）。
    ///
    /// 生命周期 = 一次 <c>FloatMenuMakerMap.GetOptions</c> 调用：FloatMenuContext 构造函数
    /// Prefix 里命中判定（L0 = SeamlessMapUtility.TryResolveMapPosition 多边形归属）解析到邻图时
    /// Begin（点击坐标已改写为邻图局部、map 已换真邻图），GetOptions 的 Finalizer 里 End。
    /// 消费方：SeamlessGenUI.ThingsUnderMouseReplay（ctor 体内 GenUI 镜像）、
    /// GetOptions 体内两处 InBounds 重定向（见 Patches_ClickReplay）。
    /// 唯一构造点 = GetOptions（原版全库仅此一处 new FloatMenuContext），配对安全。
    /// </summary>
    public static class SeamlessReplayContext
    {
        /// <summary>重放目标图（点击真正落在的邻图）；null = 无重放（本图点击，全原生）。 </summary>
        public static Map Target { get; private set; }

        /// <summary>发起重放的宿主图（Find.CurrentMap）。 </summary>
        public static Map Host { get; private set; }

        /// <summary>offset 契约同邻居表：hostLocal = targetLocal + offset。 </summary>
        public static IntVec3 Offset { get; private set; }

        /// <summary>重放点击的邻图局部格。 </summary>
        public static IntVec3 TargetCell { get; private set; }

        public static bool Active => Target != null;

        internal static void Begin(Map host, Map target, IntVec3 offset, IntVec3 targetCell)
        {
            Host = host;
            Target = target;
            Offset = offset;
            TargetCell = targetCell;
        }

        internal static void End()
        {
            Target = null;
            Host = null;
        }

        /// <summary>
        /// FloatMenuMap.StillValid 重验证的取值点替换（Patches_ClickReplay transpiler）：返回类型
        /// 必须与 Thing.PositionHeld 一致（IntVec3，调用点紧接 .ToVector3Shifted()）。revalidateClickTarget
        /// 在邻图上时其 PositionHeld 是邻图本地坐标——直接喂回 GetOptions 会按本图同数字坐标误解析；
        /// 转宿主系（+offset）后 GetOptions 能再次正确重放到目标图。
        /// </summary>
        public static IntVec3 PositionHeldForRevalidate(Thing thing)
        {
            if (SeamlessViewProjection.TryProject(thing.MapHeld, thing.PositionHeld,
                    Find.CurrentMap, out var projected)) return projected;
            return thing.PositionHeld;
        }
    }

    /// <summary>
    /// 命令目标登记表（VMF TargetMapUtility 同构）：菜单生成时（ctor Finalizer）为每个选中 pawn
    /// 登记"本次命令的目标图+格"；下游公共函数 patch（PawnGotoAction / StartPath 包装 /
    /// BestOrderedGotoDestNear）据此识别"这个格/目标是邻图框架"，无需依赖坐标数值反推。
    /// 生命周期：菜单写入 → 直接消费（PawnGotoAction），或 TryTakeOrderedJob 时转交给具体 Job；
    /// 菜单被弃用则残留到下次本图菜单清除。执行期不要再把它当权威来源：排队后的 Map 身份
    /// 由 <see cref="SeamlessJobCommandTargets"/> 跟随具体 Job，避免 Pawn 后续点击覆盖旧命令。
    /// </summary>
    public static class SeamlessCommandTargets
    {
        public class CommandTarget
        {
            public Map map;
            public IntVec3 cell;
        }

        private static readonly ConditionalWeakTable<Pawn, CommandTarget> Targets = new ConditionalWeakTable<Pawn, CommandTarget>();

        public static void Set(Pawn pawn, Map map, IntVec3 cell)
        {
            Targets.Remove(pawn);
            Targets.Add(pawn, new CommandTarget { map = map, cell = cell });
        }

        public static bool TryGet(Pawn pawn, out CommandTarget target)
        {
            target = null;
            if (pawn == null || !Targets.TryGetValue(pawn, out var t)) return false;
            if (t.map == null || t.map.Disposed) return false;
            target = t;
            return true;
        }

        public static bool TryGet(Pawn pawn, out Map map, out IntVec3 cell)
        {
            map = null;
            cell = default(IntVec3);
            if (!TryGet(pawn, out var t)) return false;
            map = t.map;
            cell = t.cell;
            return true;
        }

        public static void Remove(Pawn pawn)
        {
            if (pawn != null) Targets.Remove(pawn);
        }
    }

    /// <summary>
    /// 玩家纯 Goto 从菜单语境进入 Job 队列后的目标图载体。Job.targetA 只能保存 LocalTargetInfo，
    /// Achtung 等命令提供者会把邻图局部格直接写入 Goto，Map 身份若仍只挂在 Pawn 上，排队期间
    /// 会被下一次点击覆盖，执行期也只能等 StartPath 再猜回来。
    ///
    /// 绑定发生在 TryTakeOrderedJob（命令尚未排队前），消费发生在该 Job 真正 StartJob 时；Job
    /// 回池的 Clear 负责移除未消费绑定，防对象池复用把旧目标带给新 Job。ConditionalWeakTable
    /// 只解决 GC 生命周期，不能替代 Clear 的对象池生命周期清理。
    /// </summary>
    internal static class SeamlessJobCommandTargets
    {
        internal sealed class CommandTarget
        {
            internal Map Map;
            internal IntVec3 Cell;
        }

        private static readonly ConditionalWeakTable<Job, CommandTarget> Targets =
            new ConditionalWeakTable<Job, CommandTarget>();

        internal static bool TryBindGoto(Pawn pawn, Job job)
        {
            if (pawn?.Map == null || job?.def != RimWorld.JobDefOf.Goto
                || job.targetA.HasThing || !job.targetA.IsValid)
            {
                return false;
            }
            if (!SeamlessCommandTargets.TryGet(pawn, out var command)
                || command.map == pawn.Map || command.cell != job.targetA.Cell)
            {
                return false;
            }
            if (!SeamlessBoundaryRules.IsCrossMapOrderable(pawn)
                || !SeamlessViewProjection.TryProject(command.map, command.cell, pawn.Map, out _))
            {
                return false;
            }

            Targets.Remove(job);
            Targets.Add(job, new CommandTarget { Map = command.map, Cell = command.cell });
            return true;
        }

        internal static bool TryConsume(Job job, out CommandTarget target)
        {
            target = null;
            if (job == null || !Targets.TryGetValue(job, out var bound)) return false;
            Targets.Remove(job);
            if (bound.Map == null || bound.Map.Disposed) return false;
            target = bound;
            return true;
        }

        internal static bool Contains(Job job)
        {
            return job != null && Targets.TryGetValue(job, out _);
        }

        /// <summary>
        /// TryTakeOrderedJob 会在 Job 真正执行前调用 JobDriver_Goto 的预约，把邻图局部格错误登记
        /// 到源图。绑定仍在表示 Job 只是排队、尚未执行；逐图按 Job 精确释放，避免排队期间占住
        /// 无关源图格。执行期桥接成功也调用同一入口收尾同步启动路径的预约。
        /// </summary>
        internal static void ReleaseGotoDestinationReservations(Pawn pawn, Job job)
        {
            if (pawn == null || job == null) return;
            foreach (var map in Find.Maps)
                map.pawnDestinationReservationManager.ReleaseClaimedBy(pawn, job);
        }

        internal static void Remove(Job job)
        {
            if (job != null) Targets.Remove(job);
        }
    }
}
