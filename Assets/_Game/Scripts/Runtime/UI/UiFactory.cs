using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Kod ile kurulan UI'ın ortak stili ve yapı taşları — TEK DEĞİŞİM NOKTASI.
    ///
    /// Neden var: oyun içi menü ve ilan panosu paneli runtime'da kuruluyor (sahne/prefab çakışması
    /// olmasın ve her sahnede çalışsınlar diye). Her biri kendi rengini/boyutunu yazsaydı stil
    /// değiştirmek için birden çok dosyaya dokunmak gerekirdi. Buradaki sabitler değişince tüm
    /// kod-tabanlı UI birlikte değişir.
    ///
    /// Sahnedeki (editörde kurulan) butonlar bu sabitleri OKUYAMAZ — onlar Inspector'dan ayarlanır.
    /// İkisini aynı tutmak için değerlerin hex karşılıkları yorumda verilmiştir.
    /// UI tasarımı netleşip her şey prefab'a taşınınca burası kalkacak.
    /// </summary>
    public static class UiFactory
    {
        /// <summary>Tam ekran karartma — hex 0000008C.</summary>
        public static readonly Color PanelDim = new(0f, 0f, 0f, 0.55f);

        /// <summary>Buton arka planı — hex 26262BF2.</summary>
        public static readonly Color ButtonBackground = new(0.15f, 0.15f, 0.17f, 0.95f);

        public static readonly Vector2 ButtonSize = new(320f, 64f);
        public const float ButtonFontSize = 28f;
        public const float TitleFontSize = 36f;

        /// <summary>
        /// Ekran üstü Canvas kurar. <paramref name="interactive"/> false ise GraphicRaycaster
        /// EKLENMEZ — tıklanmayan göstergeler (ipucu yazısı gibi) oyun girdisini yutmamalı.
        /// </summary>
        public static Canvas CreateOverlayCanvas(Transform parent, string name, int sortingOrder, bool interactive)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            // RectTransform'u ebeveyne GERDİRMEK ŞART. Unity yalnız KÖK Canvas'ların RectTransform'unu
            // ekran boyutuna zorlar; bu canvas HUD kökünün ALTINDA (iç içe) olduğu için sıradan bir
            // RectTransform gibi davranır ve varsayılan 100x100'de kalır. Gerdirilmezse tam ekran
            // olması gereken karartma paneli ekranın ortasında 100x100'lük gri bir kutu olarak
            // çizilir (sahada görüldü) ve alttaki/üstteki hizalamalar da kayar.
            Stretch((RectTransform)go.transform);

            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (interactive)
                go.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        /// <summary>Ebeveynini tamamen kaplayan, karartma rengi taşıyan panel kökü.</summary>
        public static GameObject CreateDimPanel(Transform parent, string name)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Stretch((RectTransform)go.transform);

            Image dim = go.AddComponent<Image>();
            dim.color = PanelDim;
            return go;
        }

        /// <summary>Standart buton. Etiket yazısı raycast almaz — tık butona gitsin.</summary>
        public static Button CreateButton(Transform parent, string label, Vector2 anchoredPosition, UnityAction onClick)
        {
            GameObject go = new(label, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = ButtonSize;
            rect.anchoredPosition = anchoredPosition;

            Image background = go.AddComponent<Image>();
            background.color = ButtonBackground;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = background;
            if (onClick != null)
                button.onClick.AddListener(onClick);

            CreateLabel(go.transform, "Label", label, ButtonFontSize);
            return button;
        }

        /// <summary>
        /// Ebeveynini kaplayan yazı. Varsayılan ortalıdır (buton etiketleri böyle); brief gibi uzun
        /// metinler için hizalama sola/üste alınır.
        /// </summary>
        public static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float fontSize,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Stretch((RectTransform)go.transform);

            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.alignment = alignment;
            label.fontSize = fontSize;
            label.raycastTarget = false;
            return label;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
