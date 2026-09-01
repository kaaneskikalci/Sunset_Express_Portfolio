using System;
using System.Collections.Generic;
using SunsetExpress.GameLoop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// İlan panosu arayüzü: yaklaşınca ipucu, açılınca iki sütun — solda kontrat listesi, sağda
    /// seçilen kontratın detayı ve Başlat düğmesi (GDD 3.1, 8.1, 13.1).
    ///
    /// İKİ ADIMLI SEÇİM (ekip kararı): listeden tıklamak kontratı BAŞLATMAZ, yalnızca sağdaki
    /// detayı doldurur; oyun ancak Başlat'a basılınca yüklenir. Gerekçe iki katlı:
    /// ① brief'i okumadan kontrat seçmek anlamsız (GDD 3.1 brief'i akışın parçası sayar),
    /// ② tek tıkla yükleme co-op'ta bir oyuncunun tüm ekibi kazara levele çekmesi demekti.
    /// Bu, GDD 13.1'in ilan panosu (seçim) / garaj kapısı (çıkış) ayrımına giden yolun yarısıdır —
    /// ikinci yarısı hub'a gerçek geometri gelince fiziksel kapı olarak eklenecek.
    ///
    /// Sahnede değil kalıcı HUD'da yaşar (<see cref="HudBootstrap"/>): panonun kendisi sahne
    /// objesidir ama arayüzü her hub varyantında yeniden kurmak gerekmesin. Hub geometrisi Baran'ın
    /// alanında ve değişecek — arayüzün ona bağlı olmaması bilinçli.
    ///
    /// Stil <see cref="UiFactory"/>'den gelir; renk/boyut değişikliği tek yerden yapılır.
    /// </summary>
    public sealed class ContractBoardPanel : MonoBehaviour
    {
        private const float TitleY = 320f;   // başlık şeridi — liste ve detayın ÜSTÜNDE kalmalı
        private const float FirstRowY = 120f;
        private const float RowSpacing = 80f;
        private const float ListX = -300f;   // sol sütunun merkezi
        private const float DetailX = 320f;  // sağ sütunun merkezi

        private GameObject _promptRoot;
        private TextMeshProUGUI _promptLabel;
        private GameObject _panel;
        private Transform _listRoot;

        private GameObject _detailRoot;
        private TextMeshProUGUI _detailTitle;
        private TextMeshProUGUI _detailMeta;
        private TextMeshProUGUI _detailBrief;
        private Button _startButton;
        private TextMeshProUGUI _hostOnlyNote;
        private TextMeshProUGUI _emptyHint;

        private Action<int> _onSelect;
        private IReadOnlyList<ContractDefinition> _contracts;
        private int _selectedIndex = -1;
        private bool _canStart;
        private readonly List<GameObject> _rows = new();

        /// <summary>Panel açık mı — pano, açıkken tekrar E'ye basılmasını buna bakarak yönetir.</summary>
        public bool IsOpen { get; private set; }

        private void Start()
        {
            BuildVisuals();
            HidePrompt();
            Close();
        }

        private void OnDisable() => CursorArbiter.Release(this);

        private void OnDestroy() => CursorArbiter.Release(this);

        private void BuildVisuals()
        {
            // İpucu ve panel AYRI canvas'larda: ipucu tıklanmaz (raycaster yok, oyun girdisini
            // yutmaz), panel tıklanır. sortingOrder 150 — kopma uyarısının (100) üstünde, oyun içi
            // menünün (200) altında: ESC menüsü her şeyin üstünde kalmalı.
            Canvas promptCanvas = UiFactory.CreateOverlayCanvas(transform, "ContractPromptCanvas", 150, interactive: false);
            _promptRoot = new GameObject("Prompt", typeof(RectTransform));
            _promptRoot.transform.SetParent(promptCanvas.transform, false);

            RectTransform promptRect = (RectTransform)_promptRoot.transform;
            promptRect.anchorMin = promptRect.anchorMax = new Vector2(0.5f, 0f);
            promptRect.pivot = new Vector2(0.5f, 0f);
            promptRect.sizeDelta = new Vector2(600f, 60f);
            promptRect.anchoredPosition = new Vector2(0f, 140f);

            _promptLabel = UiFactory.CreateLabel(_promptRoot.transform, "Label", string.Empty, UiFactory.ButtonFontSize);

            Canvas panelCanvas = UiFactory.CreateOverlayCanvas(transform, "ContractPanelCanvas", 151, interactive: true);
            _panel = UiFactory.CreateDimPanel(panelCanvas.transform, "Panel");

            // Başlık, listenin/detayın ÜSTÜNDE ayrı bir şeritte durur. Eskiden satır ızgarasına
            // göre konumlanıyordu (FirstRowY + RowSpacing) ve sağ sütunun kontrat adıyla üst üste
            // biniyordu — ikisi de aynı yükseklikteydi.
            CreateFixedLabel(_panel.transform, "Title", "Contract Panel", UiFactory.TitleFontSize,
                new Vector2(0f, TitleY), new Vector2(800f, 70f));

            // Boş durum ipucu başlığın ALTINDA ve ORTADA durur, sağ sütunda değil: talimat panelin
            // tamamına ait ("bir şey seç"), tek bir sütuna değil. Sağda dururken ekranın kenarına
            // yapışık görünüyordu. Seçim yapılınca gizlenir; başlık yerinde kaldığı için altında
            // boşluk oluşmaz.
            _emptyHint = CreateFixedLabel(_panel.transform, "EmptyHint", "Select a contract",
                22f, new Vector2(0f, TitleY - 75f), new Vector2(800f, 50f));

            _listRoot = _panel.transform;

            BuildDetail();
        }

        /// <summary>Sağ sütun: seçilen kontratın adı, zorluğu, brief'i ve Başlat düğmesi.</summary>
        private void BuildDetail()
        {
            _detailRoot = new GameObject("Detail", typeof(RectTransform));
            _detailRoot.transform.SetParent(_panel.transform, false);

            RectTransform root = (RectTransform)_detailRoot.transform;
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(640f, 420f);
            root.anchoredPosition = new Vector2(DetailX, 20f);

            _detailTitle = CreateFixedLabel(_detailRoot.transform, "DetailTitle", string.Empty,
                UiFactory.TitleFontSize, new Vector2(0f, 160f), new Vector2(640f, 60f),
                TextAlignmentOptions.TopLeft);

            _detailMeta = CreateFixedLabel(_detailRoot.transform, "DetailMeta", string.Empty,
                20f, new Vector2(0f, 115f), new Vector2(640f, 40f), TextAlignmentOptions.TopLeft);

            _detailBrief = CreateFixedLabel(_detailRoot.transform, "DetailBrief", string.Empty,
                22f, new Vector2(0f, 0f), new Vector2(640f, 200f), TextAlignmentOptions.TopLeft);

            _startButton = UiFactory.CreateButton(_detailRoot.transform, "Start", new Vector2(0f, -160f), StartSelected);

            // Host olmayan oyuncu kontratı okuyabilir ama başlatamaz (ekip kararı). Butonu kilitli
            // göstermek yerine yerine açıklama koyuyoruz: ölü buton "bozuk" gibi okunur.
            _hostOnlyNote = CreateFixedLabel(_detailRoot.transform, "HostOnlyNote",
                "Only the host can start the contract", 20f, new Vector2(0f, -160f), new Vector2(640f, 60f));
        }

        public void ShowPrompt(string text)
        {
            // Panel açıkken ipucu gösterilmez — "E ile aç" derken zaten açık olması saçma olurdu.
            if (IsOpen)
                return;

            if (_promptLabel != null)
                _promptLabel.text = text;

            if (_promptRoot != null)
                _promptRoot.SetActive(true);
        }

        public void HidePrompt()
        {
            if (_promptRoot != null)
                _promptRoot.SetActive(false);
        }

        /// <summary>
        /// Kontrat listesini gösterir. <paramref name="onSelect"/> yalnızca BAŞLAT'a basılınca ve
        /// seçilen kontratın DİZİN'iyle çağrılır — panel hangi sahnenin yükleneceğini bilmez, o
        /// kararı panonun sunucu tarafı verir (client'ın gönderdiği sahne adına güvenilmez).
        /// </summary>
        public void Open(IReadOnlyList<ContractDefinition> contracts, bool canStart, Action<int> onSelect)
        {
            _onSelect = onSelect;
            _contracts = contracts;
            _canStart = canStart;
            ClearRows();
            ShowDetail(-1);

            float y = FirstRowY;
            if (contracts != null)
            {
                for (int i = 0; i < contracts.Count; i++)
                {
                    ContractDefinition contract = contracts[i];
                    if (contract == null || !contract.IsPlayable)
                        continue; // sahnesi tanımsız kontrat panoda yer almaz

                    int index = i; // closure tuzağı: döngü değişkeni doğrudan yakalanmaz
                    Button row = UiFactory.CreateButton(_listRoot, contract.ResolvedName,
                        new Vector2(ListX, y), () => ShowDetail(index));
                    _rows.Add(row.gameObject);
                    y -= RowSpacing;
                }
            }

            _rows.Add(UiFactory.CreateButton(_listRoot, "Close", new Vector2(ListX, y - RowSpacing * 0.5f), Close).gameObject);

            IsOpen = true;
            _panel.SetActive(true);
            HidePrompt();

            // İmlece DOĞRUDAN yazılmaz — yalnız talep bildirilir; uygulamayı CursorArbiterDriver
            // her kare yapar. Doğrudan yazsaydık her sahne geçişinde yeniden doğan kamera bizi
            // ezebilirdi (zaten yaşanan buydu).
            CursorArbiter.Request(this);
        }

        public void Close()
        {
            IsOpen = false;
            _onSelect = null;
            _contracts = null;

            if (_panel != null)
                _panel.SetActive(false);

            ClearRows();
            ShowDetail(-1);

            // Kilidi burada GERİ KOYMUYORUZ: InGameMenu talebi kalkınca kendi doğrulamasıyla
            // imleci zaten kilitler. İki taraf da yazsaydı aynı kareyi paylaşıp titretirlerdi.
            CursorArbiter.Release(this);
        }

        /// <summary>Sağ sütunu doldurur. <paramref name="index"/> negatifse "seçim yok" durumu.</summary>
        private void ShowDetail(int index)
        {
            ContractDefinition contract = null;
            if (_contracts != null && index >= 0 && index < _contracts.Count)
                contract = _contracts[index];

            _selectedIndex = contract != null ? index : -1;
            bool hasSelection = _selectedIndex >= 0;

            if (_emptyHint != null)
                _emptyHint.gameObject.SetActive(!hasSelection);

            // Başlatma yetkisi yoksa buton HİÇ gösterilmez, yerine açıklama çıkar (ekip kararı:
            // kontratı yalnız host başlatır). Sunucu tarafında da ayrıca doğrulanıyor — arayüzü
            // gizlemek yetki değildir, yalnız kullanıcıya dürüstlüktür.
            if (_startButton != null)
            {
                bool showStart = hasSelection && _canStart;
                _startButton.gameObject.SetActive(showStart);
                _startButton.interactable = showStart;
            }

            if (_hostOnlyNote != null)
                _hostOnlyNote.gameObject.SetActive(hasSelection && !_canStart);

            if (_detailTitle != null)
                _detailTitle.text = hasSelection ? contract.ResolvedName : string.Empty;

            if (_detailMeta != null)
                _detailMeta.text = hasSelection ? $"Difficulty {contract.difficulty}" : string.Empty;

            if (_detailBrief != null)
                _detailBrief.text = hasSelection ? contract.brief : string.Empty;
        }

        private void StartSelected()
        {
            if (_selectedIndex < 0 || !_canStart)
                return;

            int index = _selectedIndex;
            Action<int> callback = _onSelect;
            Close(); // önce kapat: geri çağrı sahne yüklemeyi tetikler
            callback?.Invoke(index);
        }

        /// <summary>
        /// Sabit boyutlu, merkezden konumlanan yazı. <see cref="UiFactory.CreateLabel"/> ebeveynini
        /// KAPLAR; burada her parçanın kendi kutusu olması gerektiği için araya bir taşıyıcı konur.
        /// </summary>
        private static TextMeshProUGUI CreateFixedLabel(Transform parent, string name, string text,
            float fontSize, Vector2 anchoredPosition, Vector2 size,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            return UiFactory.CreateLabel(go.transform, "Label", text, fontSize, alignment);
        }

        private void ClearRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] != null)
                    Destroy(_rows[i]);
            }
            _rows.Clear();
        }
    }
}
