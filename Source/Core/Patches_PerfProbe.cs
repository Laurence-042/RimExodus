using HarmonyLib;
using RimWorld;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// 帧侧归因探针（2026-09 临时诊断，配 RimExodusPerfProbe）：tick 剖析器的"tick wall"是
    /// 心跳墙钟差（含渲染/UI/GC），行军期"unaccounted"桶 10-15s/窗 需要拆出帧侧与 tick 侧。
    /// Game.UpdatePlay ≈ 全部游戏主线程（tick 循环 + 地图渲染调度 + UI）；TickManagerUpdate ≈
    /// tick 循环本体。wall − UpdatePlay ≈ 引擎侧渲染；UpdatePlay − TickManagerUpdate − 渲染调度
    /// 等。两个目标方法均普通 C#（非 Burst），与剖析器"不 patch DoSingleTick"的约束不冲突。
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.UpdatePlay))]
    internal static class Patch_Game_UpdatePlay_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.GameUpdatePlay, stamp);
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    internal static class Patch_TickManager_TickManagerUpdate_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.TickManagerUpdate, stamp);
        }
    }

    /// <summary>
    /// MapComponentUpdate 按图计时（裁决当前图 MapUpdate 大头 = 组件段 vs 非组件段 drawer/section/fog）。
    /// 注意 Map.MapUpdate 内含本调用；报告里与 tick 剖析器的 per-map update 列相减即得非组件段。
    /// </summary>
    [HarmonyPatch(typeof(MapComponentUtility), nameof(MapComponentUtility.MapComponentUpdate))]
    internal static class Patch_MapComponentUtility_MapComponentUpdate_PerfProbe
    {
        private static long stamp;

        internal static void Prefix(Map map)
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix(Map map)
        {
            RimExodusPerfProbe.RecordMapComponentUpdate(map, stamp);
        }
    }

    // ===== 第三轮（2026-09-24）：拆分 Map.MapUpdate 非组件段 + 调度开销归因 =====
    // 前两轮数据裁决：mapCompUpdate≈0（组件段无辜）→ 大头在 Map.MapUpdate 的非组件段。按
    // RimWorldDecompiled 的 Map.MapUpdate 实体核对：每图每帧 prologue（sky/powerNet/region/
    // glow/lord/postTickVisuals）+ 仅当前图的绘制块（MapMeshDrawerUpdate_First → DrawMapMesh →
    // DrawDynamicThings → GameConditionDraw → DrawClippers → DrawDesignations → DrawAllOverlays
    // → temporaryThingDrawer → FleckManagerDraw）+ 尾段（area/weather/FleckManagerUpdate）。
    // 以下位点全部经反编译核实为 public 且签名明确，走 attribute patch（离线验证器可扫）。
    // 剩余未挂部分（sky/powerNet/regionGrid.UpdateClean/lord/postTickVisuals/area/fleckUpdate/
    // DrawDesignations/GameConditionDraw/temporaryThing）= MapUpdate 总量 − 组件段 − 下列位点。

    /// <summary>MapDrawer.DrawMapMesh（视区内 section 层提交 + 脏 section 重建，当前图每帧）。</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    internal static class Patch_MapDrawer_DrawMapMesh_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.DrawMapMesh, stamp);
        }
    }

    /// <summary>DynamicDrawManager.DrawDynamicThings（pawn/投射物等动态物逐帧绘制，200 敌主要嫌疑）。
    /// 顺带采样 DrawThings 列表规模（窗口均值/峰值）——判读"耗时高是每 thing 贵还是 thing 多"。</summary>
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    internal static class Patch_DynamicDrawManager_DrawDynamicThings_PerfProbe
    {
        private static long stamp;

        internal static void Prefix(DynamicDrawManager __instance)
        {
            stamp = RimExodusPerfProbe.Now();
            RimExodusPerfProbe.ObserveDynamicDrawThings(__instance);
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.DrawDynamicThings, stamp);
        }
    }

    /// <summary>边界裁剪平面（当前图每帧一次；含本 mod footprint 挖洞逻辑的成本）。</summary>
    [HarmonyPatch(typeof(MapEdgeClipDrawer), nameof(MapEdgeClipDrawer.DrawClippers))]
    internal static class Patch_MapEdgeClipDrawer_DrawClippers_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.EdgeClippers, stamp);
        }
    }

    /// <summary>覆盖图标层（受击/倒地/征召等 overlay，战斗期随 pawn 数放大）。</summary>
    [HarmonyPatch(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawAllOverlays))]
    internal static class Patch_OverlayDrawer_DrawAllOverlays_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.OverlayDraw, stamp);
        }
    }

    /// <summary>当前图 Fleck 绘制（战斗水花/血迹等）。</summary>
    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.FleckManagerDraw))]
    internal static class Patch_FleckManager_FleckManagerDraw_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.FleckDraw, stamp);
        }
    }

    /// <summary>脏 region/room 重建（每图每帧；门被砸/建筑损毁会持续脏化，行军期嫌疑之一）。</summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
    internal static class Patch_RegionAndRoomUpdater_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.RegionRebuild, stamp);
        }
    }

    /// <summary>glow 网格更新（每图每帧；火灾/灯光变化触发重算，行军期嫌疑之一）。</summary>
    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GlowGridUpdate_First))]
    internal static class Patch_GlowGrid_GlowGridUpdate_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.GlowUpdate, stamp);
        }
    }

    /// <summary>天气管理器逐帧更新（每图每帧；天气域 patch 的执行侧成本也落在此处）。</summary>
    [HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.WeatherManagerUpdate))]
    internal static class Patch_WeatherManager_WeatherManagerUpdate_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.WeatherUpdate, stamp);
        }
    }

    /// <summary>TickList.Tick（全局三条 tick 表的调度本体，每 tick 三次调用）：该值 − things 桶 =
    /// 调度开销（桶遍历/区间判定/destroyed 过滤），裁决 TickManagerUpdate 里 things/pre/post
    /// 之外的缺口归属（我方节流门已单独计时）。</summary>
    [HarmonyPatch(typeof(TickList), nameof(TickList.Tick))]
    internal static class Patch_TickList_Tick_PerfProbe
    {
        private static long stamp;

        internal static void Prefix()
        {
            stamp = RimExodusPerfProbe.Now();
        }

        internal static void Postfix()
        {
            RimExodusPerfProbe.Record(RimExodusPerfProbe.TickListDispatch, stamp);
        }
    }

    /// <summary>MapDrawer.MapMeshDirty 脏源计数（按 flag 位拆分聚合）。绑定 4 参重载——2 参重载
    /// 内部转发到它，全部调用都会经过。dirty 也可能在 MapPreview 后台生成线程触发，计数加锁。</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty),
        new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
    internal static class Patch_MapDrawer_MapMeshDirty_PerfProbe
    {
        internal static void Prefix(ulong dirtyFlags)
        {
            RimExodusPerfProbe.CountMapMeshDirty(dirtyFlags);
        }
    }

    // ===== 第四轮（2026-09-24）：Map.MapUpdate 剩余成员全量包装 =====
    // 第三轮数据：map4 MapUpdate 30.9ms/帧中已探针段仅 8.9ms，剩 ~22ms 未归因；非当前图每图
    // ~1.2ms/帧同源。以下按 Map.MapUpdate（RimWorldDecompiled 实体）剩余成员逐一包装，全部
    // 全局总量口径（map4 在每个成员上占绝对大头；邻图合计每图 1.2ms 有界，可从总量中扣除）。
    // 命名空间按 1.6.9438 反编译核实（注意 DesignationManager 在 Verse、GlobalRendererUtility
    // 在 RimWorld.Planet、PlantFallColors 在 RimWorld）。

    [HarmonyPatch(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate))]
    internal static class Patch_SkyManager_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.SkyUpdate, stamp); }
    }

    [HarmonyPatch(typeof(RimWorld.PowerNetManager), nameof(RimWorld.PowerNetManager.UpdatePowerNetsAndConnections_First))]
    internal static class Patch_PowerNetManager_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.PowerNetsUpdate, stamp); }
    }

    [HarmonyPatch(typeof(RegionGrid), nameof(RegionGrid.UpdateClean))]
    internal static class Patch_RegionGrid_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.RegionClean, stamp); }
    }

    [HarmonyPatch(typeof(Verse.AI.Group.LordManager), nameof(Verse.AI.Group.LordManager.LordManagerUpdate))]
    internal static class Patch_LordManager_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.LordUpdate, stamp); }
    }

    [HarmonyPatch(typeof(PostTickVisuals), nameof(PostTickVisuals.ProcessPostTickVisuals))]
    internal static class Patch_PostTickVisuals_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.PostTickVisuals, stamp); }
    }

    [HarmonyPatch(typeof(RimWorld.Planet.GlobalRendererUtility), nameof(RimWorld.Planet.GlobalRendererUtility.UpdateGlobalShadersParams))]
    internal static class Patch_GlobalRendererUtility_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.GlobalShaders, stamp); }
    }

    [HarmonyPatch(typeof(RimWorld.PlantFallColors), nameof(RimWorld.PlantFallColors.SetFallShaderGlobals))]
    internal static class Patch_PlantFallColors_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.FallColors, stamp); }
    }

    [HarmonyPatch(typeof(WaterInfo), nameof(WaterInfo.SetTextures))]
    internal static class Patch_WaterInfo_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.WaterTextures, stamp); }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDrawerUpdate_First))]
    internal static class Patch_MapDrawer_UpdateFirst_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.MapMeshFirst, stamp); }
    }

    [HarmonyPatch(typeof(RimWorld.GameConditionManager), nameof(RimWorld.GameConditionManager.GameConditionManagerDraw))]
    internal static class Patch_GameConditionManager_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.CondDraw, stamp); }
    }

    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.DrawDesignations))]
    internal static class Patch_DesignationManager_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.Designations, stamp); }
    }

    [HarmonyPatch(typeof(TemporaryThingDrawer), nameof(TemporaryThingDrawer.Draw))]
    internal static class Patch_TemporaryThingDrawer_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.TempThingDraw, stamp); }
    }

    [HarmonyPatch(typeof(AreaManager), nameof(AreaManager.AreaManagerUpdate))]
    internal static class Patch_AreaManager_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.AreaUpdate, stamp); }
    }

    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.FleckManagerUpdate))]
    internal static class Patch_FleckManager_Update_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.FleckSim, stamp); }
    }

    // ===== 第五轮（2026-09-24）：PostTickVisuals 子链拆解（签名全部经 RimWorldDecompiled 核实；
    // PawnFootprintMaker/PawnBreathMoteMaker 在 RimWorld 命名空间，其余在 Verse） =====

    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.ProcessPostTickVisuals))]
    internal static class Patch_RotationTracker_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.RotPTV, stamp); }
    }

    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation))]
    internal static class Patch_RotationTracker_UpdateRotation_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.UpdateRotation, stamp); }
    }

    [HarmonyPatch(typeof(JitterHandler), nameof(JitterHandler.ProcessPostTickVisuals))]
    internal static class Patch_JitterHandler_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.JitterPTV, stamp); }
    }

    [HarmonyPatch(typeof(RimWorld.PawnFootprintMaker), nameof(RimWorld.PawnFootprintMaker.ProcessPostTickVisuals))]
    internal static class Patch_FootprintMaker_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.FootprintPTV, stamp); }
    }

    [HarmonyPatch(typeof(RimWorld.PawnBreathMoteMaker), nameof(RimWorld.PawnBreathMoteMaker.ProcessPostTickVisuals))]
    internal static class Patch_BreathMoteMaker_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.BreathPTV, stamp); }
    }

    [HarmonyPatch(typeof(PawnWaterRippleMaker), nameof(PawnWaterRippleMaker.ProcessPostTickVisuals))]
    internal static class Patch_WaterRippleMaker_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.RipplePTV, stamp); }
    }

    [HarmonyPatch(typeof(PawnLeaner), nameof(PawnLeaner.ProcessPostTickVisuals))]
    internal static class Patch_PawnLeaner_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.LeanPTV, stamp); }
    }

    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.ProcessPostTickVisuals))]
    internal static class Patch_PawnRenderer_PTV_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.RendererPTV, stamp); }
    }

    [HarmonyPatch(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos), MethodType.Getter)]
    internal static class Patch_DrawTracker_DrawPos_PerfProbe
    {
        private static long stamp;
        internal static void Prefix() { stamp = RimExodusPerfProbe.Now(); }
        internal static void Postfix() { RimExodusPerfProbe.Record(RimExodusPerfProbe.DrawPosGet, stamp); }
    }
}
