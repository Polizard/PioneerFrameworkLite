using System;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private Material? solarPlasmaMaterial;
        private Material? solarCompositeMaterial;
        private readonly Material?[] solarBloomMaterials = new Material?[4];
        private RenderTexture? solarHdr;
        private RenderTexture? solarBloomA;
        private RenderTexture? solarBloomB;
        private Mesh? solarCompositeMesh;
        private bool solarResourcesUnavailable;
        private bool solarImageValid;
        private float solarNextUpdate;
        private SolarImageState solarLastState;

        private readonly record struct SolarImageState(Vector4 Eye, Vector4 Right, Vector4 Up,
            Vector4 Forward, Vector4 Geometry, Vector4 Time)
        {
            public bool SameCamera(SolarImageState other) => Eye.Equals(other.Eye) && Right.Equals(other.Right)
                && Up.Equals(other.Up) && Forward.Equals(other.Forward) && Geometry.Equals(other.Geometry);
        }

        /// <summary>
        /// 三维体积在限尺寸 HDR 图中求交；近景用完整相机视口，避免贴近/平移时在面片边缘截断太阳。
        /// 远景保留原来的日照方向、角大小和背景遮挡契约。资源不可用时调用方保留圆盘回退。
        /// </summary>
        private bool TryDrawSolarPlasma(Vector3 camPos)
        {
            if (solarResourcesUnavailable || renderCamera == null) return false;
            try
            {
                if (!EnsureSolarMaterials()) return false;
                bool focused = IsStarFocus;
                float aspect = focused ? renderCamera.aspect : 1f;
                float diameter = PFViewSunDisc.DiameterPixels(sunDiscAngularRadiusDeg,
                    renderCamera.fieldOfView, renderCamera.pixelHeight);
                var size = PFViewSolarCore.TextureSize(renderCamera.pixelWidth, renderCamera.pixelHeight, focused, diameter);
                EnsureSolarTargets(size.Width, size.Height);

                Vector3 direction = SunDiscDir(camPos);
                Quaternion plane = focused ? renderCamera.transform.rotation
                    : Quaternion.LookRotation(direction, PFViewSunDisc.SafeUp(direction));
                Quaternion toInertial = Quaternion.Inverse(viewFrameRot);
                float eyeRadius = (float)PFViewSolarCore.FarEyeRadius(sunDiscAngularRadiusDeg);
                float halfTangent = focused ? Mathf.Tan(renderCamera.fieldOfView * .5f * Mathf.Deg2Rad)
                    : (float)PFViewSolarCore.ProjectionHalfTangent(eyeRadius);
                Vector3 eye = focused ? camPos / FocusBodyRadiusView() : -direction * eyeRadius;
                Vector3 forward = focused ? renderCamera.transform.forward : direction;
                var state = new SolarImageState(SolarVector(toInertial * eye),
                    SolarVector(toInertial * (plane * Vector3.right)), SolarVector(toInertial * (plane * Vector3.up)),
                    SolarVector(toInertial * forward), new Vector4(halfTangent, aspect, 0f, focused ? 96f : 64f),
                    new Vector4((float)PFViewSolarCore.Phase(RenderTick()), 2f, 2f, 2f));

                bool moved = !solarImageValid || !state.SameCamera(solarLastState);
                if (moved || (!state.Time.Equals(solarLastState.Time) && Time.realtimeSinceStartup >= solarNextUpdate))
                {
                    RenderSolarTexture(state);
                    solarLastState = state;
                    solarImageValid = true;
                    solarNextUpdate = Time.realtimeSinceStartup + 1f / 30f;
                }

                float drawHalfTangent = focused ? halfTangent : (float)PFViewSolarCore.FarQuadHalfTangent(sunDiscAngularRadiusDeg);
                Vector3 place = camPos + forward * SunPlaceDistView;
                float height = 2f * SunPlaceDistView * drawHalfTangent;
                Matrix4x4 matrix = Matrix4x4.TRS(place, plane, new Vector3(height * aspect, height, 1f));
                Graphics.DrawMesh(solarCompositeMesh, matrix, solarCompositeMaterial, SceneLayer, renderCamera);
                return true;
            }
            catch (Exception ex)
            {
                DestroySolarResources();
                solarResourcesUnavailable = true;
                Log.Warning("[PFView·太阳体积] 创建或绘制失败，本次窗口会话回退圆盘：" + ex.Message);
                return false;
            }
        }

        private static Vector4 SolarVector(Vector3 value) => new(value.x, value.y, value.z, 0f);

        private bool EnsureSolarMaterials()
        {
            if (solarPlasmaMaterial != null) return true;
            Shader plasma = Shader.Find("PF_SolarPlasma");
            Shader bloom = Shader.Find("PF_SolarBloom");
            Shader composite = Shader.Find("PF_SolarComposite");
            if (plasma == null || bloom == null || composite == null
                || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                solarResourcesUnavailable = true;
                Log.Warning("[PFView·太阳体积] 着色器或 HDR 离屏格式不可用，本次窗口会话回退圆盘。");
                return false;
            }

            solarPlasmaMaterial = new Material(plasma) { name = "PF_SolarPlasma" };
            solarCompositeMaterial = new Material(composite) { name = "PF_SolarComposite", renderQueue = PFViewLayers.SunDisc };
            solarCompositeMaterial.SetVector("_SolarComposite", new Vector4(.19f, 0f, 0f, 0f));
            // 独立材质保存每次 Blit 的采样和提亮参数，避免延迟提交读取后一次的可变属性。
            for (int i = 0; i < solarBloomMaterials.Length; i++)
                solarBloomMaterials[i] = new Material(bloom) { name = "PF_SolarBloom_" + i };

            PFViewSunDisc.DiscQuadGeometry(out var vertices, out var uvs, out var bothSides);
            var triangles = new int[6];
            Array.Copy(bothSides, triangles, triangles.Length);
            // 合成使用 Cull None，只保留一组绕序，防止前后两面重复累加辉光。
            solarCompositeMesh = new Mesh { name = "PF_SolarCompositeQuad", vertices = vertices, uv = uvs, triangles = triangles };
            solarCompositeMesh.RecalculateBounds();
            return true;
        }

        private void EnsureSolarTargets(int width, int height)
        {
            if (solarHdr != null && solarHdr.width == width && solarHdr.height == height) return;
            ReleaseSolarTarget(ref solarHdr);
            ReleaseSolarTarget(ref solarBloomA);
            ReleaseSolarTarget(ref solarBloomB);
            solarImageValid = false;
            solarHdr = CreateSolarTarget("PF_SolarHDR", width, height);
            int bloomWidth = Math.Max(1, width / 4), bloomHeight = Math.Max(1, height / 4);
            solarBloomA = CreateSolarTarget("PF_SolarBloomA", bloomWidth, bloomHeight);
            solarBloomB = CreateSolarTarget("PF_SolarBloomB", bloomWidth, bloomHeight);
            for (int i = 0; i < solarBloomMaterials.Length; i++)
            {
                bool horizontal = i % 2 == 0;
                solarBloomMaterials[i]!.SetVector("_SolarBlur", new Vector4(horizontal ? 1f / bloomWidth : 0f,
                    horizontal ? 0f : 1f / bloomHeight, i == 0 ? 1f : 0f, 0f));
            }
            solarCompositeMaterial!.SetTexture("_MainTex", solarHdr);
            solarCompositeMaterial.SetTexture("_BloomTex", solarBloomB);
            Log.Message($"[PFView·太阳体积] shader=PF_SolarPlasma · HDR={width}×{height} · 连续等离子体/八组磁环/四次辉光 · 近景上限 {PFViewSolarCore.FocusMaximumDimension}");
        }

        private static RenderTexture CreateSolarTarget(string name, int width, int height)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!target.Create())
            {
                UnityEngine.Object.Destroy(target);
                throw new InvalidOperationException("无法创建太阳 HDR 离屏纹理");
            }
            return target;
        }

        private void RenderSolarTexture(SolarImageState state)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                var material = solarPlasmaMaterial!;
                material.SetVector("_SolarEye", state.Eye);
                material.SetVector("_SolarRight", state.Right);
                material.SetVector("_SolarUp", state.Up);
                material.SetVector("_SolarForward", state.Forward);
                material.SetVector("_SolarGeometry", state.Geometry);
                material.SetVector("_SolarTime", state.Time);
                Graphics.Blit(Texture2D.whiteTexture, solarHdr, material, 0);
                for (int i = 0; i < solarBloomMaterials.Length; i++)
                {
                    RenderTexture source = i == 0 ? solarHdr! : i % 2 == 0 ? solarBloomB! : solarBloomA!;
                    RenderTexture destination = i % 2 == 0 ? solarBloomA! : solarBloomB!;
                    Material pass = solarBloomMaterials[i]!;
                    pass.SetTexture("_MainTex", source);
                    Graphics.Blit(source, destination, pass, 0);
                }
            }
            finally { RenderTexture.active = previous; }
        }

        private static void ReleaseSolarTarget(ref RenderTexture? target)
        {
            if (target == null) return;
            target.Release();
            UnityEngine.Object.Destroy(target);
            target = null;
        }

        private void DestroySolarResources()
        {
            ReleaseSolarTarget(ref solarHdr);
            ReleaseSolarTarget(ref solarBloomA);
            ReleaseSolarTarget(ref solarBloomB);
            if (solarPlasmaMaterial != null) UnityEngine.Object.Destroy(solarPlasmaMaterial);
            if (solarCompositeMaterial != null) UnityEngine.Object.Destroy(solarCompositeMaterial);
            if (solarCompositeMesh != null) UnityEngine.Object.Destroy(solarCompositeMesh);
            solarPlasmaMaterial = null;
            solarCompositeMaterial = null;
            solarCompositeMesh = null;
            for (int i = 0; i < solarBloomMaterials.Length; i++)
            {
                if (solarBloomMaterials[i] != null) UnityEngine.Object.Destroy(solarBloomMaterials[i]);
                solarBloomMaterials[i] = null;
            }
            solarImageValid = false;
            solarNextUpdate = 0f;
            solarResourcesUnavailable = false;
        }
    }
}
