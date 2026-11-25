# -*- coding: utf-8 -*-
"""Generate SVG design mockups for the Rocket Assembler window reset.
Vanilla RimWorld palette only:
  #15191D = Widgets.WindowBGFillColor
  #616C7A = Widgets.WindowBGBorderColor
  #DBB40C = ColorLibrary.Gold
  #FF3333 = ColorLibrary.RedReadable
All other values are neutral white-alpha overlays on the vanilla window fill.
"""
import os, html

BG   = "#15191D"
DEEP = "#101418"
PANEL = "#1E2226"
PANEL2 = "#23282E"
LINE = "#2E363F"
LINE2 = "#616C7A"
TEXT = "#FFFFFF"
DIM = "#A5A9AD"
FAINT = "#6E747B"
GOLD = "#DBB40C"
RED = "#FF3333"
LEAD = "#3E4854"
TANK = "#262C33"; TANK_S = "#46515C"
ENG = "#2E2718"; ENG_S = "#9C8018"
OTH = "#22272D"; OTH_S = "#39424C"

FONT = "Microsoft YaHei, 'Noto Sans SC', 'Segoe UI Symbol', sans-serif"


def e(s):
    return html.escape(str(s), quote=False)


def r(x, y, w, h, fill="none", stroke=None, sw=1, rx=0, fo=None, so=None, dash=None):
    a = [f'x="{x:g}"', f'y="{y:g}"', f'width="{w:g}"', f'height="{h:g}"', f'fill="{fill}"']
    if fo is not None:
        a.append(f'fill-opacity="{fo:g}"')
    if stroke:
        a += [f'stroke="{stroke}"', f'stroke-width="{sw:g}"']
    if so is not None:
        a.append(f'stroke-opacity="{so:g}"')
    if rx:
        a.append(f'rx="{rx:g}"')
    if dash:
        a.append(f'stroke-dasharray="{dash}"')
    return "<rect " + " ".join(a) + "/>"


def poly(pts, fill, stroke=None, sw=1):
    p = " ".join(f"{x:g},{y:g}" for x, y in pts)
    a = [f'points="{p}"', f'fill="{fill}"']
    if stroke:
        a += [f'stroke="{stroke}"', f'stroke-width="{sw:g}"']
    return "<polygon " + " ".join(a) + "/>"


def t(x, y, s, size=11, fill=TEXT, anchor="start", weight=400, ls=0, op=None, rot=False):
    a = [f'x="{x:g}"', f'y="{y:g}"', f'font-size="{size:g}"', f'fill="{fill}"',
         f'text-anchor="{anchor}"']
    if weight != 400:
        a.append(f'font-weight="{weight}"')
    if ls:
        a.append(f'letter-spacing="{ls:g}"')
    if op is not None:
        a.append(f'opacity="{op:g}"')
    if rot:
        a.append(f'transform="rotate(-90 {x:g} {y:g})"')
    return "<text " + " ".join(a) + ">" + e(s) + "</text>"


def ln(x1, y1, x2, y2, stroke=LINE, sw=1, op=None, dash=None):
    a = [f'x1="{x1:g}"', f'y1="{y1:g}"', f'x2="{x2:g}"', f'y2="{y2:g}"',
         f'stroke="{stroke}"', f'stroke-width="{sw:g}"']
    if op is not None:
        a.append(f'stroke-opacity="{op:g}"')
    if dash:
        a.append(f'stroke-dasharray="{dash}"')
    return "<line " + " ".join(a) + "/>"


def chip(x, y, w, h, label, active=False, size=10):
    if active:
        body = r(x, y, w, h, GOLD, GOLD, 1, fo=0.14)
        col = GOLD
    else:
        body = r(x, y, w, h, TEXT, TEXT, 1, fo=0.03, so=0.12)
        col = DIM
    return body + t(x + w / 2, y + h / 2 + 3.5, label, size, col, "middle")


def button(x, y, w, h, label, kind="sec", size=11):
    if kind == "dis":
        base, top, bot, edge, txt = "#3E382F", "#4A4238", "#2E2922", "#2A251E", "#8E887E"
    elif kind == "danger":
        base, top, bot, edge, txt = "#6A5130", "#7D6133", "#4F3D21", "#010101", "#FF4D4D"
    else:
        base, top, bot, edge, txt = "#6A5130", "#7D6133", "#4F3D21", "#010101", "#FFFFFF"
    th = max(3, int(round(h * 0.16)))
    bh = max(4, int(round(h * 0.20)))
    o = [r(x, y, w, h, base, edge, 1),
         r(x + 1, y + 1, w - 2, th, top),
         r(x + 1, y + h - 1 - bh, w - 2, bh, bot),
         ln(x + 1, y + 1, x + w - 1, y + 1, "#8A6E40" if kind != "dis" else "#5A5045", 1, op=0.8),
         ln(x + 1, y + h - 1, x + w - 1, y + h - 1, "#281F11", 1)]
    cy = y + h / 2 + 4
    if kind != "dis":
        o.append(t(x + w / 2 + 1, cy + 1, label, size, "#000000", "middle", op=0.5))
    o.append(t(x + w / 2, cy, label, size, txt, "middle"))
    return "".join(o)


def panel(x, y, w, h):
    return r(x, y, w, h, PANEL, LINE, 1)


def frame(w, h, title, sub):
    o = [r(0, 0, w, h, BG, LINE2, 1),
         r(0, 0, w, 44, DEEP),
         ln(0, 44, w, 44, LINE),
         t(20, 29, title, 18, TEXT, weight=600),
         t(160, 29, sub, 9, FAINT, ls=2)]
    o.append(t(w - 34, 29, "×", 16, DIM))
    return o


def status(w, state, rows, work, readouts, warn=False):
    y = 52
    o = [r(16, y, w - 32, 32, PANEL, LINE, 1),
         r(16, y, 3, 32, GOLD if not warn else RED)]
    o.append(r(28, y + 13, 6, 6, GOLD if not warn else RED))
    o.append(t(42, y + 21, state, 11, TEXT))
    o.append(ln(120, y + 8, 120, y + 24, LINE))
    o.append(t(132, y + 21, rows, 10, DIM))
    o.append(ln(250, y + 8, 250, y + 24, LINE))
    o.append(t(262, y + 21, work, 10, DIM))
    x = w - 32
    for s, col in reversed(readouts):
        o.append(t(x, y + 21, s, 10, col, "end"))
        x -= 130
    return o


def preview_panel(x, y, w, h, wide=False):
    o = [panel(x, y, w, h),
         r(x, y, w, 28, PANEL2),
         t(x + 8, y + 19, "侧视图", 11, TEXT),
         t(x + 58, y + 19, "/ SIDE ELEVATION", 9, FAINT, ls=1),
         t(x + w - 8, y + 19, "1 m ≈ 10.0 px · 100%", 9, FAINT, "end"),
         ln(x, y + 28, x + w, y + 28, LINE)]
    # cassette grid
    gx0, gy0, gx1, gy1 = x + 1, y + 28, x + w - 1, y + h - 1
    gx = gx0
    while gx < gx1:
        o.append(ln(gx, gy0, gx, gy1, TEXT, 1, op=0.028))
        gx += 48
    gy = gy0
    while gy < gy1:
        o.append(ln(gx0, gy, gx1, gy, TEXT, 1, op=0.028))
        gy += 48
    # cassette viewfinder brackets
    bx0, by0, bx1, by1 = x + 8, y + 36, x + w - 8, y + h - 8
    arm = 12
    for (cx, cy, dx, dy) in ((bx0, by0, 1, 1), (bx1, by0, -1, 1),
                             (bx0, by1, 1, -1), (bx1, by1, -1, -1)):
        o.append(ln(cx, cy, cx + dx * arm, cy, LINE2, 1, op=0.55))
        o.append(ln(cx, cy, cx, cy + dy * arm, LINE2, 1, op=0.55))
    return o


def rocket(cx_core, top, bot, wide=False):
    o = []
    core_x = cx_core - 20
    bl = cx_core - 66
    br = cx_core + 34
    cols = [(bl, 32), (core_x, 40), (br, 32)]
    # top cap
    o.append(r(cx_core - 14, top - 24, 28, 24, OTH, OTH_S, 1))
    for i, (x, wd) in enumerate(cols):
        o.append(r(x, top, wd, bot - top, TANK, TANK_S, 1))
        # tank segment seams
        yy = top + 46
        while yy < bot - 8:
            o.append(ln(x, yy, x + wd, yy, TANK_S, 1, op=0.55))
            yy += 46
        # engine
        ex = x + 3
        o.append(poly([(ex, bot), (ex + wd - 6, bot), (ex + wd - 3, bot + 16), (ex - 3, bot + 16)],
                      ENG, ENG_S, 1))
        o.append(ln(ex - 1, bot + 16, ex + wd - 4, bot + 16, GOLD, 1, op=0.55))
    return o


def preview_labels(x, y, cx, selected=True):
    o = []
    items = [
        ("控制核心", 140, 140, cx - 20),
        ("二级液氧煤油罐", 252, 252, cx - 68),
        ("一级液氧煤油大罐", 334, 334, cx - 68),
        ("小推力液氧煤油机", 470, 478, cx - 68),
    ]
    for i, (label, ty, py, px) in enumerate(items):
        hot = selected and i == 1
        col = GOLD if hot else DIM
        line_col = GOLD if hot else LINE
        lw = 1
        o.append(t(x + 108, ty, label, 9, col, "end"))
        o.append(ln(x + 112, ty, x + 146, ty, LEAD if not hot else GOLD, lw,
                    op=1 if hot else 0.9))
        o.append(ln(x + 146, ty, x + 146, py, LEAD if not hot else GOLD, lw,
                    op=1 if hot else 0.9))
        o.append(ln(x + 146, py, x + px, py, LEAD if not hot else GOLD, lw,
                    op=1 if hot else 0.9))
    return o


def legend(x, y):
    o = [t(x, y, "图例", 9, FAINT)]
    sw = [("引擎", ENG, ENG_S), ("工质箱", TANK, TANK_S), ("其他", OTH, OTH_S)]
    xx = x
    for i, (label, fill, st) in enumerate(sw):
        yy = y + 10 + i * 14
        o.append(r(xx, yy - 7, 8, 8, fill, st, 1))
        o.append(t(xx + 13, yy, label, 9, DIM))
    return o


def zoom_controls(x, y):
    o = []
    o.append(r(x, y, 24, 24, TEXT, TEXT, 1, fo=0.03, so=0.16))
    o.append(t(x + 12, y + 16, "−", 12, DIM, "middle"))
    o.append(r(x + 27, y, 44, 24, TEXT, TEXT, 1, fo=0.03, so=0.16))
    o.append(t(x + 49, y + 16, "100%", 9, DIM, "middle"))
    o.append(r(x + 74, y, 24, 24, TEXT, TEXT, 1, fo=0.03, so=0.16))
    o.append(t(x + 86, y + 16, "+", 12, DIM, "middle"))
    o.append(r(x + 101, y, 24, 24, TEXT, TEXT, 1, fo=0.03, so=0.16))
    o.append(t(x + 113, y + 16, "⟲", 12, DIM, "middle"))
    return o


def catalog(A_x, A_y, rows):
    x, y, w, h = A_x, A_y, 286, 572
    o = [panel(x, y, w, h)]
    o.append(t(x + 8, y + 20, "零件目录", 11, TEXT))
    o.append(t(x + w - 8, y + 20, "24 件", 9, FAINT, "end"))
    o.append(r(x + 8, y + 30, w - 16, 24, DEEP, LINE, 1))
    o.append(t(x + 16, y + 46, "搜索零件…", 10, FAINT))
    o.append(chip(x + 8, y + 62, 44, 20, "全部", True))
    o.append(chip(x + 56, y + 62, 44, 20, "引擎"))
    o.append(chip(x + 104, y + 62, 58, 20, "工质箱"))
    o.append(chip(x + 8, y + 86, 44, 20, "结构"))
    o.append(chip(x + 56, y + 86, 44, 20, "控制"))
    o.append(chip(x + 104, y + 86, 44, 20, "载荷"))
    o.append(ln(x, y + 114, x + w, y + 114, LINE))
    yy = y + 122
    for sec, items in rows:
        o.append(t(x + 8, yy + 10, "— " + sec + " —", 9, FAINT))
        yy += 18
        for name, spec, armed in items:
            if armed:
                o.append(r(x, yy, 3, 26, GOLD))
            o.append(t(x + 14, yy + 11, name, 10, GOLD if armed else TEXT))
            o.append(t(x + 14, yy + 21, spec, 9, FAINT))
            o.append(r(x + w - 30, yy + 4, 18, 18, TEXT, TEXT, 1, fo=0.03, so=0.16))
            o.append(t(x + w - 21, yy + 16, "+", 11, GOLD if armed else DIM, "middle"))
            yy += 26
    return o, yy


def placement(x, y):
    o = [ln(x, y, x + 286, y, LINE)]
    o.append(t(x + 8, y + 20, "放置", 9, FAINT))
    o.append(chip(x + 42, y + 8, 46, 20, "自动", True))
    o.append(chip(x + 92, y + 8, 46, 20, "轴向"))
    o.append(chip(x + 142, y + 8, 46, 20, "径向"))
    o.append(t(x + 8, y + 48, "并联", 9, FAINT))
    for i in range(7):
        o.append(chip(x + 42 + i * 26, y + 36, 22, 20, str(i + 1), i == 0))
    o.append(t(x + 8, y + 76, "点空白=追加 · 点侧面=径向 · 再点一次取消", 8, FAINT))
    return o


def design_list(x, y):
    w, h = 384, 572
    o = [panel(x, y, w, h)]
    o.append(t(x + 8, y + 20, "装配清单", 11, TEXT))
    o.append(t(x + w - 8, y + 20, "11 行 / 11 件", 9, FAINT, "end"))
    # stage filter
    o.append(chip(x + 8, y + 30, 48, 20, "级 0", True))
    o.append(chip(x + 60, y + 30, 48, 20, "级 1"))
    o.append(r(x + w - 68, y + 30, 60, 20, TEXT, TEXT, 1, fo=0.02, so=0.12))
    o.append(t(x + w - 38, y + 44, "反转", 9, DIM, "middle"))
    o.append(ln(x, y + 58, x + w, y + 58, LINE))
    o.append(t(x + 12, y + 76, "#", 8, FAINT))
    o.append(t(x + 34, y + 76, "级", 8, FAINT))
    o.append(t(x + 64, y + 76, "零件", 8, FAINT))
    o.append(t(x + w - 12, y + 76, "操作", 8, FAINT, "end"))
    rows = [
        ("01", "0", "调试控制核心", "300 kg", False),
        ("02", "0", "二级液氧煤油罐", "容量 100.0 t · 件重 5.00 t", True),
        ("03", "0", "小推力液氧煤油机", "ve 3000 · 600 kN · 0.50 t", False),
        ("04", "1", "一级液氧煤油大罐", "容量 400.0 t · 件重 20.00 t", False),
        ("05", "1", "小推力液氧煤油机", "ve 3000 · 600 kN · 0.50 t", False),
        ("06", "1", "一级液氧煤油大罐【块1】", "容量 400.0 t · 件重 20.00 t", False),
        ("07", "1", "小推力液氧煤油机【块1】", "ve 3000 · 600 kN · 0.50 t", False),
        ("08", "1", "一级液氧煤油大罐【块2】", "容量 400.0 t · 件重 20.00 t", False),
        ("09", "1", "小推力液氧煤油机【块2】", "ve 3000 · 600 kN · 0.50 t", False),
        ("10", "1", "一级液氧煤油大罐【块3】", "容量 400.0 t · 件重 20.00 t", False),
        ("11", "1", "小推力液氧煤油机【块3】", "ve 3000 · 600 kN · 0.50 t", False),
    ]
    ry = y + 84
    for idx, st, name, spec, sel in rows:
        rh = 32
        if sel:
            o.append(r(x, ry, w, rh, TEXT, None, 1, fo=0.07))
            o.append(r(x, ry, 3, rh, GOLD))
        o.append(t(x + 12, ry + 13, idx, 9, FAINT))
        o.append(r(x + 32, ry + 8, 18, 16, TEXT, TEXT, 1, fo=0.03, so=0.12))
        o.append(t(x + 41, ry + 19, st, 9, DIM, "middle"))
        o.append(t(x + 60, ry + 13, name, 10, TEXT))
        o.append(t(x + 60, ry + 25, spec, 8, FAINT))
        o.append(r(x + w - 76, ry + 7, 16, 16, TEXT, TEXT, 1, fo=0.02, so=0.12))
        o.append(t(x + w - 68, ry + 18, "↑", 9, DIM, "middle"))
        o.append(r(x + w - 56, ry + 7, 16, 16, TEXT, TEXT, 1, fo=0.02, so=0.12))
        o.append(t(x + w - 48, ry + 18, "↓", 9, DIM, "middle"))
        o.append(r(x + w - 36, ry + 7, 18, 16, TEXT, TEXT, 1, fo=0.02, so=0.12))
        o.append(t(x + w - 27, ry + 18, "×", 10, DIM, "middle"))
        o.append(ln(x, ry + rh, x + w, ry + rh, LINE, 1, op=0.6))
        ry += rh
    # summary
    o.append(ln(x, y + 444, x + w, y + 444, LINE))
    o.append(t(x + 8, y + 462, "级 0 · 3 件", 9, DIM))
    o.append(t(x + 8, y + 476, "TWR 0.21", 9, RED))
    o.append(t(x + 120, y + 462, "级 1 · 8 件 · 4 块", 9, DIM))
    o.append(t(x + 120, y + 476, "总高 43.0 m", 9, DIM))
    # stage settings
    o.append(ln(x, y + 488, x + w, y + 488, LINE))
    o.append(t(x + 8, y + 508, "当前级 0", 9, FAINT))
    o.append(chip(x + 72, y + 496, 56, 20, "不抛弃"))
    o.append(chip(x + 132, y + 496, 56, 20, "可回收"))
    o.append(chip(x + 192, y + 496, 56, 20, "并联 ✓", True))
    o.append(r(x + w - 68, y + 496, 60, 20, TEXT, TEXT, 1, fo=0.02, so=0.12))
    o.append(t(x + w - 38, y + 510, "反转级号", 8, DIM, "middle"))
    o.append(t(x + 8, y + 540, "加入级", 9, FAINT))
    for i in range(5):
        o.append(chip(x + 56 + i * 30, y + 528, 24, 20, str(i), i == 0))
    o.append(t(x + 8, y + 568, "↑↓ 调序 · Delete 删除 · 点件名联动左图", 8, FAINT))
    return o


def command_bar(w, state="design"):
    y, h = 672, 40
    o = [r(16, y, w - 32, h, PANEL, LINE, 1)]
    o.append(button(24, y + 4, 88, 32, "清空设计"))
    o.append(button(120, y + 4, 88, 32, "示范火箭"))
    o.append(t(224, y + 24, "自动加注 · 建造不耗材料", 9, FAINT))
    o.append(t(430, y + 24, "⚠ TWR 0.21 < 1：活动级 0 起飞必被拒", 10, RED))
    o.append(t(1052, y + 24, "✓ 设计有效", 10, GOLD, "end"))
    o.append(button(1064, y + 4, 112, 32, "开始建造", "pri"))
    return o


def scene_a():
    w, h = 1200, 720
    o = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d" '
         'font-family="%s">' % (w, h, w, h, FONT)]
    o += frame(w, h, "火箭组装器", "ASSEMBLY BAY / 01")
    o += status(w, "设计中", "11 行 / 11 件", "工时预算 820",
                [("总高 43.0 m", DIM), ("干重 87.80 t", DIM),
                 ("工质 1700.0 t", DIM), ("TWR 0.21 ✗", RED)])
    o += preview_panel(16, 92, 482, 572)
    o += rocket(330, 150, 470)
    # selected tank band
    o.append(r(306, 226, 48, 84, "none", GOLD, 2))
    o += preview_labels(16, 92, 330, True)
    o += legend(24, 596)
    o += zoom_controls(372, 628)
    cat_rows = [
        ("引擎", [("小推力液氧煤油机", "ve 3000 · 600 kN · 0.50 t", False),
                  ("中推力液氧煤油机", "ve 3100 · 1200 kN · 1.20 t", False),
                  ("大推力液氧煤油机", "ve 3050 · 2400 kN · 2.40 t", False),
                  ("真空版上面级发动机", "ve 3400 · 180 kN · 0.20 t", False),
                  ("固体助推器", "ve 2600 · 900 kN · 6.00 t", False),
                  ("姿态控制推进器", "ve 2900 · 2 kN · 0.02 t", True)]),
        ("工质箱", [("二级液氧煤油罐", "容量 100 t · 件重 5 t", False),
                    ("一级液氧煤油大罐", "容量 400 t · 件重 20 t", False),
                    ("RCS 工质箱", "容量 20 t · 件重 1 t", False)]),
        ("结构", [("级间结构环", "300 kg", False),
                  ("整流罩", "400 kg", False),
                  ("控制核心", "300 kg", False)]),
    ]
    o += catalog(506, 92, cat_rows)[0]
    o += placement(506, 580)
    o += design_list(800, 92)
    o += command_bar(w)
    o.append("</svg>")
    return "\n".join(o)


def readout_col(x, y):
    o = [t(x, y, "READOUT", 8, FAINT, ls=2)]
    rows = [("总高", "43.0 m", TEXT), ("干重", "87.80 t", TEXT),
            ("工质", "1700.0 t", TEXT), ("推重比", "0.21", RED), ("级数", "2", TEXT)]
    yy = y + 22
    for label, val, col in rows:
        o.append(t(x, yy, label, 9, FAINT))
        o.append(t(x + 124, yy, val, 10, col, "end"))
        o.append(ln(x, yy + 8, x + 124, yy + 8, LINE, 1, op=0.7))
        yy += 28
    return o


def scene_b():
    w, h = 1200, 720
    o = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d" '
         'font-family="%s">' % (w, h, w, h, FONT)]
    o += frame(w, h, "火箭组装器", "ASSEMBLY BAY / 01")
    o += status(w, "设计中", "11 行 / 11 件", "工时预算 820",
                [("总高 43.0 m", DIM), ("干重 87.80 t", DIM),
                 ("工质 1700.0 t", DIM), ("TWR 0.21 ✗", RED)])
    o += preview_panel(16, 92, 580, 572)
    o += rocket(376, 150, 470)
    o.append(r(352, 226, 48, 84, "none", GOLD, 2))
    o += preview_labels(16, 92, 376, True)
    o += readout_col(452, 160)
    o += legend(24, 596)
    o += zoom_controls(470, 628)

    x, y, ww, hh = 604, 92, 580, 572
    o.append(panel(x, y, ww, hh))
    # mode tabs
    o.append(r(x, y, ww, 38, PANEL2))
    tabs = [("选件", True), ("装配", False), ("发射", False)]
    for i, (label, on) in enumerate(tabs):
        tx = x + 12 + i * 84
        o.append(t(tx + 30, y + 24, label, 11, GOLD if on else DIM, "middle"))
        if on:
            o.append(r(tx, y + 35, 60, 2, GOLD))
    o.append(t(x + ww - 10, y + 24, "11 行 / 11 件", 9, FAINT, "end"))
    o.append(ln(x, y + 38, x + ww, y + 38, LINE))
    # search + filters
    o.append(r(x + 12, y + 48, 300, 24, DEEP, LINE, 1))
    o.append(t(x + 20, y + 64, "搜索零件…", 10, FAINT))
    o.append(chip(x + 320, y + 50, 44, 20, "全部", True))
    o.append(chip(x + 368, y + 50, 44, 20, "引擎"))
    o.append(chip(x + 416, y + 50, 56, 20, "工质箱"))
    o.append(ln(x, y + 82, x + ww, y + 82, LINE))
    # cards
    cards = [
        ("小推力液氧煤油机", "ve 3000 · 600 kN", "引擎", True),
        ("中推力液氧煤油机", "ve 3100 · 1200 kN", "引擎", False),
        ("大推力液氧煤油机", "ve 3050 · 2400 kN", "引擎", False),
        ("真空版上面级发动机", "ve 3400 · 180 kN", "引擎", False),
        ("姿态控制推进器", "ve 2900 · 2 kN", "引擎", False),
        ("二级液氧煤油罐", "容量 100 t · 件重 5 t", "工质箱", False),
        ("一级液氧煤油大罐", "容量 400 t · 件重 20 t", "工质箱", False),
        ("RCS 工质箱", "容量 20 t · 件重 1 t", "工质箱", False),
        ("级间结构环", "300 kg", "结构", False),
        ("控制核心", "300 kg", "控制", False),
    ]
    for i, (name, spec, cat, sel) in enumerate(cards):
        cx = x + 12 + (i % 2) * 282
        cy = y + 92 + (i // 2) * 62
        o.append(r(cx, cy, 268, 54, TEXT, GOLD if sel else LINE, 2 if sel else 1,
                   fo=0.05 if sel else 0.02))
        if sel:
            o.append(r(cx, cy, 3, 54, GOLD))
        o.append(t(cx + 10, cy + 20, name, 10, TEXT))
        o.append(t(cx + 10, cy + 38, spec, 9, FAINT))
        o.append(t(cx + 176, cy + 20, cat, 8, FAINT))
        o.append(r(cx + 238, cy + 17, 20, 20, TEXT, TEXT, 1, fo=0.03, so=0.16))
        o.append(t(cx + 248, cy + 31, "+", 11, GOLD if sel else DIM, "middle"))
    # detail / placement
    o.append(ln(x, y + 412, x + ww, y + 412, LINE))
    o.append(t(x + 12, y + 434, "已选", 9, FAINT))
    o.append(t(x + 44, y + 434, "小推力液氧煤油机", 11, GOLD))
    o.append(t(x + 12, y + 456, "放置", 9, FAINT))
    o.append(chip(x + 46, y + 444, 46, 20, "自动", True))
    o.append(chip(x + 96, y + 444, 46, 20, "轴向"))
    o.append(chip(x + 146, y + 444, 46, 20, "径向"))
    o.append(t(x + 12, y + 484, "并联", 9, FAINT))
    for i in range(7):
        o.append(chip(x + 46 + i * 26, y + 472, 22, 20, str(i + 1), i == 0))
    o.append(t(x + 12, y + 512, "点击左侧火箭放置 · 点侧面=径向 · 再点一次取消", 8, FAINT))
    o.append(button(x + ww - 120, y + 496, 108, 32, "加入级 0", "pri"))
    o += command_bar(w)
    o.append("</svg>")
    return "\n".join(o)


def tokens():
    w, h = 1200, 940
    o = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d" '
         'font-family="%s">' % (w, h, w, h, FONT)]
    o.append(r(0, 0, w, h, BG, LINE2, 1))
    o.append(t(24, 44, "PFL 火箭组装器 · 视觉基准", 20, TEXT, weight=600))
    o.append(t(24, 66, "只使用 RimWorld 原版 UI 配色；不引入 DMSNF / PFL 旧色板的军绿、冷青、暖铜。", 10, DIM))
    o.append(ln(24, 82, w - 24, 82, LINE))

    o.append(t(24, 108, "01 / 调色（全部来自原版）", 12, TEXT))
    sw = [
        ("#15191D", "窗口底", "Widgets.WindowBGFillColor"),
        ("#616C7A", "窗口边", "Widgets.WindowBGBorderColor"),
        ("#1E2226", "面板", "窗口底 + 白 4%"),
        ("#23282E", "面板高亮", "窗口底 + 白 7%"),
        ("#2E363F", "分割线", "白 10% 叠底"),
        ("#DBB40C", "选中 / 状态", "ColorLibrary.Gold"),
        ("#FF3333", "阻断 / 错误", "ColorLibrary.RedReadable"),
        ("#FFFFFF", "正文 / 读数", "ColorLibrary.White"),
    ]
    for i, (hexv, role, src) in enumerate(sw):
        x = 24 + i * 142
        o.append(r(x, 122, 130, 64, hexv, LINE, 1))
        o.append(t(x, 202, hexv, 10, TEXT))
        o.append(t(x, 216, role, 9, DIM))
        o.append(t(x, 229, src, 8, FAINT))

    o.append(t(24, 262, "02 / 字体层级（GameFont）", 12, TEXT))
    ty = [
        ("火箭组装器", 18, "Medium 18 / 600", "窗口标题"),
        ("装配清单", 16, "Medium 16 / 600", "分栏标题"),
        ("二级液氧煤油罐", 13, "Small 13 / 400", "正文、行名"),
        ("ve 3000 · 600 kN · 0.50 t", 11, "Tiny 11 / 400", "规格、标签"),
        ("TWR 0.21   1700.0 t", 11, "Tiny 11 / 400 · 等宽", "数字读数（右对齐）"),
    ]
    y = 292
    for sample, size, spec, role in ty:
        o.append(t(24, y + 12, sample, size, TEXT))
        o.append(t(360, y + 12, spec, 9, DIM))
        o.append(t(640, y + 12, role, 9, FAINT))
        o.append(ln(24, y + 24, w - 24, y + 24, LINE, 1, op=0.6))
        y += 34

    o.append(t(24, 466, "03 / 栅格：1200 窗口 = 16 边距 + 12 × 90 列 + 11 × 8 槽", 12, TEXT))
    gx = 24
    for i in range(12):
        o.append(r(gx, 480, 90, 54, TEXT, LINE, 1, fo=0.025))
        o.append(t(gx + 45, 512, "%02d" % (i + 1), 9, FAINT, "middle"))
        gx += 98
    o.append(t(24, 552, "所有 y 对齐 8 px；面板间距 8；行高 26 / 32；按钮高 28 / 32 / 44；卡片内边距 12。", 9, DIM))

    o.append(t(24, 590, "04 / 控件（磁带盒语法：1px 线、方角、状态灯、编号槽）", 12, TEXT))
    # buttons
    o.append(button(24, 604, 112, 32, "开始建造", "pri"))
    o.append(button(144, 604, 100, 32, "取消建造"))
    o.append(button(252, 604, 100, 32, "强制发射", "danger"))
    o.append(button(360, 604, 100, 32, "不可用", "dis"))
    o.append(t(24, 660, "动作按钮：原版棕色 9-slice（Widgets.ButtonText）；主/次靠尺寸与位置，不靠颜色。", 8, FAINT))
    # chips / tabs
    o.append(chip(24, 676, 44, 20, "全部", True))
    o.append(chip(72, 676, 44, 20, "引擎"))
    o.append(chip(120, 676, 56, 20, "工质箱"))
    o.append(t(190, 691, "分段选择器：选中 = 金 14% 底 + 金框；不套底板。", 8, FAINT))
    # lamps
    o.append(t(24, 726, "状态灯", 9, FAINT))
    lamps = [(GOLD, "进行中"), (GOLD, "可发射"), (RED, "阻断"), (FAINT, "空闲")]
    lx = 80
    for col, label in lamps:
        o.append(r(lx, 719, 6, 6, col))
        o.append(t(lx + 11, 726, label, 8, DIM))
        lx += 62
    # L bracket + spec bar
    o.append(r(24, 742, 150, 52, TEXT, LINE, 1, fo=0.02))
    o.append(ln(30, 748, 42, 748, LINE2)); o.append(ln(30, 748, 30, 760, LINE2))
    o.append(ln(168, 748, 156, 748, LINE2)); o.append(ln(168, 748, 168, 760, LINE2))
    o.append(t(40, 778, "L 形角标 1px / 12px 臂", 8, DIM))
    o.append(r(190, 742, 160, 22, PANEL, LINE, 1)); o.append(r(190, 742, 3, 22, GOLD))
    o.append(t(202, 757, "SpecBar 3px 语义色", 8, DIM))
    # row states
    o.append(t(24, 820, "行状态", 9, FAINT))
    o.append(r(80, 808, 200, 26, TEXT, None, 1, fo=0.0))
    o.append(t(88, 825, "常态", 9, DIM))
    o.append(r(80, 838, 200, 26, TEXT, None, 1, fo=0.05))
    o.append(t(88, 855, "悬停 白 5%", 9, DIM))
    o.append(r(80, 868, 200, 26, TEXT, None, 1, fo=0.07)); o.append(r(80, 868, 3, 26, GOLD))
    o.append(t(88, 885, "选中 白 7% + 金 3px", 9, DIM))

    o.append(t(360, 726, "05 / 纪律", 12, TEXT))
    rules = [
        "1. 金色只用于：当前选中、进行中/可发射；不画金色按钮。",
        "2. 红色只用于：阻断、错误、必被拒。不得当装饰。",
        "3. 按钮直接用原版 9-slice；不自绘渐变、发光、圆角卡片。",
        "4. 数字右对齐并用等宽字形；行号 01 两位。",
        "5. 不画字面磁带、CRT 扫描线、霓虹；设备感靠线与槽。",
        "6. 每个面板只有一个标题；标题下 1px 线，不再加卡片。",
        "7. 空状态写「当前状态 + 原因 + 下一步」，不放 — / …。",
        "8. 调试入口只在 DevMode 出现，且与主操作分栏。",
        "9. 微图标 ↑↓× 无底板，热区 16–20px；动作按钮才用原版贴图。",
    ]
    yy = 746
    for s in rules:
        o.append(t(360, yy, s, 9, DIM))
        yy += 20
    o.append("</svg>")
    return "\n".join(o)


def svg_doc(o, w, h):
    head = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" '
            f'viewBox="0 0 {w} {h}" font-family="{FONT}">')
    return "\n".join([head] + o + ["</svg>"])


def badge(x, y, n, rad=10):
    s = [r(x - rad, y - rad, 2 * rad, 2 * rad, GOLD, None, 0, rx=rad)]
    s.append(t(x, y + 4, str(n), 10, "#000000", "middle", weight=600))
    return s


def view_screen():
    w, h = 1200, 720
    o = [r(0, 0, w, h, BG, LINE2, 1),
          r(0, 0, w, 44, DEEP), ln(0, 44, w, 44, LINE),
          t(20, 29, "火箭组装器", 18, TEXT, weight=600),
          t(160, 29, "装配台 01", 9, FAINT, ls=2),
          t(w - 34, 29, "×", 16, DIM)]
    # canvas
    o.append(ln(824, 44, 824, h, LINE))
    gx = 0
    while gx < 824:
        o.append(ln(gx, 44, gx, h, TEXT, 1, op=0.022))
        gx += 64
    gy = 44
    while gy < h:
        o.append(ln(0, gy, 824, gy, TEXT, 1, op=0.022))
        gy += 64
    o += rocket(400, 120, 620)
    o.append(ln(250, 636, 550, 636, LINE, 1))
    # one dimension line only
    o.append(ln(72, 120, 72, 620, LINE2, 1, op=0.7))
    o.append(ln(64, 120, 80, 120, LINE2, 1, op=0.7))
    o.append(ln(64, 620, 80, 620, LINE2, 1, op=0.7))
    o.append(t(58, 370, "43.0 m", 9, FAINT, "middle", ls=1, rot=True))
    # rail
    o.append(r(856, 126, 8, 8, RED))
    o.append(t(874, 140, "受阻", 24, TEXT, weight=600))
    o.append(t(856, 196, "活动级 0 推重比 0.21，", 13, TEXT))
    o.append(t(856, 220, "起飞会被拒。", 13, RED))
    o.append(t(856, 272, "✗ 推重比 < 1", 12, RED))
    o.append(t(856, 298, "其余 3 项检查通过", 11, FAINT))
    o.append(button(856, 340, 168, 44, "编辑设计", "pri", 13))
    o.append(t(856, 424, "查看全部检查", 11, DIM))
    o.append(ln(856, 432, 944, 432, LINE))
    o.append(t(856, 676, "43.0 m · 87.8 t · 2 级", 10, FAINT))
    return o


def scene_view():
    return svg_doc(view_screen(), 1200, 720)


def edit_screen():
    w, h = 1200, 720
    o = [r(0, 0, w, h, BG, LINE2, 1),
          r(0, 0, w, 44, DEEP), ln(0, 44, w, 44, LINE),
          t(20, 29, "火箭组装器 · 设计", 18, TEXT, weight=600),
          button(1024, 8, 64, 28, "清单", "sec", 11),
          button(1096, 8, 80, 28, "完成", "pri", 11)]
    # stage rail
    o.append(ln(88, 44, 88, 616, LINE))
    o.append(t(16, 80, "级", 9, FAINT, ls=2))
    o.append(chip(12, 92, 64, 34, "级 0", True, 11))
    o.append(chip(12, 132, 64, 34, "级 1", False, 11))
    o.append(chip(12, 172, 64, 34, "+", False, 14))
    # canvas
    gx = 88
    while gx < 864:
        o.append(ln(gx, 44, gx, 616, TEXT, 1, op=0.022))
        gx += 64
    gy = 44
    while gy < 616:
        o.append(ln(88, gy, 864, gy, TEXT, 1, op=0.022))
        gy += 64
    o += rocket(470, 110, 580)
    o.append(ln(300, 600, 640, 600, LINE, 1))
    o.append(t(104, 610, "43.0 m · 87.8 t · TWR 1.18 · 2 级", 9, FAINT))
    # selected part: core tank, gold outline only
    o.append(r(446, 198, 48, 104, "none", GOLD, 2))
    # inspector
    o.append(ln(864, 44, 864, 616, LINE))
    o.append(r(872, 108, 3, 44, GOLD))
    o.append(t(888, 128, "二级液氧煤油罐", 15, TEXT, weight=600))
    o.append(t(888, 150, "级 0 · 1 件", 10, DIM))
    o.append(ln(888, 168, 1176, 168, LINE))
    stats = [("容量", "100 t"), ("件重", "5.00 t"), ("高度", "3.2 m")]
    yy = 196
    for k, v in stats:
        o.append(t(888, yy, k, 9, FAINT))
        o.append(t(1176, yy, v, 12, TEXT, "end"))
        o.append(ln(888, yy + 10, 1176, yy + 10, LINE, 1, op=0.6))
        yy += 30
    o.append(ln(888, 290, 1176, 290, LINE))
    o.append(button(888, 304, 136, 34, "上移"))
    o.append(button(1032, 304, 136, 34, "下移"))
    o.append(button(888, 346, 136, 34, "换级"))
    o.append(button(1032, 346, 136, 34, "删除", "danger"))
    # build bar (D3): status + 44px primary + check link
    o.append(ln(888, 492, 1176, 492, LINE))
    o.append(r(888, 510, 6, 6, GOLD))
    o.append(t(902, 518, "✓ 设计有效 · 可建造", 10, GOLD))
    o.append(button(888, 536, 304, 44, "开始建造", "pri", 12))
    o.append(t(888, 602, "查看全部检查", 9, DIM))
    o.append(ln(888, 606, 944, 606, LINE))
    # parts shelf
    o.append(ln(0, 616, w, 616, LINE))
    o.append(t(16, 640, "零件", 9, FAINT, ls=2))
    o.append(chip(56, 624, 64, 28, "引擎", True, 11))
    o.append(chip(128, 624, 72, 28, "工质箱", False, 11))
    o.append(chip(208, 624, 64, 28, "结构", False, 11))
    o.append(chip(280, 624, 64, 28, "控制", False, 11))
    o.append(ln(0, 668, w, 668, LINE))
    parts = ["小推力机", "中推力机", "大推力机", "真空版", "固体助推", "RCS"]
    for i, name in enumerate(parts):
        x = 16 + i * 178
        active = i == 0
        o.append(r(x, 680, 170, 32, TEXT, GOLD if active else TEXT, 1,
                   fo=0.04 if active else 0.02, so=1 if active else 0.14))
        o.append(t(x + 85, 700, name, 11, GOLD if active else TEXT, "middle"))
    o.append(t(1160, 700, "更多 ›", 10, FAINT, "end"))
    return o


def scene_edit():
    return svg_doc(edit_screen(), 1200, 720)


def scene_layers():
    w, h = 1200, 460
    o = ['<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d" '
         'font-family="%s">' % (w, h, w, h, FONT)]
    o += [r(0, 0, w, h, BG, LINE2, 1),
          t(24, 44, "信息分层：同一窗口，三层披露", 20, TEXT, weight=600),
          t(24, 66, "答案先出现，细节后出现；不是把 100 个字段排得更好看。", 10, DIM),
          ln(24, 82, w - 24, 82, LINE)]
    boxes = [
        (24, "L0 常驻 · 看态", "12 个文本块", ["画布", "一句话状态", "一个主按钮"]),
        (420, "L1 编辑态 · 点「编辑设计」", "≈30 个文本块", ["级轨", "零件带", "选中检视"]),
        (816, "L2 弹层 · 点「清单」", "按需出现", ["装配清单", "全部检查", "规格 tooltip"]),
    ]
    for x, title, count, items in boxes:
        o.append(r(x, 104, 360, 236, PANEL, LINE, 1))
        o.append(t(x + 20, 134, title, 12, GOLD))
        o.append(ln(x + 20, 146, x + 340, 146, LINE))
        yy = 182
        for it in items:
            o.append(r(x + 20, yy - 8, 4, 4, GOLD))
            o.append(t(x + 34, yy, it, 11, TEXT))
            yy += 34
        o.append(t(x + 20, 322, count, 9, FAINT))
    # arrows
    for ax in (384, 780):
        o.append(ln(ax, 222, ax + 28, 222, LINE2, 1))
        o.append(poly([(ax + 28, 218), (ax + 36, 222), (ax + 28, 226)], LINE2))
    o.append(t(24, 386, "旧窗口 ≈ 100+ 个文本块 → 看态 12 → 编辑 30 → 其余按需。", 11, DIM))
    o.append(t(24, 412, "一屏一决策 · 数字按需 · 列表进弹层 · 最小字号 11px · 每块 ≤ 12 字。", 11, TEXT))
    o.append("</svg>")
    return "\n".join(o)


def scene_flow():
    w, h = 1200, 720
    o = [r(0, 0, w, h, BG, LINE2, 1),
         t(24, 40, "操作路径：看态 → 编辑态 → 弹层", 20, TEXT, weight=600),
         t(24, 64, "每个状态只有一个主按钮；按钮文案就是当前唯一该做的事。", 10, DIM),
         ln(24, 82, w - 24, 82, LINE)]
    boxes = [
        (40, "L0 看态", "火箭 + 一句话 + 一个按钮", "12 个文本块"),
        (450, "L1 编辑态", "级轨 + 零件带 + 选中检视", "同一窗口，不是新窗口"),
        (860, "L2 清单弹层", "11 行 · 调序 / 换级 / 删除", "覆盖在 L1 上"),
    ]
    for x, title, l1, l2 in boxes:
        o += [r(x, 100, 300, 100, PANEL, LINE, 1), r(x, 100, 3, 100, GOLD),
              t(x + 16, 130, title, 14, TEXT, weight=600),
              t(x + 16, 156, l1, 10, DIM),
              t(x + 16, 176, l2, 9, FAINT)]
    o += [ln(340, 150, 440, 150, LINE2, 1), poly([(440, 145), (450, 150), (440, 155)], LINE2),
          t(395, 140, "点「编辑设计」", 9, DIM, "middle"),
          ln(750, 150, 850, 150, LINE2, 1), poly([(850, 145), (860, 150), (850, 155)], LINE2),
          t(805, 140, "点「清单」", 9, DIM, "middle"),
          ln(600, 218, 200, 218, LINE2, 1), poly([(200, 213), (190, 218), (200, 223)], LINE2),
          t(395, 236, "点「完成」", 9, FAINT, "middle"),
          ln(1010, 258, 760, 258, LINE2, 1), poly([(760, 253), (750, 258), (760, 263)], LINE2),
          t(880, 276, "点空白 / Esc", 9, FAINT, "middle")]
    cards = [
        ("空闲", "还没有火箭", "开始设计", "→ L1，空设计", True),
        ("设计中", "设计没完成", "继续设计", "→ L1，保留进度", True),
        ("建造中", "建造中 42%", "取消建造", "工人施工中 · 可中止", False),
        ("可发射", "可以发射", "发射", "10 秒倒计时", True),
        ("受阻", "还差 1 项：TWR 0.21", "编辑设计", "改设计 → 重新建造", True),
        ("倒计时", "8.4 秒后发射", "取消发射", "回到已完工", False),
    ]
    for i, (st, sent, btn, note, pri) in enumerate(cards):
        x = 24 + (i % 3) * 373
        y = 300 + (i // 3) * 190
        o += [r(x, y, 360, 170, PANEL, LINE, 1),
              t(x + 18, y + 32, st, 15, TEXT, weight=600),
              t(x + 18, y + 58, sent, 10, DIM),
              button(x + 18, y + 76, 120, 32, btn, "pri" if pri else "sec", 11),
              t(x + 18, y + 134, note, 9, FAINT)]
    o.append(t(24, 690, "L0 的按钮随状态变；L1 / L2 的入口固定在标题栏。", 9, FAINT))
    o.append(t(24, 708, "D4：已完工受阻 → 编辑设计 = 改设计并重新建造（B1）", 9, FAINT))
    return svg_doc(o, w, h)


def scene_l0():
    w, h = 1200, 880
    o = view_screen()
    o += [ln(1034, 350, 1024, 362, GOLD, 1), *badge(1044, 344, 1, 11),
          ln(950, 416, 944, 428, GOLD, 1), *badge(960, 410, 2, 11),
          ln(1146, 20, 1158, 26, GOLD, 1), *badge(1136, 14, 3, 11)]
    o.append(r(0, 720, w, 160, DEEP))
    o.append(ln(0, 720, w, 720, LINE))
    notes = [
        (24, "① 点「编辑设计」", ["同一窗口切成 L1：标题加「· 设计」，", "右栏换成检视栏。", "改设计后要重新建造。"]),
        (360, "② 点「查看全部检查」", ["弹出 4 行：✗ 推重比 / ✓ 推进器 /", "✓ 燃料 / ✓ 控制核心；点空白关闭。"]),
        (760, "③ 点 ×", ["关闭窗口；设计已存档，", "下次原样打开。"]),
    ]
    for x, title, body in notes:
        o.append(t(x, 748, title, 11, TEXT))
        for i, line in enumerate(body):
            o.append(t(x, 770 + i * 18, line, 9, DIM))
    o.append(t(1010, 748, "先读这句", 9, FAINT))
    o.append(t(1010, 770, "能不能发，", 10, DIM))
    o.append(t(1010, 786, "看红字。", 10, DIM))
    return svg_doc(o, w, h)


def scene_l1():
    w, h = 1200, 920
    o = edit_screen()
    o += [ln(514, 190, 494, 202, GOLD, 1), *badge(524, 184, 1, 11),
          ln(1188, 294, 1172, 310, GOLD, 1), *badge(1186, 288, 2, 10),
          ln(196, 672, 186, 698, GOLD, 1), *badge(206, 666, 3, 11),
          ln(1014, 22, 1024, 22, GOLD, 1), *badge(1004, 22, 4, 10),
          ln(1136, 42, 1136, 36, GOLD, 1), *badge(1136, 50, 5, 8),
          ln(1188, 508, 1170, 518, GOLD, 1), *badge(1186, 502, 6, 10)]
    o.append(r(0, 720, w, 200, DEEP))
    o.append(ln(0, 720, w, 720, LINE))
    row1 = [
        (24, "① 点火箭上的零件", ["金框 = 选中；点空白取消。"]),
        (380, "② 右栏上半截管选中件", ["上移 / 下移 / 换级 / 删除。", "底部建造条是全局；没选中时只写提示。"]),
        (736, "③ 点零件带里的件", ["金框 = 待放置；再点火箭空白 = 追加，", "点侧面 = 径向；再点一次取消。"]),
    ]
    for x, title, body in row1:
        o.append(t(x, 748, title, 11, TEXT))
        for i, line in enumerate(body):
            o.append(t(x, 770 + i * 18, line, 9, DIM))
    row2 = [
        (24, "④ 点「清单」", ["打开 L2 弹层（下一张）。"]),
        (520, "⑤ 点「完成」", ["回到 L0；设计已存档。"]),
        (1000, "⑥ 点「开始建造」", ["开工；上方红字=还差哪项。", "改设计后要重新建造。"]),
    ]
    for x, title, body in row2:
        o.append(t(x, 836, title, 11, TEXT))
        for i, line in enumerate(body):
            o.append(t(x, 858 + i * 18, line, 9, DIM))
    return svg_doc(o, w, h)


def l2_screen():
    w, h = 1200, 720
    o = edit_screen()
    o.append(r(0, 0, w, h, "#000000", fo=0.68))
    o += [r(280, 80, 640, 560, PANEL, LINE2, 1),
          t(304, 116, "装配清单", 16, TEXT, weight=600),
          t(860, 116, "11 行 / 11 件", 10, FAINT, "end"),
          t(892, 116, "×", 16, DIM),
          ln(280, 130, 920, 130, LINE),
          t(304, 150, "#", 8, FAINT),
          t(336, 150, "级", 8, FAINT),
          t(372, 150, "零件", 8, FAINT),
          t(828, 150, "件数", 8, FAINT, "end"),
          t(872, 150, "序", 8, FAINT, "end")]
    rows = [("01", "0", "调试控制核心"), ("02", "0", "二级液氧煤油罐"),
            ("03", "0", "小推力液氧煤油机"), ("04", "1", "一级液氧煤油大罐"),
            ("05", "1", "小推力液氧煤油机"), ("06", "1", "一级液氧煤油大罐【块1】"),
            ("07", "1", "小推力液氧煤油机【块1】"), ("08", "1", "一级液氧煤油大罐【块2】"),
            ("09", "1", "小推力液氧煤油机【块2】")]
    yy = 162
    for idx, st, name in rows:
        sel = idx == "02"
        if sel:
            o.append(r(281, yy, 638, 40, TEXT, None, 1, fo=0.07))
            o.append(r(281, yy, 3, 40, GOLD))
        o.append(t(304, yy + 25, idx, 9, FAINT))
        o.append(r(328, yy + 12, 18, 16, TEXT, TEXT, 1, fo=0.03, so=0.12))
        o.append(t(337, yy + 23, st, 9, DIM, "middle"))
        o.append(t(366, yy + 25, name, 11, TEXT))
        o.append(t(828, yy + 25, "1", 10, DIM, "end"))
        o.append(t(872, yy + 25, "≡", 12, DIM, "end"))
        o.append(ln(280, yy + 40, 920, yy + 40, LINE, 1, op=0.6))
        yy += 40
    o.append(r(908, 162, 2, 360, TEXT, None, 0, fo=0.05))
    o.append(r(908, 162, 2, 150, TEXT, None, 0, fo=0.22))
    o += [ln(280, 540, 920, 540, LINE),
          button(304, 552, 96, 30, "↑ 上移", "sec", 10),
          button(408, 552, 96, 30, "↓ 下移", "sec", 10),
          button(512, 552, 96, 30, "换级", "sec", 10),
          button(616, 552, 96, 30, "删除", "danger", 10),
          t(304, 610, "点行 = 左图高亮 · 拖 ≡ = 调序 · 点 × 关闭", 9, FAINT)]
    return o


def scene_l2():
    w, h = 1200, 860
    o = l2_screen()
    o += [ln(270, 182, 280, 182, GOLD, 1), *badge(258, 182, 1, 10),
          ln(930, 282, 884, 282, GOLD, 1), *badge(940, 282, 2, 10),
          ln(740, 656, 700, 640, GOLD, 1), *badge(750, 662, 3, 10)]
    o.append(r(0, 720, w, 140, DEEP))
    o.append(ln(0, 720, w, 720, LINE))
    notes = [
        (24, "① 点一行", ["左图对应件高亮，右栏同步。"]),
        (320, "② 拖 ≡ 或 ↑↓ 按钮", ["改顺序；同级必须连续。"]),
        (616, "③ 底部动作", ["换级 / 删除，只作用于选中行。"]),
        (912, "④ 点 ×", ["关闭，回到 L1。"]),
    ]
    for x, title, body in notes:
        o.append(t(x, 748, title, 11, TEXT))
        o.append(t(x, 770, body[0], 9, DIM))
    o.append(t(24, 812, "D12：居中面板挡住左图火箭——「左图高亮」要么删掉，要么把面板右移/缩窄。", 9, FAINT))
    return svg_doc(o, w, h)


def preview_sheet():
    w = 1200
    label_h = 36
    screen_h = 720
    gap = 28
    top = 100
    bottom = 110
    h = top + 3 * (label_h + screen_h + gap) - gap + bottom
    o = [r(0, 0, w, h, BG, LINE2, 1),
         t(24, 46, "PFL 火箭组装器 · C 方案预览", 20, TEXT, weight=600),
         t(24, 70, "L0 看态 / L1 编辑态 / L2 清单弹层 · 2026-11-23 已拍 D1–D4", 10, DIM),
         ln(24, 84, w - 24, 84, LINE)]
    items = [
        ("L0 · 看态（受阻示例）", "一个主按钮 + 查看全部检查 + 一行 meta", view_screen()),
        ("L1 · 编辑态（右栏底部：开始建造）", "级轨 + 画布选中 + 检视栏 + 零件带 + 建造条", edit_screen()),
        ("L2 · 清单弹层（≡ 一期可拖）", "68% 暗幕 + 11 行 + ↑↓ / 换级 / 删除", l2_screen()),
    ]
    y = top
    for title, sub, screen in items:
        o.append(t(24, y + 16, title, 12, TEXT, weight=600))
        o.append(t(24, y + 30, sub, 9, FAINT))
        o.append(f'<g transform="translate(0,{y + label_h})">')
        o += screen
        o.append('</g>')
        y += label_h + screen_h + gap
    o.append(ln(24, h - bottom + 8, w - 24, h - bottom + 8, LINE))
    o.append(t(24, h - bottom + 34, "已拍：D1 三分法 · D2 ≡ 一期 · D3 建造条 · D4 ReopenDesign（改设计需重建）", 9, GOLD))
    o.append(t(24, h - bottom + 54, "待定：D5 并联/对称合并 · D7 清空/示范/反转/级标签 · D9 窗口框 · D10 独立 Window · D12 L2 遮挡", 9, FAINT))
    return svg_doc(o, w, h)


def scene_input():
    w, h = 1200, 600
    o = [r(0, 0, w, h, BG, LINE2, 1),
         t(24, 44, "输入方式：只用原版，不新增按键", 20, TEXT, weight=600),
         t(24, 68, "所有操作都有可见按钮；Esc 是 Window 自带行为，不在界面上教。", 10, DIM),
         ln(24, 84, w - 24, 84, LINE),
         r(1000, 24, 176, 30, TEXT, GOLD, 1, fo=0.06),
         t(1016, 44, "自定义按键", 10, DIM),
         t(1156, 46, "0", 14, GOLD, "end", weight=600)]
    rows = [
        ("关闭窗口 / 弹层", "Esc 或点 ×", "Window.closeOnCancel（原版默认 true）"),
        ("列表滚动", "鼠标滚轮", "Widgets.BeginScrollView（原版）"),
        ("看零件规格", "鼠标悬停", "TooltipHandler.TipRegion（原版）"),
        ("选中零件", "左键点零件", "现有 pieceHits"),
        ("放置零件", "左键点火箭", "现有 HandlePlacementClick"),
        ("取消放置", "再点一次已选件", "可见按钮，无右键"),
        ("删除", "检视栏 / 清单底部「删除」", "可见按钮，无 Delete 键"),
        ("调序", "拖 ≡ 或点 ↑↓ 按钮", "可见按钮，无键盘"),
    ]
    y = 100
    o.append(r(24, y, w - 48, 36, PANEL2))
    o.append(t(40, y + 23, "操作", 10, FAINT))
    o.append(t(256, y + 23, "用什么", 10, FAINT))
    o.append(t(576, y + 23, "来源", 10, FAINT))
    y += 36
    for i, (a, b, c) in enumerate(rows):
        if i % 2 == 0:
            o.append(r(24, y, w - 48, 46, TEXT, None, 0, fo=0.02))
        o.append(t(40, y + 28, a, 11, TEXT))
        o.append(t(256, y + 28, b, 10, DIM))
        o.append(t(576, y + 28, c, 9, FAINT))
        o.append(ln(24, y + 46, w - 24, y + 46, LINE, 1, op=0.7))
        y += 46
    o.append(ln(24, 520, w - 24, 520, LINE))
    o.append(t(24, 552, "不采用：Delete 键 / ↑↓ 键 / 右键取消 / 自定义热键。", 11, RED))
    o.append(t(24, 576, "Esc 只走 Window.closeOnCancel，不写任何 Event.current.keyCode 分支。", 10, DIM))
    return svg_doc(o, w, h)


# ═══════════════════════════════════════════════════════════════
#  C+ 方案：1 + 7 + 4 / 26 · 14 · 11 / 无网格 / 金色收敛
# ═══════════════════════════════════════════════════════════════

CP_W, CP_H = 1200, 720
CP_L, CP_C1, CP_CANVAS, CP_SPLIT, CP_R, CP_EDGE, CP_CONTENT = 16, 106, 114, 800, 808, 1184, 1168
CP_BODY = 60
CP_TANK_S = "#3A4148"
CP_ENG_S = "#4A4E52"
CP_OTH_S = "#343A40"


def chip_cp(x, y, w, h, label, active=False, size=11, gold=False):
    """C+ 选择器：控件选中用白，不用金；金只留给对象/状态。"""
    if active:
        if gold:
            body = r(x, y, w, h, GOLD, GOLD, 1, fo=0.14)
            col = GOLD
        else:
            body = r(x, y, w, h, TEXT, TEXT, 1, fo=0.07, so=0.30)
            col = TEXT
    else:
        body = r(x, y, w, h, TEXT, TEXT, 1, fo=0.02, so=0.12)
        col = DIM
    return body + t(x + w / 2, y + h / 2 + 4, label, size, col, "middle")


def cp_frame(l1=False):
    o = [r(0, 0, CP_W, CP_H, BG, LINE2, 1),
         r(0, 0, CP_W, 44, DEEP), ln(0, 44, CP_W, 44, LINE),
         t(20, 30, "火箭组装器", 26, TEXT, weight=600)]
    if l1:
        o.append(chip_cp(230, 10, 46, 24, "设计", True, 11))
        o += [button(1008, 8, 64, 28, "清单", "sec", 14),
              button(1080, 8, 80, 28, "完成", "sec", 14),
              t(1168, 30, "×", 18, DIM)]
    else:
        o += [t(250, 30, "装配台 01", 11, FAINT, ls=2),
              t(1166, 30, "×", 18, DIM)]
    return o


def cp_dimension():
    return [ln(72, 120, 72, 620, LINE2, 1, op=0.7),
            ln(64, 120, 80, 120, LINE2, 1, op=0.7),
            ln(64, 620, 80, 620, LINE2, 1, op=0.7),
            t(58, 370, "43.0 m", 11, FAINT, "middle", rot=True)]


def rocket_cp(cx, top, bot, active_gold=True):
    o = []
    core_x = cx - 20
    bl = cx - 66
    br = cx + 34
    cols = [(bl, 32, True), (core_x, 40, False), (br, 32, True)]
    o.append(r(cx - 14, top - 24, 28, 24, OTH, CP_OTH_S, 1))
    for x, wd, active in cols:
        o.append(r(x, top, wd, bot - top, TANK, CP_TANK_S, 1))
        yy = top + 46
        while yy < bot - 8:
            o.append(ln(x, yy, x + wd, yy, CP_TANK_S, 1, op=0.55))
            yy += 46
        ex = x + 3
        o.append(poly([(ex, bot), (ex + wd - 6, bot), (ex + wd - 3, bot + 16), (ex - 3, bot + 16)],
                      ENG, CP_ENG_S, 1))
        col = GOLD if (active and active_gold) else CP_ENG_S
        o.append(ln(ex - 1, bot + 16, ex + wd - 4, bot + 16, col, 1, op=0.9 if active else 0.5))
    return o


def cp_stage_labels(cx, top, active=1):
    o = []
    for x, label, is_active in [(cx - 20, "级 0", active == 0),
                                 (cx - 66 + 16, "级 1", active == 1),
                                 (cx + 34 + 16, "级 1", active == 1)]:
        o.append(t(x, top - 38, label, 11, GOLD if is_active else FAINT, "middle"))
    return o


def cp_meta_cols(x, y, w, items):
    o = []
    n = len(items)
    cw = (w - (n - 1) * 8) // n
    for i, (label, value) in enumerate(items):
        cx = x + i * (cw + 8)
        o.append(t(cx, y, label, 11, FAINT))
        o.append(t(cx, y + 18, value, 14, TEXT))
    return o


def cp_l0(state="blocked"):
    o = cp_frame(False)
    o += cp_dimension()
    o.append(r(CP_CANVAS, CP_BODY, 686, 644, PANEL, LINE, 1))
    o += rocket_cp(457, 120, 620)
    o += cp_stage_labels(457, 120, 1)

    o.append(r(CP_R, CP_BODY, 376, 644, PANEL, LINE, 1))
    if state == "blocked":
        col, word = RED, "受阻"
        sent1, sent2 = "活动级 1 推重比 0.21，", "需要 >1。"
        btn = "编辑设计"
        sub = "改设计需重新建造"
        links = ["下一枚", "查看全部检查"]
    else:
        col, word = GOLD, "可发射"
        sent1, sent2 = "可以发射。倒计时 10 游戏秒。", ""
        btn = "发射"
        sub = ""
        links = ["编辑设计（需重新建造）", "下一枚", "查看全部检查"]

    o += [r(CP_R, CP_BODY, 3, 90, col),
          t(CP_R + 24, CP_BODY + 36, word, 26, TEXT, weight=600),
          t(CP_R + 24, CP_BODY + 72, sent1, 14, RED if state == "blocked" else DIM)]
    if sent2:
        o.append(t(CP_R + 24, CP_BODY + 94, sent2, 14, RED))
    if state == "blocked":
        o.append(t(CP_R + 24, CP_BODY + 136, "× 推重比", 11, RED))
        o.append(t(CP_R + 102, CP_BODY + 136, "3 项通过", 11, DIM))
    else:
        o.append(t(CP_R + 24, CP_BODY + 136, "✓ 4 项检查通过", 11, DIM))

    o.append(button(CP_R + 16, CP_BODY + 170, 344, 44, btn, "sec", 14))
    y = CP_BODY + 232
    if sub:
        o.append(t(CP_R + 16, y, sub, 11, FAINT))
        y += 24
    for s in links:
        o.append(t(CP_R + 16, y, s, 11, DIM))
        y += 24
    o += cp_meta_cols(CP_R + 16, CP_BODY + 560, 344,
                      [("总高", "43.0 m"), ("干重", "87.8 t"), ("级数", "2")])
    return o


def cp_shelf(armed=False):
    o = [r(0, 616, 1200, 104, DEEP), ln(0, 616, 1200, 616, LINE)]
    o.append(t(16, 640, "零件", 11, FAINT))
    for i, label in enumerate(["引擎", "工质箱", "结构", "控制"]):
        o.append(chip_cp(56 + i * 76, 624, 68, 28, label, i == 0, 11))
    o.append(ln(0, 668, 1200, 668, LINE))
    parts = ["小推力机", "中推力机", "大推力机", "真空版", "固体助推", "RCS"]
    for i, name in enumerate(parts):
        x = 16 + i * 178
        active = armed and i == 0
        if active:
            o.append(r(x, 680, 170, 32, TEXT, GOLD, 1, fo=0.04, dash="4 4"))
            o.append(t(x + 85, 700, name, 14, GOLD, "middle"))
        else:
            o.append(r(x, 680, 170, 32, TEXT, TEXT, 1, fo=0.02, so=0.14))
            o.append(t(x + 85, 700, name, 14, TEXT, "middle"))
    o.append(t(1160, 700, "更多 ›", 11, FAINT, "end"))
    return o


def cp_l1(mode="selected"):
    o = cp_frame(True)

    # 1 列：级轨
    o.append(r(CP_L, CP_BODY, 90, 556, PANEL, LINE, 1))
    o.append(t(CP_L + 8, CP_BODY + 20, "新件", 11, FAINT))
    o.append(t(CP_L + 8, CP_BODY + 34, "加入", 11, FAINT))
    for i, (label, on) in enumerate([("级 0", True), ("级 1", False), ("+", False)]):
        o.append(chip_cp(24, CP_BODY + 52 + i * 40, 64, 32, label, on, 11))

    # 7 列：画布
    o.append(r(CP_CANVAS, CP_BODY, 686, 556, PANEL, LINE, 1))
    o += rocket_cp(457, 110, 560)
    o += cp_stage_labels(457, 110, 1)
    if mode == "selected":
        o.append(r(437, 198, 40, 104, "none", GOLD, 1))
        o += [ln(437, 198, 453, 198, GOLD, 2), ln(437, 198, 437, 214, GOLD, 2)]
    else:
        o.append(r(440, 380, 34, 64, "none", GOLD, 1, dash="4 4"))
        o.append(t(457, 462, "待放置", 11, GOLD, "middle"))
    o += cp_meta_cols(CP_CANVAS, 580, 686,
                      [("总高", "43.0 m"), ("干重", "87.8 t"), ("TWR", "1.18"), ("级数", "2")])

    # 4 列：检视 + 建造条
    o.append(r(CP_R, CP_BODY, 376, 556, PANEL, LINE, 1))
    o.append(r(CP_R, CP_BODY, 3, 44, GOLD))
    if mode == "selected":
        o.append(t(CP_R + 16, CP_BODY + 20, "选中件", 11, FAINT))
        o.append(t(CP_R + 16, CP_BODY + 42, "二级液氧煤油罐", 14, TEXT, weight=600))
        o.append(t(CP_R + 16, CP_BODY + 66, "级 0", 11, DIM))
        o.append(t(CP_R + 72, CP_BODY + 66, "1 件", 11, DIM))
        for i, (k, v) in enumerate([("容量", "100 t"), ("件重", "5.00 t"), ("高度", "3.2 m")]):
            yy = CP_BODY + 102 + i * 30
            o.append(t(CP_R + 16, yy, k, 11, FAINT))
            o.append(t(CP_R + 360, yy, v, 14, TEXT, "end"))
            o.append(ln(CP_R + 16, yy + 10, CP_R + 360, yy + 10, LINE, 1, op=0.6))
        for i, (label, kind) in enumerate([("↑ 上移", "sec"), ("↓ 下移", "sec"),
                                           ("换级", "sec"), ("删除", "danger")]):
            o.append(button(CP_R + 16, CP_BODY + 222 + i * 34, 344, 28, label, kind, 14))
        o.append(t(CP_R + 16, CP_BODY + 400, "✓ 设计有效", 11, TEXT))
        o.append(button(CP_R + 16, CP_BODY + 420, 344, 44, "开始建造", "sec", 14))
        o.append(t(CP_R + 16, CP_BODY + 482, "查看全部检查", 11, DIM))
    else:
        o.append(t(CP_R + 16, CP_BODY + 20, "待放置", 11, GOLD))
        o.append(t(CP_R + 16, CP_BODY + 42, "小推力机", 14, TEXT, weight=600))
        o.append(t(CP_R + 16, CP_BODY + 66, "级 0", 11, DIM))
        o.append(t(CP_R + 16, CP_BODY + 96, "并联", 11, FAINT))
        for i in range(7):
            o.append(chip_cp(CP_R + 52 + i * 40, CP_BODY + 82, 36, 24, str(i + 1), i == 3, 11))
        o.append(t(CP_R + 16, CP_BODY + 128, "空白 = 追加", 11, DIM))
        o.append(t(CP_R + 16, CP_BODY + 146, "侧面 = 径向 ×4", 11, DIM))
        o.append(t(CP_R + 16, CP_BODY + 164, "再点一次 = 取消", 11, DIM))
        o.append(t(CP_R + 16, CP_BODY + 400, "✓ 设计有效", 11, TEXT))
        o.append(button(CP_R + 16, CP_BODY + 420, 344, 44, "开始建造", "sec", 14))
        o.append(t(CP_R + 16, CP_BODY + 482, "查看全部检查", 11, DIM))

    o += cp_shelf(mode == "armed")
    return o


def cp_l2():
    o = cp_l1("selected")
    o.append(r(0, 0, CP_W, CP_H, "#000000", fo=0.80))
    o += [r(137, 50, 640, 620, PANEL, LINE2, 1),
          t(161, 86, "装配清单", 26, TEXT, weight=600),
          t(700, 86, "11 行 / 11 件", 11, FAINT, "end"),
          t(721, 86, "⋯", 16, DIM),
          t(753, 86, "×", 16, DIM),
          ln(137, 100, 777, 100, LINE),
          t(161, 120, "#", 11, FAINT),
          t(193, 120, "级", 11, FAINT),
          t(225, 120, "零件", 11, FAINT),
          t(725, 120, "≡", 11, FAINT, "end")]
    rows = [("01", "0", "调试控制核心"), ("02", "0", "二级液氧煤油罐"),
            ("03", "0", "小推力液氧煤油机"), ("04", "0", "一级液氧煤油大罐"),
            ("05", "1", "小推力液氧煤油机"), ("06", "1", "一级液氧煤油大罐【块1】"),
            ("07", "1", "小推力液氧煤油机【块1】"), ("08", "1", "一级液氧煤油大罐【块2】"),
            ("09", "1", "小推力液氧煤油机【块2】"), ("10", "1", "一级液氧煤油大罐【块3】"),
            ("11", "1", "小推力液氧煤油机【块3】")]
    yy = 130
    for idx, st, name in rows:
        sel = idx == "02"
        bad = idx == "04"
        if sel:
            o.append(r(137, yy, 640, 44, TEXT, None, 1, fo=0.07))
            o.append(r(137, yy, 3, 44, GOLD))
        elif bad:
            o.append(r(137, yy, 640, 44, RED, None, 1, fo=0.08))
            o.append(r(137, yy, 3, 44, RED))
        o.append(t(161, yy + 27, idx, 11, FAINT))
        o.append(chip_cp(190, yy + 12, 22, 20, st, False, 11))
        o.append(t(225, yy + 27, name, 14, TEXT))
        o.append(t(725, yy + 27, "≡", 14, DIM, "end"))
        o.append(ln(137, yy + 44, 777, yy + 44, LINE, 1, op=0.6))
        yy += 44
    o += [ln(137, 614, 777, 614, LINE),
          button(161, 624, 96, 30, "↑ 上移", "sec", 11),
          button(265, 624, 96, 30, "↓ 下移", "sec", 11),
          button(369, 624, 96, 30, "换级", "sec", 11),
          button(473, 624, 96, 30, "删除", "danger", 11),
          t(161, 660, "点行选中　拖 ≡ 调序　× 关闭", 11, FAINT)]
    return o


def cp_checks():
    o = cp_l0("blocked")
    o += [r(390, 180, 420, 260, PANEL, LINE2, 1),
          r(390, 180, 3, 260, RED),
          t(410, 210, "发射检查", 14, TEXT, weight=600),
          ln(390, 224, 810, 224, LINE)]
    rows = [("×", "推重比", "0.21", "需要 >1", RED),
            ("✓", "推进器", "3 台", "", DIM),
            ("✓", "燃料", "2 罐", "170 t", DIM),
            ("✓", "控制核心", "1 个", "", DIM)]
    yy = 250
    for mark, label, value, sub, col in rows:
        o.append(t(410, yy, mark, 14, col))
        o.append(t(436, yy, label, 11, FAINT))
        o.append(t(790, yy, value, 14, TEXT, "end"))
        if sub:
            o.append(t(790, yy + 16, sub, 11, FAINT, "end"))
        yy += 42
    return o


def cp_system():
    w, h = 1200, 900
    o = [r(0, 0, w, h, BG, LINE2, 1),
         t(24, 46, "C+ 方案：1 + 7 + 4 / 26/14/11 / 金色收敛 / 无网格", 20, TEXT, weight=600),
         t(24, 70, "从 C 的 824/864 分栏改成同一根 12 列：左 1 列（尺寸/级轨）+ 中 7 列（画布）+ 右 4 列（状态/检视）。", 10, DIM),
         ln(24, 84, w - 24, 84, LINE)]
    # 12-col grid
    o.append(t(24, 112, "01 / 12 列（16 边距 + 12×90 + 11×8）", 12, TEXT))
    for i in range(12):
        x = 16 + i * 98
        fill = GOLD if i == 0 else (TEXT if 1 <= i <= 7 else PANEL2)
        fo = 0.10 if i == 0 else (0.035 if 1 <= i <= 7 else 0.06)
        o.append(r(x, 126, 90, 54, fill, LINE, 1, fo=fo))
        o.append(t(x + 45, 158, "%02d" % (i + 1), 11, FAINT, "middle"))
    o.append(t(24, 196, "1 列 = 尺寸/级轨　7 列 = 画布　4 列 = 状态/检视；L0/L1 共用同一根分栏，画布不横跳。", 11, DIM))
    # type
    o.append(t(24, 236, "02 / 字体：只用三级 26 / 14 / 11", 12, TEXT))
    o.append(t(24, 278, "火箭组装器", 26, TEXT, weight=600))
    o.append(t(360, 278, "26 Medium · 窗口标题 / 状态英雄", 9, FAINT))
    o.append(t(24, 312, "二级液氧煤油罐", 14, TEXT))
    o.append(t(360, 312, "14 Small · 正文 / 按钮 / 零件名", 9, FAINT))
    o.append(t(24, 340, "TWR 1.18　级 0　100 t", 11, DIM))
    o.append(t(360, 340, "11 Tiny · 标签 / 表头 / 数字", 9, FAINT))
    o.append(ln(24, 360, w - 24, 360, LINE))
    # gold rules
    o.append(t(24, 394, "03 / 金色收敛：控件选中用白，对象/状态用金", 12, TEXT))
    o.append(chip_cp(24, 410, 56, 26, "级 0", True, 11))
    o.append(t(92, 428, "控件选中：白 7% + 白 30% 1px", 9, FAINT))
    o.append(r(360, 410, 56, 26, "none", GOLD, 1))
    o.append(ln(360, 410, 376, 410, GOLD, 2))
    o.append(t(428, 428, "对象焦点：金 L 角标 + 1px", 9, FAINT))
    o.append(ln(700, 423, 756, 423, GOLD, 1, op=0.9))
    o.append(t(768, 428, "活动级：金 1px；引擎边=白 20%", 9, FAINT))
    o.append(ln(24, 452, w - 24, 452, LINE))
    # L2
    o.append(t(24, 486, "04 / L2：620 高 + 44 行 + 4 列（#/级/零件/≡）", 12, TEXT))
    o.append(r(24, 500, 620, 160, PANEL, LINE2, 1))
    o.append(t(40, 528, "装配清单", 14, TEXT, weight=600))
    o.append(t(620, 528, "11 行 / 11 件", 11, FAINT, "end"))
    for i, (idx, st, name) in enumerate([("01", "0", "调试控制核心"), ("02", "0", "二级液氧煤油罐"),
                                         ("03", "0", "小推力液氧煤油机")]):
        yy = 540 + i * 36
        if i == 1:
            o.append(r(24, yy, 620, 36, TEXT, None, 1, fo=0.07))
            o.append(r(24, yy, 3, 36, GOLD))
        o.append(t(40, yy + 23, idx, 11, FAINT))
        o.append(t(72, yy + 23, st, 11, DIM))
        o.append(t(104, yy + 23, name, 14, TEXT))
        o.append(t(620, yy + 23, "≡", 14, DIM, "end"))
    o.append(t(664, 528, "画布区居中 137–777；右栏 808–1184 保留，行→右栏同步。", 9, FAINT))
    o.append(t(664, 548, "件数全 1 时隐藏；“序”表头删掉；44 行是 DMSNF Home 的标准。", 9, FAINT))
    o.append(ln(24, 680, w - 24, 680, LINE))
    # removals / keeps
    o.append(t(24, 714, "05 / 删除", 12, TEXT))
    for i, s in enumerate(["64px 画布网格", "引擎金边", "2×2 动作墙", "· 拼事实"]):
        o.append(t(24, 742 + i * 20, "— " + s, 11, RED))
    o.append(t(360, 714, "06 / 保留", 12, TEXT))
    for i, s in enumerate(["1+7+4 分栏", "26/14/11", "3px SpecBar", "一个 L 角标", "L2 620/44", "L1 ×", "Complete 出口"]):
        o.append(t(360, 742 + i * 20, "✓ " + s, 11, DIM))
    return svg_doc(o, w, h)


def cp_preview():
    w = 1200
    label_h = 36
    screen_h = 720
    gap = 28
    top = 100
    bottom = 110
    h = top + 3 * (label_h + screen_h + gap) - gap + bottom
    o = [r(0, 0, w, h, BG, LINE2, 1),
         t(24, 46, "C+ 方案预览：L0 可发射 / L1 选中 / L2 清单", 20, TEXT, weight=600),
         t(24, 70, "1+7+4 · 26/14/11 · 无网格 · 金色收敛 · L2 620/44", 10, DIM),
         ln(24, 84, w - 24, 84, LINE)]
    items = [
        ("L0 · 可发射（26 字状态 + 3px SpecBar + 发射/编辑/下一枚）", cp_l0("ready")),
        ("L1 · 选中件（竖排动作 + 建造条 + 级轨 + 级标签）", cp_l1("selected")),
        ("L2 · 清单（画布区居中 137–777，右栏保留；620/44/4 列）", cp_l2()),
    ]
    y = top
    for title, screen in items:
        o.append(t(24, y + 16, title, 12, TEXT, weight=600))
        o.append(f'<g transform="translate(0,{y + label_h})">')
        o += screen
        o.append('</g>')
        y += label_h + screen_h + gap
    o.append(ln(24, h - bottom + 8, w - 24, h - bottom + 8, LINE))
    o.append(t(24, h - bottom + 34, "C+ 变更：同一根 12 列 / 26-14-11 / 删画布网格 / 引擎边撤金 / L2 620+44+4 列 / L1 × / Complete 出口", 9, GOLD))
    o.append(t(24, h - bottom + 54, "待定：D12 画布区居中（本图已按此画）/ 字号 26-14-11 是否最终 / 窗口是否保留 resize", 9, FAINT))
    return svg_doc(o, w, h)


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    for name, data in (("assembler-A-workbench.svg", scene_a()),
                       ("assembler-B-modes.svg", scene_b()),
                       ("assembler-tokens.svg", tokens()),
                       ("assembler-C-view.svg", scene_view()),
                       ("assembler-C-edit.svg", scene_edit()),
                       ("assembler-layers.svg", scene_layers()),
                       ("assembler-flow.svg", scene_flow()),
                       ("assembler-L0-steps.svg", scene_l0()),
                       ("assembler-L1-steps.svg", scene_l1()),
                       ("assembler-L2-steps.svg", scene_l2()),
                       ("assembler-input.svg", scene_input()),
                       ("assembler-preview.svg", preview_sheet()),
                       ("cplus-L0-ready.svg", svg_doc(cp_l0("ready"), 1200, 720)),
                       ("cplus-L0-blocked.svg", svg_doc(cp_l0("blocked"), 1200, 720)),
                       ("cplus-L1-selected.svg", svg_doc(cp_l1("selected"), 1200, 720)),
                       ("cplus-L1-armed.svg", svg_doc(cp_l1("armed"), 1200, 720)),
                       ("cplus-L2.svg", svg_doc(cp_l2(), 1200, 720)),
                       ("cplus-checks.svg", svg_doc(cp_checks(), 1200, 720)),
                       ("cplus-system.svg", cp_system()),
                       ("cplus-preview.svg", cp_preview())):
        with open(os.path.join(here, name), "w", encoding="utf-8") as f:
            f.write(data)
        print("wrote", name, len(data))
