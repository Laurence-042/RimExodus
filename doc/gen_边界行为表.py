# -*- coding: utf-8 -*-
"""生成 doc/边界行为表.md 的"二、行为表"章节（主体 × 移动来源 × 目标格 全组合）。

用法：python doc/gen_边界行为表.py
脚本读取 doc/边界行为表.md，替换 <!-- GEN:行为表 BEGIN --> ... <!-- GEN:行为表 END -->
标记之间的内容。改行为规则直接改本文件的规则区，重跑即可再生。

定稿语义（2026-08 用户定稿）：
- 预加载只能由玩家操作触发。
- NPC 撤离场景（撤离 duty/囚犯越狱/野性恐慌逃跑）：对端未加载 → 正常原生撤离；
  对端已加载 → 传送进对端继续撤离，直到到达没有加载对端的撤离点（视为跑出视野）。
- 玩家征召 goto 传送点格（殖民者）= 显式要求脱离地图 → 原生撤离/组队，不跨图。
  殖民地机械族保留原版 !IsColonyMech 例外（站住）。
- 跨图 goto 覆盖：殖民者、殖民地机械族、被征召的驯养动物。
- 敌方主体战斗移动（追击/走位）踩已加载传送点 → 传送（跨图追击）。
- 驯养动物跟随主人踩已加载传送点 → 传送；其余闲逛/工作/无 flag 逃跑一律无事。
- 目标格不是传送点的行，传送点不激活（踩点两列恒"—"）。
"""
import io
import os

DOC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "边界行为表.md")
BEGIN = "<!-- GEN:行为表 BEGIN -->"
END = "<!-- GEN:行为表 END -->"

# ---------------------------------------------------------------------------
# 规则区（按需修改后重跑脚本）
# ---------------------------------------------------------------------------

# 主体：(名称, 是否动物, 身份说明)
SUBJECTS = [
    ("殖民者", False, "玩家阵营人形"),
    ("殖民地机械族", False, "IsColonyMech"),
    ("敌方人形", False, "敌对派系"),
    ("敌方机械族", False, "Mechanoids"),
    ("盟友增援", False, "友军 NPC（LordJob_AssistColony）"),
    ("访客/旅行者/商队", False, "非敌对 NPC（VisitColony/TravelAndExit/TradeWithColony）"),
    ("囚犯", False, "玩家囚徒"),
    ("野生动物", True, "无阵营"),
    ("驯养动物", True, "玩家阵营动物（含可征召）"),
]

# 目标格类型（图内普通格不可能踩到传送点，不单列）
TARGETS = ["传送点格（=接缝带格）", "预加载边界带非传送点格（距 void 3-15 格）"]

# 行为取值（踩点两列的组合）
ACT_TRANSFER_CONT = "**传送** + 续程 Goto"
ACT_TRANSFER_EXIT = "**传送** + 对端续发撤离 job（继续跑，直到无加载对端的出口→原生撤离）"
ACT_TRANSFER_PLAIN = "**传送**"
ACT_NATIVE_EXIT = "原生撤离/组队（身份受限）"
ACT_NOTHING = "无事"
NA = "—（目标非传送点，传送点不激活）"

# 撤离类来源对 spot 的行为（对端已加载/未生成）
EXIT_DUTY = (ACT_TRANSFER_EXIT, "原生撤离（视为跑出视野）")


def spot_behaviors(flag, eligible, act_loaded, act_unloaded):
    if flag:
        return act_loaded, act_unloaded
    if eligible:
        return act_loaded, ACT_NOTHING
    return ACT_NOTHING, ACT_NOTHING


# 移动来源：每条 = (名称, 适用主体谓词, 规则函数(主体名, 是否动物, 目标key) -> dict 或 None)
# dict 键：expect（玩家预期/场景分类）、flag、elig（传送资格）、loaded、unloaded、
#          preload（spot/band 两键）、vanilla、patch
SOURCES = [
    (
        "玩家征召 goto（右键/拖框）",
        lambda name, animal: name in ("殖民者", "殖民地机械族"),
        lambda name, animal, tgt: {
            "殖民者": {
                "expect": "玩家要求殖民者快速脱离地图成为远行队" if tgt == "spot" else "玩家要求殖民者去开新图",
                "flag": "有（DraftedMove 原生：点击格是 exit cell）" if tgt == "spot" else "无（点击格非 exit cell 不设）",
                "elig": False,
                "loaded": ACT_NATIVE_EXIT if tgt == "spot" else ACT_NOTHING,
                "unloaded": ACT_NATIVE_EXIT if tgt == "spot" else ACT_NOTHING,
                "preload": {"spot": "否（待办：现状会误触发，需排除传送点格）",
                            "band": "是（现状已生效：playerForced 分支）"},
                "vanilla": "出口格→撤离/组队" if tgt == "spot" else "无事",
                "patch": "现有（ExitMapGrid 标传送点为出口格）" if tgt == "spot"
                    else "现有（预加载已有）",
            },
            "殖民地机械族": {
                "expect": "保留原版例外（不自行离图）；跨图移动走跨图 goto",
                "flag": "无（原版 `!IsColonyMech` 例外）" if tgt == "spot" else "无（点击格非 exit cell 不设）",
                "elig": False,
                "loaded": ACT_NOTHING,
                "unloaded": ACT_NOTHING,
                "preload": {"spot": "否（待办：同殖民者，排除传送点格）",
                            "band": "是（现状已生效：playerForced 分支）"},
                "vanilla": "无事（mech 不设 flag，不撤离）",
                "patch": "无需（原版例外自动生效）",
            },
        }[name],
    ),
    (
        "跨图 goto（mod 注入桥接）",
        lambda name, animal: name in ("殖民者", "殖民地机械族", "驯养动物"),
        lambda name, animal, tgt: None if tgt != "spot" else {
            "expect": {
                "殖民者": "玩家要求殖民者去新地图",
                "殖民地机械族": "玩家要求机械族去新地图（需扩展桥接注入对象）",
                "驯养动物": "玩家要求被征召动物去新地图（需扩展桥接注入对象）",
            }[name],
            "flag": "无",
            "elig": True,
            "loaded": ACT_TRANSFER_CONT,
            "unloaded": "不涉及（对端必须先生成才能 goto）",
            "preload": {"spot": "不涉及（对端必须先生成才能 goto）", "band": "不涉及"},
            "vanilla": "不存在",
            "patch": "现有 + 待办（桥接注入扩展至机械族/被征召动物；桥接 job 需带传送许可标记）",
        },
    ),
    (
        "AI 闲逛/工作移动",
        lambda name, animal: True,
        lambda name, animal, tgt: {
            "expect": {
                "殖民者": "殖民者不该瞎逛到另一地图然后回不来",
                "殖民地机械族": "同殖民者",
                "敌方人形": "等待突袭的敌人不该瞎逛到另一地图然后回不来",
                "敌方机械族": "同敌方人形",
                "盟友增援": "同敌人（NPC）",
                "访客/旅行者/商队": "同敌人（NPC）",
                "囚犯": "同敌人（NPC）",
                "野生动物": "同敌人（NPC）",
                "驯养动物": "同殖民者（不该瞎逛到另一地图）",
            }[name],
            "flag": "无（GotoWander/工作 job 均不带）",
            "elig": False,
            "loaded": ACT_NOTHING,
            "unloaded": ACT_NOTHING,
            "preload": {"spot": "否", "band": "否"},
            "vanilla": "无事（可停在出口格但不离图）",
            "patch": "收紧（现状无资格即传，需收紧为不传）",
        },
    ),
    (
        "AI 战斗移动（追击/走位）",
        lambda name, animal: name in ("敌方人形", "敌方机械族", "盟友增援"),
        lambda name, animal, tgt: {
            "expect": "玩家不能通过跨图逃离追击，除非敌人和旧地图一起被清理",
            "flag": "无（AIFightEnemy 等不带）",
            "elig": True,
            "loaded": ACT_TRANSFER_PLAIN,
            "unloaded": ACT_NOTHING,
            "preload": {"spot": "否", "band": "否"},
            "vanilla": "无事",
            "patch": "已实现（2026-08 阶段5：TryRegisterCombatStepTransfer——NPC 战斗体+战斗 job（AttackMelee/AttackStatic/战斗 lord duty 下 Goto）+追击目标在对端图才即席 Pursue 许可；MarkPursuers 意图三判据——Attack 系 job / mindState.enemyTarget / 推进期 Goto 的 targetA=刚跨图者（还能跨缝射则原地打）；主动推进亦落地——GotoNearestHostile 跨图版取邻图最近 → StartPath 结构判据桥接过缝 → Bridge 落地收编袭击 lord（防无 lord 被 JobGiver_ExitMap 离场“过缝秒退”））",
        },
    ),
    (
        "AI 逃跑（flee，无 flag）",
        lambda name, animal: name != "殖民地机械族",
        lambda name, animal, tgt: {
            "expect": "原版 AI 因恐慌逃跑时不会跑出地图；按 NPC 原则不该跨图",
            "flag": "无（普通 Flee 不设）",
            "elig": False,
            "loaded": ACT_NOTHING,
            "unloaded": ACT_NOTHING,
            "preload": {"spot": "否", "band": "否"},
            "vanilla": "无事",
            "patch": "收紧（现状无资格即传，需收紧为不传）",
        },
    ),
    (
        "撤离 duty（袭击撤退/盟友·访客·商队·旅行者离场/囚犯越狱）",
        lambda name, animal: name in ("敌方人形", "敌方机械族", "盟友增援", "访客/旅行者/商队", "囚犯"),
        lambda name, animal, tgt: {
            "expect": "敌人需要能进入对端已加载地图继续跑，直到其到达没有加载对端地图的撤离点（视为跑出玩家视野）",
            "flag": "有（JobGiver_ExitMap / JobGiver_PrisonerEscape）",
            "elig": False,
            "loaded": EXIT_DUTY[0],
            "unloaded": EXIT_DUTY[1],
            "preload": {"spot": "否", "band": "否"},
            "vanilla": "出口格→撤离（NPC 只能加入现有队）",
            "patch": "分流（现状 flag 一律放行原生撤离；需改为对端已加载→传送+续发撤离）",
        },
    ),
    (
        "野生动物恐慌逃跑（原生 50% Flee+flag）",
        lambda name, animal: name == "野生动物",
        lambda name, animal, tgt: {
            "expect": "逃跑的动物需要能进入对端已加载地图继续跑，以此允许玩家跨图追猎，直到其到达没有加载对端地图的撤离点（视为跑出玩家视野）。未掷中 flag 的 flee 不跨图（原则同上）",
            "flag": "有（Pawn_MindState 原生）",
            "elig": False,
            "loaded": EXIT_DUTY[0],
            "unloaded": EXIT_DUTY[1],
            "preload": {"spot": "否", "band": "否"},
            "vanilla": "出口格→撤离（ExitMap 回世界）",
            "patch": "分流（同撤离 duty：对端已加载→传送继续跑）",
        },
    ),
    (
        "驯养动物跟随主人",
        lambda name, animal: name == "驯养动物",
        lambda name, animal, tgt: {
            "expect": "跟随主人时应该能在主人跨过接缝前往新地图时跟随主人；但不该独立触发地图生成",
            "flag": "无（Follow/FollowClose 不带）",
            "elig": True,
            "loaded": ACT_TRANSFER_PLAIN,
            "unloaded": ACT_NOTHING,
            "preload": {"spot": "否", "band": "否"},
            "vanilla": "无事",
            "patch": "收紧（现状『动物即传』过宽，应收窄为跟随 job；跟随传送保留）",
        },
    ),
]

TARGET_KEYS = ["spot", "band"]


def build_table():
    lines = []
    lines.append("全组合枚举（脚本 `doc/gen_边界行为表.py` 生成，改规则后重跑再生）。")
    lines.append("判定只看 pawn 实际踩到传送点格且该移动的目标/意图指向跨图；")
    lines.append("目标格非传送点的行，传送点一概不激活（踩点两列恒为\"—\"）。动物分野生/驯养，行为不同。")
    lines.append("")
    lines.append("| # | 主体 | 移动来源 | 目标格 | flag | 玩家预期/场景分类 | 传送资格 | 踩传送点(已加载) | 踩传送点(未生成) | 预加载 | 原版行为 | patch 依赖 |")
    lines.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
    n = 0
    for s_name, s_animal, _s_desc in SUBJECTS:
        for src_name, applies, make_rule in SOURCES:
            if not applies(s_name, s_animal):
                continue
            for tgt, key in zip(TARGETS, TARGET_KEYS):
                rule = make_rule(s_name, s_animal, key)
                if rule is None:
                    continue
                loaded, unloaded = (rule["loaded"], rule["unloaded"]) if key == "spot" else (NA, NA)
                elig = "有" if rule["elig"] else "无"
                n += 1
                lines.append(
                    "| %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
                        n, s_name, src_name, tgt, rule["flag"], rule["expect"], elig,
                        loaded, unloaded, rule["preload"][key],
                        rule["vanilla"], rule["patch"],
                    )
                )
    return "\n".join(lines)


def main():
    with io.open(DOC, "r", encoding="utf-8") as f:
        content = f.read()
    i, j = content.index(BEGIN), content.index(END)
    table = build_table()
    rows = len([ln for ln in table.splitlines() if ln.startswith("| ")]) - 1
    new = content[: i + len(BEGIN)] + "\n" + table + "\n\n" + content[j:]
    with io.open(DOC, "w", encoding="utf-8") as f:
        f.write(new)
    print("regenerated %d rows" % rows)


if __name__ == "__main__":
    main()
