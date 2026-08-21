using System;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 评估期虚拟传送（VMF VirtualTeleporter 同构）：直写 <c>Thing.mapIndexOrState</c>（private
    /// sbyte，编码 = Find.Maps 索引，-1 未生成）与 Position，让"完全原版"的评估函数认为 pawn
    /// 站在目标图/目标格上运行；Dispose 恢复。短窗口括号用法（Prefix 进 / Postfix 还原），
    /// 不触发 spawn/despawn 副作用，网格不更新（窗口内只做只读评估）。
    /// </summary>
    public struct SeamlessVirtualTeleporter : IDisposable
    {
        private static readonly AccessTools.FieldRef<Thing, sbyte> MapIndexOrStateRef =
            AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

        private readonly Thing thing;
        private readonly sbyte oldMapIndex;
        private readonly IntVec3 oldPosition;

        public SeamlessVirtualTeleporter(Thing thing, Map map, IntVec3 position = default(IntVec3))
        {
            this.thing = thing;
            oldMapIndex = MapIndexOrStateRef(thing);
            oldPosition = thing.Position;
            MapIndexOrStateRef(thing) = (sbyte)map.Index;
            if (position.IsValid)
            {
                thing.Position = position;
            }
        }

        public void Dispose()
        {
            if (thing == null) return;
            MapIndexOrStateRef(thing) = oldMapIndex;
            thing.Position = oldPosition;
        }
    }
}
