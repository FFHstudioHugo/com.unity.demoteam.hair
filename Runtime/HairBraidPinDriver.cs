using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.DemoTeam.Hair
{
    [DefaultExecutionOrder(10000)]
    [ExecuteAlways]
    public partial class HairRadialPinDriver : MonoBehaviour
    {
        public const int PIN_MAX = 20;

        [Header("Target")]
        public HairInstance hairInstance;
        [Min(0)] public int groupIndex = 0;

        [Header("Enable")]
        public bool enable = true;

        [Header("Pull (Attractor)")]
        public bool enablePull = true;
        [Range(0f, 1f)] public float pullMultiplier = 1f;

        [Header("Chain (Attachment)")]
        public bool enableChain = true;
        [Range(0f, 1f)] public float chainMultiplier = 1f;

        [Header("Chain Bind (one-shot)")]
        [Range(0f, 1f)] public float bindThreshold = 0.01f;
        [Range(0f, 1f)] public float releaseThreshold = 0.0f; // kept for compatibility

        [Serializable]
        public struct Pin
        {
            public Transform transform;

            [Min(0f)] public float radius;

            [Header("Pull")]
            [Range(0f, 1f)] public float pullStrength;
            [Min(0.01f)] public float pullFalloffPower;

            [Header("Chain")]
            [Range(0f, 1f)] public float chainStrength;
            [Min(0.01f)] public float chainFalloffPower;
        }

        [Header("Pins (0..20)")]
        public List<Pin> pins = new List<Pin>(5);

        [Header("Gizmos")]
        public bool drawGizmos = true;
        public bool drawGizmosWhenNotSelected = false;
        public bool drawChainLines = true;
        public bool drawIndexLabels = false;

        // Upload buffers
        private readonly Vector4[] _pinSpheres = new Vector4[PIN_MAX]; // xyz posWS, w radius
        private readonly Vector4[] _pinParams = new Vector4[PIN_MAX]; // x pullStrength, y pullPow, z chainStrength, w chainPow
        private readonly Vector4[] _pinGlobals = new Vector4[1];       // x pullMul, y chainMul, z bindTh, w releaseTh
        private readonly Vector4[] _pinRotations = new Vector4[PIN_MAX];

        private bool _bindRequested;
        private bool _bindClearBefore = true;

        private static Vector4[] s_zeroCache;

        void Reset() => hairInstance = GetComponent<HairInstance>();

        public void RequestBindHairsToChainsGPU(bool clearBeforeBind = true)
        {
            _bindRequested = true;
            _bindClearBefore = clearBeforeBind;
        }

        public void RebindChainNow() => RequestBindHairsToChainsGPU(true);

        public void ClearChainBindings()
        {
            if (hairInstance == null) hairInstance = GetComponent<HairInstance>();
            if (hairInstance == null) return;

            var sdArr = hairInstance.solverData;
            if (sdArr == null || sdArr.Length == 0) return;
            if (groupIndex < 0 || groupIndex >= sdArr.Length) return;

            var sd = sdArr[groupIndex];
            var b0 = sd.buffers._ParticleChainBind0;
            var b1 = sd.buffers._ParticleChainBind1;

#if UNITY_2021_1_OR_NEWER
            if (b0 == null || !b0.IsValid()) return;
            if (b1 == null || !b1.IsValid()) return;
            int count = b0.count;
#else
            if (b0 == null || b1 == null) return;
            int count = b0.count;
#endif
            EnsureZeroCache(count);
            b0.SetData(s_zeroCache, 0, 0, count);
            b1.SetData(s_zeroCache, 0, 0, count);
        }

        void LateUpdate()
        {
            if (hairInstance == null) hairInstance = GetComponent<HairInstance>();
            if (hairInstance == null) return;

            var sdArr = hairInstance.solverData;
            if (sdArr == null || sdArr.Length == 0) return;
            if (groupIndex < 0 || groupIndex >= sdArr.Length) return;

            var sd = sdArr[groupIndex];

            var bufGlobals = sd.buffers._PinGlobals;
            var bufSpheres = sd.buffers._PinSpheres;
            var bufParams = sd.buffers._PinParams;

#if UNITY_2021_1_OR_NEWER
            if (bufGlobals == null || !bufGlobals.IsValid()) return;
            if (bufSpheres == null || !bufSpheres.IsValid()) return;
            if (bufParams == null || !bufParams.IsValid()) return;
#else
            if (bufGlobals == null || bufSpheres == null || bufParams == null) return;
#endif
            float pullMul = (enable && enablePull) ? Mathf.Clamp01(pullMultiplier) : 0f;
            float chainMul = (enable && enableChain) ? Mathf.Clamp01(chainMultiplier) : 0f;

            float bTh = Mathf.Clamp01(bindThreshold);
            float rTh = Mathf.Clamp01(releaseThreshold);
            if (rTh > bTh) rTh = bTh;

            _pinGlobals[0] = new Vector4(pullMul, chainMul, bTh, rTh);

            for (int i = 0; i < PIN_MAX; i++)
            {
                _pinSpheres[i] = Vector4.zero;
                _pinParams[i] = Vector4.zero;
                _pinRotations[i] = new Vector4(0, 0, 0, 1); // identity
            }


            int n = Mathf.Min(PIN_MAX, pins.Count);
            for (int i = 0; i < n; i++)
            {
                var p = pins[i];
                if (p.transform == null || p.radius <= 0f) continue;

                var pos = p.transform.position;
                _pinSpheres[i] = new Vector4(pos.x, pos.y, pos.z, p.radius);

                _pinParams[i] = new Vector4(
                    Mathf.Clamp01(p.pullStrength),
                    Mathf.Max(0.01f, p.pullFalloffPower),
                    Mathf.Clamp01(p.chainStrength),
                    Mathf.Max(0.01f, p.chainFalloffPower)
                );
                Quaternion q = p.transform.rotation;
                _pinRotations[i] = new Vector4(q.x, q.y, q.z, q.w);
            }





            bufGlobals.SetData(_pinGlobals);
            bufSpheres.SetData(_pinSpheres);
            bufParams.SetData(_pinParams);
            var bufRot = sd.buffers._PinRotations;
            bufRot.SetData(_pinRotations);

            // bind request AFTER upload
            if (_bindRequested)
            {
                _bindRequested = false;
                
                if (_bindClearBefore)
                    ClearChainBindings();

                var cmd = new CommandBuffer { name = "Hair Chain Bind" };
                HairSim.PushChainBind(cmd, in sd);
                Debug.Log("[HairChain] Dispatching KChainBind now");
                Debug.Log($"[HairChain] About to call PushChainBind: group={groupIndex} strands={sd.constants._StrandCount} particlesPerStrand={sd.constants._StrandParticleCount}");
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();

                var b0 = new Vector4[4096];
                sd.buffers._ParticleChainBind0.GetData(b0, 0, 0, b0.Length);

                int firstBound = -1;
                float firstW = 0f;

                for (int idx = 0; idx < b0.Length; idx++)
                {
                    if (b0[idx].w > 0.5f) // segIdx+1
                    {
                        firstBound = idx;
                        firstW = b0[idx].w;
                        break;
                    }
                }

                Debug.Log($"[HairChain] After dispatch: firstBoundIndex={firstBound} w={firstW}");

            }
        }

        private static void EnsureZeroCache(int count)
        {
            if (count <= 0) count = 1;
            if (s_zeroCache == null || s_zeroCache.Length != count)
                s_zeroCache = new Vector4[count];
        }

        void OnDrawGizmos()
        {
            if (!drawGizmos || (!drawGizmosWhenNotSelected && !IsSelected()))
                return;
            DrawGizmosInternal();
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos)
                return;
            DrawGizmosInternal();
        }

        void DrawGizmosInternal()
        {
            int n = Mathf.Min(PIN_MAX, pins.Count);

            for (int i = 0; i < n; i++)
            {
                var p = pins[i];
                if (p.transform == null || p.radius <= 0f) continue;
                Gizmos.DrawWireSphere(p.transform.position, p.radius);
            }

            if (drawChainLines)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    var a = pins[i].transform;
                    var b = pins[i + 1].transform;
                    if (a == null || b == null) continue;
                    Gizmos.DrawLine(a.position, b.position);
                }
            }
        }

        bool IsSelected()
        {
#if UNITY_EDITOR
            return UnityEditor.Selection.activeGameObject == gameObject;
#else
            return false;
#endif
        }
#if UNITY_EDITOR
        public void DumpChainState(int sampleCount = 4096)
        {
            if (hairInstance == null) hairInstance = GetComponent<HairInstance>();
            if (hairInstance == null) { Debug.LogWarning("No HairInstance"); return; }

            var sdArr = hairInstance.solverData;
            if (sdArr == null || sdArr.Length == 0) { Debug.LogWarning("No solverData"); return; }
            if (groupIndex < 0 || groupIndex >= sdArr.Length) { Debug.LogWarning("Bad groupIndex"); return; }

            var sd = sdArr[groupIndex];

            // --- Read pins (sanity) ---
            var spheres = new Vector4[PIN_MAX];
            sd.buffers._PinSpheres.GetData(spheres);

            var g = new Vector4[1];
            sd.buffers._PinGlobals.GetData(g);
            Debug.Log($"[HairChainDump] globals pullMul={g[0].x} chainMul={g[0].y} bindTh={g[0].z}");

            int nonZeroPins = 0;
            for (int i = 0; i < PIN_MAX; i++)
                if (spheres[i].w > 0f) nonZeroPins++;

            // Read pin params too (needed for CPU weight debug)
            var parms = new Vector4[PIN_MAX];
            sd.buffers._PinParams.GetData(parms);

            // CPU capsule sanity test for segment 0-1 (only if both pins exist)
            if (spheres[0].w > 0f && spheres[1].w > 0f)
            {
                int countSphere = sd.buffers._ParticlePosition.count;
                int n_sphere = Mathf.Clamp(sampleCount, 1, countSphere);
                var pos = new Vector3[n_sphere];
                sd.buffers._ParticlePosition.GetData(pos, 0, 0, n_sphere);
                Debug.Log($"[HairChainDump] ParticlePosition strideBytes={sd.buffers._ParticlePosition.stride}");


                Vector3 a = new Vector3(spheres[0].x, spheres[0].y, spheres[0].z);
                Vector3 b = new Vector3(spheres[1].x, spheres[1].y, spheres[1].z);
                float ra = spheres[0].w;
                float rb = spheres[1].w;

                float strength = 0.5f * (parms[0].z + parms[1].z);
                float fallPow = 0.5f * (parms[0].w + parms[1].w);

                Vector3 ab = b - a;
                float ab2 = Vector3.Dot(ab, ab);
                float invAb2 = (ab2 > 1e-6f) ? (1f / ab2) : 0f;

                int particlesPerStrand = (int)sd.constants._StrandParticleCount;

                int insideCount = 0;
                float maxW = 0f;
                float minDist = float.MaxValue;

                int strandOffset = (int)sd.constants._StrandParticleOffset;

                for (int i = 0; i < n_sphere; i++)
                {
                    // skip roots (k==0)
                    if (strandOffset > 0 && (i % strandOffset) == 0) // root index
                        continue;

                    Debug.Log($"[HairChainDump] constants offset={sd.constants._StrandParticleOffset} stride={sd.constants._StrandParticleStride} particleCount={sd.constants._StrandCount * sd.constants._StrandParticleCount}");

                    Vector3 p = new Vector3(pos[i].x, pos[i].y, pos[i].z);
                    float t = (invAb2 > 0f) ? Mathf.Clamp01(Vector3.Dot(p - a, ab) * invAb2) : 0f;
                    Vector3 c = a + t * ab;

                    float r = Mathf.Lerp(ra, rb, t);
                    if (r <= 0f) continue;

                    float dist = (p - c).magnitude;
                    if (dist < minDist) minDist = dist;

                    if (dist >= r) continue;

                    float u = Mathf.Clamp01(1f - dist / r);
                    float w = Mathf.Clamp01(strength) * Mathf.Pow(u, Mathf.Max(0.01f, fallPow));
                    if (w > maxW) maxW = w;

                    insideCount++;
                }

                Debug.Log($"[HairChainDump] CPU capsule(0-1) sampleInside={insideCount}/{n_sphere} minDist={minDist} maxW={maxW}");
            }
            else
            {
                Debug.Log("[HairChainDump] CPU capsule(0-1) skipped: pin0 or pin1 radius is 0.");
            }

            // --- Read bind buffers (sample) ---
            int count = sd.buffers._ParticleChainBind0.count;
            int n = Mathf.Clamp(sampleCount, 1, count);

            var b0 = new Vector4[n];
            var b1 = new Vector4[n];

            sd.buffers._ParticleChainBind0.GetData(b0, 0, 0, n);
            sd.buffers._ParticleChainBind1.GetData(b1, 0, 0, n);

            int activeCount = 0;
            int firstActive = -1;
            int firstSeg = -1;
            float firstT = 0f;

            // FULL SCAN active total (seg stored as float: 0=unbound, segIdx+1=bound)
            var allB0 = new Vector4[count];
            sd.buffers._ParticleChainBind0.GetData(allB0);

            int activeTotal = 0;
            for (int i = 0; i < count; i++)
            {
                if (allB0[i].w > 0.5f)
                    activeTotal++;
            }

            Debug.Log($"[HairChainDump] ACTIVE TOTAL = {activeTotal}/{count}");

            // SAMPLE scan (same rule)
            for (int i = 0; i < n; i++)
            {
                float segF = b0[i].w;
                if (segF > 0.5f)
                {
                    activeCount++;
                    if (firstActive < 0)
                    {
                        firstActive = i;
                        firstSeg = (int)segF - 1;
                        firstT = b1[i].x;
                    }
                }
            }

            Debug.Log($"[HairChainDump] pinsNonZero={nonZeroPins}/{PIN_MAX}  sample={n}/{count}  active={activeCount}  firstActive={firstActive} seg={firstSeg} t={firstT}");

            for (int i = 0; i < PIN_MAX; i++)
                if (spheres[i].w > 0f)
                    Debug.Log($"[Pins] idx={i} r={spheres[i].w} chainS={parms[i].z} chainPow={parms[i].w}");

        }
#endif
    }
}

