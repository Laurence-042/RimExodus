using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// GenerateMap postfix 通用复放器（2026-09，VVE Tier 3 残骸案落地）。
    ///
    /// 【问题】增量分帧生成路径内联复刻 <c>MapGenerator.GenerateMap</c> 的 genStep 组装与收尾、
    /// 从不调用该方法本体——挂在它上面的第三方 Harmony Postfix（如 VEF 的 ObjectSpawns 地图
    /// 物体刷出，VVE 残骸/Dark Ages 巢穴等全走它）在增量生成的地块图上静默不跑（与 GL 当年
    /// GenerateContentsIntoMap Prefix 失明同因，见 SeamlessLandformsCompat 类注释）。
    ///
    /// 【机制】生成成功收尾后，经 <see cref="Harmony.GetPatchInfo"/> 枚举该方法当前全部 postfix
    /// （每次现读——会话中途新打的 patch 也会被拾取），逐个以"原方法刚成功返回"的语义手工调用：
    /// 参数编组复刻 Harmony 注入子集（__result / 按名或 __N 取原方法实参 / ___xxx 读 MapGenerator
    /// 静态字段 / __state 给 default / __runOriginal=true / __exception=null），无法编组的参数
    /// （未知名且无默认值）跳过该 patch 并 Warning（签名漂移可观测）。
    ///
    /// 【只复放 postfix，勿扩到 prefix/transpiler/finalizer】prefix 的语义是"原方法执行前改参/短路"，
    /// 脱离原方法单独跑是错的（GL 的 GenerateContentsIntoMap Prefix 由其专属 compat 复刻，不在本处）；
    /// transpiler 是 IL 级改写、对"未被调用的方法"无从复放（维持既有盲区）；finalizer 无异常可交。
    ///
    /// 【挂点纪律】只在增量路径（IncrementalMapGenerator.FinishGeneration 的 finally 之后）触发——
    /// 同步逃生 / POI 原生 / 营地 / 初始图路径调原生方法本体，patch 原生生效，复放会造成双跑。
    /// finally 之后调用对齐原生时点：Harmony postfix 在原方法 try/finally（清 MapGenerator static）
    /// 完成后才执行，此时 mapBeingGenerated 等静态已是清理后的值。
    ///
    /// 【风险收敛】依赖 prefix 所置 __state 的 postfix 会拿到 default 值——逐 patch try/catch
    /// （一个坏 patch 不拖累其它）+ Warning 点名 owner；对比现状"完全不跑"严格更接近原版。
    /// <see cref="BlockedOwners"/> 为外科手术式禁用预留（复放出问题的单个 mod 可按 Harmony 实例
    /// id 拉黑，不动其它）。
    /// </summary>
    internal static class GenerateMapPostfixReplay
    {
        /// <summary>本 mod 的 Harmony 实例 id（RimExodusMod ctor）——防御性自排除（当前无自 patch）。</summary>
        private const string SelfOwner = "RimExodus.SeamlessWorld";

        /// <summary>
        /// 复放排除表（owner = Harmony 实例 id）。初始为空；某 mod 的 postfix 复放确认有害时
        /// 在此加其 id 精确禁用，勿为此加设置项。
        /// </summary>
        private static readonly string[] BlockedOwners = Array.Empty<string>();

        private static MethodInfo _generateMapMethod;

        /// <summary>
        /// 复放挂在 GenerateMap 上的全部 postfix。<paramref name="generateMapArgs"/> 必须与原方法
        /// 形参表逐位对应（IncrementalMapGenerator.Start 按其收到的实参拼装）。
        /// </summary>
        public static void Replay(Map map, object[] generateMapArgs)
        {
            try
            {
                if (map == null) return;
                if (_generateMapMethod == null)
                    _generateMapMethod = AccessTools.Method(typeof(MapGenerator), nameof(MapGenerator.GenerateMap));
                if (_generateMapMethod == null)
                {
                    Log.Warning("[RimExodus] GenerateMap postfix replay: GenerateMap method not found — replay disabled.");
                    return;
                }

                var patchInfo = Harmony.GetPatchInfo(_generateMapMethod);
                var postfixes = patchInfo?.Postfixes;
                var skippedPrefixes = patchInfo != null ? patchInfo.Prefixes?.Count ?? 0 : 0;
                var skippedTranspilers = patchInfo != null ? patchInfo.Transpilers?.Count ?? 0 : 0;
                if ((postfixes == null || postfixes.Count == 0))
                {
                    if (skippedPrefixes > 0 || skippedTranspilers > 0)
                    {
                        RimExodusLog.Message(RimExodusLogModule.Compat,
                            $"GenerateMap postfix replay: map={map.uniqueID} no postfixes to replay "
                            + $"(not replayed by design: {skippedPrefixes} prefixes, {skippedTranspilers} transpilers).");
                    }
                    return;
                }

                var replayedOwners = new List<string>();
                foreach (var patch in postfixes)
                {
                    var owner = patch.owner ?? "?";
                    if (owner == SelfOwner || Array.IndexOf(BlockedOwners, owner) >= 0)
                        continue;

                    var patchMethod = patch.PatchMethod;
                    if (patchMethod == null) continue;

                    var args = BuildInvokeArgs(patchMethod, map, generateMapArgs);
                    if (args == null)
                    {
                        Log.Warning($"[RimExodus] GenerateMap postfix replay: skipped patch of owner={owner} "
                            + $"({patchMethod.DeclaringType?.Name}::{patchMethod.Name}) — unmarshalable parameter. "
                            + "Signature drift or unsupported injection.");
                        continue;
                    }

                    try
                    {
                        patchMethod.Invoke(null, args);
                        replayedOwners.Add(owner);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[RimExodus] GenerateMap postfix replay failed for owner={owner} "
                            + $"({patchMethod.DeclaringType?.Name}::{patchMethod.Name}): {ex.InnerException ?? ex}");
                    }
                }

                if (replayedOwners.Count > 0)
                {
                    RimExodusLog.Message(RimExodusLogModule.Compat,
                        $"GenerateMap postfix replay: map={map.uniqueID} replayed {replayedOwners.Count} postfixes "
                        + $"(owners: {string.Join(", ", replayedOwners)}; not replayed by design: "
                        + $"{skippedPrefixes} prefixes, {skippedTranspilers} transpilers).");
                }
            }
            catch (Exception ex)
            {
                // 复放器自身故障不拖累生成收尾（调用点在 FinishGeneration 成功路径末尾）。
                Log.Warning($"[RimExodus] GenerateMap postfix replay: internal failure (replay abandoned): {ex}");
            }
        }

        /// <summary>
        /// 为单个 postfix 编组调用实参；遇到无法编组的参数返回 null（调用方跳过该 patch）。
        /// 注入语义对照 Harmony 2.x：__result / __N（位置） / 按原名 / ___静态字段 / __state /
        /// __runOriginal / __exception。__instance 对静态原方法不存在——Harmony 打 patch 时就会拒绝，
        /// 此处按未知参数处理。
        /// </summary>
        private static object[] BuildInvokeArgs(MethodInfo patchMethod, Map map, object[] generateMapArgs)
        {
            var originalParams = _generateMapMethod.GetParameters();
            var patchParams = patchMethod.GetParameters();
            if (patchParams.Length == 0) return Array.Empty<object>();

            var args = new object[patchParams.Length];
            for (var i = 0; i < patchParams.Length; i++)
            {
                var p = patchParams[i];
                var name = p.Name;

                if (name == "__result")
                {
                    args[i] = map;
                    continue;
                }
                if (name == "__runOriginal")
                {
                    // 生成确实发生了（经我们的分帧链），语义上原方法"已执行"。
                    args[i] = true;
                    continue;
                }
                if (name == "__exception")
                {
                    args[i] = null;
                    continue;
                }
                if (name == "__state")
                {
                    // 前置 prefix 在增量路径上同样没跑过——state 保持 default 是最接近的语义。
                    args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                    continue;
                }
                if (name != null && name.StartsWith("___"))
                {
                    var fieldName = name.Substring(3);
                    var field = AccessTools.Field(typeof(MapGenerator), fieldName);
                    if (field == null || !field.IsStatic)
                        return null;
                    args[i] = field.GetValue(null);
                    continue;
                }
                if (name != null && name.StartsWith("__"))
                {
                    var rest = name.Substring(2);
                    if (int.TryParse(rest, out var position))
                    {
                        if (position < 0 || position >= generateMapArgs.Length) return null;
                        args[i] = generateMapArgs[position];
                        continue;
                    }
                    // 其它双下划线保留名（如 __instance 对静态原方法）按无法编组处理。
                    if (p.HasDefaultValue) { args[i] = Type.Missing; continue; }
                    return null;
                }

                // 按原方法形参名注入。
                var matched = false;
                for (var j = 0; j < originalParams.Length; j++)
                {
                    if (originalParams[j].Name == name)
                    {
                        if (j >= generateMapArgs.Length) return null;
                        args[i] = generateMapArgs[j];
                        matched = true;
                        break;
                    }
                }
                if (matched) continue;

                if (p.HasDefaultValue)
                {
                    args[i] = Type.Missing;
                    continue;
                }
                return null;
            }
            return args;
        }
    }
}
