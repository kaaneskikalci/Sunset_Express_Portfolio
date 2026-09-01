using UnityEditor;
using UnityEngine;

namespace SunsetExpress.ArtEditor
{
    /// <summary>
    /// <c>SunsetExpress/CharacterSkin</c> materyal editörü.
    ///
    /// NEDEN KENDİ GUI'MİZ VAR: shader URP Lit'ten türetildi ama URP'nin kendi
    /// inspector sınıfı (<c>UnityEditor.Rendering.Universal.ShaderGUI.LitShader</c>)
    /// <b>internal</b> — miras alınamıyor. CustomEditor'ı tamamen kaldırmak da
    /// çalışmıyordu: URP Lit'in yarısı KEYWORD'e bağlı (normal map atarsın,
    /// <c>_NORMALMAP</c> açılmadığı için hiçbir şey olmaz). Bu sınıf iki işi
    /// yapar: özelleştirme alanlarını okunur biçimde çizer ve keyword'leri
    /// atanan dokulardan türetir.
    ///
    /// Sahiplik: Art alanı (Baran).
    /// </summary>
    public sealed class CharacterSkinShaderGUI : ShaderGUI
    {
        private static readonly GUIContent BaseMapLabel = new("Albedo (nötr çiz)", "Ten ve kumaş bölgeleri BEYAZ/GRİ olmalı — renk oyundan gelir. Buraya renk boyarsan tint'le çarpılır ve çamurlaşır.");
        private static readonly GUIContent MaskLabel = new("Bölge Maskesi (RGB)", "R = ten/ana gövde, G = kumaş A, B = kumaş B. Kanallar üst üste binmez; binerse B > G > R.");
        private static readonly GUIContent FaceMapLabel = new("Yüz Atlası", "Kafa UV'sinde çizilmiş, ızgaraya dizilmiş yüz varyantları. ALFA = nerede yüz var. Alfa 0 olan yerde ten rengi görünür.");
        private static readonly GUIContent FaceGridLabel = new("Izgara (sütun, satır)", "3x3 = 9 yüz. Index 0 sol üsttür, satır satır sağa doğru gider.");

        private bool _surfaceFoldout = true;
        private bool _customizeFoldout = true;
        private bool _faceFoldout = true;
        private bool _advancedFoldout;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            var material = materialEditor.target as Material;
            if (material == null)
                return;

            EditorGUI.BeginChangeCheck();

            _surfaceFoldout = EditorGUILayout.Foldout(_surfaceFoldout, "Yüzey", true);
            if (_surfaceFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    TextureWithColor(materialEditor, properties, "_BaseMap", "_BaseColor", BaseMapLabel);
                    TextureWithFloat(materialEditor, properties, "_MetallicGlossMap", "_Metallic", new GUIContent("Metallic (A = smoothness)"));
                    Slider(materialEditor, properties, "_Smoothness", "Smoothness");
                    TextureWithFloat(materialEditor, properties, "_BumpMap", "_BumpScale", new GUIContent("Normal Map"));
                    TextureWithFloat(materialEditor, properties, "_OcclusionMap", "_OcclusionStrength", new GUIContent("Occlusion"));
                    TileOffset(materialEditor, properties, "_BaseMap");
                }
            }

            EditorGUILayout.Space();
            _customizeFoldout = EditorGUILayout.Foldout(_customizeFoldout, "Özelleştirme — bölge renkleri", true);
            if (_customizeFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Texture(materialEditor, properties, "_MaskMap", MaskLabel);
                    Color(materialEditor, properties, "_ColorSkin", "Ten / Ana renk");
                    Color(materialEditor, properties, "_ColorClothA", "Kumaş A");
                    Color(materialEditor, properties, "_ColorClothB", "Kumaş B");

                    if (material.GetTexture("_MaskMap") == null)
                    {
                        EditorGUILayout.HelpBox(
                            "Maske atanmadı — bölge renkleri hiçbir şey yapmaz. " +
                            "Bu güvenli varsayılan: materyal stok URP Lit gibi davranır.",
                            MessageType.Info);
                    }
                }
            }

            EditorGUILayout.Space();
            _faceFoldout = EditorGUILayout.Foldout(_faceFoldout, "Yüz overlay", true);
            if (_faceFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Toggle(materialEditor, properties, "_FaceOverlay", "Yüz overlay açık");

                    bool wantsFace = material.GetFloat("_FaceOverlay") > 0.5f;
                    using (new EditorGUI.DisabledScope(!wantsFace))
                    {
                        Texture(materialEditor, properties, "_FaceMap", FaceMapLabel);
                        Vector(materialEditor, properties, "_FaceGrid", FaceGridLabel);
                        FaceIndexField(materialEditor, properties, material);
                        Slider(materialEditor, properties, "_FaceOpacity", "Opaklık");
                    }

                    if (wantsFace && material.GetTexture("_FaceMap") == null)
                    {
                        EditorGUILayout.HelpBox(
                            "Overlay açık ama atlas atanmadı. Unity'nin varsayılan \"black\" dokusunun " +
                            "ALFASI 1'dir — çizilseydi karakter tamamen siyaha boyanırdı. " +
                            "Bu yüzden atlas atanana kadar keyword kapalı tutuluyor.",
                            MessageType.Warning);
                    }
                }
            }

            EditorGUILayout.Space();
            _advancedFoldout = EditorGUILayout.Foldout(_advancedFoldout, "Gelişmiş", true);
            if (_advancedFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    Float(materialEditor, properties, "_Cull", "Cull (0 kapalı, 1 ön, 2 arka)");
                    Toggle(materialEditor, properties, "_AlphaClip", "Alpha clip");
                    if (material.GetFloat("_AlphaClip") > 0.5f)
                        Slider(materialEditor, properties, "_Cutoff", "Cutoff");
                    Toggle(materialEditor, properties, "_ReceiveShadows", "Gölge alsın");

                    materialEditor.EnableInstancingField();
                    materialEditor.DoubleSidedGIField();
                    materialEditor.RenderQueueField();
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                foreach (Object target in materialEditor.targets)
                {
                    if (target is Material m)
                        SyncKeywords(m);
                }
            }
        }

        /// <summary>
        /// Shader değiştirilerek bu materyale geçildiğinde de keyword'ler doğru kurulsun.
        /// (Inspector hiç açılmadan script'ten materyal üretilen yol.)
        /// </summary>
        public override void ValidateMaterial(Material material) => SyncKeywords(material);

        /// <summary>
        /// Keyword'leri materyalin GERÇEK içeriğinden türetir — kullanıcıya "doku attım
        /// ama görünmüyor" tuzağı bırakmaz. Yüz overlay'i ayrıca doku varlığına bağlı;
        /// gerekçesi yukarıdaki HelpBox'ta.
        /// </summary>
        private static void SyncKeywords(Material m)
        {
            SetKeyword(m, "_NORMALMAP", m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap") != null);
            SetKeyword(m, "_METALLICSPECGLOSSMAP", m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null);
            SetKeyword(m, "_OCCLUSIONMAP", m.HasProperty("_OcclusionMap") && m.GetTexture("_OcclusionMap") != null);

            bool alphaClip = m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f;
            SetKeyword(m, "_ALPHATEST_ON", alphaClip);
            if (m.HasProperty("_AlphaToMask"))
                m.SetFloat("_AlphaToMask", alphaClip ? 1f : 0f);

            bool receiveShadows = !m.HasProperty("_ReceiveShadows") || m.GetFloat("_ReceiveShadows") > 0.5f;
            SetKeyword(m, "_RECEIVE_SHADOWS_OFF", !receiveShadows);

            bool faceOn = m.HasProperty("_FaceOverlay")
                          && m.GetFloat("_FaceOverlay") > 0.5f
                          && m.GetTexture("_FaceMap") != null;
            SetKeyword(m, "_FACEOVERLAY_ON", faceOn);
        }

        private static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }

        // ---- küçük çizim yardımcıları ---------------------------------------
        // Hepsi property YOKSA sessizce atlar: shader'dan bir alan silinince
        // inspector NullReference ile patlamasın.

        private static MaterialProperty Get(MaterialProperty[] props, string name)
            => FindProperty(name, props, false);

        private static void Texture(MaterialEditor editor, MaterialProperty[] props, string name, GUIContent label)
        {
            MaterialProperty p = Get(props, name);
            if (p != null) editor.TexturePropertySingleLine(label, p);
        }

        private static void TextureWithColor(MaterialEditor editor, MaterialProperty[] props, string tex, string color, GUIContent label)
        {
            MaterialProperty t = Get(props, tex);
            MaterialProperty c = Get(props, color);
            if (t != null && c != null) editor.TexturePropertySingleLine(label, t, c);
            else if (t != null) editor.TexturePropertySingleLine(label, t);
        }

        private static void TextureWithFloat(MaterialEditor editor, MaterialProperty[] props, string tex, string scalar, GUIContent label)
        {
            MaterialProperty t = Get(props, tex);
            MaterialProperty s = Get(props, scalar);
            if (t != null && s != null) editor.TexturePropertySingleLine(label, t, s);
            else if (t != null) editor.TexturePropertySingleLine(label, t);
        }

        private static void TileOffset(MaterialEditor editor, MaterialProperty[] props, string name)
        {
            MaterialProperty p = Get(props, name);
            if (p != null) editor.TextureScaleOffsetProperty(p);
        }

        private static void Color(MaterialEditor editor, MaterialProperty[] props, string name, string label)
        {
            MaterialProperty p = Get(props, name);
            if (p != null) editor.ColorProperty(p, label);
        }

        private static void Slider(MaterialEditor editor, MaterialProperty[] props, string name, string label)
        {
            MaterialProperty p = Get(props, name);
            if (p != null) editor.RangeProperty(p, label);
        }

        private static void Float(MaterialEditor editor, MaterialProperty[] props, string name, string label)
        {
            MaterialProperty p = Get(props, name);
            if (p != null) editor.FloatProperty(p, label);
        }

        private static void Vector(MaterialEditor editor, MaterialProperty[] props, string name, GUIContent label)
        {
            MaterialProperty p = Get(props, name);
            if (p != null) editor.VectorProperty(p, label.text);
        }

        private static void Toggle(MaterialEditor editor, MaterialProperty[] props, string name, string label)
        {
            MaterialProperty p = Get(props, name);
            if (p == null) return;

            EditorGUI.BeginChangeCheck();
            bool value = EditorGUILayout.Toggle(label, p.floatValue > 0.5f);
            if (EditorGUI.EndChangeCheck())
                p.floatValue = value ? 1f : 0f;
        }

        /// <summary>
        /// Yüz index'i int gibi davranmalı — shader tarafında zaten yuvarlanıyor,
        /// ama inspector'da 3.7 yazabilmek "hangi yüz seçili" sorusunu belirsizleştirir.
        /// Üst sınır ızgaradan türetilir, koda gömülmez.
        /// </summary>
        private static void FaceIndexField(MaterialEditor editor, MaterialProperty[] props, Material material)
        {
            MaterialProperty p = Get(props, "_FaceIndex");
            if (p == null) return;

            Vector4 grid = material.HasProperty("_FaceGrid") ? material.GetVector("_FaceGrid") : new Vector4(3, 3, 0, 0);
            int cells = Mathf.Max(1, Mathf.RoundToInt(grid.x) * Mathf.RoundToInt(grid.y));

            EditorGUI.BeginChangeCheck();
            int value = EditorGUILayout.IntSlider($"Yüz index (0-{cells - 1})", Mathf.RoundToInt(p.floatValue), 0, cells - 1);
            if (EditorGUI.EndChangeCheck())
                p.floatValue = value;
        }
    }
}
