using FishNet;
using FishNet.Managing.Server;
using Steamworks;
using SunsetExpress.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Ana menü — iki durum: lobi yokken "Create Lobby", lobi kurulduktan sonra davet/ayrıl.
    ///
    /// GDD yönü (13.1, Lethal Company sadeliği): menü minimum tutulur. Kontrat seçimi, ekipman ve
    /// kontratı başlatma menüde DEĞİL, Hub'da dünya içi (diegetic) yapılır — ilan panosu ve garaj
    /// kapısı. Bu yüzden burada oyuncu listesi ya da ready ekranı YOKTUR ve olmayacaktır; lobi
    /// panelindeki tek bilgi "kaç kişiyiz", tek eylem davet ve ayrılmadır.
    ///
    /// Oturum durumu neden SteamLobby'den değil FishNet'ten okunuyor: `SteamLobby._state` private
    /// (durum köprüsü ayrı bir iş). `InstanceFinder` üzerinden okumak hem public hem de lokal host
    /// (Steam'siz) yolunu da kapsıyor. Davet penceresi yalnız Steam lobisi varken anlamlı olduğu
    /// için `OpenInviteDialog()` kendi içinde guard'lı — burada ayrıca kontrol gerekmiyor.
    ///
    /// Assembly notu (2026-08-04): `SunsetExpress.Runtime.asmdef` kaldırıldı, tüm oyun kodu
    /// Assembly-CSharp'ta. Sebep Steamworks.NET ve FishySteamworks'ün asmdef'siz olması — asmdef →
    /// Assembly-CSharp referansı Unity'de mümkün olmadığı için menü/UI ↔ networking arası her
    /// bağlantı o duvara çarpıyordu. Bu yüzden `SteamLobby`'ye doğrudan erişim var.
    /// </summary>
    public sealed class MainMenuController : MonoBehaviour
    {
        [Header("Menü paneli (lobi yokken)")]
        [Tooltip("Create Lobby butonunu içeren kök obje. Lobi kurulunca gizlenir.")]
        [SerializeField] private GameObject _menuPanel;
        [SerializeField] private Button _createLobbyButton;

        [Header("Lobi paneli (host/oturum varken)")]
        [Tooltip("Davet + ayrıl butonlarını ve durum yazısını içeren kök obje. Başlangıçta gizli.")]
        [SerializeField] private GameObject _lobbyPanel;
        [Tooltip("Steam davet penceresini açar (Shift+Tab overlay). Yalnız host'ta anlamlı.")]
        [SerializeField] private Button _inviteButton;
        [SerializeField] private Button _leaveButton;
        [Tooltip("İsteğe bağlı. Boş bırakılabilir.")]
        [SerializeField] private TMP_Text _statusText;

        [Header("Geçiş paneli (lobi kuruluyor / bağlanılıyor)")]
        [Tooltip("Steam callback'i beklenirken görünür. Olmadan süreç GÖRÜNMEZ: Create ölü görünür, " +
                 "Ayrıl gizlidir ve kullanıcı iptal edemez. Başlangıçta gizli.")]
        [SerializeField] private GameObject _transitionPanel;
        [Tooltip("İsteğe bağlı. Boş bırakılabilir.")]
        [SerializeField] private TMP_Text _transitionText;
        [Tooltip("Uzayan/askıda kalan bağlantıdan çıkış — LeaveSession() çağırır.")]
        [SerializeField] private Button _cancelButton;

        [Header("SteamLobby bulunamazsa gösterilecek uyarı paneli")]
        [Tooltip("İçine istediğin metni koy. Menü sahnesi Bootstrap'tan geçilmeden doğrudan " +
                 "açıldığında NetworkManager olmaz — buton kilitlenir ve bu panel açılır.")]
        [SerializeField] private GameObject _unavailableNotice;

        [Header("Gösterim")]
        [Tooltip("Durum yazısındaki üst sınır — YALNIZCA metin için. SteamLobby'deki _maxPlayers " +
                 "private olduğu için buradan okunamıyor; iki değeri elle aynı tut.")]
        [SerializeField] private int _maxPlayersDisplay = 4;

        /// <summary>Menünün gösterebileceği dört durum. Panel değişimi yalnız durum DEĞİŞİNCE yapılır.</summary>
        private enum MenuState
        {
            /// <summary>SteamLobby yok — menü doğrudan açılmış (Bootstrap'tan geçilmemiş).</summary>
            Unavailable,
            /// <summary>Oturum kurulabilir: Create Lobby açık.</summary>
            Idle,
            /// <summary>Steam callback'i uçuşta (Creating/Joining) ya da oturum kapanıyor.</summary>
            Transitioning,
            /// <summary>Host veya client olarak oturumdayız.</summary>
            InSession
        }

        private SteamLobby _lobby;
        private MenuState _shownState = (MenuState)(-1); // ilk Update'te kesin uygulansın
        private int _shownCount = -1;

        /// <summary>
        /// Awake DEĞİL Start: SteamLobby, Bootstrap'ta doğup DontDestroyOnLoad'a taşınan
        /// NetworkManager'ın üzerinde yaşıyor. Start, sahnedeki tüm Awake'lerden sonra koştuğu için
        /// arama o noktada güvenli. (FindFirstObjectByType, DontDestroyOnLoad sahnesindeki objeleri
        /// de bulur.)
        /// </summary>
        private void Start()
        {
            // Menüde imleç HER ZAMAN serbest olmalı. OrbitCamera Awake'te imleci kilitliyor
            // (OrbitCamera.cs:41); oyundan menüye dönüldüğünde o kilit üstümüzde kalır ve butonlara
            // tıklanamaz. Menü kendi girdi bağlamını kendisi kurar, kimseye güvenmez.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            _lobby = FindFirstObjectByType<SteamLobby>();
            bool ready = _lobby != null;

            if (_createLobbyButton != null)
                _createLobbyButton.onClick.AddListener(OnCreateLobbyClicked);

            if (_inviteButton != null)
                _inviteButton.onClick.AddListener(OnInviteClicked);

            if (_leaveButton != null)
                _leaveButton.onClick.AddListener(OnLeaveClicked);

            if (_cancelButton != null)
                _cancelButton.onClick.AddListener(OnLeaveClicked); // iptal = oturumu bırak

            BuildLocalTestPanel();

            // Panel görünürlüğü tek noktadan (ApplyState) yönetilir; burada elle kurulmaz.
            if (!ready)
            {
                Debug.LogWarning("[MainMenu] SteamLobby bulunamadı — Create Lobby kilitli. " +
                                 "Oyunu Bootstrap sahnesinden başlat (build index 0); NetworkManager " +
                                 "ve SteamLobby orada doğuyor.", this);
            }
        }

        private void OnDestroy()
        {
            if (_createLobbyButton != null)
                _createLobbyButton.onClick.RemoveListener(OnCreateLobbyClicked);
            if (_inviteButton != null)
                _inviteButton.onClick.RemoveListener(OnInviteClicked);
            if (_leaveButton != null)
                _leaveButton.onClick.RemoveListener(OnLeaveClicked);
            if (_cancelButton != null)
                _cancelButton.onClick.RemoveListener(OnLeaveClicked);
        }

        private void Update()
        {
            MenuState state = EvaluateState();

            if (state != _shownState)
            {
                _shownState = state;
                ApplyState(state);
                _shownCount = -1; // panel değişti, yazı bir sonraki karede tazelensin
            }

            if (state == MenuState.InSession)
                RefreshStatus();
        }

        /// <summary>
        /// Menünün hangi durumda olduğunu belirler.
        ///
        /// "Oturumda mıyız" bilgisi FishNet'ten, "yeni oturum kurabilir miyiz" bilgisi
        /// <see cref="SteamLobby.CanStartSession"/>'dan gelir — İKİSİ AYRI SORU. FishNet
        /// IsServerStarted/IsClientStarted bayraklarını kapanış (Stopping) sırasında da false yapar;
        /// yalnız onlara bakılsaydı oturum kapanırken menü "Create Lobby"yi açar, basılınca HostSteam
        /// reddeder ve buton ÖLÜ görünürdü. CanStartSession o pencereyi kapatıyor.
        ///
        /// Ne oturumdayız ne de başlatabiliyorsak süreç uçuşta demektir (Creating/Joining ya da
        /// kapanış) — kullanıcıya boş ekran değil "kuruluyor…" + iptal gösterilir.
        /// </summary>
        private MenuState EvaluateState()
        {
            if (_lobby == null)
                return MenuState.Unavailable;

            if (InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted)
                return MenuState.InSession;

            return _lobby.CanStartSession ? MenuState.Idle : MenuState.Transitioning;
        }

        private void ApplyState(MenuState state)
        {
            if (_menuPanel != null)
                _menuPanel.SetActive(state == MenuState.Idle);

            if (_transitionPanel != null)
                _transitionPanel.SetActive(state == MenuState.Transitioning);

            if (_lobbyPanel != null)
                _lobbyPanel.SetActive(state == MenuState.InSession);

            if (_unavailableNotice != null)
                _unavailableNotice.SetActive(state == MenuState.Unavailable);

            // Panel zaten yalnız Idle'da görünüyor ama butonu ayrıca da kilitliyoruz: tek kaynak
            // CanStartSession olsun, panel görünürlüğüyle buton etkinliği ayrışmasın.
            if (_createLobbyButton != null)
                _createLobbyButton.interactable = state == MenuState.Idle;

            if (_transitionText != null && state == MenuState.Transitioning)
                _transitionText.text = "Lobi kuruluyor…";

            // Davet yalnız host'un elinde: OpenInviteDialog zaten Hosting değilse hiçbir şey yapmaz,
            // ama kullanıcıya tıklanabilir ölü buton göstermemek için ayrıca kilitliyoruz.
            // DAVET HOST'A ÖZEL DEĞİL: Steam'de lobiye üye olan HERKES arkadaş davet edebilir ve
            // `CurrentLobby` katılma yolunda client'ta da doluyor. Kısıt bir tasarım kararı değildi,
            // `OpenInviteDialog`'un eski `Hosting`-only guard'ının kalıntısıydı; o guard
            // `Hosting || Connected` olarak genişletildi ve ESC menüsü de çoktan client'a açıldı —
            // host şartı yalnız BURADA unutulmuştu.
            //
            // Ölçüt artık GERÇEK BİR STEAM LOBİSİ olması: lokal host'ta (Tugboat, MPPM) davet
            // edilecek lobi yoktur ve buton eskiden host'ta tıklanabilir ama SESSİZ kalıyordu —
            // ölü buton "bozuk" diye okunur.
            if (_inviteButton != null)
                _inviteButton.interactable = state == MenuState.InSession
                                             && _lobby != null && _lobby.CurrentLobby.IsValid();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Lokal test yolu yalnız oturum yokken anlamlı.
            if (_localTestPanel != null)
                _localTestPanel.SetActive(state == MenuState.Idle);
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private GameObject _localTestPanel;
#endif

        /// <summary>
        /// LOKAL TEST YOLU — yalnız editör/geliştirme derlemesinde.
        ///
        /// NEDEN VAR: menüde KATILMA yolu yok, yalnız "Create Lobby" var. İki MPPM penceresi de ona
        /// bastığında ikisi de KENDİ lobisini kuruyor ve asla buluşmuyorlar (sahada yaşandı, tüm
        /// çok-oyunculu testi tıkadı). Steam daveti editörde çalışmıyor — overlay kendini oyunun
        /// render'ına iliştirir, editöre iliştiremez — yani MPPM'de tek buluşma yolu Tugboat'ın
        /// lokal host/bağlan çifti.
        ///
        /// SAHNEYE DOKUNULMADAN kuruluyor: MainMenu sahnesinde yeni buton alanları açmak sahne
        /// düzenlemesi gerektirirdi ve bu bir geliştirici aracı — sürüm derlemesine hiç girmiyor,
        /// sahnede de iz bırakmıyor.
        ///
        /// Çağrılar `SteamLobby`'nin MEVCUT public metotlarına gidiyor (`HostLocal`/`ConnectLocal`);
        /// Kaan'ın dosyasında değişiklik yok.
        /// </summary>
        private void BuildLocalTestPanel()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_lobby == null)
                return;

            // sortingOrder 5: menünün kendi canvas'ının ALTINDA kalması gerekmiyor ama düşük
            // tutuyoruz — bu bir geliştirici köşesi, menünün önüne geçmemeli.
            Canvas canvas = UiFactory.CreateOverlayCanvas(transform, "LocalTestCanvas", 5, interactive: true);

            _localTestPanel = new GameObject("LocalTest", typeof(RectTransform));
            _localTestPanel.transform.SetParent(canvas.transform, false);

            RectTransform root = (RectTransform)_localTestPanel.transform;
            root.anchorMin = root.anchorMax = new Vector2(0f, 0f);
            root.pivot = new Vector2(0f, 0f);
            root.sizeDelta = new Vector2(360f, 220f);
            root.anchoredPosition = new Vector2(24f, 24f);

            CreateHint(root, "Local test (dev only)", new Vector2(180f, 190f), 20f);
            CreateHint(root, "Steam daveti editörde açılmaz", new Vector2(180f, 162f), 15f);

            UiFactory.CreateButton(root, "Host (Local)", new Vector2(180f, 110f),
                () => _lobby.HostLocal());

            UiFactory.CreateButton(root, "Join (Local)", new Vector2(180f, 40f),
                () => _lobby.ConnectLocal());
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static void CreateHint(Transform parent, string text, Vector2 position, float fontSize)
        {
            GameObject go = new("Hint", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(340f, 28f);
            rect.anchoredPosition = position;

            UiFactory.CreateLabel(go.transform, "Label", text, fontSize);
        }
#endif

        /// <summary>Oyuncu sayısını yalnız DEĞİŞİNCE yazar — her kare string üretmemek için.</summary>
        private void RefreshStatus()
        {
            if (_statusText == null)
                return;

            // Client tarafında ServerManager.Clients boştur (server o makinede değil), o yüzden
            // sayı yalnız host'ta anlamlı. Client'a sayı yerine durum yazılır.
            if (!InstanceFinder.IsServerStarted)
            {
                if (_shownCount == -2)
                    return;
                _shownCount = -2;
                _statusText.text = "Lobiye katıldın";
                return;
            }

            ServerManager server = InstanceFinder.ServerManager;
            int count = server != null ? server.Clients.Count : 0;

            if (count == _shownCount)
                return;

            _shownCount = count;
            _statusText.text = $"Lobi kuruldu · {count}/{_maxPlayersDisplay} oyuncu";
        }

        /// <summary>
        /// Steam lobisi kurar ve host olur. Lobi büyüklüğü SteamLobby'deki `_maxPlayers` alanından
        /// geliyor (varsayılan 4). 2 kişilik seçenek, `HostSteam(int maxPlayers)` aşırı yüklemesi
        /// eklendiğinde buraya parametre olarak bağlanacak — o yüzden çağrı tek noktada tutuldu.
        ///
        /// Çift tıklama guard'ı gerekmiyor: HostSteam() zaten `_state != Idle` ise erken dönüyor.
        /// </summary>
        private void OnCreateLobbyClicked()
        {
            if (_lobby != null)
                _lobby.HostSteam();
        }

        /// <summary>
        /// Steam davet penceresini açar. "Hiçbir şey olmadı" iki AYRI sebepten gelebilir ve
        /// <see cref="SteamLobby.OpenInviteDialog"/> sessizce no-op olduğu için ayırt edilemiyor:
        /// (a) ortada geçerli bir Steam lobisi yok (lokal host), (b) lobi var ama overlay çizilmiyor.
        /// (b) Unity Editor'de NORMALDİR — Steam overlay kendini oyunun render'ına iliştirir,
        /// editöre iliştiremez; gerçek testi build alarak yapmak gerekir. İkisini logla ayırıyoruz.
        /// </summary>
        private void OnInviteClicked()
        {
            if (_lobby == null)
                return;

            if (!_lobby.CurrentLobby.IsValid())
            {
                Debug.LogWarning("[MainMenu] Geçerli bir Steam lobisi yok (lokal host olabilir) — " +
                                 "davet penceresi açılamaz.", this);
                return;
            }

            Debug.Log($"[MainMenu] Davet penceresi isteniyor (lobi {_lobby.CurrentLobby}). " +
                      "Overlay açılmıyorsa: Steam overlay Unity Editor üstünde çizilmez — " +
                      "build alıp dene ya da Steam'den davet et.", this);

            _lobby.OpenInviteDialog();
        }

        /// <summary>
        /// Oturumu kapatır. LeaveSession idempotent ve tüm çıkış yollarının ortak ucu — lobiden
        /// çıkar, server/client bağlantılarını durdurur. Panel değişimi ayrıca yapılmaz: bağlantı
        /// düşünce <see cref="Update"/> durumu görüp menüye kendisi döner.
        /// </summary>
        private void OnLeaveClicked()
        {
            if (_lobby != null)
                _lobby.LeaveSession();
        }
    }
}
