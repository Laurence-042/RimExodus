using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 弹丸跨缝交接（阶段5，缝交接机制——用户定夺 2026-08，勿回退为 VMF 式 transpiler
    /// 统一坐标拦截：那会在每个 GetThingList/CanHit 调用点套合并层，全图常驻成本）。
    ///
    /// ① <see cref="Patch_Projectile_Launch"/> Postfix——跨图发射（弹所在图 ≠ 目标锚点图且互为
    ///    活跃邻居）时，destination 从目标图本地坐标平移到统一坐标（+offset，覆盖命中/强制 miss/
    ///    掩体 miss/野 miss 四种落点），并按统一距离重算 ticksToImpact/lifetime（origin 是
    ///    caster.DrawPos，宿主坐标，不动）。
    /// ② <see cref="Patch_Projectile_TickInterval"/> Prefix——飞行中检测步起/步终格归属，
    ///    跨越共享边的瞬间把 origin/destination 同 offset 平移（向量模长不变 → 飞行进度精确
    ///    保持）、respawn 到邻图，再放行原方法体在邻图原生完成该步的拦截与结算。必须 Prefix：
    ///    原版弹速下跨缝飞行常只有 1-3 tick，高速弹一步即冲过缝并结算，Postfix 来不及介入。
    ///
    /// 性能（2026-08 定稿口径）：同图弹丸 = Launch 两次引用比较早退；TickInterval 两次
    /// 缓存多边形包含测试（内圈格首次即中、零分配，ContainsPointAt 为 6 次叉积循环）。
    /// 凸多边形保证"步起/步终皆本图格 ⟺ 步内无越界"，因此只在两端归属不同时才细查。
    /// 已知边界：单步越缝弹丸的本图侧子段拦截跳过（罕见：仅超高速弹 + 射手深入带内；
    /// void 段本无可拦之物）。
    /// </summary>
    public static class Patches_Projectile
    {
        internal static readonly FieldInfo OriginField = AccessTools.Field(typeof(Projectile), "origin");
        internal static readonly FieldInfo DestinationField = AccessTools.Field(typeof(Projectile), "destination");
        internal static readonly FieldInfo TicksToImpactField = AccessTools.Field(typeof(Projectile), "ticksToImpact");
        internal static readonly FieldInfo LifetimeField = AccessTools.Field(typeof(Projectile), "lifetime");
        internal static readonly FieldInfo LandedField = AccessTools.Field(typeof(Projectile), "landed");
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch),
        new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
            typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) })]
    public static class Patch_Projectile_Launch
    {
        /// <summary>
        /// 跨图发射落点归一。usedTarget 的坐标系三分支：命中/强制 miss/掩体 miss = 目标图本地；
        /// 野 miss（ShootLine.ChangeDestToMissWild 作用于我们的统一 ShootLine）= 已统一。
        /// 用"落点到目标（两种坐标系下）的距离"判别——offset 幅度 ≈ 0.87×mapSize（相邻六边形
        /// 中心距）≫ miss 散布半径（≤ ~10 格），判别无歧义。落点 clamp 进宿主方形
        /// （miss 散布可能推出界；不收会被原版出界自毁静默吞掉，收进来落点视觉正确）。
        /// </summary>
        public static void Postfix(Projectile __instance, Vector3 origin, LocalTargetInfo usedTarget, LocalTargetInfo intendedTarget)
        {
            if (!SeamlessCombatCoords.Enabled || __instance.Map == null) return;

            var anchor = intendedTarget.HasThing ? intendedTarget.Thing : (usedTarget.HasThing ? usedTarget.Thing : null);
            if (anchor?.Map == null || anchor.Map == __instance.Map) return;
            if (!SeamlessCombatCoords.TryGetCombatLink(__instance.Map, anchor.Map, out var link)) return;

            var dest = (Vector3)Patches_Projectile.DestinationField.GetValue(__instance);
            var usedCell = usedTarget.Cell;
            var anchorUnified = anchor.Position + link.offset;
            if (usedCell.DistanceToSquared(anchor.Position) <= usedCell.DistanceToSquared(anchorUnified))
            {
                // 目标图本地坐标 → 统一坐标。
                dest.x += link.offset.x;
                dest.z += link.offset.z;
            }

            // clamp 仅兜底"野 miss 散布推出两张图"的落点（原版出界自毁会静默吞掉）；
            // 合法深目标（2026-08 门限放宽后统一格可越宿主方形）不收——缝交接（TickInterval
            // Prefix）会在跨缝时把 destination 平移回目标图本地坐标系，落点在目标图界内原生有效。
            var targetSize = anchor.Map.Size;
            var localOnTarget = dest - new Vector3(link.offset.x, 0f, link.offset.z);
            if (localOnTarget.x < 0.5f || localOnTarget.x > targetSize.x - 0.5f
                || localOnTarget.z < 0.5f || localOnTarget.z > targetSize.z - 0.5f)
            {
                var size = __instance.Map.Size;
                dest.x = Mathf.Clamp(dest.x, 0.5f, size.x - 0.5f);
                dest.z = Mathf.Clamp(dest.z, 0.5f, size.z - 0.5f);
            }

            // 按统一距离重算飞行时长（复刻 Launch 的 StartingTicksToImpact 语义：水平距离/速度）。
            var dir = origin - dest;
            dir.y = 0f;
            var speed = __instance.def.projectile.SpeedTilesPerTick;
            var total = dir.magnitude / speed;
            if (total <= 0f) total = 0.001f;
            var newTicks = Mathf.Max(1, Mathf.CeilToInt(total));

            Patches_Projectile.DestinationField.SetValue(__instance, dest);
            Patches_Projectile.TicksToImpactField.SetValue(__instance, newTicks);
            Patches_Projectile.LifetimeField.SetValue(__instance, newTicks);
        }
    }

    [HarmonyPatch(typeof(Projectile), "TickInterval")]
    public static class Patch_Projectile_TickInterval
    {
        /// <summary>
        /// 缝交接：本步起点/终点（模拟推进算出）任一归属邻图 → 找步内首个邻图格为交接点，
        /// 字段平移 + respawn，放行原方法体在邻图原生跑完该步（拦截余段 + 结算全覆盖——
        /// 含"一步冲过缝并到达落点"的高速弹）。多邻居（顶点角区）以终点归属优先。
        /// </summary>
        public static void Prefix(Projectile __instance, int delta)
        {
            if (!SeamlessCombatCoords.Enabled || __instance.Destroyed || !__instance.Spawned) return;
            var map = __instance.Map;
            if (map == null || (bool)Patches_Projectile.LandedField.GetValue(__instance)) return;
            if (!SeamlessCombatCoords.HasActiveSeamNeighbors(map)) return;

            var startCell = __instance.Position;

            // 模拟本步推进（复刻 TickInterval：ticksToImpact -= delta 后按 fraction 插值到 destination）。
            var origin = (Vector3)Patches_Projectile.OriginField.GetValue(__instance);
            var dest = (Vector3)Patches_Projectile.DestinationField.GetValue(__instance);
            var tti = (int)Patches_Projectile.TicksToImpactField.GetValue(__instance);
            var dir = dest - origin;
            dir.y = 0f;
            var total = dir.magnitude / __instance.def.projectile.SpeedTilesPerTick;
            if (total <= 0f) total = 0.001f;
            var fractionAfter = Mathf.Clamp01(1f - (tti - delta) / total);
            var endCell = (origin + dir * fractionAfter).ToIntVec3();
            if (endCell == startCell) return; // 同格步：归属不变

            var startOwned = SeamlessTileRegistry.TryGetOwnerNeighbor(map, startCell, out _, out _);
            var endOwned = SeamlessTileRegistry.TryGetOwnerNeighbor(map, endCell, out var ownerMap, out var ownerLocal);
            if (!startOwned && !endOwned) return; // 两端皆本图（凸性 ⟹ 步内无越界）

            // 交接点：终点归属优先（弹去往的图）；否则沿步细查首个邻图格。
            var crossCell = endCell;
            if (endOwned)
            {
                // ownerMap/ownerLocal 即 endCell 的邻图归属。
            }
            else
            {
                // startOwned=true：起点已在邻图辖区，从起点细查。
                crossCell = IntVec3.Invalid;
                foreach (var cell in GenSight.PointsOnLineOfSight(startCell, endCell))
                {
                    if (SeamlessTileRegistry.TryGetOwnerNeighbor(map, cell, out ownerMap, out ownerLocal))
                    {
                        crossCell = cell;
                        break;
                    }
                }
                if (!crossCell.IsValid || ownerMap == null) return;
            }

            if (ownerMap == null) return;
            if (!SeamlessTileGraph.TryGetNeighborLinkByWorldTile(map, SeamlessTileRegistry.GetMapWorldTile(ownerMap), out var info))
            {
                return; // owner 解析与邻居表不一致（防御）：不动，交原版出界自毁兜底
            }

            // 同 offset 平移（模长不变 → 飞行进度精确保持），当前精确位置连续无跳变。
            var offsetV = new Vector3(info.offset.x, 0f, info.offset.z);
            Patches_Projectile.OriginField.SetValue(__instance, origin - offsetV);
            Patches_Projectile.DestinationField.SetValue(__instance, dest - offsetV);

            var spawnCell = crossCell - info.offset;
            if (!spawnCell.InBounds(ownerMap)) return; // 防御（TryGetOwnerNeighbor 已保证，双保险不动状态）

            __instance.DeSpawn();
            GenSpawn.Spawn(__instance, spawnCell, ownerMap);
            // 原方法体随后在邻图原生跑完本步：拦截余段（CheckForFreeInterceptBetween 用
            // 平移后的 ExactPosition）、落点结算（ImpactSomething）全部正确。
        }
    }
}
