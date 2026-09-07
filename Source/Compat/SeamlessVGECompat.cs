using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RimExodus
{
    /// <summary>
    /// Vanilla Gravship Expanded 世界炮击兼容。
    ///
    /// VGE 的 Projectile_Artillery 先在发射图飞向方形真边界，抵边后才转换为世界炮弹。
    /// 无缝弹丸交接若在六边形接缝处先把它搬进邻图，原定方形边界会变成邻图内部坐标；
    /// VGE 永远等不到抵边转换，且会压掉带有效 targetTile 弹丸的 Impact，最终原地卡死。
    /// 因此仅让“有效世界目标 != 当前图”的 VGE 炮弹保留在当前图，交还 VGE 自己出图。
    /// 抵达目标图后新生成的落地弹丸 targetTile 无效，不受此门影响。
    /// </summary>
    internal static class SeamlessVGECompat
    {
        private static readonly System.Type ArtilleryProjectileType =
            AccessTools.TypeByName("VanillaGravshipExpanded.Projectile_Artillery");

        private static readonly FieldInfo TargetTileField =
            ArtilleryProjectileType == null ? null : AccessTools.Field(ArtilleryProjectileType, "targetTile");

        internal static bool IsWorldArtilleryLeavingMap(Projectile projectile)
        {
            if (ArtilleryProjectileType == null || TargetTileField == null
                || projectile == null || !ArtilleryProjectileType.IsInstanceOfType(projectile))
            {
                return false;
            }

            return TargetTileField.GetValue(projectile) is PlanetTile targetTile
                && targetTile.Valid
                && targetTile != projectile.Tile;
        }
    }
}
