using System.IO;
using UnityEditor;
using UnityEngine;

namespace SunsetExpress.ArtEditor
{
    /// <summary>
    /// Terrain detay çimi için çapraz quad mesh'i + prefab üretir.
    ///
    /// NEDEN ARAÇ: <c>SunsetExpress/TerrainGrass</c> shader'ı mesh hakkında üç şey
    /// varsayıyor ve üçü de yanlış olduğunda SESSİZCE yanlış çalışıyor:
    ///   • taban <b>y = 0</b> — değilse boy varyasyonu çimi yere gömer veya havada bırakır
    ///   • <b>UV.y = 0 kökte, 1 uçta</b> — tersse rüzgar kökten eğer, uç çakılı kalır
    ///   • pivot tutamın <b>merkezinde</b> — atlas hücresi ve varyasyon tohumu buradan
    ///
    /// Blender'dan gelen bir mesh de olur; bu araç sadece üçünü garantiler.
    ///
    /// Sahiplik: Art alanı (Baran).
    /// </summary>
    public sealed class GrassQuadBuilder : EditorWindow
    {
        private const string DefaultFolder = "Assets/_Game/Art/Materials/Grass/Generated";

        private int _quadCount = 3;
        private float _width = 0.5f;
        private float _height = 0.5f;
        private bool _pivotCentered = true;
        private Material _material;
        private string _assetName = "Grass_CrossQuad";

        [MenuItem("Sunset Express/Art/Çim Mesh'i Üret", false, 100)]
        private static void Open()
        {
            var window = GetWindow<GrassQuadBuilder>(true, "Çim Mesh'i Üret");
            window.minSize = new Vector2(340f, 260f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Terrain → Paint Details → Add Detail Mesh için çapraz quad üretir.\n" +
                "Taban y = 0, UV.y = 0 kökte — TerrainGrass shader'ının beklediği düzen.",
                MessageType.Info);

            EditorGUILayout.Space();
            _assetName = EditorGUILayout.TextField("Asset adı", _assetName);
            _quadCount = EditorGUILayout.IntSlider("Quad sayısı", _quadCount, 1, 4);
            _width = EditorGUILayout.Slider("Genişlik (m)", _width, 0.05f, 3f);
            _height = EditorGUILayout.Slider("Yükseklik (m)", _height, 0.05f, 3f);
            _pivotCentered = EditorGUILayout.Toggle(
                new GUIContent("Pivot ortada", "Kapalıysa quad'lar tek yöne dizilir; çapraz düzende her zaman açık olmalı."),
                _pivotCentered);
            _material = (Material)EditorGUILayout.ObjectField("Materyal", _material, typeof(Material), false);

            EditorGUILayout.Space();

            if (_quadCount == 1)
            {
                EditorGUILayout.HelpBox(
                    "Tek quad yandan bakınca kaybolur. Cull Off olduğu için arkadan da görünür " +
                    "ama derinliği olmaz — 2 veya 3 önerilir.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_assetName)))
            {
                if (GUILayout.Button("Üret", GUILayout.Height(28f)))
                    Build();
            }
        }

        private void Build()
        {
            Directory.CreateDirectory(DefaultFolder);

            Mesh mesh = BuildMesh(_quadCount, _width, _height, _pivotCentered);
            mesh.name = _assetName;

            string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{DefaultFolder}/{_assetName}.asset");
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject(_assetName);
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                // Çim kendi gölgesini yazmaz — shader'da ShadowCaster pass'i yok.
                // Renderer'da açık bırakmak Unity'yi var olmayan bir pass'i aramaya
                // iter; kapatmak hem doğru hem ucuz.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = true;

                string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{DefaultFolder}/{_assetName}.prefab");
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);

                AssetDatabase.SaveAssets();
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);

                Debug.Log($"[GrassQuadBuilder] Üretildi: {prefabPath}\n" +
                          "Terrain → Paint Details → Add Detail Mesh → bu prefab'ı seç, " +
                          "\"Use GPU Instancing\" AÇIK olmalı.", prefab);
            }
            finally
            {
                DestroyImmediate(go);
            }
        }

        /// <summary>
        /// N adet, Y ekseni etrafında eşit açılarla dizilmiş dikey quad.
        /// Taban y = 0, tepe y = height. UV: (0,0) sol alt, (1,1) sağ üst.
        /// </summary>
        private static Mesh BuildMesh(int quadCount, float width, float height, bool pivotCentered)
        {
            int vertexCount = quadCount * 4;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var triangles = new int[quadCount * 6];

            float half = width * 0.5f;
            float offset = pivotCentered ? 0f : half;

            for (int q = 0; q < quadCount; q++)
            {
                float angle = Mathf.PI * q / quadCount;
                var right = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                // Yüz normali quad düzlemine dik. Shader bunu _NormalUpBlend ile
                // dünya yukarısına harmanlıyor; ham normali burada bozmuyoruz ki
                // harman miktarı materyalden ayarlanabilsin.
                Vector3 normal = Vector3.Cross(right, Vector3.up).normalized;

                Vector3 center = right * offset;
                int v = q * 4;

                vertices[v + 0] = center - right * half;                        // sol alt
                vertices[v + 1] = center + right * half;                        // sağ alt
                vertices[v + 2] = center - right * half + Vector3.up * height;  // sol üst
                vertices[v + 3] = center + right * half + Vector3.up * height;  // sağ üst

                uvs[v + 0] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(1f, 0f);
                uvs[v + 2] = new Vector2(0f, 1f);
                uvs[v + 3] = new Vector2(1f, 1f);

                for (int i = 0; i < 4; i++)
                    normals[v + i] = normal;

                int t = q * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 2;
                triangles[t + 2] = v + 1;
                triangles[t + 3] = v + 2;
                triangles[t + 4] = v + 3;
                triangles[t + 5] = v + 1;
            }

            var mesh = new Mesh
            {
                vertices = vertices,
                normals = normals,
                uv = uvs,
                triangles = triangles
            };

            // Rüzgar vertex'leri kaydırıyor; bounds'u biraz şişirmezsek çim
            // ekran kenarında erken culling'e girer ve bir anda kaybolur.
            Bounds bounds = mesh.bounds;
            bounds.Expand(Mathf.Max(width, height) * 0.5f);
            mesh.bounds = bounds;

            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
