# TODO

## 2026-09：PS 跨缝反复传送修复（方向意图门 rev4，`SeamlessPerspectiveShiftCompat`）— 主场景已验证，剩余抽查

- [x] 垂直/斜向过缝单次传送（含 dot=0.24 浅斜角场景，噪声地板 0.05 放行）
- [x] 恒定方向沿缝行走无连环弹射（rev3 符号修正后玩家实测通过；镜像对称保证任意阈值下不可能双侧过门）
- [x] 真掉头立即折返放行
- [ ] PS 驾驶载具（VF 双装）沿缝行驶/过缝复测（门已同款接入 inputDir，未实测）
- [ ] 跨缝战斗/撤离链零回归抽查（门只挂 PS 触发路径，理论零接触）

### 观察项（独立发现，未处理）

- **影子远行队期望集对 Settlement parent 无派系过滤**（`SeamlessShadowCaravan.Refresh` 的 `map.Parent is Settlement`）——原版玩家殖民地 parent 即 Settlement 类，玩家自己家园也挂影子：家园上跨缝出发的殖民者在注入窗口内 DeSpawn 静默失败，持续制造"Scrubbed N stale spawnedThings"噪音（自愈机制在工作，功能无损）。是否给玩家派系据点加过滤待定夺（2026-09 PS 振荡日志顺带暴露）。
