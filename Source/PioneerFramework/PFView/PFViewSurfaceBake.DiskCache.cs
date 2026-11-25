using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Verse;

namespace PioneerFramework;

internal static partial class PFViewSurfaceBake
{
    /// <summary>
    /// 磁盘补丁缓存的保留天数（2026-11-24 用户裁定：**只做自动清理，超过 7 天未用自动淘汰**）。
    /// “未用”= 没有重新烘焙、没有从旁挂文件成功读回、也没有在内存里持续显示（内存命中也按 6 小时节流刷新时间戳）。
    /// </summary>
    internal const int DiskCacheMaxAgeDays = 7;

    /// <summary>内存命中刷新磁盘时间戳的节流间隔（每帧路径，6 小时一次足够表达“还在用”）。</summary>
    private static readonly long CacheTouchIntervalTicks = 6L * 60 * 60 * Stopwatch.Frequency;

    /// <summary>
    /// 正在写/正在读的缓存文件名。清理任务在后台运行，必须能看见“此刻正在用”的文件；
    /// 只在主线程登记/注销，后台只读（ConcurrentDictionary 保证线程安全）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> inFlightCacheFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记/注销与“最后复查 + Delete”共用的一把锁；只在主线程登记、后台删除时短暂持有。</summary>
    private static readonly object CacheFileGate = new();

    private static Task<BakeCacheSweepResult>? diskSweep;

    internal readonly record struct BakeCacheSweepResult(
        int DeletedCount, long DeletedBytes, int KeptCount, long KeptBytes, int FailedCount);

    /// <summary>
    /// 把文件时间戳刷新为“刚刚用过”。三个调用点：烘焙落盘、旁挂文件成功读回、内存命中（6 小时节流）。
    /// 为什么不用文件系统的“最后访问时间”：Windows 默认关闭 last-access 更新（且可被优化掉），不可靠；
    /// 这里用 LastWriteTimeUtc 作为“最后一次使用/生成”的唯一口径。
    /// </summary>
    internal static void TouchCacheFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (IsCacheFileIoError(ex))
        {
            Log.WarningOnce("[PFView·烘焙缓存] 更新使用时间失败（不影响本次使用）：" + ex.Message, 0x5F502);
        }
    }

    internal static void TrackCacheFileInFlight(string? path)
    {
        string? name = CacheFileNameOf(path);
        if (name == null) return;
        lock (CacheFileGate)
        {
            inFlightCacheFiles.AddOrUpdate(name, 1, (_, count) => count + 1);
        }
    }

    internal static void UntrackCacheFileInFlight(string? path)
    {
        string? name = CacheFileNameOf(path);
        if (name == null) return;

        // 引用计数：同一张图可能同时被“落盘写入”和“旁挂读回”登记（例如先排队读、又手动重烤），
        // 任意一方先结束都不能撤掉另一方的保护。只在计数归零时才真正移除。
        lock (CacheFileGate)
        {
            while (inFlightCacheFiles.TryGetValue(name, out int count))
            {
                if (count <= 1)
                {
                    if (((ICollection<KeyValuePair<string, int>>)inFlightCacheFiles)
                        .Remove(new KeyValuePair<string, int>(name, count)))
                        return;
                }
                else if (inFlightCacheFiles.TryUpdate(name, count - 1, count))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 只认本系统生成的缓存文件：当前 <c>&lt;64 位 DiskKey&gt;_patch3.png</c>、旧版
    /// <c>&lt;种子&gt;_&lt;bodyId&gt;_patchN.png</c>（N 可省）与旧版整球图 <c>&lt;种子&gt;_&lt;bodyId&gt;.png</c>
    /// （种子可为负）。别的文件一概不碰。
    /// </summary>
    internal static bool IsBakeCacheFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;

        // 当前命名 = <hash>_patch3.png（以及旧版 _patch2/_patch1）：标记后必须全是数字，且紧邻 .png。
        const string PatchMarker = "_patch";
        int marker = fileName.LastIndexOf(PatchMarker, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            string prefix = fileName[..marker];
            int digitsStart = marker + PatchMarker.Length;
            int extensionStart = fileName.Length - 4;
            if (digitsStart < extensionStart)
            {
                bool allDigits = true;
                for (int i = digitsStart; i < extensionStart; i++)
                {
                    if (fileName[i] < '0' || fileName[i] > '9') { allDigits = false; break; }
                }
                // 前缀必须是本系统的两种之一：64 位 DiskKey 哈希，或旧版 <种子>_<bodyId>。
                if (allDigits && (IsHash64(prefix) || IsSeedBodyStem(prefix))) return true;
            }
            else if (digitsStart == extensionStart && (IsHash64(prefix) || IsSeedBodyStem(prefix)))
            {
                // 旧版 v2 命名：<世界种子>_<bodyId>_patch.png（无版本数字）
                return true;
            }
        }

        return IsSeedBodyStem(fileName[..^4]);
    }

    private static bool IsHash64(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>旧版文件名前缀：<c>&lt;int 世界种子&gt;_&lt;int bodyId&gt;</c>（种子可为负）。</summary>
    private static bool IsSeedBodyStem(string stem)
    {
        int separator = stem.IndexOf('_');
        if (separator <= 0 || separator == stem.Length - 1) return false;
        return int.TryParse(stem.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(stem.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// 淘汰 <paramref name="maxAge"/> 之前写入/使用过的缓存文件。纯文件操作、无 Verse 依赖，测试可传临时目录。
    /// <paramref name="protectedFileNames"/> 是调用时刻的保护快照；<paramref name="liveFileNames"/> 是后台任务运行期间
    /// 仍可追加的“正在读/写”集合（键为文件名）。任何保护中的文件都不删，哪怕已经过期。
    /// 单文件失败只记数，不中断其余文件；目录不存在/参数非法时直接返回空结果。
    /// </summary>
    internal static BakeCacheSweepResult SweepBakeCache(
        string? directory,
        DateTime utcNow,
        TimeSpan maxAge,
        IReadOnlyCollection<string>? protectedFileNames = null,
        IReadOnlyDictionary<string, int>? liveFileNames = null)
    {
        int deletedCount = 0, keptCount = 0, failedCount = 0;
        long deletedBytes = 0, keptBytes = 0;
        if (string.IsNullOrEmpty(directory) || maxAge <= TimeSpan.Zero || !Directory.Exists(directory))
            return new(deletedCount, deletedBytes, keptCount, keptBytes, failedCount);

        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedFileNames != null)
            foreach (string path in protectedFileNames)
            {
                string? name = CacheFileNameOf(path);
                if (name != null) protectedNames.Add(name);
            }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!IsBakeCacheFile(name)) continue;

                long bytes = 0;
                try { bytes = new FileInfo(file).Length; }
                catch (Exception ex) when (IsCacheFileIoError(ex)) { }

                if (protectedNames.Contains(name) ||
                    (liveFileNames != null && liveFileNames.ContainsKey(name)))
                {
                    keptCount++;
                    keptBytes += bytes;
                    continue;
                }

                DateTime lastWriteUtc;
                try { lastWriteUtc = File.GetLastWriteTimeUtc(file); }
                catch (Exception ex) when (IsCacheFileIoError(ex))
                {
                    failedCount++;
                    continue;
                }

                if (utcNow - lastWriteUtc <= maxAge)
                {
                    keptCount++;
                    keptBytes += bytes;
                    continue;
                }

                // 再查一次“正在读/写”：上面读时间戳的这几毫秒里，主线程可能刚好登记了一个旁挂读请求。
                if (liveFileNames != null && liveFileNames.ContainsKey(name))
                {
                    keptCount++;
                    keptBytes += bytes;
                    continue;
                }

                // 最后复查 + Delete 与 Track/Untrack 共用 CacheFileGate：
                // 防止“清理线程手里是旧时间戳，主线程刚写完新文件/刚登记 in-flight”时误删。
                lock (CacheFileGate)
                {
                    if (!File.Exists(file)) continue;      // 枚举后已消失：不算失败，也不算保留

                    DateTime recheckUtc;
                    try { recheckUtc = File.GetLastWriteTimeUtc(file); }
                    catch (Exception ex) when (IsCacheFileIoError(ex))
                    {
                        failedCount++;
                        continue;
                    }

                    if (recheckUtc != lastWriteUtc || utcNow - recheckUtc <= maxAge ||
                        (liveFileNames != null && liveFileNames.ContainsKey(name)))
                    {
                        keptCount++;
                        keptBytes += bytes;
                        continue;
                    }

                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                        deletedBytes += bytes;
                    }
                    catch (Exception ex) when (IsCacheFileIoError(ex))
                    {
                        // 如果文件是在临界区内被别的进程删掉的，不记为失败。
                        if (File.Exists(file)) failedCount++;
                    }
                }
            }
        }
        catch (Exception ex) when (IsCacheFileIoError(ex))
        {
            failedCount++;
        }

        return new(deletedCount, deletedBytes, keptCount, keptBytes, failedCount);
    }

    /// <summary>
    /// 请求一次后台清理。打开 PFView 与每次烘焙成功后调用；已有清理在跑时直接复用，不叠加。
    /// 目录/任务创建失败只记一次警告，绝不让缓存维护拖垮视图打开或烘焙。
    /// </summary>
    internal static void RequestDiskSweep()
    {
        PumpDiskSweep();
        if (diskSweep is { IsCompleted: false }) return;

        string directory;
        try
        {
            directory = Path.Combine(GenFilePaths.SaveDataFolderPath, FolderName);
        }
        catch (Exception ex)
        {
            Log.WarningOnce("[PFView·烘焙缓存] 取不到缓存目录，跳过自动清理：" + ex.Message, 0x5F503);
            return;
        }

        var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in baked.Values) AddProtectedName(protectedNames, patch.path);
        foreach (var request in pendingSidecars.Values) AddProtectedName(protectedNames, request.Path);

        try
        {
            diskSweep = Task.Run(() => SweepBakeCache(directory, DateTime.UtcNow,
                TimeSpan.FromDays(DiskCacheMaxAgeDays), protectedNames, inFlightCacheFiles));
        }
        catch (Exception ex)
        {
            Log.WarningOnce("[PFView·烘焙缓存] 启动后台清理失败（下次再试）：" + ex.Message, 0x5F504);
        }
    }

    private static void PumpDiskSweep()
    {
        if (diskSweep is not { IsCompleted: true } task) return;
        diskSweep = null;
        if (task.IsCanceled) return;
        if (task.IsFaulted)
        {
            Log.WarningOnce("[PFView·烘焙缓存] 自动清理失败（下次再试）：" +
                            (task.Exception?.GetBaseException().Message ?? "unknown"), 0x5F505);
            return;
        }

        BakeCacheSweepResult result;
        try { result = task.Result; }
        catch (Exception ex)
        {
            Log.WarningOnce("[PFView·烘焙缓存] 自动清理结果读取失败（下次再试）：" + ex.GetBaseException().Message, 0x5F507);
            return;
        }

        if (result.DeletedCount > 0)
            Log.Message($"[PFView·烘焙缓存] 自动淘汰（超过 {DiskCacheMaxAgeDays} 天未用）：删除 {result.DeletedCount} 张 / " +
                        $"{Megabytes(result.DeletedBytes)} MB；保留 {result.KeptCount} 张 / {Megabytes(result.KeptBytes)} MB");
        if (result.FailedCount > 0)
            Log.WarningOnce($"[PFView·烘焙缓存] 自动淘汰有 {result.FailedCount} 个文件未能处理（下次再试）", 0x5F506);
    }

    private static void AddProtectedName(HashSet<string> names, string? path)
    {
        string? name = CacheFileNameOf(path);
        if (name != null) names.Add(name);
    }

    private static string? CacheFileNameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            string? name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex) when (IsCacheFileIoError(ex))
        {
            return null;
        }
    }

    private static bool IsCacheFileIoError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);
}
