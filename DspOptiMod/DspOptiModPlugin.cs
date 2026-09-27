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

    /// <summary>共用工具：定位节点所属戴森球 / 判断节点阶段是否结束。</summary>
    internal static class DysonSphereUtil
    {
        private static readonly System.Collections.Generic.HashSet<DysonSphere> FramesPhase =
            new System.Collections.Generic.HashSet<DysonSphere>();

        public static DysonSphere FindSphere(DysonNode node)
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

        public static bool AllNodesBuilt(DysonSphere sphere)
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
                        FramesPhase.Remove(sphere);
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>节点阶段刚结束时（只发生一次），按原版公式重算全戴森球所有节点的 _spReq，
        /// 让发射井开始为框架下单。</summary>
        public static void EnsureFramesPhaseRecalc(DysonSphere sphere)
        {
            if (!AllNodesBuilt(sphere) || FramesPhase.Contains(sphere))
            {
                return;
            }
            FramesPhase.Add(sphere);
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
                    if (n != null && n.id == j)
                    {
                        n.RecalcSpReq();
                    }
                }
            }
            // 关键：节点阶段 autoNodes 已被逐个清空（每个节点下满 30 单即移出），
            // 原版只在 OrderConstructSp 时补位；阶段切换后必须手动重选目标，
            // 否则发射井永久待机、框架不再被打。
            for (int k = sphere.GetAutoNodeCount(); k < 8; k++)
            {
                sphere.PickAutoNode();
            }
        }
    }

    /// <summary>
    /// 节点阶段把 _spReq 压缩为“仅节点本体缺口”，发射井就只会为每个节点精确下 30 发订单，
    /// 不会多打一发；所有节点建成后恢复原版公式（节点+框架）。
    /// </summary>
    [HarmonyPatch(typeof(DysonNode), "RecalcSpReq")]
    internal static class NodeRecalcSpReqPatch
    {
        private static readonly System.Reflection.FieldInfo SpReqField =
            AccessTools.Field(typeof(DysonNode), "_spReq");

        private static bool Prefix(DysonNode __instance)
        {
            if (!DspOptiModPlugin.FramesFirst.Value)
            {
                return true;
            }
            DysonSphere sphere = DysonSphereUtil.FindSphere(__instance);
            if (sphere != null && !DysonSphereUtil.AllNodesBuilt(sphere))
            {
                lock (__instance)
                {
                    SpReqField.SetValue(__instance, __instance.spMax - __instance.sp);
                }
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 功能2：改变 ConstructSp 的分配顺序 —— 原版是"节点本体填满后，本节点的框架就能开始建"。
    /// 这里改为：整颗戴森球【所有节点本体】全部建成后，才开始给框架分配结构点。
    /// </summary>
    [HarmonyPatch(typeof(DysonNode), "ConstructSp")]
    internal static class NodeConstructSpPatch
    {

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
                    DysonSphere sphere = DysonSphereUtil.FindSphere(__instance);
                    framesAllowed = sphere != null && DysonSphereUtil.AllNodesBuilt(sphere);
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
                if (result == null)
                {
                    // 本节点已满且框架尚未开放：把结构点转移给同球其他未建满的节点，
                    // 避免火箭白白浪费（原版会转给框架，节点优先模式下框架被禁止）
                    DysonSphere sphere = DysonSphereUtil.FindSphere(__instance);
                    if (sphere != null)
                    {
                        DysonSphereLayer[] layers = sphere.layersIdBased;
                        for (int li = 1; li < layers.Length && result == null; li++)
                        {
                            DysonSphereLayer layer = layers[li];
                            if (layer == null || layer.id != li)
                            {
                                continue;
                            }
                            DysonNode[] pool = layer.nodePool;
                            for (int nj = 1; nj < layer.nodeCursor; nj++)
                            {
                                DysonNode n = pool[nj];
                                if (n != null && n.id == nj && n.sp < n.spMax)
                                {
                                    lock (n)
                                    {
                                        if (n.sp < n.spMax)
                                        {
                                            n.sp++;
                                            result = n;
                                        }
                                    }
                                    if (result != null)
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                        if (result is DysonNode donor)
                        {
                            donor.RecalcSpReq();
                        }
                    }
                }
                __instance.spOrdered--;
                if (__instance.spOrdered < 0)
                {
                    __instance.spOrdered = 0;
                }
                __result = result;
            }
            __instance.RecalcSpReq();
            // 节点阶段刚结束（最后一发节点火箭落地）时，恢复原版 _spReq 让发射井开始为框架下单
            DysonSphere sphereAfter = DysonSphereUtil.FindSphere(__instance);
            if (sphereAfter != null)
            {
                DysonSphereUtil.EnsureFramesPhaseRecalc(sphereAfter);
            }
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
            StarData starData = __instance.starData;
            GameData gameData = __instance.gameData;
            if (starData == null || gameData == null)
            {
                return false;
            }

            // ---- 环带只在戴森球编辑界面(云带视图)显示；星图/宇宙视角不画 ----
            // 戴森编辑器里世界坐标 = 恒星本地坐标 * 0.00025（与原版帆/子弹一致）
            bool drawRings = DysonSphere.renderPlace == ERenderPlace.Dysonmap;
            float mapScale = 0.00025f;
            if (drawRings)
            {
                // ---- 每条轨道一条环带 ----
                int segments = DspOptiModPlugin.RingSegments.Value;
                float widthFactor = DspOptiModPlugin.RingWidthFactor.Value;
                SailOrbit[] orbits = __instance.orbits;
                Vector4[] orbitColors = __instance.orbitColorsHSVA;
                Vector3 ringCenter = Vector3.zero; // Dysonmap 视角以编辑器原点为中心
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
                        float radius = orbits[i].radius * mapScale;
                        Quaternion worldRot = __instance.orbits[i].rotation;
                        Color c = Color.HSVToRGB(orbitColors[i].x, orbitColors[i].y, orbitColors[i].z);
                        float fill = Mathf.Clamp01(orbits[i].count / 6000f);
                        c.a = Mathf.Max(DspOptiModPlugin.RingMinAlpha.Value,
                            DspOptiModPlugin.RingAlphaScale.Value * (0.35f + 0.65f * fill));
                        float width = radius * widthFactor;
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
                            GL.Vertex(ringCenter + p0 - side);
                            GL.Vertex(ringCenter + p0 + side);
                            GL.Vertex(ringCenter + p1 + side);
                            GL.Vertex(ringCenter + p1 - side);
                        }
                    }
                }
                finally
                {
                    GL.End();
                }
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
