using System;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 评估期虚拟传送（VMF VirtualTeleporter 同构）：直写 <c>Thing.mapIndexOrState</c>（private
    /// sbyte，编码 = Find.Maps 索引，-1 未生成）与 <c>positionInt</c>（Position 属性的后备字段），
    /// 让"完全原版"的评估函数认为 pawn 站在目标图/目标格上运行；Dispose 恢复。短窗口括号用法
    /// （Prefix 进 / Postfix 在正常路径尽早还原，并由 Finalizer 覆盖异常路径），不触发
    /// spawn/despawn 副作用，网格不更新（窗口内只做只读评估）。任何新调用点都必须提供
    /// 异常安全的恢复路径；只配 Prefix/Postfix 会在原方法抛错时把 Thing 永久留在假图/假格。
    ///
    /// **Position 必须直写后备字段、绝不能走 <c>thing.Position = ...</c> 属性赋值（2026-08 修复，
    /// "Exception in BreadthFirstTraverse ... ThingFromRegionListerReachable NRE" 的根因，勿回退）**：
    /// Position 是带完整网格簿记的属性——setter 会按当前 Map 做 region lister 注销 + thingGrid
    /// 注销，换位后再重新注册。而本传送器的时序是先切 mapIndexOrState 再写 Position：
    /// ctor 在**邻图**上注销（无操作）并把 pawn 注册进邻图的 thingGrid + region lister（本图原
    /// 注册未动）；Dispose 又在**本图**按邻图坐标注销（错过本图真实注册位）→ 两张图各泄漏
    /// region lister 陈旧条目 + thingGrid 重复/陈旧条目，pawn 后续 DeSpawn（死亡进尸体/跨图传送/
    /// 组队离场）清不掉它们——残留 pawn 一旦变为"未生成且无 holder"（尸体销毁等），任何
    /// <c>GenClosest.ClosestThingReachable</c> BFS 扫到该 region 即 NRE。直写 positionInt
    /// 与 mapIndexOrState 同款纪律：评估窗口对两张图完全零写入。
    /// </summary>
    public struct SeamlessVirtualTeleporter : IDisposable
    {
        private static readonly AccessTools.FieldRef<Thing, sbyte> MapIndexOrStateRef =
            AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

        private static readonly AccessTools.FieldRef<Thing, IntVec3> PositionIntRef =
            AccessTools.FieldRefAccess<Thing, IntVec3>("positionInt");

        private readonly Thing thing;
        private readonly sbyte oldMapIndex;
        private readonly IntVec3 oldPosition;

        public SeamlessVirtualTeleporter(Thing thing, Map map, IntVec3 position = default(IntVec3))
        {
            this.thing = thing;
            oldMapIndex = MapIndexOrStateRef(thing);
            oldPosition = PositionIntRef(thing);
            MapIndexOrStateRef(thing) = (sbyte)map.Index;
            if (position.IsValid)
            {
                PositionIntRef(thing) = position;
            }
        }

        public void Dispose()
        {
            if (thing == null) return;
            MapIndexOrStateRef(thing) = oldMapIndex;
            PositionIntRef(thing) = oldPosition;
        }
    }
}
