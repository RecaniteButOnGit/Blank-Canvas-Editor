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

public static class CreationExporter
{
    [MenuItem("Tools/Creation/Export Creation")]
    public static void ExportCreation()
    {
        // Save location
        string defaultName = SanitizeFileName(EditorSceneManager.GetActiveScene().name);
        if (string.IsNullOrEmpty(defaultName)) defaultName = "BlankCanvasCreation";

        string zipPath = EditorUtility.SaveFilePanel(
            "Save Creation Zip",
            "",
            defaultName + ".zip",
            "zip");

        if (string.IsNullOrEmpty(zipPath))
            return;

        // Always-on settings (per your request)
        const bool INCLUDE_COLLIDERS = true;
        const bool EXPORT_TEXTURES = true;

        // Temp folder
        string tempRoot = Path.Combine(Path.GetTempPath(), "BlankCanvasExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            EditorUtility.DisplayProgressBar("Exporting", "Collecting scene objects...", 0.05f);

            // Find ALL renderers in ALL LOADED scenes, including inactive.
            // FindObjectsOfTypeAll includes disabled objects; filter out assets/prefabs.
            var allRenderers = Resources.FindObjectsOfTypeAll<Renderer>()
                .Where(r =>
                    r != null &&
                    !EditorUtility.IsPersistent(r) &&
                    r.gameObject.scene.IsValid() &&
                    r.gameObject.scene.isLoaded)
                .ToList();

            var meshPairs = allRenderers
                .OfType<MeshRenderer>()
                .Select(r => new { r, mf = r.GetComponent<MeshFilter>() })
                .Where(x => x.r != null && x.mf != null && x.mf.sharedMesh != null)
                .ToList();

            var skinned = allRenderers
                .OfType<SkinnedMeshRenderer>()
                .Where(sr => sr != null && sr.sharedMesh != null)
                .ToList();

            if (meshPairs.Count == 0 && skinned.Count == 0)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Export Creation", "No MeshRenderers/SkinnedMeshRenderers found in loaded scenes.", "OK");
                return;
            }

            // Maps/caches
            var meshKey = new Dictionary<string, string>();   // mesh source id -> assetKey
            var texKey = new Dictionary<Texture, string>();   // texture -> assetKey
            var matKey = new Dictionary<Material, string>();  // material -> materialKey

            var assets = new List<AssetKvp>();
            var materials = new List<MaterialKvp>();
            var objects = new List<ObjectEntry>();

            int meshCounter = 0;
            int texCounter = 0;
            int matCounter = 0;

            // Bounds
            var bounds = ComputeWorldBounds(allRenderers);
            var boundsData = new BoundsData
            {
                center = new[] { bounds.center.x, bounds.center.y, bounds.center.z },
                size = new[] { bounds.size.x, bounds.size.y, bounds.size.z }
            };

            int total = meshPairs.Count + skinned.Count;
            int index = 0;

            // MeshRenderer exports
            foreach (var x in meshPairs)
            {
                index++;
                float p = 0.10f + 0.70f * (index / Mathf.Max(1f, total));
                EditorUtility.DisplayProgressBar("Exporting", $"Processing {x.r.name} ({index}/{total})", p);

                var tr = x.r.transform;

                string meshSourceId = "MF:" + x.mf.sharedMesh.GetInstanceID();
                string mKey = GetOrExportMesh(x.mf.sharedMesh, meshSourceId, tempRoot, ref meshCounter, meshKey, assets);

                string matK = GetOrExportMaterial(
                    x.r.sharedMaterial,
                    tempRoot,
                    EXPORT_TEXTURES,
                    ref matCounter,
                    ref texCounter,
                    matKey,
                    texKey,
                    assets,
                    materials);

                (string colType, bool isTrig) = GetColliderInfo(tr, INCLUDE_COLLIDERS);

                objects.Add(new ObjectEntry
                {
                    name = tr.name,
                    mesh = mKey,
                    material = string.IsNullOrEmpty(matK) ? null : matK,
                    pos = new[] { tr.position.x, tr.position.y, tr.position.z },
                    rot = new[] { tr.rotation.x, tr.rotation.y, tr.rotation.z, tr.rotation.w },
                    scale = new[] { tr.lossyScale.x, tr.lossyScale.y, tr.lossyScale.z },
                    collider = colType,
                    isTrigger = isTrig,

                    // Preserve original per-object active toggle
                    active = tr.gameObject.activeSelf
                });
            }

            // SkinnedMeshRenderer exports (baked)
            foreach (var sr in skinned)
            {
                index++;
                float p = 0.10f + 0.70f * (index / Mathf.Max(1f, total));
                EditorUtility.DisplayProgressBar("Exporting", $"Baking {sr.name} ({index}/{total})", p);

                var baked = new Mesh();
                sr.BakeMesh(baked);

                string meshSourceId = "SMR:" + sr.GetInstanceID();
                string mKey = GetOrExportMesh(baked, meshSourceId, tempRoot, ref meshCounter, meshKey, assets);

                string matK = GetOrExportMaterial(
                    sr.sharedMaterial,
                    tempRoot,
                    EXPORT_TEXTURES,
                    ref matCounter,
                    ref texCounter,
                    matKey,
                    texKey,
                    assets,
                    materials);

                var tr = sr.transform;
                (string colType, bool isTrig) = GetColliderInfo(tr, INCLUDE_COLLIDERS);

                objects.Add(new ObjectEntry
                {
                    name = tr.name,
                    mesh = mKey,
                    material = string.IsNullOrEmpty(matK) ? null : matK,
                    pos = new[] { tr.position.x, tr.position.y, tr.position.z },
                    rot = new[] { tr.rotation.x, tr.rotation.y, tr.rotation.z, tr.rotation.w },
                    scale = new[] { tr.lossyScale.x, tr.lossyScale.y, tr.lossyScale.z },
                    collider = colType,
                    isTrigger = isTrig,
                    active = tr.gameObject.activeSelf
                });
            }

            // Write manifest.json
            EditorUtility.DisplayProgressBar("Exporting", "Writing manifest...", 0.85f);

            var manifest = new ManifestWrap
            {
                formatVersion = 3,
                name = defaultName,
                bounds = boundsData,
                assets = assets.ToArray(),
                materials = materials.ToArray(),
                objects = objects.ToArray()
            };

            File.WriteAllText(Path.Combine(tempRoot, "manifest.json"),
                JsonUtility.ToJson(manifest, true), Encoding.UTF8);

            // Zip
            EditorUtility.DisplayProgressBar("Exporting", "Zipping...", 0.95f);

            if (File.Exists(zipPath)) File.Delete(zipPath);

            ZipFile.CreateFromDirectory(
                tempRoot,
                zipPath,
                System.IO.Compression.CompressionLevel.Optimal, // fully qualified to avoid ambiguity
                false
            );

            EditorUtility.ClearProgressBar();

            int meshCount = assets.Count(a => a.value != null && a.value.type == "mesh");
            int texCount = assets.Count(a => a.value != null && a.value.type == "texture");

            EditorUtility.RevealInFinder(zipPath);
            EditorUtility.DisplayDialog(
                "Export Creation",
                $"Export complete!\n\nObjects: {objects.Count}\nMeshes: {meshCount}\nTextures: {texCount}",
                "OK");
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError(ex);
            EditorUtility.DisplayDialog("Export Creation", "Export failed:\n" + ex.Message, "OK");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // ==========================
    // Manifest DTOs
    // ==========================
    [Serializable] class ManifestWrap
    {
        public int formatVersion = 3;
        public string name;
        public BoundsData bounds;

        public AssetKvp[] assets;
        public MaterialKvp[] materials;
        public ObjectEntry[] objects;
    }

    [Serializable] class BoundsData { public float[] center; public float[] size; }

    [Serializable] class AssetKvp { public string key; public AssetEntry value; }
    [Serializable] class MaterialKvp { public string key; public MaterialEntry value; }

    [Serializable] class AssetEntry { public string type; public string path; } // mesh/texture

    [Serializable] class MaterialEntry
    {
        public string shader = "PBR_Lite";
        public string albedo;             // texture asset key
        public string albedoFilter = "";  // point/bilinear/trilinear
        public float[] tint;              // RGBA
        public float metallic = 0f;
        public float roughness = 1f;
    }

    [Serializable] class ObjectEntry
    {
        public string name;
        public string mesh;
        public string material;
        public float[] pos;
        public float[] rot;
        public float[] scale;
        public string collider;
        public bool isTrigger;

        public bool active; // preserve inactive objects
    }

    // ==========================
    // Export helpers
    // ==========================
    static string GetOrExportMesh(
        Mesh mesh,
        string meshSourceId,
        string tempRoot,
        ref int meshCounter,
        Dictionary<string, string> meshKey,
        List<AssetKvp> assets)
    {
        if (meshKey.TryGetValue(meshSourceId, out var existing))
            return existing;

        string mKey = $"mesh_{meshCounter:0000}";
        meshCounter++;

        string objName = SanitizeFileName(string.IsNullOrEmpty(mesh.name) ? "mesh" : mesh.name);
        if (string.IsNullOrEmpty(objName)) objName = mKey;

        string objRel = $"meshes/{objName}_{mKey}.obj";
        string objAbs = Path.Combine(tempRoot, objRel);

        WriteMeshAsObj(mesh, objAbs);

        meshKey[meshSourceId] = mKey;
        assets.Add(new AssetKvp { key = mKey, value = new AssetEntry { type = "mesh", path = objRel } });

        return mKey;
    }

    static string GetOrExportMaterial(
        Material mat,
        string tempRoot,
        bool exportTextures,
        ref int matCounter,
        ref int texCounter,
        Dictionary<Material, string> matKey,
        Dictionary<Texture, string> texKey,
        List<AssetKvp> assets,
        List<MaterialKvp> materials)
    {
        if (mat == null) return "";

        if (matKey.TryGetValue(mat, out var mk))
            return mk;

        string matK = $"mat_{matCounter:0000}";
        matCounter++;
        matKey[mat] = matK;

        var entry = BuildMaterialEntry(mat, exportTextures, tempRoot, ref texCounter, texKey, assets);

        materials.Add(new MaterialKvp { key = matK, value = entry });
        return matK;
    }

    static MaterialEntry BuildMaterialEntry(
        Material mat,
        bool exportTextures,
        string tempRoot,
        ref int texCounter,
        Dictionary<Texture, string> texKey,
        List<AssetKvp> assets)
    {
        var entry = new MaterialEntry();

        // Tint (URP: _BaseColor, Standard: _Color)
        Color tint = Color.white;
        bool hasTint = false;
        if (mat.HasProperty("_BaseColor")) { tint = mat.GetColor("_BaseColor"); hasTint = true; }
        else if (mat.HasProperty("_Color")) { tint = mat.GetColor("_Color"); hasTint = true; }

        if (hasTint)
            entry.tint = new[] { tint.r, tint.g, tint.b, tint.a };

        // Metallic / smoothness -> roughness
        float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
        float smoothness = 0.5f;
        if (mat.HasProperty("_Smoothness")) smoothness = mat.GetFloat("_Smoothness");
        else if (mat.HasProperty("_Glossiness")) smoothness = mat.GetFloat("_Glossiness");

        entry.metallic = Mathf.Clamp01(metallic);
        entry.roughness = 1f - Mathf.Clamp01(smoothness);

        // Albedo texture + filter mode (point support)
        if (exportTextures)
        {
            Texture tex = null;
            if (mat.HasProperty("_BaseMap")) tex = mat.GetTexture("_BaseMap");
            if (tex == null && mat.HasProperty("_MainTex")) tex = mat.GetTexture("_MainTex");

            if (tex != null)
            {
                if (!texKey.TryGetValue(tex, out string tKey))
                {
                    tKey = $"tex_{texCounter:0000}";
                    texCounter++;
                    texKey[tex] = tKey;

                    string safeName = SanitizeFileName(string.IsNullOrEmpty(tex.name) ? "tex" : tex.name);
                    if (string.IsNullOrEmpty(safeName)) safeName = tKey;

                    string pngRel = $"textures/{safeName}_{tKey}.png";
                    string pngAbs = Path.Combine(tempRoot, pngRel);

                    byte[] png = TextureToPngBytes(tex);
                    if (png != null && png.Length > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(pngAbs));
                        File.WriteAllBytes(pngAbs, png);

                        assets.Add(new AssetKvp { key = tKey, value = new AssetEntry { type = "texture", path = pngRel } });

                        entry.albedo = tKey;
                        entry.albedoFilter = GetFilterModeString(tex);
                    }
                }
                else
                {
                    entry.albedo = tKey;
                    entry.albedoFilter = GetFilterModeString(tex);
                }
            }
        }

        return entry;
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

    static (string, bool) GetColliderInfo(Transform tr, bool includeColliders)
    {
        if (!includeColliders) return ("none", false);

        var col = tr.GetComponent<Collider>();
        if (col == null) return ("none", false);

        bool isTrig = col.isTrigger;
        if (col is BoxCollider) return ("box", isTrig);
        if (col is SphereCollider) return ("sphere", isTrig);
        if (col is CapsuleCollider) return ("capsule", isTrig);
        if (col is MeshCollider) return ("mesh", isTrig);
        return ("none", isTrig);
    }

    static Bounds ComputeWorldBounds(List<Renderer> renderers)
    {
        var rs = renderers.Where(r => r != null).ToList();
        if (rs.Count == 0) return new Bounds(Vector3.zero, Vector3.one);

        var b = rs[0].bounds;
        for (int i = 1; i < rs.Count; i++)
            b.Encapsulate(rs[i].bounds);
        return b;
    }

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

    static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Trim();
    }
}
#endif
