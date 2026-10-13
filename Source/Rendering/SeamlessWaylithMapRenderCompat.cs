using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 只适用于 1.7 Waylith：其 MapRenderFeature.CaptureFrame 要求
    /// MapGenerator.mapBeingGenerated == null 才生成地图帧。RimExodus 分帧生成邻图时，
    /// genStep 跨帧依赖此 static，当前已完成的地图却被 Waylith 一并停绘，表现为地图黑屏而 UI 正常。
    /// 这里只替换渲染门槛的那一次字段读取；生成中的图不是 CurrentMap 且确由我方增量生成器
    /// 管理时才向门槛返回 null，不改共享 static，也不放行其他生成状态。
    /// 1.6 使用 Unity 相机/CommandBuffer 渲染链，没有 MapRenderFeature 及该门槛；
    /// 将补丁并入 1.6 路径既无目标方法，也会误改已经正常工作的绘制链。注册时必须按运行时隔离。
    /// </summary>
    internal static class SeamlessWaylithMapRenderCompat
    {
        internal static void Register(Harmony harmony)
        {
            if (!SeamlessTileRenderer.IsWaylithRuntime) return;
            try
            {
                var targetType = AccessTools.TypeByName("RimWorldWaylith.Rendering.MapRenderFeature");
                var target = targetType?.GetMethod("CaptureFrame", BindingFlags.Public | BindingFlags.Instance);
                var transpiler = AccessTools.Method(typeof(SeamlessWaylithMapRenderCompat), nameof(Transpiler));
                if (target == null || transpiler == null)
                    throw new MissingMethodException("MapRenderFeature.CaptureFrame");
                harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                Log.Message("[RimExodus] 1.7 map render gate bound: " + target.DeclaringType?.FullName + "." + target.Name);
            }
            catch (Exception ex)
            {
                Log.Warning("[RimExodus] 1.7 map render gate unavailable; incremental generation may blank the map: " + ex);
            }
        }

        private static Map MapBeingGeneratedForRenderGate()
        {
            var generating = MapGenerator.mapBeingGenerated;
            if (generating != null && generating != Find.CurrentMap && IncrementalMapGenerator.IsGenerating(generating))
                return null;
            return generating;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(typeof(SeamlessWaylithMapRenderCompat), nameof(MapBeingGeneratedForRenderGate));
            var replaced = 0;
            foreach (var code in instructions)
            {
                // 原地改写，保留 Harmony labels/exception blocks。1.7 IL 在 isActive 条件处
                // 只有这一处 ldsfld MapGenerator.mapBeingGenerated -> ldnull -> ceq。
                if (code.opcode == OpCodes.Ldsfld && code.operand is FieldInfo field &&
                    field.DeclaringType == typeof(MapGenerator) && field.Name == nameof(MapGenerator.mapBeingGenerated))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = replacement;
                    replaced++;
                }
                yield return code;
            }
            if (replaced != 1)
                throw new InvalidOperationException("Expected one MapRenderFeature mapBeingGenerated gate, found " + replaced);
        }
    }
}
