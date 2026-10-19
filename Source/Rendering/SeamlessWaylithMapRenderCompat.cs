using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 1.7 MapRenderFeature.CaptureFrame 的场景门槛要求 mapBeingGenerated == null。
    /// RimExodus 分帧路径必须跨帧保留此 static 给 genStep 使用，但绘制的是另一张已完成的
    /// CurrentMap。只在该门槛处把我方后台生成视为“不阻止当前地图绘制”；不改 static 本身。
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
