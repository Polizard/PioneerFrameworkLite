using System;
using System.IO;
using PioneerFramework;
using Verse;
using Xunit;

namespace PioneerFramework.Tests
{
    /// <summary>
    /// **B1 `nextPartId` 落盘**：分配器必须随实体存档往返，否则读档后 AddPart 会复用旧 id。
    /// 沿用 `SpacePartScribeTests` 的两段式 Scribe 范式（见该类头注）。
    /// </summary>
    public class SpaceEntityPartScribeTests
    {
        private const string XmlRoot = "entity";

        private static string SaveToTemp(SpaceEntity e)
        {
            string path = Path.Combine(Path.GetTempPath(), $"pfl_entity_{Guid.NewGuid():N}.xml");
            Scribe.saver.InitSaving(path, XmlRoot);
            e.ExposeData();
            Scribe.saver.FinalizeSaving();
            return path;
        }

        private static void SafeStop()
        {
            if (Scribe.mode != LoadSaveMode.Inactive)
                Scribe.ForceStop();
        }

        [Fact]
        public void NextPartId_RoundTrips_AndAllocatesAboveSavedValue()
        {
            SafeStop();
            var src = new SpaceEntity(42, 0);
            for (int i = 0; i < 4; i++) src.AllocPartId();   // nextPartId = 5
            string path = SaveToTemp(src);

            try
            {
                var dst = new SpaceEntity();
                Scribe.loader.InitLoading(path);
                dst.ExposeData();
                Scribe.ExitNode();
                Scribe.mode = LoadSaveMode.Inactive;

                var part = new SpacePart
                {
                    defName = "PF_ShipPart_Cube",
                    positionShip = DVector3.zero,
                    boundsSizeShip = new DVector3(2, 2, 2),
                };
                Assert.True(dst.AddPart(part));
                Assert.Equal(5, part.partId);
            }
            finally
            {
                SafeStop();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Rotation_DQuaternion_RoundTrips()
        {
            SafeStop();
            var src = new SpaceEntity(43, 0);
            DQuaternion q = DQuaternion.AngleAxis(0.777, new DVector3(0.2, 0.5, -0.8));
            src.SetRotation(q);
            string path = SaveToTemp(src);

            try
            {
                var dst = new SpaceEntity();
                Scribe.loader.InitLoading(path);
                dst.ExposeData();
                Scribe.ExitNode();
                Scribe.mode = LoadSaveMode.Inactive;

                var r = dst.Rotation;
                Assert.Equal(q.x, r.x, 12);
                Assert.Equal(q.y, r.y, 12);
                Assert.Equal(q.z, r.z, 12);
                Assert.Equal(q.w, r.w, 12);
            }
            finally
            {
                SafeStop();
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
