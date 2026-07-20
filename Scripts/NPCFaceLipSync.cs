using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using XNPCVoiceControl.Core;

namespace XNPCVoiceControl.TTS
{
    /// <summary>
    /// Lip-sync driver — blendshape (tier 0), avatar-controller param gate, or procedural jaw (tier 3).
    /// Mode: auto | off | blendshape | animator | procedural.
    /// Blink runs independently whenever a blink shape exists + enabled.
    /// </summary>
    public class NPCFaceLipSync : MonoBehaviour
    {
        // Fallback messages — first occurrence per session at Warning, subsequent at Debug
        private static bool _warnedNoBlendshape = false;
        private static bool _warnedNoAnimParam = false;

        // --- Mode ---
        private string _mode;           // "auto", "off", "blendshape", "animator", "procedural"
        private string _activeMode = "off";

        // --- Shared amplitude envelope (computed in Update, consumed by blendshape/procedural paths) ---
        private float _envelope = 0f;

        // Skip redundant SetBlendShapeWeight calls when the envelope hasn't moved.
        private float _lastWrittenMouthWeight = -1f; // −1 forces first write

        // --- Blendshape mouth (tier 0) ---
        private SkinnedMeshRenderer _faceSmr;
        private int _mouthShapeIndex = -1;

        // --- Animator mode (avatar controller path) ---
        private EntityAlive _npcEntity;
        private string _animParamName;
        private int _animParamHash;       // Animator.StringToHash of param name
        private bool _animParamExists = false;
        private float _animReleaseTimer = 0f; // hold param=1 for this many seconds after audio stops
        private const float AnimReleaseHold = 0.25f;

        // --- Procedural jaw + blink (tier 3) ---
        private static Dictionary<int, (Mesh mesh, int jawShapeIndex, int blinkShapeIndex, int winkLeftShapeIndex, int winkRightShapeIndex)> _procCache = new();

        /// <summary>
        /// Destroy all cached baked meshes and clear the cache. Called at game/world start so a
        /// previous world's meshes never accumulate across sessions. Safe to call any time no
        /// NPCs are alive; live instances re-bake on demand via the normal cache-miss path.
        /// </summary>
        public static void ClearProcCache()
        {
            foreach (var entry in _procCache.Values)
            {
                if (entry.mesh != null)
                    UnityEngine.Object.Destroy(entry.mesh);
            }
            _procCache.Clear();
            Log.Debug(() => "[LIPSYNC] Procedural mesh cache cleared");
        }
        private SkinnedMeshRenderer _procSmr;
        private int _procShapeIndex = -1;           // jaw
        private int _procBlinkShapeIndex = -1;      // both eyes
        private int _procWinkLeftShapeIndex = -1;   // left eye only
        private int _procWinkRightShapeIndex = -1;  // right eye only
        private bool _procTestHold;
        private float _procOpenAngle;
        private float _procLowerMaxFrac;
        private float _procForwardMinFrac;
        private float _procHingeYFrac;
        private float _procHingeZFrac;
        // Procedural blink tuning
        private float _procBlinkEyeYFrac;
        private float _procBlinkBandHeightFrac;
        private float _procBlinkBandWidthFrac;
        private float _procBlinkCloseAmount;
        private float _procBlinkForwardMinFrac;
        private string _procBlinkWinkMode;   // "off", "left", "right", "random"
        private float _procBlinkWinkChance;
        private Mesh _originalMesh;       // original sharedMesh before instancing (for reload re-bake)
        private XNPCVoiceControl.FaceOverride _faceOverride;  // per-character override (stored for ReloadConfig)

        // --- Static registry (for vc reloadface) ---
        private static List<NPCFaceLipSync> _registry = new();

        // --- Blink (independent of mode) ---
        private SkinnedMeshRenderer _blinkSmr;
        private int _blinkShapeIndex = -1;
        private bool _blinking = false;
        private float _blinkNextAt = 0f;
        private float _blinkIntervalMin;
        private float _blinkIntervalMax;
        private float _blinkDurationMs;
        private bool _blinkEnabled;

        // --- Audio ---
        private AudioSource _audioSource;
        private float[] _rmsBuffer = new float[256];
        private float _gain;
        private float _attackSpeed;
        private float _releaseSpeed;
        private float _noiseGateThreshold;
        private float _maxWeight;

        // --- Calibration (logged once per utterance) ---
        private float _peakRms = 0f;
        private float _peakEnvelope = 0f;

        private bool _wasPlaying = false;
        private string _npcLabel;

        // ========================================================================
        // Init
        // ========================================================================

        public void Initialize(
            EntityAlive npcEntity,
            AudioSource audioSource,
            float gain, float attackSpeed, float releaseSpeed,
            float noiseGateThreshold, float maxWeight,
            string npcName,
            bool blinkEnabled, float blinkIntervalMin, float blinkIntervalMax, float blinkDurationMs,
            string mode, string animParamName,
            // Procedural jaw (tier 3) tuning
            float procOpenAngle, float procLowerMaxFrac, float procForwardMinFrac,
            float procHingeYFrac, float procHingeZFrac, bool procTestHold,
            // Procedural blink/wink (tier 3 fallback)
            float procBlinkEyeYFrac, float procBlinkBandHeightFrac, float procBlinkBandWidthFrac, float procBlinkCloseAmount,
            float procBlinkForwardMinFrac, string procBlinkWinkMode, float procBlinkWinkChance,
            // Per-character face override (stored for ReloadConfig re-apply)
            XNPCVoiceControl.FaceOverride faceOverride)
        {
            if (GameManager.IsDedicatedServer)
            {
                Log.Debug("[LIPSYNC] Dedicated server, disabling");
                enabled = false;
                return;
            }

            _npcEntity = npcEntity;
            _audioSource = audioSource;
            _gain = gain;
            _attackSpeed = attackSpeed;
            _releaseSpeed = releaseSpeed;
            _noiseGateThreshold = noiseGateThreshold;
            _maxWeight = maxWeight;
            _npcLabel = npcName ?? gameObject.name;
            _blinkEnabled = blinkEnabled;
            _blinkIntervalMin = blinkIntervalMin;
            _blinkIntervalMax = blinkIntervalMax;
            _blinkDurationMs = blinkDurationMs;
            _mode = mode;
            _animParamName = animParamName;

            // Procedural params (bbox-relative fractions)
            _procOpenAngle = procOpenAngle;
            _procLowerMaxFrac = procLowerMaxFrac;
            _procForwardMinFrac = procForwardMinFrac;
            _procHingeYFrac = procHingeYFrac;
            _procHingeZFrac = procHingeZFrac;
            _procTestHold = procTestHold;

            // Procedural blink tuning
            _procBlinkEyeYFrac = procBlinkEyeYFrac;
            _procBlinkBandHeightFrac = procBlinkBandHeightFrac;
            _procBlinkBandWidthFrac = procBlinkBandWidthFrac;
            _procBlinkCloseAmount = procBlinkCloseAmount;
            _procBlinkForwardMinFrac = procBlinkForwardMinFrac;
            _procBlinkWinkMode = procBlinkWinkMode;
            _procBlinkWinkChance = procBlinkWinkChance;

            // Store face override for ReloadConfig re-apply
            _faceOverride = faceOverride;

            // Resolve face mesh (blendshapes) — always scan regardless of mode
            ResolveFaceMesh();

            // Resolve animator parameter existence + avatar controller
            ResolveAnimatorParam();

            // Resolve procedural jaw (readable mesh with head-weighted verts)
            ResolveProceduralJaw();

            // Determine active mouth driver
            ResolveMouthMode();

            // Register for hot-reload
            if (!_registry.Contains(this))
                _registry.Add(this);

            // Schedule first blink
            if (_blinkEnabled)
                _blinkNextAt = Time.time + UnityEngine.Random.Range(_blinkIntervalMin, _blinkIntervalMax);

            _lastWrittenMouthWeight = -1f; // force first write after init
            Log.Debug(() => $"[LIPSYNC] Ready on {_npcLabel} — mode={_activeMode}, mouthShape={_mouthShapeIndex}, animParam={(_animParamExists ? _animParamName : "none")}, procJaw={_procShapeIndex}, procBlink={_procBlinkShapeIndex}, procWinkL={_procWinkLeftShapeIndex}, procWinkR={_procWinkRightShapeIndex}, blinkShape={_blinkShapeIndex}");
        }

        private void ResolveMouthMode()
        {
            bool hasBlendshape = _faceSmr != null && _mouthShapeIndex >= 0;
            bool hasAnimParam = _animParamExists;
            bool hasProcedural = _procSmr != null && _procShapeIndex >= 0;

            switch (_mode)
            {
                case "off":
                    _activeMode = "off";
                    break;

                case "blendshape":
                    if (hasBlendshape)
                        _activeMode = "blendshape";
                    else
                    {
                        if (!_warnedNoBlendshape) { _warnedNoBlendshape = true; Log.Warning($"[LIPSYNC] {_npcLabel}: mode=blendshape but no mouth blendshape found — lip-sync OFF"); }
                        else { Log.Debug(() => $"[LIPSYNC] {_npcLabel}: mode=blendshape but no mouth blendshape found — lip-sync OFF"); }
                        _activeMode = "off";
                    }
                    break;

                case "animator":
                    if (hasAnimParam)
                        _activeMode = "animator";
                    else
                    {
                        if (!_warnedNoAnimParam) { _warnedNoAnimParam = true; Log.Warning($"[LIPSYNC] {_npcLabel}: mode=animator but no Float parameter '{_animParamName}' found — lip-sync OFF"); }
                        else { Log.Debug(() => $"[LIPSYNC] {_npcLabel}: mode=animator but no Float parameter '{_animParamName}' found — lip-sync OFF"); }
                        _activeMode = "off";
                    }
                    break;

                case "procedural":
                    if (hasProcedural)
                        _activeMode = "procedural";
                    else
                    {
                        Log.Warning($"[LIPSYNC] {_npcLabel}: mode=procedural but no readable face mesh with head-weighted verts — lip-sync OFF");
                        _activeMode = "off";
                    }
                    break;

                case "auto":
                default:
                    // Tier priority: blendshape > animator > procedural > off
                    if (hasBlendshape)
                        _activeMode = "blendshape";
                    else if (hasAnimParam)
                        _activeMode = "animator";
                    else if (hasProcedural)
                        _activeMode = "procedural";
                    else
                    {
                        _activeMode = "off";
                        Log.Debug(() => $"[LIPSYNC] {_npcLabel}: auto — no blendshapes, animator param, or readable mesh found, lip-sync OFF");
                    }
                    break;
            }
        }

        // ========================================================================
        // Mesh / Animator resolution
        // ========================================================================

        private void ResolveFaceMesh()
        {
            var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (var smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;

                int shapeCount = 0;
                try { shapeCount = smr.sharedMesh.blendShapeCount; } catch { continue; }
                if (shapeCount == 0) continue;

                int mouthIdx = ResolveMouthShape(smr);
                if (mouthIdx >= 0)
                {
                    _faceSmr = smr;
                    _mouthShapeIndex = mouthIdx;
                    int blinkIdx = ResolveBlinkShape(smr);
                    if (blinkIdx >= 0)
                    {
                        _blinkSmr = smr;
                        _blinkShapeIndex = blinkIdx;
                    }
                    return;
                }

                // No mouth here — check for blink on this SMR anyway
                if (_blinkShapeIndex < 0)
                {
                    int blinkIdx = ResolveBlinkShape(smr);
                    if (blinkIdx >= 0)
                    {
                        _blinkSmr = smr;
                        _blinkShapeIndex = blinkIdx;
                    }
                }
            }

            // If we have a mouth SMR but no blink, scan all SMRs again for blink
            if (_mouthShapeIndex >= 0 && _blinkShapeIndex < 0)
            {
                foreach (var smr in smrs)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    int shapeCount = 0;
                    try { shapeCount = smr.sharedMesh.blendShapeCount; } catch { continue; }
                    if (shapeCount == 0) continue;

                    int blinkIdx = ResolveBlinkShape(smr);
                    if (blinkIdx >= 0)
                    {
                        _blinkSmr = smr;
                        _blinkShapeIndex = blinkIdx;
                        return;
                    }
                }
            }
        }

        private int ResolveMouthShape(SkinnedMeshRenderer smr)
        {
            int shapeCount = 0;
            try { shapeCount = smr.sharedMesh.blendShapeCount; } catch { return -1; }

            // Pass 1: strong matches (jawopen, mouthopen, etc.)
            for (int i = 0; i < shapeCount; i++)
            {
                string name = "";
                try { name = smr.sharedMesh.GetBlendShapeName(i); } catch { continue; }
                string lower = name.ToLowerInvariant();

                if (lower.Contains("jawopen") || lower.Contains("mouthopen") ||
                    lower.Contains("jaw_open") || lower.Contains("mouth_open") ||
                    lower.Contains("openmouth"))
                {
                    return i;
                }
            }

            // Pass 2: viseme matches (aa, ai, ah, oh)
            for (int i = 0; i < shapeCount; i++)
            {
                string name = "";
                try { name = smr.sharedMesh.GetBlendShapeName(i); } catch { continue; }
                string lower = name.ToLowerInvariant();

                if (lower == "aa" || lower == "ai" || lower == "ah" || lower == "oh")
                {
                    return i;
                }
            }

            // Pass 3: last resort single letters (o, e, u)
            for (int i = 0; i < shapeCount; i++)
            {
                string name = "";
                try { name = smr.sharedMesh.GetBlendShapeName(i); } catch { continue; }
                string lower = name.ToLowerInvariant();

                if (lower == "o" || lower == "e" || lower == "u")
                {
                    return i;
                }
            }

            // Pass 4: contains "open"
            for (int i = 0; i < shapeCount; i++)
            {
                string name = "";
                try { name = smr.sharedMesh.GetBlendShapeName(i); } catch { continue; }
                if (name.ToLowerInvariant().Contains("open"))
                {
                    return i;
                }
            }

            return -1;
        }

        private int ResolveBlinkShape(SkinnedMeshRenderer smr)
        {
            int shapeCount = 0;
            try { shapeCount = smr.sharedMesh.blendShapeCount; } catch { return -1; }

            for (int i = 0; i < shapeCount; i++)
            {
                string name = "";
                try { name = smr.sharedMesh.GetBlendShapeName(i); } catch { continue; }
                if (name.ToLowerInvariant().Contains("blink"))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Verify a Float parameter matching animParamName exists on the Animator,
        /// and capture the avatar controller for driving it.
        /// Mirrors MinEventActionAnimatorSetFloat: entity.emodel.avatarController.UpdateFloat(name, value, true).
        /// </summary>
        private void ResolveAnimatorParam()
        {
            var animator = GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                Log.Debug(() => $"[LIPSYNC] {_npcLabel}: no Animator found on hierarchy");
                return;
            }

            // Check for a Float parameter with the given name
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                Log.Debug(() => $"[LIPSYNC] {_npcLabel}: Animator '{animator.gameObject.name}' has {parameters.Length} params");
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].name == _animParamName && parameters[i].type == AnimatorControllerParameterType.Float)
                    {
                        _animParamExists = true;
                        _animParamHash = Animator.StringToHash(_animParamName);
                        Log.Debug(() => $"[LIPSYNC] {_npcLabel}: matched Float param '{_animParamName}' (hash={_animParamHash})");

                        // Capture avatar controller for driving the param
                        if (_npcEntity != null && _npcEntity.emodel != null && _npcEntity.emodel.avatarController != null)
                        {
                            Log.Debug(() => $"[LIPSYNC] {_npcLabel}: avatarController captured — will use UpdateFloat");
                        }
                        else
                        {
                            Log.Warning($"[LIPSYNC] {_npcLabel}: avatarController is null (emodel={(_npcEntity?.emodel != null ? "ok" : "null")}) — animator mode will fail at runtime");
                        }
                        return;
                    }
                }
                Log.Warning($"[LIPSYNC] {_npcLabel}: param '{_animParamName}' not found as Float in Animator '{animator.gameObject.name}' (has {parameters.Length} params)");
            }
            catch
            {
                // Some animators throw on .parameters access — treat as missing
                Log.Warning($"[LIPSYNC] {_npcLabel}: Animator threw on .parameters access");
            }
        }

        // ========================================================================
        // Procedural jaw (tier 3) — runtime blendshape from head-weighted chin verts
        // ========================================================================

        /// <summary>
        /// Find a readable SMR with head-weighted verts and generate (or cache-reuse) a ProcJawOpen blendshape.
        /// </summary>
        private void ResolveProceduralJaw()
        {
            var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // Pick the SMR with the MOST head-weighted verts (main face/body mesh).
            // On multi-SMR characters this avoids tiny sub-meshes (teeth, tongue, etc.).
            SkinnedMeshRenderer bestSmr = null;
            int bestHeadIdx = -1;
            int bestHeadCount = 0;

            foreach (var smr in smrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;

                // Must be readable to extract vertices
                bool readable;
                try { readable = smr.sharedMesh.isReadable; } catch { continue; }
                if (!readable) continue;

                // Find head bone index within this SMR's bones array
                int headIdx = FindHeadBoneIndexInSmr(smr);
                if (headIdx < 0) continue;

                // Count head-weighted verts
                int headVertCount = 0;
                try
                {
                    var bw = smr.sharedMesh.boneWeights;
                    for (int i = 0; i < bw.Length; i++)
                    {
                        if (IsWeightedToBone(bw[i], headIdx, 0.5f))
                            headVertCount++;
                    }
                }
                catch { continue; }

                if (headVertCount > bestHeadCount)
                {
                    bestSmr = smr;
                    bestHeadIdx = headIdx;
                    bestHeadCount = headVertCount;
                }
            }

            if (bestSmr != null && bestHeadCount > 0)
            {
                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: face SMR = {bestSmr.gameObject.name} ({bestHeadCount} head verts)");
                GenerateProceduralShapes(bestSmr, bestHeadIdx);
            }
            else
            {
                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: no readable SMR with head-weighted verts found — procedural OFF");
            }
        }

        /// <summary>
        /// Bake ALL procedural blendshapes (ProcJawOpen + ProcBlink + ProcWinkLeft + ProcWinkRight)
        /// into a SINGLE instanced mesh, or retrieve from static cache.
        /// CRITICAL: jaw and blink MUST share one Instantiate(original) — two separate bakes
        /// would silently destroy each other's blendshapes via smr.sharedMesh reassignment.
        /// </summary>
        private void GenerateProceduralShapes(SkinnedMeshRenderer smr, int headBoneIdx)
        {
            Mesh original = smr.sharedMesh;
            _originalMesh = original;  // store for reload re-bake
            int meshId = original.GetInstanceID();

            // Check static cache first — reuse across spawns of same character type
            if (_procCache.TryGetValue(meshId, out var cached))
            {
                smr.sharedMesh = cached.mesh;
                _procSmr = smr;
                _procShapeIndex = cached.jawShapeIndex;
                _procBlinkShapeIndex = cached.blinkShapeIndex;
                _procWinkLeftShapeIndex = cached.winkLeftShapeIndex;
                _procWinkRightShapeIndex = cached.winkRightShapeIndex;
                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: reused cached shapes — jaw={cached.jawShapeIndex}, blink={cached.blinkShapeIndex}, winkL={cached.winkLeftShapeIndex}, winkR={cached.winkRightShapeIndex}");
                return;
            }

            // Generate from scratch
            try
            {
                var vertices = original.vertices;
                var boneWeights = original.boneWeights;
                Matrix4x4 bindpose = original.bindposes[headBoneIdx];
                int vertCount = vertices.Length;

                // --- Collect head-weighted verts and compute head-local positions + bbox ---
                var headVertIndices = new List<int>();
                Vector3 bboxMin = Vector3.positiveInfinity;
                Vector3 bboxMax = Vector3.negativeInfinity;

                for (int i = 0; i < vertCount; i++)
                {
                    if (!IsWeightedToBone(boneWeights[i], headBoneIdx, 0.5f)) continue;

                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    bboxMin = Vector3.Min(bboxMin, localPos);
                    bboxMax = Vector3.Max(bboxMax, localPos);
                    headVertIndices.Add(i);
                }

                // CALIBRATION LOG — essential for tuning thresholds
                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel} headLocal bbox: X[{bboxMin.x:F4},{bboxMax.x:F4}] Y[{bboxMin.y:F4},{bboxMax.y:F4}] Z[{bboxMin.z:F4},{bboxMax.z:F4}] ({headVertIndices.Count} verts)");

                // --- Map bbox-relative fractions to absolute positions ---
                float lowerMaxAbs = bboxMin.y + _procLowerMaxFrac * (bboxMax.y - bboxMin.y);
                float forwardMinAbs = bboxMin.z + _procForwardMinFrac * (bboxMax.z - bboxMin.z);
                float hingeYAbs = bboxMin.y + _procHingeYFrac * (bboxMax.y - bboxMin.y);
                float hingeZAbs = bboxMin.z + _procHingeZFrac * (bboxMax.z - bboxMin.z);

                // Blink eye line Y and band edges
                float eyeYAbs = bboxMin.y + _procBlinkEyeYFrac * (bboxMax.y - bboxMin.y);
                float bandHalfHeight = (_procBlinkBandHeightFrac * 0.5f) * (bboxMax.y - bboxMin.y);
                float eyeBandMin = eyeYAbs - bandHalfHeight;
                float eyeBandMax = eyeYAbs + bandHalfHeight;

                // X split for left/right eyes — use bbox midpoint (not literal 0, in case head-local isn't centered)
                float xMid = (bboxMin.x + bboxMax.x) * 0.5f;

                // =================================================================
                // Jaw verts (below lowerMaxAbs and forward of forwardMinAbs)
                // =================================================================
                var jawVertIndices = new List<int>();
                float maxDistBelowHinge = 0f;
                Vector3 hingePoint = new Vector3(0f, hingeYAbs, hingeZAbs);

                foreach (int i in headVertIndices)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    if (localPos.y < lowerMaxAbs && localPos.z > forwardMinAbs)
                    {
                        float distBelowHinge = hingePoint.y - localPos.y;
                        if (distBelowHinge > maxDistBelowHinge)
                            maxDistBelowHinge = distBelowHinge;
                        jawVertIndices.Add(i);
                    }
                }

                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: {jawVertIndices.Count} jaw verts selected (maxDistBelowHinge={maxDistBelowHinge:F4})");

                // =================================================================
                // Eye verts — split into left/right by X vs bbox midpoint, forward of blinkForwardMinAbs
                // =================================================================
                float blinkForwardMinAbs = bboxMin.z + _procBlinkForwardMinFrac * (bboxMax.z - bboxMin.z);
                var leftEyeVerts = new List<int>();
                var rightEyeVerts = new List<int>();
                Vector3 leftEyeCenter = Vector3.zero;
                Vector3 rightEyeCenter = Vector3.zero;
                float leftMaxDist = 0f;
                float rightMaxDist = 0f;

                foreach (int i in headVertIndices)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    if (localPos.y >= eyeBandMin && localPos.y <= eyeBandMax && localPos.z > blinkForwardMinAbs)
                    {
                        if (localPos.x < xMid)
                            leftEyeVerts.Add(i);
                        else
                            rightEyeVerts.Add(i);
                    }
                }

                // Pass 1: compute eye centers (average position)
                foreach (int i in leftEyeVerts)
                    leftEyeCenter += bindpose.MultiplyPoint3x4(vertices[i]);
                if (leftEyeVerts.Count > 0)
                    leftEyeCenter /= leftEyeVerts.Count;

                foreach (int i in rightEyeVerts)
                    rightEyeCenter += bindpose.MultiplyPoint3x4(vertices[i]);
                if (rightEyeVerts.Count > 0)
                    rightEyeCenter /= rightEyeVerts.Count;

                // Pass 1b: narrow width — remove verts outside ±halfWidth of each eye center
                float halfWidth = _procBlinkBandWidthFrac * (bboxMax.x - bboxMin.x);
                int leftBeforeTrim = leftEyeVerts.Count;
                int rightBeforeTrim = rightEyeVerts.Count;
                leftEyeVerts.RemoveAll(i =>
                {
                    Vector3 lp = bindpose.MultiplyPoint3x4(vertices[i]);
                    return Mathf.Abs(lp.x - leftEyeCenter.x) > halfWidth;
                });
                rightEyeVerts.RemoveAll(i =>
                {
                    Vector3 lp = bindpose.MultiplyPoint3x4(vertices[i]);
                    return Mathf.Abs(lp.x - rightEyeCenter.x) > halfWidth;
                });

                // Pass 2: compute max RADIAL (XZ-planar) distance from center — same metric
                // ComputeEyeDeltas' falloff actually uses, so the falloff correctly reaches 0
                // at the true edge of the selected region instead of comparing XZ distance
                // against a Y-axis-only max.
                foreach (int i in leftEyeVerts)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    float dx = localPos.x - leftEyeCenter.x;
                    float dz = localPos.z - leftEyeCenter.z;
                    float radialDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (radialDist > leftMaxDist) leftMaxDist = radialDist;
                }

                foreach (int i in rightEyeVerts)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    float dx = localPos.x - rightEyeCenter.x;
                    float dz = localPos.z - rightEyeCenter.z;
                    float radialDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (radialDist > rightMaxDist) rightMaxDist = radialDist;
                }

                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: eye verts — left={leftEyeVerts.Count} (from {leftBeforeTrim}), right={rightEyeVerts.Count} (from {rightBeforeTrim}), eyeYAbs={eyeYAbs:F4}, band=[{eyeBandMin:F4},{eyeBandMax:F4}], xMid={xMid:F4}, halfWidth={halfWidth:F4}, blinkForwardMinAbs={blinkForwardMinAbs:F4}");

                // Diagnostic: measure max Y displacement for blink (how far the closest vert is from eyeYAbs)
                float maxBlinkDisplacement = 0f;
                foreach (int i in leftEyeVerts)
                {
                    Vector3 lp = bindpose.MultiplyPoint3x4(vertices[i]);
                    maxBlinkDisplacement = Mathf.Max(maxBlinkDisplacement, Mathf.Abs(eyeYAbs - lp.y));
                }
                foreach (int i in rightEyeVerts)
                {
                    Vector3 lp = bindpose.MultiplyPoint3x4(vertices[i]);
                    maxBlinkDisplacement = Mathf.Max(maxBlinkDisplacement, Mathf.Abs(eyeYAbs - lp.y));
                }
                float effectiveMaxDelta = maxBlinkDisplacement * _procBlinkCloseAmount;
                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: blink displacement — maxRaw={maxBlinkDisplacement:F4}, effective={effectiveMaxDelta:F4} (CloseAmount={_procBlinkCloseAmount})");

                // =================================================================
                // Compute vertex deltas for each shape (all share same vertCount array)
                // =================================================================

                // --- ProcJawOpen: rotation about hinge X axis with falloff ---
                Vector3[] jawDeltas = new Vector3[vertCount];
                if (maxDistBelowHinge > 0f)
                {
                    foreach (int i in jawVertIndices)
                    {
                        Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                        float distBelowHinge = hingePoint.y - localPos.y;
                        float falloff = Mathf.Clamp01(distBelowHinge / maxDistBelowHinge);
                        float angle = _procOpenAngle * falloff;

                        Vector3 relative = localPos - hingePoint;
                        Quaternion rot = Quaternion.Euler(angle, 0f, 0f);
                        Vector3 rotatedLocal = rot * relative + hingePoint;
                        Vector3 rotatedMeshSpace = bindpose.inverse.MultiplyPoint3x4(rotatedLocal);
                        jawDeltas[i] = rotatedMeshSpace - vertices[i];
                    }
                }

                // --- ProcBlink: both eyes close (linear Y toward eyeYAbs, radial falloff) ---
                Vector3[] blinkDeltas = new Vector3[vertCount];
                bool hasBlink = leftEyeVerts.Count > 0 || rightEyeVerts.Count > 0;
                if (hasBlink)
                {
                    ComputeEyeDeltas(leftEyeVerts, leftEyeCenter, leftMaxDist, eyeYAbs, bindpose, vertices, blinkDeltas);
                    ComputeEyeDeltas(rightEyeVerts, rightEyeCenter, rightMaxDist, eyeYAbs, bindpose, vertices, blinkDeltas);
                }

                // --- ProcWinkLeft: left eye only ---
                Vector3[] winkLeftDeltas = new Vector3[vertCount];
                bool hasWinkLeft = leftEyeVerts.Count > 0;
                if (hasWinkLeft)
                    ComputeEyeDeltas(leftEyeVerts, leftEyeCenter, leftMaxDist, eyeYAbs, bindpose, vertices, winkLeftDeltas);

                // --- ProcWinkRight: right eye only ---
                Vector3[] winkRightDeltas = new Vector3[vertCount];
                bool hasWinkRight = rightEyeVerts.Count > 0;
                if (hasWinkRight)
                    ComputeEyeDeltas(rightEyeVerts, rightEyeCenter, rightMaxDist, eyeYAbs, bindpose, vertices, winkRightDeltas);

                // =================================================================
                // BAKE: ONE Instantiate, MULTIPLE AddBlendShapeFrame, ONE assignment
                // =================================================================
                Mesh inst = Instantiate(original);

                int jawIdx = -1;
                if (maxDistBelowHinge > 0f)
                {
                    inst.AddBlendShapeFrame("ProcJawOpen", 100f, jawDeltas, null, null);
                    jawIdx = inst.blendShapeCount - 1;
                }

                int blinkIdx = -1;
                if (hasBlink)
                {
                    inst.AddBlendShapeFrame("ProcBlink", 100f, blinkDeltas, null, null);
                    blinkIdx = inst.blendShapeCount - 1;
                }

                int winkLIdx = -1;
                if (hasWinkLeft)
                {
                    inst.AddBlendShapeFrame("ProcWinkLeft", 100f, winkLeftDeltas, null, null);
                    winkLIdx = inst.blendShapeCount - 1;
                }

                int winkRIdx = -1;
                if (hasWinkRight)
                {
                    inst.AddBlendShapeFrame("ProcWinkRight", 100f, winkRightDeltas, null, null);
                    winkRIdx = inst.blendShapeCount - 1;
                }

                smr.sharedMesh = inst;
                _procSmr = smr;
                _procShapeIndex = jawIdx;
                _procBlinkShapeIndex = blinkIdx;
                _procWinkLeftShapeIndex = winkLIdx;
                _procWinkRightShapeIndex = winkRIdx;

                // Cache for reuse across spawns
                _procCache[meshId] = (inst, jawIdx, blinkIdx, winkLIdx, winkRIdx);

                Log.Debug(() => $"[LIPSYNC PROC] {_npcLabel}: baked — jaw={jawIdx} ({jawVertIndices.Count} verts), blink={blinkIdx} ({leftEyeVerts.Count + rightEyeVerts.Count} verts), winkL={winkLIdx}, winkR={winkRIdx}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[LIPSYNC PROC] {_npcLabel}: failed to generate procedural shapes — {ex.Message}");
            }
        }

        /// <summary>
        /// Compute Y-displacement deltas for eye-region verts: move toward eyeYAbs (eyelid close)
        /// with radial falloff from eye center.
        /// </summary>
        private void ComputeEyeDeltas(List<int> vertIndices, Vector3 eyeCenter, float maxDist, float eyeYAbs, Matrix4x4 bindpose, Vector3[] vertices, Vector3[] deltas)
        {
            if (vertIndices.Count == 0 || maxDist <= 0f) return;

            foreach (int i in vertIndices)
            {
                Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);

                // Radial falloff from eye center (2D: XZ plane distance)
                float radialDist = Mathf.Sqrt((localPos.x - eyeCenter.x) * (localPos.x - eyeCenter.x) + (localPos.z - eyeCenter.z) * (localPos.z - eyeCenter.z));
                float falloff = Mathf.Clamp01(1f - (radialDist / maxDist));

                // Linear Y delta toward eyeYAbs (upper lid down, lower lid up)
                float yDelta = (eyeYAbs - localPos.y) * _procBlinkCloseAmount * falloff;

                // Convert to mesh space for blendshape delta
                Vector3 displacedLocal = new Vector3(localPos.x, localPos.y + yDelta, localPos.z);
                Vector3 displacedMeshSpace = bindpose.inverse.MultiplyPoint3x4(displacedLocal);
                deltas[i] = displacedMeshSpace - vertices[i];
            }
        }

        /// <summary>
        /// Find the Head bone's index within this SMR's bones array.
        /// Tries Animator.GetBoneTransform(HumanBodyBones.Head) first, falls back to bone name "Head".
        /// </summary>
        private int FindHeadBoneIndexInSmr(SkinnedMeshRenderer smr)
        {
            Transform headTransform = null;

            // Try Animator.GetBoneTransform(HumanBodyBones.Head)
            var animator = GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                try { headTransform = animator.GetBoneTransform(HumanBodyBones.Head); } catch { /* bone not mapped in avatar - headTransform stays null, caller handles */ }
            }

            // Fallback: find bone named "Head" in this SMR's bones
            if (headTransform == null)
            {
                foreach (var bone in smr.bones)
                {
                    if (bone != null && bone.name == "Head")
                    {
                        headTransform = bone;
                        break;
                    }
                }
            }

            if (headTransform == null) return -1;

            // Find index in this SMR's bones array
            for (int i = 0; i < smr.bones.Length; i++)
            {
                if (smr.bones[i] == headTransform) return i;
            }
            return -1;
        }

        /// <summary>
        /// Check if a BoneWeight has significant weight on the given bone index.
        /// </summary>
        private static bool IsWeightedToBone(BoneWeight bw, int boneIdx, float threshold)
        {
            if ((int)bw.boneIndex0 == boneIdx && bw.weight0 >= threshold) return true;
            if ((int)bw.boneIndex1 == boneIdx && bw.weight1 >= threshold) return true;
            if ((int)bw.boneIndex2 == boneIdx && bw.weight2 >= threshold) return true;
            if ((int)bw.boneIndex3 == boneIdx && bw.weight3 >= threshold) return true;
            return false;
        }

        // ========================================================================
        // Update — amplitude envelope + blendshape mouth + animator gate + procedural jaw + blink
        // ========================================================================

        private void Update()
        {
            // --- Shared amplitude envelope ---
            float target = 0f;
            bool isPlaying = _audioSource != null && _audioSource.isPlaying;

            if (isPlaying)
            {
                // --- Primary: read amplitude from raw AudioClip samples (full amplitude, machine-independent) ---
                AudioClip clip = _audioSource.clip;
                if (clip != null)
                {
                    try
                    {
                        int pos = _audioSource.timeSamples;
                        int ch = clip.channels;
                        int maxFrames = _rmsBuffer.Length / ch; // 256/ch frames
                        int framesToRead = Mathf.Min(maxFrames, clip.samples - pos);

                        if (framesToRead > 0)
                        {
                            int sampleCount = framesToRead * ch;
                            clip.GetData(_rmsBuffer, pos);

                            float rms = 0f;
                            for (int i = 0; i < sampleCount; i++)
                            {
                                rms += _rmsBuffer[i] * _rmsBuffer[i];
                            }
                            rms = Mathf.Sqrt(rms / sampleCount);

                            if (rms >= _noiseGateThreshold)
                            {
                                target = Mathf.Clamp01(rms * _gain);
                            }

                            if (rms > _peakRms) _peakRms = rms;
                        }
                    }
                    catch
                    {
                        // GetData can throw on a disposed/streamed clip — skip frame
                    }
                }


            }

            float target100 = Mathf.Clamp01(target) * _maxWeight;
            if (target100 > _envelope)
                _envelope = Mathf.Lerp(_envelope, target100, Time.deltaTime * _attackSpeed);
            else
                _envelope = Mathf.Lerp(_envelope, target100, Time.deltaTime * _releaseSpeed);

            if (_envelope > _peakEnvelope) _peakEnvelope = _envelope;

            // --- Blendshape mouth (Update — no animator conflict) ---
            if (_activeMode == "blendshape" && _faceSmr != null && _mouthShapeIndex >= 0)
            {
                float weight = _envelope;
                if (Mathf.Abs(weight - _lastWrittenMouthWeight) >= 0.01f)
                {
                    _faceSmr.SetBlendShapeWeight(_mouthShapeIndex, weight);
                    _lastWrittenMouthWeight = weight;
                }
            }

            // --- Animator gate via avatar controller (mirrors MinEventActionAnimatorSetFloat) ---
            if (_activeMode == "animator" && _animParamExists && _npcEntity != null)
            {
                var ac = _npcEntity.emodel?.avatarController;
                if (ac != null)
                {
                    float value;
                    if (isPlaying)
                    {
                        _animReleaseTimer = AnimReleaseHold;
                        value = 1f;
                    }
                    else if (_animReleaseTimer > 0f)
                    {
                        _animReleaseTimer -= Time.deltaTime;
                        value = 1f;
                    }
                    else
                    {
                        value = 0f;
                    }
                    ac.UpdateFloat(_animParamName, value, true);
                }
            }

            // --- Procedural jaw drive (same amplitude envelope as tier 0) ---
            if (_activeMode == "procedural" && _procSmr != null && _procShapeIndex >= 0)
            {
                float weight = _procTestHold ? 100f : _envelope;
                // TestHold (dev diagnostic) always writes so the jaw stays locked deterministic.
                if (_procTestHold || Mathf.Abs(weight - _lastWrittenMouthWeight) >= 0.01f)
                {
                    _procSmr.SetBlendShapeWeight(_procShapeIndex, weight);
                    _lastWrittenMouthWeight = weight;
                }
            }

            // --- Calibration log — emit once per utterance when audio stops ---
            if (_wasPlaying && !isPlaying && _activeMode != "off")
            {
                Log.Debug(() => $"[LIPSYNC] {_npcLabel} ({_activeMode}): peakClipRMS={_peakRms:F3} peakEnvelope={_peakEnvelope:F0}");
                _peakRms = 0f;
                _peakEnvelope = 0f;
            }
            _wasPlaying = isPlaying;

            // --- Blink (independent of mode) ---
            // Trigger on EITHER artist blink OR procedural blink existing — DoBlink() itself
            // picks the right one via its artist>procedural priority logic. _procBlinkShapeIndex
            // is the correct single check here: whenever procedural blink baked successfully for
            // EITHER eye, PickBlinkShape()'s fallback chain guarantees a valid index in every
            // WinkMode branch (blink bakes if leftEyeVerts.Count > 0 || rightEyeVerts.Count > 0,
            // and every PickBlinkShape() branch falls back to _procBlinkShapeIndex).
            bool hasArtistBlink = _blinkSmr != null && _blinkShapeIndex >= 0;
            bool hasProceduralBlink = _procSmr != null && _procBlinkShapeIndex >= 0;
            if (_blinkEnabled && (hasArtistBlink || hasProceduralBlink))
            {
                if (!_blinking)
                {
                    if (Time.time >= _blinkNextAt)
                    {
                        StartCoroutine(DoBlink());
                    }
                }
            }
        }

        // ========================================================================
        // Blink coroutine
        // ========================================================================

        private IEnumerator DoBlink()
        {
            _blinking = true;

            // Determine which SMR + shape index to animate:
            // Priority: artist blink > procedural blink/wink (per WinkMode/WinkChance)
            SkinnedMeshRenderer targetSmr;
            int targetShapeIndex;

            if (_blinkSmr != null && _blinkShapeIndex >= 0)
            {
                // Artist blink exists — use it, ignore procedural
                targetSmr = _blinkSmr;
                targetShapeIndex = _blinkShapeIndex;
            }
            else if (_procSmr != null)
            {
                // Procedural fallback — pick shape based on wink mode
                targetSmr = _procSmr;
                targetShapeIndex = PickBlinkShape();
            }
            else
            {
                // No blink shape at all — abort
                _blinking = false;
                yield break;
            }

            float blinkWeight = 0f;
            float duration = _blinkDurationMs / 1000f;
            float half = duration * 0.5f;
            float elapsed = 0f;

            while (elapsed < half)
            {
                elapsed += Time.deltaTime;
                blinkWeight = Mathf.Lerp(0f, 100f, elapsed / half);
                targetSmr.SetBlendShapeWeight(targetShapeIndex, blinkWeight);
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < half)
            {
                elapsed += Time.deltaTime;
                blinkWeight = Mathf.Lerp(100f, 0f, elapsed / half);
                targetSmr.SetBlendShapeWeight(targetShapeIndex, blinkWeight);
                yield return null;
            }

            targetSmr.SetBlendShapeWeight(targetShapeIndex, 0f);
            _blinking = false;
            if (_blinkEnabled)
                _blinkNextAt = Time.time + UnityEngine.Random.Range(_blinkIntervalMin, _blinkIntervalMax);
        }

        /// <summary>
        /// Pick which procedural blink shape to use based on wink mode.
        /// Returns -1 if no procedural blink shape exists (caller should check before using).
        /// </summary>
        private int PickBlinkShape()
        {
            switch (_procBlinkWinkMode)
            {
                case "left":
                    return _procWinkLeftShapeIndex;
                case "right":
                    return _procWinkRightShapeIndex;
                case "random":
                    if (UnityEngine.Random.value < _procBlinkWinkChance)
                    {
                        // Wink — pick left or right randomly
                        if (_procWinkLeftShapeIndex >= 0 && _procWinkRightShapeIndex >= 0)
                            return UnityEngine.Random.value < 0.5f ? _procWinkLeftShapeIndex : _procWinkRightShapeIndex;
                        else if (_procWinkLeftShapeIndex >= 0)
                            return _procWinkLeftShapeIndex;
                        else if (_procWinkRightShapeIndex >= 0)
                            return _procWinkRightShapeIndex;
                        else
                            return _procBlinkShapeIndex; // fallback to full blink
                    }
                    return _procBlinkShapeIndex; // full blink
                default: // "off" or unknown
                    return _procBlinkShapeIndex; // full blink
            }
        }

        private void OnDestroy()
        {
            if (_blinking)
            {
                StopCoroutine(DoBlink());
            }
            // Remove from registry
            _registry.Remove(this);
            // Cached meshes in _procCache are shared across instances — don't destroy on individual cleanup.
            // Cache is evicted wholesale at next game start via ClearProcCache().
        }

        // ========================================================================
        // Hot-reload (vc reloadface)
        // ========================================================================

        /// <summary>
        /// Re-apply FaceLipSync config from modconfig.xml to all live instances.
        /// Called from console command on main thread.
        /// </summary>
        public static int ReloadAll(TTSConfig config)
        {
            int reloaded = 0;
            foreach (var instance in _registry)
            {
                if (instance != null && instance.gameObject.activeInHierarchy)
                {
                    // Look up the NPC's currently assigned personality for fresh FaceOverride data.
                    // AssignPersonality hits the existing assignment cache — it does NOT reassign,
                    // just returns the current PersonalityDefinition built from freshly-reloaded dict.
                    XNPCVoiceControl.FaceOverride freshFaceOverride = null;
                    if (instance._npcEntity != null)
                    {
                        var personality = PersonalityManager.Instance.AssignPersonality(instance._npcEntity);
                        freshFaceOverride = personality?.FaceOverride;
                    }
                    instance.ReloadConfig(config, freshFaceOverride);
                    reloaded++;
                }
            }
            return reloaded;
        }

        /// <summary>
        /// Update runtime knobs and re-resolve mode. For procedural: regenerate baked blendshape.
        /// </summary>
        public void ReloadConfig(TTSConfig config, XNPCVoiceControl.FaceOverride freshFaceOverride)
        {
            // Refresh the cached per-character override so edits to personalities.xml take effect live.
            _faceOverride = freshFaceOverride;

            // Update envelope knobs
            _gain = config.FaceLipSyncGain;
            _attackSpeed = config.FaceLipSyncAttack;
            _releaseSpeed = config.FaceLipSyncRelease;
            _noiseGateThreshold = config.FaceLipSyncNoiseGate;
            _maxWeight = config.FaceLipSyncMaxWeight;

            // Update blink knobs
            _blinkEnabled = config.FaceLipSyncBlinkEnabled;
            _blinkIntervalMin = config.FaceLipSyncBlinkIntervalMin;
            _blinkIntervalMax = config.FaceLipSyncBlinkIntervalMax;
            _blinkDurationMs = config.FaceLipSyncBlinkDurationMs;

            // Update mode + animator param
            _mode = config.FaceLipSyncMode;
            _animParamName = config.FaceLipSyncAnimParam;

            // Update procedural knobs (bbox-relative fractions)
            // Merge per-character FaceOverride over global config defaults
            var fo = _faceOverride;
            _procOpenAngle = fo?.OpenAngle ?? config.FaceLipSyncProcOpenAngle;
            _procLowerMaxFrac = fo?.LowerMaxFrac ?? config.FaceLipSyncProcLowerMaxFrac;
            _procForwardMinFrac = fo?.ForwardMinFrac ?? config.FaceLipSyncProcForwardMinFrac;
            _procHingeYFrac = fo?.HingeYFrac ?? config.FaceLipSyncProcHingeYFrac;
            _procHingeZFrac = fo?.HingeZFrac ?? config.FaceLipSyncProcHingeZFrac;
            _procTestHold = fo?.TestHold ?? config.FaceLipSyncProcTestHold;

            // Update procedural blink knobs (override or global default)
            _procBlinkEyeYFrac = fo?.BlinkEyeYFrac ?? config.FaceLipSyncProcBlinkEyeYFrac;
            _procBlinkBandHeightFrac = fo?.BlinkBandHeightFrac ?? config.FaceLipSyncProcBlinkBandHeightFrac;
            _procBlinkBandWidthFrac = fo?.BlinkBandWidthFrac ?? config.FaceLipSyncProcBlinkBandWidthFrac;
            _procBlinkCloseAmount = fo?.BlinkCloseAmount ?? config.FaceLipSyncProcBlinkCloseAmount;
            _procBlinkForwardMinFrac = fo?.BlinkForwardMinFrac ?? config.FaceLipSyncProcBlinkForwardMinFrac;
            _procBlinkWinkMode = !string.IsNullOrEmpty(fo?.BlinkWinkMode) ? fo.BlinkWinkMode : config.FaceLipSyncProcBlinkWinkMode;
            _procBlinkWinkChance = fo?.BlinkWinkChance ?? config.FaceLipSyncProcBlinkWinkChance;

            // If currently in procedural mode, regenerate the baked blendshape from original mesh
            if (_activeMode == "procedural" && _procSmr != null && _originalMesh != null)
            {
                RegenerateProceduralShapes();
            }

            // Re-resolve active mode (may change if mode string changed)
            ResolveMouthMode();

            _lastWrittenMouthWeight = -1f; // force first write after reload
            Log.Debug(() => $"[LIPSYNC RELOAD] {_npcLabel}: reloaded -> mode={_activeMode}, procJaw={_procShapeIndex}, procBlink={_procBlinkShapeIndex}, winkL={_procWinkLeftShapeIndex}, winkR={_procWinkRightShapeIndex}");
        }

        /// <summary>
        /// Re-bake ALL procedural blendshapes from the stored original mesh with current proc params.
        /// Mirrors GenerateProceduralShapes — unified single bake pass.
        /// </summary>
        private void RegenerateProceduralShapes()
        {
            if (_originalMesh == null || _procSmr == null)
            {
                Log.Warning($"[LIPSYNC RELOAD] {_npcLabel}: can't regenerate — original mesh or SMR missing");
                return;
            }

            try
            {
                int headBoneIdx = FindHeadBoneIndexInSmr(_procSmr);
                if (headBoneIdx < 0)
                {
                    Log.Warning($"[LIPSYNC RELOAD] {_npcLabel}: head bone not found for re-bake");
                    return;
                }

                var vertices = _originalMesh.vertices;
                var boneWeights = _originalMesh.boneWeights;
                Matrix4x4 bindpose = _originalMesh.bindposes[headBoneIdx];
                int vertCount = vertices.Length;

                // Collect head-weighted verts and compute bbox
                var headVertIndices = new List<int>();
                Vector3 bboxMin = Vector3.positiveInfinity;
                Vector3 bboxMax = Vector3.negativeInfinity;
                for (int i = 0; i < vertCount; i++)
                {
                    if (!IsWeightedToBone(boneWeights[i], headBoneIdx, 0.5f)) continue;
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    bboxMin = Vector3.Min(bboxMin, localPos);
                    bboxMax = Vector3.Max(bboxMax, localPos);
                    headVertIndices.Add(i);
                }

                // Map fractions to absolute positions
                float lowerMaxAbs = bboxMin.y + _procLowerMaxFrac * (bboxMax.y - bboxMin.y);
                float forwardMinAbs = bboxMin.z + _procForwardMinFrac * (bboxMax.z - bboxMin.z);
                float hingeYAbs = bboxMin.y + _procHingeYFrac * (bboxMax.y - bboxMin.y);
                float hingeZAbs = bboxMin.z + _procHingeZFrac * (bboxMax.z - bboxMin.z);

                // Blink eye line Y and band edges
                float eyeYAbs = bboxMin.y + _procBlinkEyeYFrac * (bboxMax.y - bboxMin.y);
                float bandHalfHeight = (_procBlinkBandHeightFrac * 0.5f) * (bboxMax.y - bboxMin.y);
                float eyeBandMin = eyeYAbs - bandHalfHeight;
                float eyeBandMax = eyeYAbs + bandHalfHeight;

                // X split for left/right eyes
                float xMid = (bboxMin.x + bboxMax.x) * 0.5f;

                // --- Jaw verts ---
                var jawVertIndices = new List<int>();
                float maxDistBelowHinge = 0f;
                Vector3 hingePoint = new Vector3(0f, hingeYAbs, hingeZAbs);

                foreach (int i in headVertIndices)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    if (localPos.y < lowerMaxAbs && localPos.z > forwardMinAbs)
                    {
                        float distBelowHinge = hingePoint.y - localPos.y;
                        if (distBelowHinge > maxDistBelowHinge)
                            maxDistBelowHinge = distBelowHinge;
                        jawVertIndices.Add(i);
                    }
                }

                // --- Eye verts — split into left/right, forward of blinkForwardMinAbs ---
                float blinkForwardMinAbsReload = bboxMin.z + _procBlinkForwardMinFrac * (bboxMax.z - bboxMin.z);
                var leftEyeVerts = new List<int>();
                var rightEyeVerts = new List<int>();
                Vector3 leftEyeCenter = Vector3.zero;
                Vector3 rightEyeCenter = Vector3.zero;
                float leftMaxDist = 0f;
                float rightMaxDist = 0f;

                foreach (int i in headVertIndices)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    if (localPos.y >= eyeBandMin && localPos.y <= eyeBandMax && localPos.z > blinkForwardMinAbsReload)
                    {
                        if (localPos.x < xMid)
                            leftEyeVerts.Add(i);
                        else
                            rightEyeVerts.Add(i);
                    }
                }

                // Pass 1: compute eye centers (average position)
                foreach (int i in leftEyeVerts)
                    leftEyeCenter += bindpose.MultiplyPoint3x4(vertices[i]);
                if (leftEyeVerts.Count > 0)
                    leftEyeCenter /= leftEyeVerts.Count;

                foreach (int i in rightEyeVerts)
                    rightEyeCenter += bindpose.MultiplyPoint3x4(vertices[i]);
                if (rightEyeVerts.Count > 0)
                    rightEyeCenter /= rightEyeVerts.Count;

                // Pass 1b: narrow width — remove verts outside ±halfWidth of each eye center
                float halfWidthReload = _procBlinkBandWidthFrac * (bboxMax.x - bboxMin.x);
                leftEyeVerts.RemoveAll(i =>
                {
                    Vector3 lp = bindpose.MultiplyPoint3x4(vertices[i]);
                    return Mathf.Abs(lp.x - leftEyeCenter.x) > halfWidthReload;
                });
                rightEyeVerts.RemoveAll(i =>
                {
                    Vector3 lp = bindpose.MultiplyPoint3x4(vertices[i]);
                    return Mathf.Abs(lp.x - rightEyeCenter.x) > halfWidthReload;
                });

                // Pass 2: compute max RADIAL (XZ-planar) distance from center — same metric
                // ComputeEyeDeltas' falloff actually uses, so the falloff correctly reaches 0
                // at the true edge of the selected region instead of comparing XZ distance
                // against a Y-axis-only max.
                foreach (int i in leftEyeVerts)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    float dx = localPos.x - leftEyeCenter.x;
                    float dz = localPos.z - leftEyeCenter.z;
                    float radialDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (radialDist > leftMaxDist) leftMaxDist = radialDist;
                }

                foreach (int i in rightEyeVerts)
                {
                    Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                    float dx = localPos.x - rightEyeCenter.x;
                    float dz = localPos.z - rightEyeCenter.z;
                    float radialDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (radialDist > rightMaxDist) rightMaxDist = radialDist;
                }

                // --- Compute vertex deltas ---
                Vector3[] jawDeltas = new Vector3[vertCount];
                if (maxDistBelowHinge > 0f)
                {
                    foreach (int i in jawVertIndices)
                    {
                        Vector3 localPos = bindpose.MultiplyPoint3x4(vertices[i]);
                        float distBelowHinge = hingePoint.y - localPos.y;
                        float falloff = Mathf.Clamp01(distBelowHinge / maxDistBelowHinge);
                        float angle = _procOpenAngle * falloff;

                        Vector3 relative = localPos - hingePoint;
                        Quaternion rot = Quaternion.Euler(angle, 0f, 0f);
                        Vector3 rotatedLocal = rot * relative + hingePoint;
                        Vector3 rotatedMeshSpace = bindpose.inverse.MultiplyPoint3x4(rotatedLocal);
                        jawDeltas[i] = rotatedMeshSpace - vertices[i];
                    }
                }

                Vector3[] blinkDeltas = new Vector3[vertCount];
                bool hasBlink = leftEyeVerts.Count > 0 || rightEyeVerts.Count > 0;
                if (hasBlink)
                {
                    ComputeEyeDeltas(leftEyeVerts, leftEyeCenter, leftMaxDist, eyeYAbs, bindpose, vertices, blinkDeltas);
                    ComputeEyeDeltas(rightEyeVerts, rightEyeCenter, rightMaxDist, eyeYAbs, bindpose, vertices, blinkDeltas);
                }

                Vector3[] winkLeftDeltas = new Vector3[vertCount];
                bool hasWinkLeft = leftEyeVerts.Count > 0;
                if (hasWinkLeft)
                    ComputeEyeDeltas(leftEyeVerts, leftEyeCenter, leftMaxDist, eyeYAbs, bindpose, vertices, winkLeftDeltas);

                Vector3[] winkRightDeltas = new Vector3[vertCount];
                bool hasWinkRight = rightEyeVerts.Count > 0;
                if (hasWinkRight)
                    ComputeEyeDeltas(rightEyeVerts, rightEyeCenter, rightMaxDist, eyeYAbs, bindpose, vertices, winkRightDeltas);

                // --- BAKE: ONE Instantiate, MULTIPLE AddBlendShapeFrame, ONE assignment ---
                Mesh inst = Instantiate(_originalMesh);

                int jawIdx = -1;
                if (maxDistBelowHinge > 0f)
                {
                    inst.AddBlendShapeFrame("ProcJawOpen", 100f, jawDeltas, null, null);
                    jawIdx = inst.blendShapeCount - 1;
                }

                int blinkIdx = -1;
                if (hasBlink)
                {
                    inst.AddBlendShapeFrame("ProcBlink", 100f, blinkDeltas, null, null);
                    blinkIdx = inst.blendShapeCount - 1;
                }

                int winkLIdx = -1;
                if (hasWinkLeft)
                {
                    inst.AddBlendShapeFrame("ProcWinkLeft", 100f, winkLeftDeltas, null, null);
                    winkLIdx = inst.blendShapeCount - 1;
                }

                int winkRIdx = -1;
                if (hasWinkRight)
                {
                    inst.AddBlendShapeFrame("ProcWinkRight", 100f, winkRightDeltas, null, null);
                    winkRIdx = inst.blendShapeCount - 1;
                }

                _procSmr.sharedMesh = inst;
                _procShapeIndex = jawIdx;
                _procBlinkShapeIndex = blinkIdx;
                _procWinkLeftShapeIndex = winkLIdx;
                _procWinkRightShapeIndex = winkRIdx;

                // Update static cache
                int meshId = _originalMesh.GetInstanceID();
                _procCache[meshId] = (inst, jawIdx, blinkIdx, winkLIdx, winkRIdx);

                _lastWrittenMouthWeight = -1f; // force first write after re-bake
                Log.Debug(() => $"[LIPSYNC PROC RELOAD] {_npcLabel}: re-baked — jaw={jawIdx} ({jawVertIndices.Count} verts), blink={blinkIdx} ({leftEyeVerts.Count + rightEyeVerts.Count} verts), winkL={winkLIdx}, winkR={winkRIdx}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[LIPSYNC PROC RELOAD] {_npcLabel}: failed — {ex.Message}");
            }
        }
    }
}
