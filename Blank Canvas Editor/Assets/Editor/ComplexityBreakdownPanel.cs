// Assets/Editor/ComplexityBreakdownWindow.cs
//
// Tools > Creation > Complexity Breakdown
//
// FIX: allowlists URP additional components so they don't count as "Unsupported".
// (Same approach as exporter: match by type FullName string, no URP reference required.)

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

public class ComplexityBreakdownWindow : EditorWindow
{
    // =========================
    // Complexity config (must match exporter)
    // =========================
    const int COMPLEXITY_LIMIT = 100_000;
    const int LIGHT_COST = 100;
    const int TRIANGLE_COST = 1;
    const int COLLIDER_OBJECT_COST = 4;

    // URP allowlist (prevents your Directional Light from being flagged because of UniversalAdditionalLightData)
    static readonly HashSet<string> AllowedComponentFullNames = new HashSet<string>()
    {
        "UnityEngine.Rendering.Universal.UniversalAdditionalLightData",
        "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData",
        "UnityEngine.Rendering.Universal.UniversalAdditionalReflectionData",
        "UnityEngine.Rendering.Universal.UniversalAdditionalRenderingData",
    };

    // UI behavior
    const double AUTO_REFRESH_SECONDS = 0.5;
    const float SHOW_TOP_MESHES_MIN_HEIGHT = 420f;
    const int TOP_MESHES_COUNT = 10;
    const int TOP_UNSUPPORTED_CATS = 5;

    // Cached results
    ScanResult _scan;
    double _nextRefreshAt;
    Vector2 _scroll;

    [MenuItem("Tools/Creation/Complexity Breakdown")]
    public static void Open()
    {
        var w = GetWindow<ComplexityBreakdownWindow>("Complexity Breakdown");
        w.minSize = new Vector2(360, 260);
        w.Show();
    }

    void OnEnable()
    {
        _nextRefreshAt = 0;
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.hierarchyChanged += MarkDirty;
        EditorApplication.projectChanged += MarkDirty;
        MarkDirty();
    }

    void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.hierarchyChanged -= MarkDirty;
        EditorApplication.projectChanged -= MarkDirty;
    }

    void MarkDirty()
    {
        _nextRefreshAt = 0;
        Repaint();
    }

    void OnEditorUpdate()
    {
        if (EditorApplication.timeSinceStartup >= _nextRefreshAt)
        {
            _nextRefreshAt = EditorApplication.timeSinceStartup + AUTO_REFRESH_SECONDS;
            _scan = ScanNow();
            Repaint();
        }
    }

    void OnGUI()
    {
        if (_scan == null)
            _scan = ScanNow();

        using (new EditorGUILayout.VerticalScope())
        {
            DrawHeader();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawLine1();

            EditorGUILayout.Space(10);

            if (position.height >= SHOW_TOP_MESHES_MIN_HEIGHT)
                DrawTopMeshes();

            EditorGUILayout.EndScrollView();
        }
    }

    void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("Live scan (includes inactive, all loaded scenes)", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
            {
                _scan = ScanNow();
                Repaint();
            }
        }
    }

    void DrawLine1()
    {
        var used = _scan.totalComplexity;
        var pct = PercentInt(used, COMPLEXITY_LIMIT);

        var mainLine = $"{Fmt(used)}/{Fmt(COMPLEXITY_LIMIT)} Complexity ({pct}%)";

        bool over = used > COMPLEXITY_LIMIT;
        bool hasUnsupported = _scan.unsupportedObjectCount > 0;

        var status = over
            ? "⚠ OVER LIMIT"
            : (hasUnsupported ? "⚠ UNSUPPORTED OBJECTS PRESENT" : "✅ OK");

        EditorGUILayout.LabelField(mainLine, BoldBig());
        EditorGUILayout.LabelField(status, EditorStyles.boldLabel);

        EditorGUILayout.Space(6);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField($"Triangles: {Fmt(_scan.triangleCount)}  (+{Fmt(_scan.triangleComplexity)})");
            EditorGUILayout.LabelField($"Lights: {Fmt(_scan.lightCount)}  (+{Fmt(_scan.lightComplexity)})");
            EditorGUILayout.LabelField($"Colliders: {Fmt(_scan.colliderObjectCount)}  (+{Fmt(_scan.colliderComplexity)})");
        }

        if (hasUnsupported)
        {
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField($"Unsupported Objects: {_scan.unsupportedObjectCount}", EditorStyles.boldLabel);

                var top = _scan.unsupportedCategoryCounts
                    .OrderByDescending(kv => kv.Value)
                    .Take(TOP_UNSUPPORTED_CATS)
                    .ToList();

                foreach (var kv in top)
                    EditorGUILayout.LabelField($"• {kv.Key}: {kv.Value}");

                if (_scan.unsupportedCategoryCounts.Count > TOP_UNSUPPORTED_CATS)
                    EditorGUILayout.LabelField("• …", EditorStyles.miniLabel);
            }
        }
    }

    void DrawTopMeshes()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Top Highest-Poly Meshes", EditorStyles.boldLabel);

            if (_scan.topMeshes.Count == 0)
            {
                EditorGUILayout.LabelField("(No meshes found)", EditorStyles.miniLabel);
                return;
            }

            for (int i = 0; i < _scan.topMeshes.Count; i++)
            {
                var m = _scan.topMeshes[i];
                string inst = m.instances > 1 ? $" (x{m.instances})" : "";
                EditorGUILayout.LabelField($"#{i + 1} {m.displayName}{inst} — {Fmt(m.triangles)} tris");
            }
        }
    }

    // =========================
    // Scan implementation
    // =========================
    class TopMesh
    {
        public string displayName;
        public int triangles;
        public int instances;
    }

    class ScanResult
    {
        public int triangleCount;
        public int lightCount;
        public int colliderObjectCount;

        public int triangleComplexity => triangleCount * TRIANGLE_COST;
        public int lightComplexity => lightCount * LIGHT_COST;
        public int colliderComplexity => colliderObjectCount * COLLIDER_OBJECT_COST;

        public int totalComplexity => triangleComplexity + lightComplexity + colliderComplexity;

        public int unsupportedObjectCount;
        public Dictionary<string, int> unsupportedCategoryCounts = new Dictionary<string, int>();

        public List<TopMesh> topMeshes = new List<TopMesh>();
    }

    ScanResult ScanNow()
    {
        var res = new ScanResult();

        var colliderObjectSet = new HashSet<int>();
        var meshAgg = new Dictionary<string, (string label, int tris, int count)>();
        var unsupportedSeen = new HashSet<int>();

        var allGOs = Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(go =>
                go != null &&
                go.scene.IsValid() &&
                go.scene.isLoaded &&
                !EditorUtility.IsPersistent(go) &&
                (go.hideFlags == HideFlags.None || go.hideFlags == HideFlags.NotEditable))
            .ToList();

        foreach (var go in allGOs)
        {
            var light = go.GetComponent<Light>();
            if (light != null)
                res.lightCount++;

            if (go.GetComponent<Collider>() != null)
            {
                int id = go.GetInstanceID();
                if (colliderObjectSet.Add(id))
                    res.colliderObjectCount++;
            }

            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf != null && mr != null && mf.sharedMesh != null)
            {
                int tris = SafeTriangleCount(mf.sharedMesh);
                res.triangleCount += tris;

                string meshName = string.IsNullOrEmpty(mf.sharedMesh.name) ? "Mesh" : mf.sharedMesh.name;
                string key = $"{meshName}|{tris}";
                if (!meshAgg.TryGetValue(key, out var e))
                    meshAgg[key] = ($"{go.name}", tris, 1);
                else
                    meshAgg[key] = (e.label, e.tris, e.count + 1);
            }

            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                int tris = SafeTriangleCount(smr.sharedMesh);
                res.triangleCount += tris;

                string meshName = string.IsNullOrEmpty(smr.sharedMesh.name) ? "SkinnedMesh" : smr.sharedMesh.name;
                string key = $"{meshName}|{tris}";
                if (!meshAgg.TryGetValue(key, out var e))
                    meshAgg[key] = ($"{go.name}", tris, 1);
                else
                    meshAgg[key] = (e.label, e.tris, e.count + 1);
            }

            var cats = GetUnsupportedCategories(go);
            if (cats.Count > 0)
            {
                int id = go.GetInstanceID();
                if (unsupportedSeen.Add(id))
                {
                    res.unsupportedObjectCount++;
                    foreach (var c in cats)
                    {
                        if (!res.unsupportedCategoryCounts.ContainsKey(c))
                            res.unsupportedCategoryCounts[c] = 1;
                        else
                            res.unsupportedCategoryCounts[c]++;
                    }
                }
            }
        }

        res.topMeshes = meshAgg.Values
            .OrderByDescending(v => v.tris)
            .Take(TOP_MESHES_COUNT)
            .Select(v => new TopMesh
            {
                displayName = v.label,
                triangles = v.tris,
                instances = v.count
            })
            .ToList();

        return res;
    }

    static int SafeTriangleCount(Mesh m)
    {
        try
        {
            if (m == null) return 0;
            var tris = m.triangles;
            return (tris == null) ? 0 : (tris.Length / 3);
        }
        catch { return 0; }
    }

    static List<string> GetUnsupportedCategories(GameObject go)
    {
        var cats = new HashSet<string>();
        var comps = go.GetComponents<Component>();

        foreach (var c in comps)
        {
            if (c == null) continue;

            // Supported
            if (c is Transform) continue;
            if (c is MeshFilter) continue;
            if (c is MeshRenderer) continue;
            if (c is SkinnedMeshRenderer) continue;
            if (c is Collider) continue;
            if (c is Light) continue;

            // Allowed extra components (URP “additional” data etc.)
            string fn = c.GetType().FullName ?? c.GetType().Name;
            if (AllowedComponentFullNames.Contains(fn))
                continue;

            cats.Add(CategoryFor(c));
        }

        return cats.ToList();
    }

    static string CategoryFor(Component c)
    {
        string fn = c.GetType().FullName ?? "";

        if (fn == "UnityEngine.Rendering.Universal.UniversalAdditionalLightData")
            return "URP Light Data";

        if (c is Camera || c is AudioListener) return "Cameras";
        if (c is Canvas) return "UI / Canvas objects";
        if (fn.Contains("TMPro")) return "UI / Canvas objects";
        if (c is ParticleSystem) return "Particle systems";
        if (c is Terrain) return "Terrain";
        if (fn.Contains("UnityEngine.Video") || c.GetType().Name.Contains("Video")) return "Video players";
        if (c is Rigidbody) return "Physics components";
        if (c is MonoBehaviour) return "Scripts";

        return c.GetType().Name + " components";
    }

    // =========================
    // Formatting / styles
    // =========================
    static string Fmt(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static int PercentInt(int used, int limit)
    {
        if (limit <= 0) return 0;
        return (int)Math.Round((used / (double)limit) * 100.0);
    }

    static GUIStyle BoldBig()
    {
        var s = new GUIStyle(EditorStyles.boldLabel);
        s.fontSize = 14;
        return s;
    }
}
#endif
