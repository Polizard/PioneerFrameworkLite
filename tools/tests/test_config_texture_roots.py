"""§7 贴图按包分流的回归（2026-11-28）。

背景：`main()` 早把 `NFexp/1.7/Defs` 放进了 `xml_roots`，但贴图查找原先只扫
`VERSION_DIRS` ⇒ 任何带贴图的 NFexp 内容都被误报 missing texture（11 款载荷 Def
迁入时撞上）。修法不是"把 NFexp 的 Textures 也加进搜索列表"，而是按 About.xml 的
**依赖方向**分流：

  · PFL 的 def 只查 PFL 自己的 Textures —— 否则 PFL 可以悄悄指向只存在于 NFexp 的图，
    只装 PFL 时才白图，而闸门反而放行；
  · NFexp 的 def 可查 NFexp + PFL 的贴图，但**只限 1.7** —— NFexp 无 1.6 线，
    让它看见 PFL/1.6 的图会制造「闸门绿、运行时白图」的错判。

本文件钉住这三条，防止将来被"顺手改宽"（那是本次改动最该防的退化）。
夹具不落临时文件（同 `test_map_generation_config.py` 的约定：Windows 受限环境下
unittest 的 tempfile 会被 ACL 拒绝），只用纯路径推演 + 仓库内的真实贴图做行为断言。
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from validate_config import package_texture_roots, resolve_texture

ROOT = Path(__file__).resolve().parents[2]
VERSION_DIRS = [d for d in (ROOT / "1.6", ROOT / "1.7") if d.exists()]

# 假版本目录：`package_texture_roots` 只做路径拼接与 `.name` 判断，不碰文件系统。
FAKE_ROOT = Path("/fake/repo")
FAKE_VERSIONS = [FAKE_ROOT / "1.6", FAKE_ROOT / "1.7"]

# 仓库里真实存在的 NFexp 专属贴图（11 款载荷之一），用来做跨包行为断言。
NFEXP_ONLY_TEX = "Things/Resource/Payloads/BMEMP"


class PackageTextureRootsTests(unittest.TestCase):
    def test_pfl_def_sees_only_pfl_textures(self):
        roots = package_texture_roots(FAKE_ROOT / "1.7" / "Defs" / "a.xml", FAKE_ROOT, FAKE_VERSIONS)
        self.assertEqual([FAKE_ROOT / "1.6" / "Textures", FAKE_ROOT / "1.7" / "Textures"], roots)

    def test_pfl_def_cannot_resolve_nfexp_only_texture(self):
        """只装 PFL 时的白图，必须在这个闸门上表现为 FAIL 而不是放行。"""
        pfl_roots = package_texture_roots(ROOT / "1.7" / "Defs" / "a.xml", ROOT, VERSION_DIRS)
        self.assertIsNone(resolve_texture(NFEXP_ONLY_TEX, pfl_roots))

    def test_nfexp_def_sees_own_textures_first(self):
        roots = package_texture_roots(FAKE_ROOT / "NFexp" / "1.7" / "Defs" / "a.xml", FAKE_ROOT, FAKE_VERSIONS)
        self.assertEqual(FAKE_ROOT / "NFexp" / "1.7" / "Textures", roots[0])

    def test_nfexp_def_sees_pfl_17_textures(self):
        """依赖方向允许：NFexp loadAfter PFL ⇒ 可以复用 PFL 1.7 的图。"""
        roots = package_texture_roots(FAKE_ROOT / "NFexp" / "1.7" / "Defs" / "a.xml", FAKE_ROOT, FAKE_VERSIONS)
        self.assertIn(FAKE_ROOT / "1.7" / "Textures", roots)

    def test_nfexp_def_does_not_see_pfl_16_textures(self):
        """NFexp 只有 1.7 线；1.6 的贴图在 1.7 游戏里不加载，看见它就是假绿灯。"""
        roots = package_texture_roots(FAKE_ROOT / "NFexp" / "1.7" / "Defs" / "a.xml", FAKE_ROOT, FAKE_VERSIONS)
        self.assertNotIn(FAKE_ROOT / "1.6" / "Textures", roots)

    def test_nfexp_def_resolves_own_shipped_texture(self):
        roots = package_texture_roots(ROOT / "NFexp" / "1.7" / "Defs" / "a.xml", ROOT, VERSION_DIRS)
        found = resolve_texture(NFEXP_ONLY_TEX, roots)
        self.assertIsNotNone(found)
        self.assertEqual(ROOT / "NFexp" / "1.7" / "Textures" / f"{NFEXP_ONLY_TEX}.png", found)

    def test_shipped_trees_keep_the_split(self):
        """真实仓库的 1.7/NFexp 两个源都走一遍，防止将来把根列表改宽。"""
        pfl_roots = package_texture_roots(ROOT / "1.7" / "Defs" / "a.xml", ROOT, VERSION_DIRS)
        self.assertFalse(any("NFexp" in str(r) for r in pfl_roots), pfl_roots)

        nfexp_roots = package_texture_roots(ROOT / "NFexp" / "1.7" / "Defs" / "a.xml", ROOT, VERSION_DIRS)
        self.assertFalse(any(r == ROOT / "1.6" / "Textures" for r in nfexp_roots), nfexp_roots)

    def test_missing_texture_stays_missing(self):
        roots = package_texture_roots(ROOT / "NFexp" / "1.7" / "Defs" / "a.xml", ROOT, VERSION_DIRS)
        self.assertIsNone(resolve_texture("Things/Resource/Payloads/NotARealTexture", roots))


if __name__ == "__main__":
    unittest.main()
