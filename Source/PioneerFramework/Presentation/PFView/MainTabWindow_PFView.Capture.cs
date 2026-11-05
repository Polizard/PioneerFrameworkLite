using System;
using System.IO;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        /// <summary>
        /// 调试用：把当前 PFView 画面落成 PNG（2026-10-14）。
        ///
        /// 用途：像"西瓜纹/亮度"这类**只能看**的问题，落成文件就变成可读判据（离屏看图，不靠描述）；
        /// PNG 编解码链在 1.7 上已被烘焙流水线验证过（`ReadPixels` + `EncodeToPNG` 都通）。
        /// 落点：`GenFilePaths.SaveDataFolderPath/PFView_shots/pfview_&lt;时间戳&gt;.png`。
        /// </summary>
        internal static string? CaptureToPng()
        {
            var win = Find.WindowStack?.WindowOfType<MainTabWindow_PFView>();
            if (win == null) return null;
            var rt = win.renderTexture;
            if (rt == null || rt.width <= 0 || rt.height <= 0) return null;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;

            byte[] png = ImageConversion.EncodeToPNG(tex);
            UnityEngine.Object.Destroy(tex);
            if (png == null || png.Length == 0) return null;

            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "PFView_shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "pfview_" + $"{DateTime.Now:yyyyMMdd_HHmmss}" + ".png");
            File.WriteAllBytes(path, png);
            return path;
        }
    }
}
