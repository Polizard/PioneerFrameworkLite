using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PioneerFramework.TestSupport.Celestial;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>
/// 2026-11-24 用户裁定：地表补丁缓存只做“超过 7 天未用自动淘汰”。
/// 这里只测文件选择逻辑（临时目录），不碰真实 LocalLow、不启动游戏。
/// </summary>
public sealed class PFViewSurfaceBakeDiskCacheTests : IDisposable
{
    private readonly string directory;
    private readonly DateTime now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    public PFViewSurfaceBakeDiskCacheTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "pfl-bake-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SweepDeletesOnlyOldCachePngsAndKeepsProtectedAndFresh()
    {
        string oldPatch = Write("123_47_patch3.png", now.AddDays(-8), 100);
        string freshPatch = Write("123_48_patch3.png", now.AddDays(-1), 200);
        string oldLegacy = Write("123_45.png", now.AddDays(-8), 300);
        string oldNegativeSeed = Write("-7_9.png", now.AddDays(-8), 400);
        string oldPatch2 = Write("123_46_patch2.png", now.AddDays(-8), 500);
        string oldPatchV2 = Write("123_45_patch.png", now.AddDays(-8), 550);
        string oldProtected = Write("123_49_patch3.png", now.AddDays(-30), 600);
        string oldLive = Write("123_50_patch3.png", now.AddDays(-30), 700);
        string notes = Write("notes.txt", now.AddDays(-30), 800);
        string randomPng = Write("random.png", now.AddDays(-30), 900);

        var live = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        live["123_50_patch3.png"] = 1;

        var result = PFViewSurfaceBake.SweepBakeCache(directory, now, TimeSpan.FromDays(7),
            new[] { Path.Combine(directory, "123_49_patch3.png") }, live);

        Assert.False(File.Exists(oldPatch));
        Assert.False(File.Exists(oldLegacy));
        Assert.False(File.Exists(oldNegativeSeed));
        Assert.False(File.Exists(oldPatch2));
        Assert.False(File.Exists(oldPatchV2));
        Assert.True(File.Exists(freshPatch));
        Assert.True(File.Exists(oldProtected));
        Assert.True(File.Exists(oldLive));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(randomPng));
        Assert.Equal(5, result.DeletedCount);
        Assert.Equal(100 + 300 + 400 + 500 + 550, result.DeletedBytes);
        Assert.Equal(3, result.KeptCount);                 // fresh + protected + live；非缓存文件不计
        Assert.Equal(200 + 600 + 700, result.KeptBytes);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void SweepUsesStrictlyMoreThanSevenDays()
    {
        string exactly = Write("123_51_patch3.png", now.AddDays(-7), 10);
        string older = Write("123_52_patch3.png", now.AddDays(-7).AddSeconds(-1), 10);
        string newer = Write("123_53_patch3.png", now.AddDays(-7).AddSeconds(1), 10);

        var result = PFViewSurfaceBake.SweepBakeCache(directory, now, TimeSpan.FromDays(7));

        Assert.True(File.Exists(exactly));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(2, result.KeptCount);
    }

    [Fact]
    public void TouchCacheFileRefreshesLastUseBeforeSweep()
    {
        string path = Write("123_54_patch3.png", now.AddDays(-30), 10);
        DateTime before = DateTime.UtcNow.AddSeconds(-1);

        PFViewSurfaceBake.TouchCacheFile(path);
        Assert.True(File.GetLastWriteTimeUtc(path) >= before);

        var result = PFViewSurfaceBake.SweepBakeCache(directory, DateTime.UtcNow, TimeSpan.FromDays(7));
        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF_patch3.png", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF_patch2.PNG", true)]
    [InlineData("123_45.png", true)]
    [InlineData("-123_45.png", true)]
    [InlineData("notes.txt", false)]
    [InlineData("random.png", false)]
    [InlineData("123_45_patch.png", true)]
    [InlineData("123_46_patch2.png", true)]
    [InlineData("abc_patch3.png", false)]
    [InlineData("abc_patch2.PNG", false)]
    [InlineData("abc_patch.png", false)]
    [InlineData("my_patch.png", false)]
    [InlineData("abc_patch3_extra.png", false)]
    [InlineData("123_45_fill.png", false)]
    [InlineData("123_.png", false)]
    [InlineData("_45.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CacheFilePatternIsConservative(string name, bool expected) =>
        Assert.Equal(expected, PFViewSurfaceBake.IsBakeCacheFile(name));

    [Fact]
    public void StorePatchReleasesTheSupersededPendingSidecarProtection()
    {
        using var native = new CelestialNativeWorldTestScope();
        const int handle = 34131;
        PFViewSurfaceBake.Clear();
        try
        {
            Assert.False(PFViewSurfaceBake.EnsureReady(handle, null));
            Assert.True(PFViewSurfaceBake.IsSidecarPending(handle));

            string path = (string)typeof(PFViewSurfaceBake)
                .GetMethod("PathFor", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { native.World.info.Seed, handle })!;
            string name = Path.GetFileName(path);

            var patchType = typeof(PFViewSurfaceBake).GetNestedType("Patch", BindingFlags.NonPublic)!;
            var patch = Activator.CreateInstance(patchType)!;
            patchType.GetField("path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(patch, path);
            typeof(PFViewSurfaceBake).GetMethod("StorePatch", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new[] { (object)handle, patch });

            var inFlight = (IDictionary)typeof(PFViewSurfaceBake)
                .GetField("inFlightCacheFiles", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            Assert.False(inFlight.Contains(name), "superseded request left an in-flight reference");
            Assert.False(PFViewSurfaceBake.IsSidecarPending(handle));
        }
        finally
        {
            PFViewSurfaceBake.Clear();
        }
    }

    [Fact]
    public void InFlightReferenceCountKeepsFileUntilAllUsersRelease()
    {
        string path = Write("123_45_patch3.png", now.AddDays(-30), 10);
        var live = (IReadOnlyDictionary<string, int>)typeof(PFViewSurfaceBake)
            .GetField("inFlightCacheFiles", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        PFViewSurfaceBake.TrackCacheFileInFlight(path);
        PFViewSurfaceBake.TrackCacheFileInFlight(path);
        PFViewSurfaceBake.UntrackCacheFileInFlight(path);

        var kept = PFViewSurfaceBake.SweepBakeCache(directory, now, TimeSpan.FromDays(7), null, live);
        Assert.True(File.Exists(path));
        Assert.Equal(0, kept.DeletedCount);

        PFViewSurfaceBake.UntrackCacheFileInFlight(path);
        var deleted = PFViewSurfaceBake.SweepBakeCache(directory, now, TimeSpan.FromDays(7), null, live);
        Assert.False(File.Exists(path));
        Assert.Equal(1, deleted.DeletedCount);
    }

    [Fact]
    public void SweepMissingDirectoryIsNoOp()
    {
        var result = PFViewSurfaceBake.SweepBakeCache(Path.Combine(directory, "missing"),
            now, TimeSpan.FromDays(7));
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.KeptCount);
    }

    private string Write(string name, DateTime lastWriteUtc, int bytes)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
