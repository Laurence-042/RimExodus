其他派系据点需要提上日程
[RimExodus] World tile 77535 is occupied by Settlement without a live map, skip generation.
UnityEngine.StackTraceUtility:ExtractStackTrace ()
Verse.Log:Warning (string)
RimExodus.SeamlessTileManager:GenerateTileMap (int,int,Verse.IntVec3)
RimExodus.SeamlessTileManager:TryPreloadNeighbor (int)
RimExodus.SeamlessTilePreloader:ConsumeQueued ()
RimExodus.SeamlessTileManager:MapComponentTick ()
Verse.MapComponentUtility:MapComponentTick (Verse.Map)
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Map.MapPostTick_Patch2 (Verse.Map)
Verse.TickManager:DoSingleTick ()
Verse.TickManager:TickManagerUpdate ()
Verse.Game:UpdatePlay ()
Verse.Root_Play:Update ()
按理说这个实现应该也比较简单，正常按照据点地图生成（GetOrGenerateMapUtility.GetOrGenerateMap）。如果是好感度低到敌对的，正常结束，玩家会看到全是敌人的据点。如果好感度使其可以交易，那么额外从生成的对应据点pawn里找到满足以下条件且价格最高的pawn，将其设置为settlement.trader.TraderKind类型的贸易商
- 是对应据点派系成员
- 非动物、机械体
- 可以承担社交工作
- 当前可以行动（意识、移动能力不能太低）
- 并非已经是贸易商
- 并非囚犯、奴隶（按理说会被承担社交工作的过滤给过滤掉）
- 在可达位置（可以在室内，但玩家的pawn必须能正常走到其所在位置）

更多可配置项，比如滚动时的唤醒距离、休眠距离、删除距离——尚需考虑还需要哪些配置
