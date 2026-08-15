# -*- coding: utf-8 -*-
"""生成 doc/边界行为表.md 的"二、行为表"章节（主体 × 移动来源 × 目标格 全组合）。

用法：python doc/gen_边界行为表.py
脚本读取 doc/边界行为表.md，替换 <!-- GEN:行为表 BEGIN --> ... <!-- GEN:行为表 END -->
标记之间的内容。改行为规则直接改本文件的规则区，重跑即可再生。

行为语义（与文档第三章一致）：
- flag = job.exitMapOnArrival：撤离/远行队资格（原生链，RimExodus 不干预）。
- 传送资格 = 跨图登记 ∨ 动物。只看"踩上传送点格"，与目标格无关。
- 预加载 = 玩家征召 goto 且目标在预加载边界带（15 格）内且非传送点格（待办②收紧后）。
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
    ("驯养动物", True, "玩家阵营动物"),
]

# 目标格类型（图内普通格不可能踩到传送点，不单列）
TARGETS = ["传送点格（=接缝带格）", "预加载边界带非传送点格（距 void 3-15 格）"]

# 移动来源：每条 = (名称, 适用主体谓词, 规则函数)
# 规则函数 subject(名称, 是否动物, 目标格 key) -> dict，键：
#   flag      : "有"/"无"/自定义文案（如 mech 例外）；为 None 表示该目标组合不适用（不生成行）
#   eligible  : True=有传送资格（动物或跨图登记）
#   cont      : 传送后是否续程 Goto（仅跨图登记）
#   vanilla   : 原版行为文案
#   patch     : patch 依赖文案
#   preload   : 该目标格的预加载文案
def _no_preload():
    return "否"


SOURCES = [
    (
        "玩家征召 goto（右键/拖框）",
        lambda name, animal: name in ("殖民者", "殖民地机械族"),
        lambda name, animal, tgt: {
            "flag": (
                ("有（DraftedMove 原生：点击格是 exit cell）" if name == "殖民者"
                 else "无（原版 `!IsColonyMech` 例外）")
                if tgt == "spot" else "无（点击格非 exit cell 不设）"
            ),
            "eligible": False,
            "cont": False,
            "vanilla": "出口格→撤离/组队" if (tgt == "spot" and name == "殖民者")
                else "无事（mech 不设 flag，不撤离）" if name == "殖民地机械族" else "无事",
            "patch": "现有（ExitMapGrid 标传送点为出口格）" if (tgt == "spot" and name == "殖民者")
                else "无需（原版例外自动生效）" if name == "殖民地机械族"
                else "现有（预加载已有；踩点无事待待办①收紧）",
            "preload": {
                "spot": "否（待办②：现状会误触发，需排除传送点格）",
                "band": "是（现状已生效：playerForced 分支）",
            }[tgt],
        },
    ),
    (
        "跨图 goto（mod 注入桥接）",
        lambda name, animal: name == "殖民者",
        lambda name, animal, tgt: None if tgt != "spot" else {
            "flag": "无",
            "eligible": True,
            "cont": True,
            "vanilla": "不存在",
            "patch": "现有 + 待办③（桥接 job 需带传送许可标记）",
            "preload": "无需（对端隐式已加载）",
        },
    ),
    (
        "AI 闲逛/工作移动",
        lambda name, animal: True,
        lambda name, animal, tgt: {
            "flag": "无（GotoWander/工作 job 均不带）",
            "eligible": animal,
            "cont": False,
            "vanilla": "无事（可停在出口格但不离图）",
            "patch": "现有（动物即传，保留）" if animal else "收紧（待办①：现状无资格即传）",
            "preload": _no_preload(),
        },
    ),
    (
        "AI 战斗移动（追击/走位）",
        lambda name, animal: name in ("敌方人形", "敌方机械族", "盟友增援"),
        lambda name, animal, tgt: {
            "flag": "无（AIFightEnemy 等不带）",
            "eligible": False,
            "cont": False,
            "vanilla": "无事",
            "patch": "收紧（待办①）",
            "preload": _no_preload(),
        },
    ),
    (
        "AI 逃跑（flee，无 flag）",
        lambda name, animal: name != "殖民地机械族",
        lambda name, animal, tgt: {
            "flag": "无（普通 Flee 不设）",
            "eligible": animal,
            "cont": False,
            "vanilla": "无事",
            "patch": "现有（动物即传）" if animal else "收紧（待办①）",
            "preload": _no_preload(),
        },
    ),
    (
        "野生动物恐慌逃跑（原生 50% Flee+flag）",
        lambda name, animal: name == "野生动物",
        lambda name, animal, tgt: {
            "flag": "有（Pawn_MindState 原生）",
            "eligible": False,
            "cont": False,
            "vanilla": "出口格→撤离（ExitMap 回世界）",
            "patch": "现有（RCellFinder 出口导向）",
            "preload": _no_preload(),
        },
    ),
    (
        "撤离 duty（袭击撤退/盟友·访客·商队·旅行者离场）",
        lambda name, animal: name in ("敌方人形", "敌方机械族", "盟友增援", "访客/旅行者/商队"),
        lambda name, animal, tgt: {
            "flag": "有（JobGiver_ExitMap）",
            "eligible": False,
            "cont": False,
            "vanilla": "出口格→撤离（NPC 只能加入现有队）",
            "patch": "现有（RCellFinder 出口导向）",
            "preload": _no_preload(),
        },
    ),
    (
        "囚犯越狱",
        lambda name, animal: name == "囚犯",
        lambda name, animal, tgt: {
            "flag": "有（JobGiver_PrisonerEscape）",
            "eligible": False,
            "cont": False,
            "vanilla": "出口格→撤离",
            "patch": "现有（RCellFinder 出口导向）",
            "preload": _no_preload(),
        },
    ),
    (
        "驯养动物跟随主人",
        lambda name, animal: name == "驯养动物",
        lambda name, animal, tgt: {
            "flag": "无（Follow/FollowClose 不带）",
            "eligible": True,
            "cont": False,
            "vanilla": "无事",
            "patch": "现有（动物即传）。⚠ 踩点会与主人分图，待用户定夺",
            "preload": _no_preload(),
        },
    ),
]

TARGET_KEYS = ["spot", "band"]


def spot_behaviors(rule):
    """返回 (踩点已加载, 踩点未生成)。"""
    if rule["flag"].startswith("有"):
        return ("原生撤离/组队（身份受限）", "同左（不依赖对端）")
    if rule["eligible"]:
        return ("**传送**" + ("+ 续程 Goto" if rule["cont"] else ""), "无事")
    return ("无事", "无事")


def build_table():
    lines = []
    lines.append("全组合枚举（脚本 `doc/gen_边界行为表.py` 生成，改规则后重跑再生）。")
    lines.append("传送/撤离判定只看 pawn 实际踩到传送点格，与该格是目标还是路径经过无关；")
    lines.append("目标格列影响的是**预加载触发**与到达后站位。动物 = 野生 + 驯养。")
    lines.append("")
    lines.append("| # | 主体 | 移动来源 | 目标格 | flag | 传送资格 | 踩传送点(已加载) | 踩传送点(未生成) | 预加载 | 原版行为 | patch 依赖 |")
    lines.append("|---|---|---|---|---|---|---|---|---|---|---|")
    n = 0
    for s_name, s_animal, _s_desc in SUBJECTS:
        for src_name, applies, make_rule in SOURCES:
            if not applies(s_name, s_animal):
                continue
            for tgt, key in zip(TARGETS, TARGET_KEYS):
                rule = make_rule(s_name, s_animal, key)
                if rule is None:
                    continue
                loaded, unloaded = spot_behaviors(rule)
                elig = "有（动物）" if (rule["eligible"] and not rule["cont"]) else (
                    "有（跨图登记）" if rule["eligible"] else "无")
                n += 1
                lines.append(
                    "| %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % (
                        n, s_name, src_name, tgt, rule["flag"], elig,
                        loaded, unloaded, rule["preload"],
                        rule["vanilla"], rule["patch"],
                    )
                )
    return "\n".join(lines)


def main():
    with io.open(DOC, "r", encoding="utf-8") as f:
        content = f.read()
    i, j = content.index(BEGIN), content.index(END)
    table = build_table()
    rows = table.count("\n| ") + 1
    new = content[: i + len(BEGIN)] + "\n" + table + "\n\n" + content[j:]
    with io.open(DOC, "w", encoding="utf-8") as f:
        f.write(new)
    print("regenerated %d rows" % rows)


if __name__ == "__main__":
    main()
