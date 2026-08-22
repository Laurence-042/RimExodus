家园地块（通过定居或逆重飞船降落产生）不应因为滚动丢失，其他派系据点、扎营、远行队埋伏、遗迹建筑等原版中所有人员离开后被删除的地图不会在人员离开后删除，而是会通过滚动删除

当前家园地块的入口因为休眠消失了，但是已经到了删除距离后却没有删除，怀疑有什么实现偏差——按照我们的预期应该是不休眠不删除的，但当前实现错误地休眠了但却没有错误地删除，这让人怀疑其有隐含删除相关的bug
[RimExodus] Dormancy WAKE: map 0 (wt=97338) — entering map (CurrentMap switch)

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

更多可配置项，比如滚动时的唤醒距离、休眠距离、删除距离——尚需考虑哪些需要配置