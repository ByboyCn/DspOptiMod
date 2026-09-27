using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace DspOptiMod
{
    [BepInPlugin("byboy.dspopti", "DspOptiMod", "1.0.0")]
    public class DspOptiModPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource L;
        internal static ConfigEntry<bool> OptimizeSwarm;
        internal static ConfigEntry<bool> DisableNearPass;
        internal static ConfigEntry<bool> FramesFirst;
        internal static ConfigEntry<int> RingSegments;
        internal static ConfigEntry<float> RingWidthFactor;
        internal static ConfigEntry<float> RingMinAlpha;
        internal static ConfigEntry<float> RingAlphaScale;

        private void Awake()
        {
            L = base.Logger;
            OptimizeSwarm = Config.Bind("Swarm", "OptimizeSwarm", true,
                "优化戴森云渲染：远景不再逐帆绘制，每条轨道只画一条环带");
            DisableNearPass = Config.Bind("Swarm", "DisableNearPass", false,
                "同时禁用近处帆的精细网格渲染(2500m 内本来也只画近处的帆)");
            RingSegments = Config.Bind("Swarm", "RingSegments", 128, "环带分段数(64~256)");
            RingWidthFactor = Config.Bind("Swarm", "RingWidthFactor", 0.025f, "环带宽度 = 轨道半径 * 该系数");
            RingMinAlpha = Config.Bind("Swarm", "RingMinAlpha", 0.25f, "环带最低透明度");
            RingAlphaScale = Config.Bind("Swarm", "RingAlphaScale", 0.6f, "环带基础透明度");
            FramesFirst = Config.Bind("Rocket", "FramesFirst", true,
                "小火箭：所有节点本体全部建成后才开始建造框架");
            RingSegments.Value = Mathf.Clamp(RingSegments.Value, 32, 512);

            new Harmony("byboy.dspopti").PatchAll(Assembly.GetExecutingAssembly());
            L.LogInfo("DspOptiMod 已加载");
        }

        private void Update()
        {
            // F9 切换戴森云渲染优化（含近处帆渲染）
            if (Input.GetKeyDown(KeyCode.F9))
            {
                OptimizeSwarm.Value = !OptimizeSwarm.Value;
                L.LogInfo($"戴森云渲染优化: {(OptimizeSwarm.Value ? "开" : "关")}");
            }
        }
    }

    /// <summary>
    /// 功能2：改变 ConstructSp 的分配顺序 —— 原版是"节点本体填满后，本节点的框架就能开始建"。
    /// 这里改为：整颗戴森球【所有节点本体】全部建成后，才开始给框架分配结构点。
    /// </summary>
    [HarmonyPatch(typeof(DysonNode), "ConstructSp")]
    internal static class NodeConstructSpPatch
    {
        private static DysonSphere FindSphere(DysonNode node)
        {
            var spheres = GameMain.data?.dysonSpheres;
            if (spheres == null)
            {
                return null;
            }
            for (int s = 0; s < spheres.Length; s++)
            {
                DysonSphere sphere = spheres[s];
                if (sphere == null) continue;
                DysonSphereLayer layer = sphere.layersIdBased[node.layerId];
                if (layer != null && layer.nodePool[node.id] == node)
                {
                    return sphere;
                }
            }
            return null;
        }

        private static bool AllNodesBuilt(DysonSphere sphere)
        {
            DysonSphereLayer[] layers = sphere.layersIdBased;
            for (int i = 1; i < layers.Length; i++)
            {
                DysonSphereLayer layer = layers[i];
                if (layer == null || layer.id != i)
                {
                    continue;
                }
                DysonNode[] pool = layer.nodePool;
                for (int j = 1; j < layer.nodeCursor; j++)
                {
                    DysonNode n = pool[j];
                    if (n != null && n.id == j && n.sp < n.spMax)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool Prefix(DysonNode __instance, ref object __result)
        {
            if (!DspOptiModPlugin.FramesFirst.Value)
            {
                return true;
            }
            lock (__instance)
            {
                object result = null;
                bool framesAllowed = false;
                if (__instance.sp >= __instance.spMax)
                {
                    // 本节点已满，只有在全球节点都建成后才允许分给框架
                    DysonSphere sphere = FindSphere(__instance);
                    framesAllowed = sphere != null && AllNodesBuilt(sphere);
                }
                if (framesAllowed)
                {
                    var frames = __instance.frames;
                    if (frames != null)
                    {
                        for (int i = __instance.frameTurn; i < __instance.frameTurn + frames.Count; i++)
                        {
                            int num = i % frames.Count;
                            DysonFrame frame = frames[num];
                            int half = frame.spMax >> 1;
                            if (frame.nodeA == __instance && frame.spA < half)
                            {
                                frame.spA++;
                                __instance.frameTurn = num + 1;
                                result = frame;
                                break;
                            }
                            if (frame.nodeB == __instance && frame.spB < half)
                            {
                                frame.spB++;
                                __instance.frameTurn = num + 1;
                                result = frame;
                                break;
                            }
                        }
                    }
                }
                if (result == null && __instance.sp < __instance.spMax)
                {
                    __instance.sp++;
                    result = __instance;
                }
                __instance.spOrdered--;
                if (__instance.spOrdered < 0)
                {
                    __instance.spOrdered = 0;
                }
                __result = result;
            }
            __instance.RecalcSpReq();
            return false;
        }
    }

    /// <summary>
    /// 功能1：替换 DysonSwarm.DrawPost 远景渲染。
    /// 原版对每个帆做 DrawProceduralNow(Quads, sailCursor * 12)，几万帆时顶点/像素开销巨大。
    /// 这里整段跳过，改为每条轨道一条半透明环带(GL 立即模式，Hidden/Internal-Colored)，
    /// 同时保留原版的太阳帆子弹(发射轨迹)渲染。
    /// </summary>
    [HarmonyPatch(typeof(DysonSwarm), "DrawPost")]
    internal static class SwarmDrawPostPatch
    {
        private static Material _ringMat;
        private static FieldInfo _bulletBufferField;

        private static Material RingMaterial()
        {
            if (_ringMat == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null)
                {
                    DspOptiModPlugin.L.LogWarning("找不到 Hidden/Internal-Colored，回退原版渲染");
                    return null;
                }
                _ringMat = new Material(shader);
                _ringMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _ringMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _ringMat.SetInt("_Cull", (int)CullMode.Off);
                _ringMat.SetInt("_ZWrite", 0);
            }
            return _ringMat;
        }

        private static bool Prefix(DysonSwarm __instance)
        {
            if (!DspOptiModPlugin.OptimizeSwarm.Value)
            {
                return true; // 原版渲染
            }
            Material ringMat = RingMaterial();
            if (ringMat == null)
            {
                return true;
            }

            // ---- 与原版一致地计算太阳中心与本地旋转 ----
            Vector3 sunPos = Vector3.zero;
            Vector4 localRot4 = new Vector4(0f, 0f, 0f, 1f);
            StarData starData = __instance.starData;
            GameData gameData = __instance.gameData;
            if (starData == null || gameData == null)
            {
                return false;
            }
            PlanetData localPlanet = gameData.localPlanet;
            Player mainPlayer = gameData.mainPlayer;
            VectorLF3 starOffset = starData.uPosition;
            if (localPlanet != null)
            {
                starOffset -= mainPlayer.uPosition;
                starOffset = Maths.QInvRotateLF(localPlanet.runtimeRotation, starOffset);
                starOffset += (VectorLF3)mainPlayer.position;
                localRot4 = new Vector4(localPlanet.runtimeRotation.x, localPlanet.runtimeRotation.y,
                    localPlanet.runtimeRotation.z, localPlanet.runtimeRotation.w);
            }
            else
            {
                starOffset -= mainPlayer.uPosition;
            }
            sunPos = (Vector3)starOffset;
            if (DysonSphere.renderPlace == ERenderPlace.Starmap)
            {
                sunPos = (Vector3)((starData.uPosition - UIStarmap.viewTargetStatic) * 0.00025);
            }
            // Dysonmap 视角：原版 vector 保持 zero，环带以编辑器原点为中心
            Quaternion invLocal = Quaternion.identity;
            if (localPlanet != null && DysonSphere.renderPlace == ERenderPlace.Universe)
            {
                invLocal = Quaternion.Inverse(new Quaternion(localRot4.x, localRot4.y, localRot4.z, localRot4.w));
            }

            // ---- 每条轨道一条环带 ----
            int segments = DspOptiModPlugin.RingSegments.Value;
            float widthFactor = DspOptiModPlugin.RingWidthFactor.Value;
            SailOrbit[] orbits = __instance.orbits;
            Vector4[] orbitColors = __instance.orbitColorsHSVA;
            ringMat.SetPass(0);
            GL.Begin(GL.QUADS);
            try
            {
                for (int i = 1; i < __instance.orbitCursor && i < orbits.Length; i++)
                {
                    if (orbits[i].id != i || orbits[i].count <= 0 || !orbits[i].enabled)
                    {
                        continue;
                    }
                    float radius = orbits[i].radius;
                    Quaternion worldRot = invLocal * __instance.orbits[i].rotation;
                    Color c = Color.HSVToRGB(orbitColors[i].x, orbitColors[i].y, orbitColors[i].z);
                    float fill = Mathf.Clamp01(orbits[i].count / 6000f);
                    c.a = Mathf.Max(DspOptiModPlugin.RingMinAlpha.Value,
                        DspOptiModPlugin.RingAlphaScale.Value * (0.35f + 0.65f * fill));
                    float width = Mathf.Max(radius * widthFactor, 40f);
                    for (int k = 0; k < segments; k++)
                    {
                        float a0 = (float)k / segments * Mathf.PI * 2f;
                        float a1 = (float)(k + 1) / segments * Mathf.PI * 2f;
                        Vector3 p0 = worldRot * new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                        Vector3 p1 = worldRot * new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);
                        Vector3 n = worldRot * Vector3.up;
                        // 沿切向的宽度方向
                        Vector3 side = Vector3.Cross(n, (p1 - p0).normalized).normalized * width;
                        GL.Color(c);
                        GL.Vertex(sunPos + p0 - side);
                        GL.Vertex(sunPos + p0 + side);
                        GL.Vertex(sunPos + p1 + side);
                        GL.Vertex(sunPos + p1 - side);
                    }
                }
            }
            finally
            {
                GL.End();
            }

            // ---- 保留原版的太阳帆子弹(发射轨迹)渲染 ----
            if (_bulletBufferField == null)
            {
                _bulletBufferField = AccessTools.Field(typeof(DysonSwarm), "bulletBuffer");
            }
            ComputeBuffer bulletBuffer = (ComputeBuffer)_bulletBufferField.GetValue(__instance);
            Material bulletMaterial = __instance.bulletMaterial;
            if (bulletMaterial != null && bulletBuffer != null && __instance.bulletCursor > 1)
            {
                bulletMaterial.SetBuffer("_BulletBuffer", bulletBuffer);
                bulletMaterial.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Quads, __instance.bulletCursor * 8);
            }
            return false;
        }
    }

    /// <summary>
    /// 可选：同时跳过近处帆的精细渲染（DrawModel 里的 AppendNear + 实例化网格）。
    /// 近处仍会有环带 + 子弹可见。
    /// </summary>
    [HarmonyPatch(typeof(DysonSwarm), "DrawModel")]
    internal static class SwarmDrawModelPatch
    {
        private static bool Prefix()
        {
            if (DspOptiModPlugin.OptimizeSwarm.Value && DspOptiModPlugin.DisableNearPass.Value)
            {
                return false;
            }
            return true;
        }
    }
}
