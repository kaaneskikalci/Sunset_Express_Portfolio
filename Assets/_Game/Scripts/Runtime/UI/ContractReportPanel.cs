using System;
using SunsetExpress.GameLoop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Cenaze raporu ekranı (GDD 3.1 "Gömme" → rapor): kontrat tamamlanınca herkeste açılır,
    /// host "Return to Hub" ile ekibi geri götürür.
    ///
    /// Sahnede değil kalıcı HUD'da yaşar (<see cref="HudBootstrap"/>) — teslim noktası SAHNE
    /// objesidir ve level'dan level'a değişir; raporu her level'da yeniden kurmak gerekmesin.
    /// Aynı gerekçe <see cref="ContractBoardPanel"/> için de geçerliydi.
    ///
    /// sortingOrder 195: ölüm ekranının (190) ÜSTÜNDE — ölü bir oyuncunun karartması raporu
    /// örtmemeli, kontrat bitişi terminal durumdur. ESC menüsünün (200) ALTINDA — "Lobiden Ayrıl"
    /// her koşulda erişilebilir kalmalı.
    ///
    /// ⚠ RAPOR ŞU AN ÜÇ SATIR: süre, tabut hasarı, ceset. Ücret/bonus ve suçluluk istatistikleri
    /// (GDD 3.1 "Ödeme ve Hesaplaşma") BİLEREK yok — GDD 9/10 `kismen-acik`, ekonomi formülü ekip
    /// kararı bekliyor. Panel satır tabanlı çiziyor; karar çıkınca satır eklemek yeterli.
    /// </summary>
    public sealed class ContractReportPanel : MonoBehaviour
    {
        private const float TitleY = 240f;
        private const float SubtitleY = 185f;
        private const float BriefY = 105f;
        private const float BriefHeight = 110f;  // künye çok satırlı: ölüm sebebi, boy, mevki…
        private const float FirstLineY = 0f;
        private const float LineSpacing = 52f;
        private const float ButtonY = -190f;

        private GameObject _panel;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _contractName;
        private TextMeshProUGUI _brief;
        private TextMeshProUGUI _duration;
        private TextMeshProUGUI _damage;
        private TextMeshProUGUI _corpse;
        private Button _returnButton;
        private TextMeshProUGUI _hostOnlyNote;

        private Action _onReturn;

        /// <summary>Rapor ekranda mı — teslim noktası tekrar göstermeye kalkmasın diye.</summary>
        public bool IsOpen { get; private set; }

        private void Start()
        {
            BuildVisuals();
            Hide();
        }

        private void OnDisable() => CursorArbiter.Release(this);

        private void OnDestroy() => CursorArbiter.Release(this);

        private void BuildVisuals()
        {
            Canvas canvas = UiFactory.CreateOverlayCanvas(transform, "ContractReportCanvas", 195, interactive: true);
            _panel = UiFactory.CreateDimPanel(canvas.transform, "Panel");

            _title = CreateLine("Title", "CONTRACT COMPLETE", UiFactory.TitleFontSize, TitleY);

            // Kontrat adı başlığın ALTINDA ayrı bir şerit: "hangi iş bitti" sorusunun cevabı,
            // rapor satırlarından biri değil. Veri toplanıyordu ama hiçbir yere yazılmıyordu —
            // ekranda yalnız sabit başlık görünüyordu (sahada bildirildi).
            _contractName = CreateLine("ContractName", string.Empty, 26f, SubtitleY);

            // Künye ÇOK SATIRLI ve kendi kutusunda: ölçüm satırlarından (süre/hasar) daha küçük
            // puntoda çünkü o bir ölçüm değil, merhumun hikâyesi. Yüksek kutu + TMP'nin kendi
            // sarması uzun metni taşırmadan sığdırır.
            _brief = CreateLine("Brief", string.Empty, 22f, BriefY, BriefHeight);

            _duration = CreateLine("Duration", string.Empty, 24f, FirstLineY);
            _damage = CreateLine("Damage", string.Empty, 24f, FirstLineY - LineSpacing);
            _corpse = CreateLine("Corpse", string.Empty, 24f, FirstLineY - LineSpacing * 2f);

            _returnButton = UiFactory.CreateButton(_panel.transform, "Return to Hub",
                new Vector2(0f, ButtonY), ReturnToHub);

            // Host olmayan oyuncu raporu okur ama dönüşü başlatamaz (ContractBoard ile aynı ekip
            // kararı). Kilitli buton "bozuk" gibi okunur; yerine açıklama konur.
            _hostOnlyNote = CreateLine("HostOnlyNote", "Waiting for the host to return to the hub",
                20f, ButtonY);
        }

        private TextMeshProUGUI CreateLine(string name, string text, float fontSize, float y,
            float height = 48f)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(_panel.transform, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(900f, height);
            rect.anchoredPosition = new Vector2(0f, y);

            return UiFactory.CreateLabel(go.transform, "Label", text, fontSize);
        }

        /// <summary>
        /// Raporu gösterir. <paramref name="canReturn"/> yalnız host'ta true — yetki sunucuda
        /// AYRICA doğrulanır, arayüzü gizlemek yetki değildir.
        /// </summary>
        public void Show(ContractReport report, bool canReturn, Action onReturn)
        {
            _onReturn = onReturn;

            SetOptionalLine(_contractName, report.ContractName);

            // Merhumun adı ceset varyant profilinden gelir. Cesetsiz test tabutunda profil yok —
            // o zaman satır hiç çizilmez, "Deceased: —" gibi boş bir satır bırakmaktansa.
            SetOptionalLine(_brief, report.Brief);

            if (_duration != null)
                _duration.text = $"Time: {FormatDuration(report.Duration)}";

            if (_damage != null)
                _damage.text = $"Coffin damage: {report.CoffinDamage01:P0}";

            // Ceset kaybı GDD 3.4'te KALICI ve utanç satırı olarak raporlanır — nötr bir "hayır"
            // değil, açık bir suçlama olmalı.
            if (_corpse != null)
                _corpse.text = report.CorpseDelivered
                    ? "Body delivered: Yes"
                    : "Body delivered: No — lost on the way";

            if (_returnButton != null)
            {
                _returnButton.gameObject.SetActive(canReturn);
                _returnButton.interactable = canReturn;
            }

            if (_hostOnlyNote != null)
                _hostOnlyNote.gameObject.SetActive(!canReturn);

            IsOpen = true;

            if (_panel != null)
                _panel.SetActive(true);

            // İmlece DOĞRUDAN yazılmaz — yalnız talep bildirilir; uygulamayı CursorArbiterDriver
            // her kare yapar (ContractBoardPanel'deki aynı gerekçe: sahne geçişinde yeniden doğan
            // kamera doğrudan yazımı eziyordu).
            CursorArbiter.Request(this);
        }

        public void Hide()
        {
            IsOpen = false;
            _onReturn = null;

            if (_panel != null)
                _panel.SetActive(false);

            CursorArbiter.Release(this);
        }

        /// <summary>
        /// Metin boşsa satırı GİZLER. Boş bir etiketi yerinde bırakmak, raporun ortasında sebepsiz
        /// bir boşluk açıyordu; bilgi yoksa satır da olmamalı.
        /// </summary>
        private static void SetOptionalLine(TextMeshProUGUI label, string text)
        {
            if (label == null)
                return;

            bool has = !string.IsNullOrWhiteSpace(text);
            label.text = has ? text : string.Empty;
            label.gameObject.SetActive(has);
        }

        /// <summary>Dakika:saniye — teslim süreleri dakikalar ölçeğinde, çıplak saniye okunmuyor.</summary>
        private static string FormatDuration(float seconds)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60:00}:{total % 60:00}";
        }

        private void ReturnToHub()
        {
            Action callback = _onReturn;

            // Önce kapat: geri çağrı sahne yüklemeyi tetikliyor ve panel açık kalırsa imleç talebi
            // yeni sahneye taşınırdı.
            Hide();
            callback?.Invoke();
        }
    }
}
