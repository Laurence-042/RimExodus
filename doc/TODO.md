- [x] 已处置（2026-08-29，待游戏内回归）：卸载前恢复原版兼容模式。根因定案：mod 移除后存档里的 MapParent_SeamlessTile WorldObject 类解析失败 → map.info.parent == null → Map.Tile = PlanetTile.Invalid(-1) → WorldGrid[-1] 越界（每帧 UI 读 CurrentMap.Biome 触发循环红字）；RimExodus_Void 等 def 失落为第二症状。实现 = ①三层快照紧凑序列化（SeamlessBaseSnapshotData，仅原生 parent 图，terrain 稠密 ushort+def 表 / building/roof 稀疏；开关 serializeBaseSnapshots 默认开，关时二次确认 + 可选清除已有快照）——读档后内存快照可用，卸载恢复不再受"同会话"限制；②设置高级诊断 tab"恢复原版兼容模式（卸载前）"按钮：原生图 void 格按快照回填（terrain/roof/岩石重生，无快照降级就近复制边界地形）+ 删传送点/岩链 + 清 RimExodus job/贸易商引用 + 删全部地块图（RemoveTileMap 语义）+ 拆影子/清手动休眠锁/天气域重算；地块图有玩家 pawn 或分帧生成中拒绝执行。恢复只做 void 格（接缝带混合差异属观感、原版可加载）。机制见 AGENTS.md 存档兼容性说明节。
Root level exception in OnGUI(): System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
Parameter name: index
[Ref 21E89298]
 [0x0000c] in <51fded79cd284d4d911c5949aff4cb21>:0 
  at System.Collections.Generic.List`1[T].get_Item (System.Int32 index) [0x00009] in <51fded79cd284d4d911c5949aff4cb21>:0 
  at RimWorld.Planet.WorldGrid.get_Item (RimWorld.Planet.PlanetTile tile) [0x0000c] in <61e4173561894da49d210260257b5097>:0 
  at Verse.Map.get_TileInfo () [0x00013] in <61e4173561894da49d210260257b5097>:0 
  at Verse.Map.get_Biome () [0x00000] in <61e4173561894da49d210260257b5097>:0 
  at Verse.GenUI.BackgroundDarkAlphaForText () [0x0001c] in <61e4173561894da49d210260257b5097>:0 
  at Verse.GenUI.DrawTextWinterShadow (UnityEngine.Rect rect) [0x00000] in <61e4173561894da49d210260257b5097>:0 
  at Verse.MouseoverReadout.MouseoverReadoutOnGUI () [0x00037] in <61e4173561894da49d210260257b5097>:0 
  at RimWorld.MapInterface.MapInterfaceOnGUI_BeforeMainTabs () [0x000fe] in <61e4173561894da49d210260257b5097>:0 
  at RimWorld.UIRoot_Play.UIRootOnGUI () [0x00024] in <61e4173561894da49d210260257b5097>:0 
    - PREFIX Dubwise.PerformanceAnalyzer: Void Analyzer.H_KeyPresses:OnGUI()
  at Verse.Root.OnGUI () [0x00040] in <61e4173561894da49d210260257b5097>:0 
UnityEngine.StackTraceUtility:ExtractStackTrace ()
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log.Error_Patch2 (string)
Verse.Root:OnGUI ()


- 距离友方基地很远后，友方基地不会休眠和删除，这是个问题——注意删除是删除地图，不要把worldpawn和据点全删了