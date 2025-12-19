// Assets/Editor/CreationExporter.cs
// Menu: Tools > Creation > Export Creation
// - Exports ALL loaded scene objects (no root)
// - Includes inactive objects + writes activeSelf so loader can keep them inactive
// - Exports meshes (OBJ), textures (PNG), colliders, lights
// - Complexity budget: lights +100, each triangle +1, each collider-object +4, limit 100,000
// - If unsupported objects exist: shows a 3-button window: Delete all / Ignore all / Clean all
//
// FIX: Allowlist URP UniversalAdditionalLightData (and a few related URP “additional” components)
// so your Directional Light doesn’t get flagged as unsupported.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

public static class CreationExporter
{
    // =========================
    // Complexity config
    // =========================
    public const int COMPLEXITY_LIMIT = 100_000;
    public const int LIGHT_COST = 100;
    public const int COLLIDER_OBJECT_COST = 4; // per GameObject that has at least 1 collider
    public const int TRIANGLE_COST = 1;        // per triangle

    // Optional allowlist for scripts you consider “supported” (won’t trigger Unsupported Objects).
    // Put full type names if needed, e.g. "MyGame.BlankCanvasUGCMarker"
    static readonly HashSet<string> AllowedMonoBehaviours = new HashSet<string>()
    {
        // "BlankCanvasUGCMarker",
        // "BlankCanvasRuntimeOnlyTag",
    };

    // Allow extra non-Transform components that are safe to keep (don’t block export, don’t get cleaned).
    // No URP package reference needed; we match by type FullName string.
    static readonly HashSet<string> AllowedComponentFullNames = new HashSet<string>()
    {
        // URP light extras (this is the one that was breaking your Directional Light)
        "UnityEngine.Rendering.Universal.UniversalAdditionalLightData",

        // Optional “next things that will annoy you” if you ever scan cameras/reflections
        "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData",
        "UnityEngine.Rendering.Universal.UniversalAdditionalReflectionData",
        "UnityEngine.Rendering.Universal.UniversalAdditionalRenderingData",
    };

    // =========================
    // Menu entry (NO WINDOW)
    // =========================
    [MenuItem("Tools/Creation/Export Creation")]
    public static void ExportCreation()
    {
        string defaultName = SanitizeFileName(EditorSceneManager.GetActiveScene().name);
        if (string.IsNullOrEmpty(defaultName)) defaultName = "BlankCanvasCreation";

        string zipPath = EditorUtility.SaveFilePanel("Save Creation Zip", "", defaultName + ".zip", "zip");
        if (string.IsNullOrEmpty(zipPath)) return;

        // First scan
        var scan = ScanScene();

        // If unsupported exists, show the 3-button fixer window
        if (scan.UnsupportedObjects.Count > 0)
        {
            UnsupportedObjectsWindow.Open(zipPath, scan);
            return;
        }

        // Complexity gate
        if (scan.TotalComplexity > COMPLEXITY_LIMIT)
        {
            ShowComplexityFail(scan, "Scene is too complex!");
            return;
        }

        // Export now
        DoExport(zipPath, scan, ignoreUnsupported: false);
    }

    // =========================
    // Scan
    // =========================
    class ScanResult
    {
        public List<GameObject> AllSceneObjects = new List<GameObject>();

        public List<MeshRenderInfo> Meshes = new List<MeshRenderInfo>();      // MeshRenderer+MeshFilter + Skinned
        public List<LightInfo> Lights = new List<LightInfo>();
        public List<ColliderInfo> Colliders = new List<ColliderInfo>();      // collider-only OR collider-on-mesh/light

        public List<UnsupportedObject> UnsupportedObjects = new List<UnsupportedObject>();

        public int TriangleCount;
        public int LightCount;
        public int ColliderObjectCount;

        public int TriComplexity => TriangleCount * TRIANGLE_COST;
        public int LightComplexity => LightCount * LIGHT_COST;
        public int ColliderComplexity => ColliderObjectCount * COLLIDER_OBJECT_COST;
        public int TotalComplexity => TriComplexity + LightComplexity + ColliderComplexity;

        public string ComplexityLine()
        {
            int used = TotalComplexity;
            int pct = (int)Math.Round((used / (double)COMPLEXITY_LIMIT) * 100.0);
            return $"{Fmt(used)}/{Fmt(COMPLEXITY_LIMIT)} Complexity ({pct}%)";
        }

        public string BreakdownLines()
        {
            return
                $"Triangles: {Fmt(TriangleCount)} (+{Fmt(TriComplexity)})\n" +
                $"Lights: {Fmt(LightCount)} (+{Fmt(LightComplexity)})\n" +
                $"Colliders: {Fmt(ColliderObjectCount)} (+{Fmt(ColliderComplexity)})";
        }
    }

    class MeshRenderInfo
    {
        public Transform tr;
        public Mesh mesh;               // for MeshFilter
        public bool isSkinned;
        public SkinnedMeshRenderer smr; // if skinned, bake from this
        public Material mat;
        public bool hasRenderer;
        public string name;
    }

    class LightInfo
    {
        public Transform tr;
        public Light light;
        public string name;
    }

    class ColliderInfo
    {
        public Transform tr;
        public Collider collider;
        public string name;
    }

    class UnsupportedObject
    {
        public GameObject go;
        public List<string> reasons = new List<string>();
        public List<Component> unsupportedComponents = new List<Component>();

        public string ReasonSummary()
        {
            // nice readable: "Cameras and Particle systems are not currently supported by exports."
            var cats = reasons.Distinct().ToList();
            if (cats.Count == 0) return "Unsupported Objects";
            if (cats.Count == 1) return $"{cats[0]} are not currently supported by exports.";
            if (cats.Count == 2) return $"{cats[0]} and {cats[1]} are not currently supported by exports.";
            return $"{string.Join(", ", cats.Take(cats.Count - 1))}, and {cats.Last()} are not currently supported by exports.";
        }
    }

    static ScanResult ScanScene()
    {
        var scan = new ScanResult();

        // All loaded scene objects, including inactive, excluding assets/prefabs
        var allGOs = Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(go =>
                go != null &&
                go.scene.IsValid() &&
                go.scene.isLoaded &&
                !EditorUtility.IsPersistent(go))
            .ToList();

        scan.AllSceneObjects = allGOs;

        // Track collider objects once per GO
        var colliderObjectSet = new HashSet<int>();

        foreach (var go in allGOs)
        {
            if (go == null) continue;

            // Skip editor-only hidden junk
            if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) continue;

            // Gather supported/exportable components
            var tr = go.transform;

            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            var light = go.GetComponent<Light>();
            var col = go.GetComponent<Collider>();

            bool isMesh = (mf != null && mr != null && mf.sharedMesh != null);
            bool isSkinned = (smr != null && smr.sharedMesh != null);
            bool isLight = (light != null);
            bool hasCollider = (col != null);

            // Supported content tracking
            if (isMesh)
            {
                scan.Meshes.Add(new MeshRenderInfo
                {
                    tr = tr,
                    mesh = mf.sharedMesh,
                    isSkinned = false,
                    smr = null,
                    mat = mr.sharedMaterial,
                    hasRenderer = true,
                    name = go.name
                });

                scan.TriangleCount += SafeTriangleCount(mf.sharedMesh);
            }
            else if (isSkinned)
            {
                scan.Meshes.Add(new MeshRenderInfo
                {
                    tr = tr,
                    mesh = null,
                    isSkinned = true,
                    smr = smr,
                    mat = smr.sharedMaterial,
                    hasRenderer = true,
                    name = go.name
                });

                scan.TriangleCount += SafeTriangleCount(smr.sharedMesh);
            }

            if (isLight)
            {
                scan.Lights.Add(new LightInfo { tr = tr, light = light, name = go.name });
                scan.LightCount += 1;
            }

            if (hasCollider)
            {
                scan.Colliders.Add(new ColliderInfo { tr = tr, collider = col, name = go.name });

                if (!colliderObjectSet.Contains(go.GetInstanceID()))
                {
                    colliderObjectSet.Add(go.GetInstanceID());
                    scan.ColliderObjectCount += 1;
                }

                // MeshCollider might reference a mesh even when there is no MeshFilter
                // You did NOT want mesh triangles in complexity for colliders, so we keep it flat +4.
            }

            // Unsupported detection (per-object)
            var unsupported = GetUnsupportedFor(go);
            if (unsupported.reasons.Count > 0)
                scan.UnsupportedObjects.Add(unsupported);
        }

        return scan;
    }

    static int SafeTriangleCount(Mesh m)
    {
        try
        {
            if (m == null) return 0;
            var tris = m.triangles;
            if (tris == null) return 0;
            return tris.Length / 3;
        }
        catch { return 0; }
    }

    // =========================
    // Unsupported classification
    // =========================
    static UnsupportedObject GetUnsupportedFor(GameObject go)
    {
        var u = new UnsupportedObject { go = go };

        // Supported components (don’t warn)
        bool IsSupported(Component c)
        {
            if (c == null) return true;
            if (c is Transform) return true;

            // Allowlisted component full names (URP extras etc.)
            string fullName = c.GetType().FullName ?? c.GetType().Name;
            if (AllowedComponentFullNames.Contains(fullName))
                return true;

            // Export-supported
            if (c is MeshFilter) return true;
            if (c is MeshRenderer) return true;
            if (c is SkinnedMeshRenderer) return true;
            if (c is Collider) return true;
            if (c is Light) return true;

            // Allowlisted scripts
            if (c is MonoBehaviour mb)
            {
                string typeName = mb.GetType().Name;
                string fn = mb.GetType().FullName ?? typeName;
                return AllowedMonoBehaviours.Contains(typeName) || AllowedMonoBehaviours.Contains(fn);
            }

            return false;
        }

        // Categorize unsupported into user-friendly buckets
        string CategoryFor(Component c)
        {
            if (c == null) return "Unknown";

            string fn = c.GetType().FullName ?? "";
            if (fn == "UnityEngine.Rendering.Universal.UniversalAdditionalLightData")
                return "URP Light Data";

            if (c is Camera || c is AudioListener)
                return "Cameras";

            if (c is Canvas || c.GetType().Name.Contains("RectTransform"))
                return "UI / Canvas objects";

            // TMPro types (no direct reference needed)
            if (fn.Contains("TMPro"))
                return "UI / Canvas objects";

            if (c is ParticleSystem)
                return "Particle systems";

            if (c is Terrain)
                return "Terrain";

            if (c is VideoPlayer)
                return "Video players";

            if (c is Rigidbody)
                return "Physics components";

            // Any script not allowlisted becomes “Scripts”
            if (c is MonoBehaviour)
                return "Scripts";

            return c.GetType().Name + " components";
        }

        // Only flag objects that actually exist in scene (ignore hidden editor objects)
        var comps = go.GetComponents<Component>();
        foreach (var c in comps)
        {
            if (c == null) continue;
            if (IsSupported(c)) continue;

            u.unsupportedComponents.Add(c);
            u.reasons.Add(CategoryFor(c));
        }

        return u;
    }

    // =========================
    // Export
    // =========================
    static void DoExport(string zipPath, ScanResult scan, bool ignoreUnsupported)
    {
        // Complexity gate again (after potential cleanup)
        if (scan.TotalComplexity > COMPLEXITY_LIMIT)
        {
            ShowComplexityFail(scan, "Scene is too complex!");
            return;
        }

        // If still unsupported and not ignoring, fail
        if (!ignoreUnsupported && scan.UnsupportedObjects.Count > 0)
        {
            EditorUtility.DisplayDialog("Export Creation",
                "❌ Export failed: Unsupported Objects\n\n" +
                scan.UnsupportedObjects[0].ReasonSummary() + "\n\n" +
                scan.ComplexityLine(),
                "OK");
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "BlankCanvasExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            EditorUtility.DisplayProgressBar("Exporting", "Preparing export...", 0.05f);

            // Asset/material maps
            var meshAssetKey = new Dictionary<string, string>();    // mesh source id -> key
            var texAssetKey = new Dictionary<Texture, string>();    // texture -> key
            var matKey = new Dictionary<Material, string>();        // material -> key

            var assets = new List<AssetKvp>();
            var materials = new List<MaterialKvp>();
            var objects = new List<ObjectEntry>();
            var lights = new List<LightEntry>();

            int meshCounter = 0, texCounter = 0, matCounter = 0;

            int total = scan.Meshes.Count + scan.Colliders.Count + scan.Lights.Count;
            int step = 0;

            // --- Mesh objects ---
            foreach (var mi in scan.Meshes)
            {
                step++;
                EditorUtility.DisplayProgressBar("Exporting", $"Exporting mesh: {mi.name}", 0.05f + 0.70f * (step / Mathf.Max(1f, total)));

                string meshKey;
                if (!mi.isSkinned)
                {
                    string srcId = "MF:" + mi.mesh.GetInstanceID();
                    meshKey = GetOrWriteObjForMesh(mi.mesh, srcId, tempRoot, ref meshCounter, meshAssetKey, assets);
                }
                else
                {
                    var baked = new Mesh();
                    mi.smr.BakeMesh(baked);

                    string srcId = "SMR:" + mi.smr.GetInstanceID();
                    meshKey = GetOrWriteObjForMesh(baked, srcId, tempRoot, ref meshCounter, meshAssetKey, assets);
                }

                string materialKey = GetOrWriteMaterial(mi.mat, tempRoot, ref matCounter, ref texCounter, matKey, texAssetKey, assets, materials);

                var tr = mi.tr;
                var col = tr.GetComponent<Collider>();

                objects.Add(new ObjectEntry
                {
                    name = tr.name,
                    pos = V3(tr.position),
                    rot = Q4(tr.rotation),
                    scale = V3(tr.lossyScale),
                    active = tr.gameObject.activeSelf,

                    mesh = meshKey,
                    material = string.IsNullOrEmpty(materialKey) ? null : materialKey,

                    collider = ColliderToData(col, tempRoot, ref meshCounter, meshAssetKey, assets),
                });
            }

            // --- Collider-only objects (no mesh/light) ---
            foreach (var ci in scan.Colliders)
            {
                step++;
                EditorUtility.DisplayProgressBar("Exporting", $"Exporting collider: {ci.name}", 0.05f + 0.70f * (step / Mathf.Max(1f, total)));

                var tr = ci.tr;
                bool already = objects.Any(o => o != null && o.name == tr.name && AlmostSamePos(o.pos, tr.position));
                if (already) continue;

                objects.Add(new ObjectEntry
                {
                    name = tr.name,
                    pos = V3(tr.position),
                    rot = Q4(tr.rotation),
                    scale = V3(tr.lossyScale),
                    active = tr.gameObject.activeSelf,

                    mesh = null,
                    material = null,

                    collider = ColliderToData(ci.collider, tempRoot, ref meshCounter, meshAssetKey, assets),
                });
            }

            // --- Lights ---
            foreach (var li in scan.Lights)
            {
                step++;
                EditorUtility.DisplayProgressBar("Exporting", $"Exporting light: {li.name}", 0.05f + 0.70f * (step / Mathf.Max(1f, total)));

                var tr = li.tr;
                var l = li.light;

                lights.Add(new LightEntry
                {
                    name = tr.name,
                    pos = V3(tr.position),
                    rot = Q4(tr.rotation),
                    active = tr.gameObject.activeSelf,

                    type = LightTypeToString(l.type),
                    color = new[] { l.color.r, l.color.g, l.color.b, l.color.a },
                    intensity = l.intensity,
                    range = l.range,
                    spotAngle = l.type == LightType.Spot ? l.spotAngle : 0f,
                    shadows = ShadowsToString(l.shadows)
                });
            }

            // Write manifest
            EditorUtility.DisplayProgressBar("Exporting", "Writing manifest...", 0.80f);

            var manifest = new Manifest
            {
                formatVersion = 4,
                name = SceneManager.GetActiveScene().name,
                complexityUsed = scan.TotalComplexity,
                complexityLimit = COMPLEXITY_LIMIT,
                complexityPercent = (int)Math.Round((scan.TotalComplexity / (double)COMPLEXITY_LIMIT) * 100.0),

                complexityTriangles = scan.TriangleCount,
                complexityLights = scan.LightCount,
                complexityColliderObjects = scan.ColliderObjectCount,

                assets = assets.ToArray(),
                materials = materials.ToArray(),
                objects = objects.ToArray(),
                lights = lights.ToArray(),
            };

            File.WriteAllText(Path.Combine(tempRoot, "manifest.json"),
                JsonUtility.ToJson(manifest, true), Encoding.UTF8);

            // Zip
            EditorUtility.DisplayProgressBar("Exporting", "Zipping...", 0.92f);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            ZipFile.CreateFromDirectory(
                tempRoot,
                zipPath,
                System.IO.Compression.CompressionLevel.Optimal,
                false
            );

            EditorUtility.ClearProgressBar();
            EditorUtility.RevealInFinder(zipPath);

            // Final message
            string okMsg =
                "✅ Export succeeded.\n\n" +
                scan.ComplexityLine() + "\n\n" +
                scan.BreakdownLines();

            EditorUtility.DisplayDialog("Export Creation", okMsg, "OK");
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError(ex);

            EditorUtility.DisplayDialog("Export Creation",
                "❌ Export failed.\n\n" +
                ex.Message + "\n\n" +
                scan.ComplexityLine(),
                "OK");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    static void ShowComplexityFail(ScanResult scan, string reason)
    {
        string msg =
            $"❌ Export failed: {reason}\n\n" +
            scan.ComplexityLine() + "\n\n" +
            scan.BreakdownLines();

        EditorUtility.DisplayDialog("Export Creation", msg, "OK");
    }

    // =========================
    // Unsupported window (3 buttons)
    // =========================
    class UnsupportedObjectsWindow : EditorWindow
    {
        string zipPath;
        ScanResult scan;
        Vector2 scroll;

        public static void Open(string zipPath, ScanResult scan)
        {
            var w = CreateInstance<UnsupportedObjectsWindow>();
            w.titleContent = new GUIContent("Unsupported Objects");
            w.zipPath = zipPath;
            w.scan = scan;
            w.minSize = new Vector2(520, 360);
            w.ShowUtility();
        }

        void OnGUI()
        {
            if (scan == null)
            {
                Close();
                return;
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("❌ Export failed: Unsupported Objects", EditorStyles.boldLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                scan.UnsupportedObjects[0].ReasonSummary() + "\n\n" +
                scan.ComplexityLine() + "\n\n" +
                scan.BreakdownLines() + "\n\n" +
                $"Unsupported objects found: {scan.UnsupportedObjects.Count}",
                MessageType.Warning);

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Delete all", GUILayout.Height(34)))
                {
                    DeleteAllUnsupported();
                    Close();
                    ReScanAndExport();
                }

                if (GUILayout.Button("Ignore all", GUILayout.Height(34)))
                {
                    Close();
                    DoExport(zipPath, scan, ignoreUnsupported: true);
                }

                if (GUILayout.Button("Clean all", GUILayout.Height(34)))
                {
                    CleanAllUnsupportedComponents();
                    Close();
                    ReScanAndExport();
                }
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Objects (sample):", EditorStyles.boldLabel);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            int show = Mathf.Min(200, scan.UnsupportedObjects.Count);
            for (int i = 0; i < show; i++)
            {
                var u = scan.UnsupportedObjects[i];
                if (u == null || u.go == null) continue;

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.ObjectField(u.go, typeof(GameObject), true);
                    EditorGUILayout.LabelField(u.ReasonSummary(), EditorStyles.wordWrappedMiniLabel);
                }
            }
            if (scan.UnsupportedObjects.Count > show)
                EditorGUILayout.LabelField($"…and {scan.UnsupportedObjects.Count - show} more", EditorStyles.miniLabel);

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Cancel"))
                Close();
        }

        void DeleteAllUnsupported()
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            foreach (var u in scan.UnsupportedObjects)
            {
                if (u == null || u.go == null) continue;
                Undo.DestroyObjectImmediate(u.go);
            }

            Undo.CollapseUndoOperations(group);
        }

        void CleanAllUnsupportedComponents()
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();

            foreach (var u in scan.UnsupportedObjects)
            {
                if (u == null || u.go == null) continue;

                foreach (var c in u.unsupportedComponents)
                {
                    if (c == null) continue;
                    if (c is Transform) continue;
                    Undo.DestroyObjectImmediate(c);
                }
            }

            Undo.CollapseUndoOperations(group);
        }

        void ReScanAndExport()
        {
            var newScan = ScanScene();

            if (newScan.UnsupportedObjects.Count > 0)
            {
                Open(zipPath, newScan);
                return;
            }

            if (newScan.TotalComplexity > COMPLEXITY_LIMIT)
            {
                ShowComplexityFail(newScan, "Scene is too complex!");
                return;
            }

            DoExport(zipPath, newScan, ignoreUnsupported: false);
        }
    }

    // =========================
    // Manifest DTOs
    // =========================
    [Serializable] class Manifest
    {
        public int formatVersion = 4;
        public string name;

        public int complexityUsed;
        public int complexityLimit;
        public int complexityPercent;

        public int complexityTriangles;
        public int complexityLights;
        public int complexityColliderObjects;

        public AssetKvp[] assets;
        public MaterialKvp[] materials;

        public ObjectEntry[] objects;
        public LightEntry[] lights;
    }

    [Serializable] class AssetKvp { public string key; public AssetEntry value; }
    [Serializable] class AssetEntry { public string type; public string path; }

    [Serializable] class MaterialKvp { public string key; public MaterialEntry value; }
    [Serializable] class MaterialEntry
    {
        public string shader = "PBR_Lite";
        public string albedo;
        public string albedoFilter = "";
        public float[] tint;
        public float metallic = 0f;
        public float roughness = 1f;
    }

    [Serializable] class ObjectEntry
    {
        public string name;
        public float[] pos;
        public float[] rot;
        public float[] scale;
        public bool active;

        public string mesh;
        public string material;
        public ColliderData collider;
    }

    [Serializable] class ColliderData
    {
        public string type;
        public bool isTrigger;

        public float[] center;
        public float[] size;
        public float radius;
        public float height;
        public int direction;

        public bool convex;
        public string mesh;
    }

    [Serializable] class LightEntry
    {
        public string name;
        public float[] pos;
        public float[] rot;
        public bool active;

        public string type;
        public float[] color;
        public float intensity;
        public float range;
        public float spotAngle;
        public string shadows;
    }

    // =========================
    // Mesh + material + texture exporting
    // =========================
    static string GetOrWriteObjForMesh(
        Mesh mesh,
        string meshSourceId,
        string tempRoot,
        ref int meshCounter,
        Dictionary<string, string> meshAssetKey,
        List<AssetKvp> assets)
    {
        if (meshAssetKey.TryGetValue(meshSourceId, out var existing))
            return existing;

        string key = $"mesh_{meshCounter:0000}";
        meshCounter++;

        string safeName = SanitizeFileName(string.IsNullOrEmpty(mesh.name) ? "mesh" : mesh.name);
        if (string.IsNullOrEmpty(safeName)) safeName = key;

        string rel = $"meshes/{safeName}_{key}.obj";
        string abs = Path.Combine(tempRoot, rel);

        WriteMeshAsObj(mesh, abs);

        meshAssetKey[meshSourceId] = key;
        assets.Add(new AssetKvp { key = key, value = new AssetEntry { type = "mesh", path = rel } });
        return key;
    }

    static string GetOrWriteMaterial(
        Material mat,
        string tempRoot,
        ref int matCounter,
        ref int texCounter,
        Dictionary<Material, string> matKey,
        Dictionary<Texture, string> texAssetKey,
        List<AssetKvp> assets,
        List<MaterialKvp> materials)
    {
        if (mat == null) return "";

        if (matKey.TryGetValue(mat, out var existing))
            return existing;

        string mk = $"mat_{matCounter:0000}";
        matCounter++;
        matKey[mat] = mk;

        var entry = new MaterialEntry();

        Color tint = Color.white;
        bool hasTint = false;
        if (mat.HasProperty("_BaseColor")) { tint = mat.GetColor("_BaseColor"); hasTint = true; }
        else if (mat.HasProperty("_Color")) { tint = mat.GetColor("_Color"); hasTint = true; }
        if (hasTint) entry.tint = new[] { tint.r, tint.g, tint.b, tint.a };

        float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
        float smoothness = 0.5f;
        if (mat.HasProperty("_Smoothness")) smoothness = mat.GetFloat("_Smoothness");
        else if (mat.HasProperty("_Glossiness")) smoothness = mat.GetFloat("_Glossiness");
        entry.metallic = Mathf.Clamp01(metallic);
        entry.roughness = 1f - Mathf.Clamp01(smoothness);

        Texture tex = null;
        if (mat.HasProperty("_BaseMap")) tex = mat.GetTexture("_BaseMap");
        if (tex == null && mat.HasProperty("_MainTex")) tex = mat.GetTexture("_MainTex");

        if (tex != null)
        {
            string tk;
            if (!texAssetKey.TryGetValue(tex, out tk))
            {
                tk = $"tex_{texCounter:0000}";
                texCounter++;
                texAssetKey[tex] = tk;

                string safeTexName = SanitizeFileName(string.IsNullOrEmpty(tex.name) ? "tex" : tex.name);
                if (string.IsNullOrEmpty(safeTexName)) safeTexName = tk;

                string rel = $"textures/{safeTexName}_{tk}.png";
                string abs = Path.Combine(tempRoot, rel);

                var png = TextureToPngBytes(tex);
                if (png != null && png.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(abs));
                    File.WriteAllBytes(abs, png);
                    assets.Add(new AssetKvp { key = tk, value = new AssetEntry { type = "texture", path = rel } });
                }
            }

            entry.albedo = tk;
            entry.albedoFilter = GetFilterModeString(tex);
        }

        materials.Add(new MaterialKvp { key = mk, value = entry });
        return mk;
    }

    static byte[] TextureToPngBytes(Texture tex)
    {
        if (tex == null) return null;

        int w = Mathf.Max(2, tex.width);
        int h = Mathf.Max(2, tex.height);

        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;

        try
        {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;

            var t2d = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t2d.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t2d.Apply(false, false);

            return t2d.EncodeToPNG();
        }
        catch
        {
            return null;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    static string GetFilterModeString(Texture tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (!string.IsNullOrEmpty(path))
        {
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
                return FilterToString(ti.filterMode);
        }
        return FilterToString(tex.filterMode);
    }

    static string FilterToString(FilterMode fm)
    {
        switch (fm)
        {
            case FilterMode.Point: return "point";
            case FilterMode.Trilinear: return "trilinear";
            default: return "bilinear";
        }
    }

    // =========================
    // Collider export
    // =========================
    static ColliderData ColliderToData(
        Collider col,
        string tempRoot,
        ref int meshCounter,
        Dictionary<string, string> meshAssetKey,
        List<AssetKvp> assets)
    {
        if (col == null) return null;

        var data = new ColliderData
        {
            isTrigger = col.isTrigger
        };

        if (col is BoxCollider bc)
        {
            data.type = "box";
            data.center = V3(bc.center);
            data.size = V3(bc.size);
        }
        else if (col is SphereCollider sc)
        {
            data.type = "sphere";
            data.center = V3(sc.center);
            data.radius = sc.radius;
        }
        else if (col is CapsuleCollider cc)
        {
            data.type = "capsule";
            data.center = V3(cc.center);
            data.radius = cc.radius;
            data.height = cc.height;
            data.direction = cc.direction;
        }
        else if (col is MeshCollider mc)
        {
            data.type = "mesh";
            data.convex = mc.convex;
            data.center = V3(Vector3.zero);

            if (mc.sharedMesh != null)
            {
                string srcId = "MC:" + mc.sharedMesh.GetInstanceID();
                string key = GetOrWriteObjForMesh(mc.sharedMesh, srcId, tempRoot, ref meshCounter, meshAssetKey, assets);
                data.mesh = key;
            }
        }
        else
        {
            data.type = "none";
        }

        return data;
    }

    // =========================
    // OBJ writer
    // =========================
    static void WriteMeshAsObj(Mesh mesh, string outPath)
    {
        var sb = new StringBuilder(1 << 20);

        sb.AppendLine("# BlankCanvas OBJ Export");
        sb.AppendLine("o " + SanitizeFileName(string.IsNullOrEmpty(mesh.name) ? "mesh" : mesh.name));

        var v = mesh.vertices;
        var n = mesh.normals;
        var uv = mesh.uv;

        for (int i = 0; i < v.Length; i++)
        {
            var p = v[i];
            sb.AppendFormat(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", p.x, p.y, p.z);
        }

        bool hasUv = (uv != null && uv.Length == v.Length);
        if (hasUv)
        {
            for (int i = 0; i < uv.Length; i++)
            {
                var t = uv[i];
                sb.AppendFormat(CultureInfo.InvariantCulture, "vt {0} {1}\n", t.x, t.y);
            }
        }

        bool hasN = (n != null && n.Length == v.Length);
        if (hasN)
        {
            for (int i = 0; i < n.Length; i++)
            {
                var nn = n[i];
                sb.AppendFormat(CultureInfo.InvariantCulture, "vn {0} {1} {2}\n", nn.x, nn.y, nn.z);
            }
        }

        int[] tris = mesh.triangles;
        for (int i = 0; i < tris.Length; i += 3)
        {
            int a = tris[i] + 1;
            int b = tris[i + 1] + 1;
            int c = tris[i + 2] + 1;

            if (hasUv && hasN)
                sb.AppendFormat("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\n", a, b, c);
            else if (hasUv)
                sb.AppendFormat("f {0}/{0} {1}/{1} {2}/{2}\n", a, b, c);
            else if (hasN)
                sb.AppendFormat("f {0}//{0} {1}//{1} {2}//{2}\n", a, b, c);
            else
                sb.AppendFormat("f {0} {1} {2}\n", a, b, c);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
    }

    // =========================
    // Small utils
    // =========================
    static float[] V3(Vector3 v) => new[] { v.x, v.y, v.z };
    static float[] Q4(Quaternion q) => new[] { q.x, q.y, q.z, q.w };

    static bool AlmostSamePos(float[] p, Vector3 v)
    {
        if (p == null || p.Length < 3) return false;
        return Mathf.Abs(p[0] - v.x) < 0.0001f && Mathf.Abs(p[1] - v.y) < 0.0001f && Mathf.Abs(p[2] - v.z) < 0.0001f;
    }

    static string LightTypeToString(LightType t)
    {
        switch (t)
        {
            case LightType.Spot: return "spot";
            case LightType.Directional: return "directional";
            case LightType.Rectangle: return "area";
            default: return "point";
        }
    }

    static string ShadowsToString(LightShadows s)
    {
        switch (s)
        {
            case LightShadows.Hard: return "hard";
            case LightShadows.Soft: return "soft";
            default: return "none";
        }
    }

    static string Fmt(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Trim();
    }
}
#endif
