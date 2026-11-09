"""ShaderDef / GLSL 元数据回归（2026-11-09）。

背景：Waylith 的 ShaderDef 注册要求每个 `layout(location = N) in ...` 顶点输入带
`// @vertex <semantic> required|optional` 注解；漏写会让 Shader 注册失败并在启动时弹错
（PF_TerrainLit 首次提交就是这么炸的）。本测试扫描 `1.7/Shaders/*.vert.glsl` 与
`1.7/Defs/ShaderDefs/*.xml`，把"游戏启动才炸"提前到 CI。
"""
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHADER_DIR = ROOT / "1.7" / "Shaders"
SHADER_DEF_DIR = ROOT / "1.7" / "Defs" / "ShaderDefs"

VERTEX_INPUT = re.compile(r"^\s*layout\s*\(\s*location\s*=\s*\d+\s*\)\s*in\s+")


def vertex_lines(path):
    return path.read_text(encoding="utf-8").splitlines()


class ShaderMetadataTests(unittest.TestCase):
    def test_every_vertex_input_has_semantic_annotation(self):
        for path in sorted(SHADER_DIR.glob("*.vert.glsl")):
            for number, line in enumerate(vertex_lines(path), start=1):
                if not VERTEX_INPUT.match(line):
                    continue
                self.assertIn("@vertex", line,
                              f"{path.name}:{number} 缺少 // @vertex 注解：{line.strip()}")
                self.assertRegex(line, r"@vertex\s+\w+\s+(required|optional)",
                                 f"{path.name}:{number} 注解缺少 required/optional：{line.strip()}")

    def test_shader_def_files_exist(self):
        self.assertTrue(SHADER_DEF_DIR.is_dir(), SHADER_DEF_DIR)
        seen = 0
        for path in sorted(SHADER_DEF_DIR.glob("*.xml")):
            root = ET.parse(path).getroot()
            for program in root.iter("shaderProgram"):
                for tag in ("vertexShaderFileName", "fragmentShaderFileName"):
                    node = program.find(tag)
                    if node is None or not node.text:
                        continue
                    seen += 1
                    self.assertTrue((SHADER_DIR / node.text.strip()).is_file(),
                                    f"{path.name}: {tag} 指向不存在的 GLSL：{node.text}")
        self.assertGreater(seen, 0)


if __name__ == "__main__":
    unittest.main()