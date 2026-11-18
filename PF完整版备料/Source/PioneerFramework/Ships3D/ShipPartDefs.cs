using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.Components;

namespace PioneerFramework
{
    /// <summary>
    /// ShipPartDef 的轻量注册表。不依赖 DefDatabase（避免反射 Add 失败），
    /// 同时提供 GetDef 统一查询入口（优先查 DefDatabase，回退到本地字典）。
    /// 功能角色由预置组件表达；几何形状纯几何（Cube / SphericalCorner）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ShipPartDefs
    {
        private static readonly Dictionary<string, ShipPartDef> localDefs = new Dictionary<string, ShipPartDef>();

        static ShipPartDefs()
        {
            Register("PF_ShipPart_Cube", ShipPartShape.Cube,
                new Vector3(1f, 1f, 1f), 100f, new Color(0.3f, 0.6f, 1.0f));

            Register("PF_ShipPart_SphericalCorner", ShipPartShape.SphericalCorner,
                new Vector3(1f, 1f, 1f), 80f, new Color(1.0f, 0.5f, 0.2f));

            Register("PF_ShipPart_Engine", ShipPartShape.Cube,
                new Vector3(2f, 2f, 3f), 0f, new Color(1.0f, 0.3f, 0.2f),
                new EngineComponentData
                {
                    exhaustVelocity = 30000f,
                    massFlowRate = 5f,
                    dryMass = 500f
                });

            Register("PF_ShipPart_PropellantTank", ShipPartShape.Cube,
                new Vector3(2f, 2f, 2f), 0f, new Color(0.2f, 0.8f, 0.5f),
                new PropellantTankComponentData
                {
                    dryMass = 500f,
                    wetMass = 5000f
                });

            Register("PF_ShipPart_Weapon", ShipPartShape.Cube,
                new Vector3(2f, 1f, 2f), 0f, new Color(0.7f, 0.2f, 0.2f),
                new WeaponMountComponentData
                {
                    weaponDefName = "PF_Weapon_ProjectileCannon",
                    turretRotationSpeed = 30f,
                    trackingArc = 360f,
                    muzzleOffset = new DVector3(0f, 15f, 0f), // 1.5 Unity 单位 = 15 物理米
                    dryMass = 400f
                });

            Register("PF_ShipPart_CargoHold", ShipPartShape.Cube,
                new Vector3(2f, 3f, 2f), 0f, new Color(0.8f, 0.8f, 0.3f),
                new CargoComponentData
                {
                    capacity = 10000f,
                    dryMass = 200f
                });

            Register("PF_ShipPart_RoomModule", ShipPartShape.Cube,
                new Vector3(2f, 2f, 2f), 0f, new Color(0.3f, 1.0f, 1.0f),
                new RoomComponentData
                {
                    roomTypeDefName = null,
                    mapWidth = 10,
                    mapHeight = 10,
                    dryMass = 400f
                });

            Log.Message("[ShipPartDefs] Registered " + localDefs.Count + " default ShipPartDefs.");
        }

        private static void Register(string defName, ShipPartShape shape,
            Vector3 bounds, float massBase, Color previewColor,
            params PartComponentData[] presetComponents)
        {
            var def = new ShipPartDef
            {
                defName = defName,
                label = defName,
                shapeType = shape,
                bounds = bounds,
                massBase = massBase,
                previewColor = previewColor,
                presetComponents = presetComponents != null
                    ? new List<PartComponentData>(presetComponents)
                    : null
            };

            localDefs[defName] = def;

            // 尝试注入 DefDatabase（可选，失败不影响使用）
            TryInjectToDefDatabase(def);
        }

        private static void TryInjectToDefDatabase(ShipPartDef def)
        {
            try
            {
                var dbType = typeof(DefDatabase<>).MakeGenericType(typeof(ShipPartDef));
                var addMethod = dbType.GetMethod("Add",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (addMethod != null)
                {
                    addMethod.Invoke(null, new object[] { def });
                }
            }
            catch
            {
                // 静默失败，使用本地字典兜底
            }
        }

        /// <summary>
        /// 统一查询入口：先查 DefDatabase，回退到本地字典。
        /// </summary>
        public static ShipPartDef GetDef(string defName)
        {
            var def = DefDatabase<ShipPartDef>.GetNamedSilentFail(defName);
            if (def != null) return def;
            localDefs.TryGetValue(defName, out def);
            return def;
        }

        /// <summary>
        /// 获取几何形状对应的默认 Def 名称。
        /// </summary>
        public static string GetDefaultDefName(ShipPartShape shape)
        {
            switch (shape)
            {
                case ShipPartShape.SphericalCorner: return "PF_ShipPart_SphericalCorner";
                default: return "PF_ShipPart_Cube";
            }
        }
    }
}
