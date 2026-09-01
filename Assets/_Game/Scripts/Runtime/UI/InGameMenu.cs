using FishNet;
using Steamworks;
using SunsetExpress.Networking;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Oturum içi çıkış menüsü (ESC). Hub'a geçildiğinde `MainMenu` sahnesi değiştiği için lobi
    /// paneli de yok oluyor ve oyuncunun oturumdan çıkacak hiçbir yolu kalmıyordu — bu onu kapatır.
    ///
    /// Neden sahnede değil de kalıcı HUD'da: menü HER gameplay sahnesinde gerekli (Hub, level ve
    /// ileride Baran'ın kuracağı sahneler). Sahne başına kurmak hem tekrar hem de başkasının
    /// sahnesine dokunmak demek olurdu. `HudBootstrap` bunu DontDestroyOnLoad kökünde ayağa kaldırır.
    ///
    /// "Pause" DEĞİLDİR: çok oyunculu oturumda oyun durdurulamaz, bu yalnız bir üst katman menüdür.
    /// Açıkken oyuncu hâlâ hareket edebilir — bilinçli; ileride girdi bağlamı ayrılırsa burası
    /// değişir.
    ///
    /// Görsel şu an KOD İLE kurulur (GripWarningHud ile aynı gerekçe) — UI tasarımı netleşince
    /// prefab'a taşınacak. Kendi Canvas'ını AYRI bir çocuk objede kurar: GripWarningHud kendi
    /// Canvas'ını HUD kökünün ÜSTÜNE ekliyor ve orada bilinçli olarak GraphicRaycaster yok
    /// (tıklanamaz olmalı), buradaki menü ise tıklanabilir olmak zorunda.
    /// </summary>
    public sealed class InGameMenu : MonoBehaviour
    {
        private GameObject _panel;
        private Button _inviteButton;
        private bool _open;

        /// <summary>Oturumda mıyız — menü yalnız oyun içinde anlamlı, ana menüde ESC bir şey yapmaz.</summary>
        private static bool InSession => InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted;

        private void Start()
        {
            BuildVisuals();
            SetOpen(false);
        }

        private void Update()
        {
            // Oturum bittiyse (Ayrıl / bağlantı koptu) menü kapanmalı — ana menüye dönülüyor.
            if (_open && !InSession)
            {
                SetOpen(false);
                return;
            }

            if (!InSession || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
                return;

            // Başka bir panel açıkken (ilan panosu) ESC bu menüyü AÇMAZ: iki panel üst üste binince
            // okunamaz hale geliyor. Menü ZATEN açıkken kontrol atlanır — yoksa kendi talebimiz
            // yüzünden kapatamazdık.
            if (!_open && CursorArbiter.AnyoneElseWantsCursor(this))
                return;

            // ESC'yi bu kare için sahiplen. Sıra bağımsızlığının ikinci yarısı: pano bizden ÖNCE
            // koşup paneli kapatmışsa talebini bırakmış olur ve yukarıdaki kontrol geçerdi —
            // kapı o durumda tek ESC'nin iki panele birden gitmesini engeller.
            if (!UiEscapeGate.TryConsume())
                return;

            SetOpen(!_open);
        }

        private void OnDisable() => CursorArbiter.Release(this);

        private void OnDestroy() => CursorArbiter.Release(this);

        /// <summary>
        /// Menüyü açar/kapatır. İmlece DOĞRUDAN yazılmaz — yalnız hakemde talep bildirilir;
        /// uygulamayı <see cref="CursorArbiterDriver"/> her kare yapar. İki taraf da yazsaydı
        /// (menü + kamera + panel) birbirlerini ezerlerdi, zaten yaşanan buydu.
        /// </summary>
        private void SetOpen(bool open)
        {
            _open = open;

            if (_panel != null)
                _panel.SetActive(open);

            if (open)
            {
                CursorArbiter.Request(this);
                RefreshInviteButton();
            }
            else
            {
                CursorArbiter.Release(this);
            }
        }

        /// <summary>
        /// Davet HOST'a DA CLIENT'a DA açık: Steam'de lobiye üye olan herkes arkadaş davet edebilir
        /// ve `CurrentLobby` katılma yolunda client'ta da doluyor. Eskiden host-only'ydi çünkü
        /// `SteamLobby.OpenInviteDialog` yalnız `Hosting` durumunu kabul ediyordu ve client'ın
        /// düğmesi ölü kalıyordu — guard genişletildi, kısıtın sebebi kalmadı.
        ///
        /// Tek koşul GERÇEK bir Steam lobisi olması: lokal host'ta (MPPM/geliştirme) davet edilecek
        /// lobi yok, orada buton gizli kalır. Menü her açıldığında tazelenir çünkü oturum durumu
        /// menü kapalıyken değişmiş olabilir.
        ///
        /// Kontratı BAŞLATMA yetkisiyle karıştırma: orası host-only kalıyor (Tasarım
        /// sapmaları ②). Davet zararsız, başlatma değil.
        /// </summary>
        private void RefreshInviteButton()
        {
            if (_inviteButton == null)
                return;

            SteamLobby lobby = FindFirstObjectByType<SteamLobby>();
            bool inSession = InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted;
            bool canInvite = inSession && lobby != null && lobby.CurrentLobby.IsValid();

            _inviteButton.gameObject.SetActive(canInvite);
        }

        private void BuildVisuals()
        {
            // sortingOrder 200: kopma uyarısı HUD'ının (100) ve ilan panosu panelinin (150/151)
            // ÜSTÜNDE — ESC menüsü her zaman en üstte olmalı, altında kalan bir çıkış menüsü tuzaktır.
            Canvas canvas = UiFactory.CreateOverlayCanvas(transform, "InGameMenuCanvas", 200, interactive: true);

            // Yarı saydam karartma: menünün açık olduğu okunur olsun, arkadaki oyun görünmeye devam
            // etsin (oyun DURMUYOR — takım arkadaşları hâlâ oynuyor, bunu görmek gerekiyor).
            _panel = UiFactory.CreateDimPanel(canvas.transform, "Panel");

            UiFactory.CreateButton(_panel.transform, "Continue", new Vector2(0f, 120f), Resume);
            _inviteButton = UiFactory.CreateButton(_panel.transform, "Invite Friends", new Vector2(0f, 40f), InviteFriends);
            UiFactory.CreateButton(_panel.transform, "Leave Lobby", new Vector2(0f, -40f), LeaveSession);
        }

        private void Resume() => SetOpen(false);

        /// <summary>
        /// Steam davet penceresini açar. Lobi kurulurken de otomatik açılıyor
        /// (SteamLobby, `_state = Hosting` anında) ama o an oyuncu hemen Hub'a geçtiği için
        /// kaçırılıyor — asıl ihtiyaç "lobideyim, şimdi davet etmek istiyorum".
        ///
        /// `OpenInviteDialog` kendi içinde guard'lı: host değilsen ya da geçerli Steam lobisi yoksa
        /// hiçbir şey yapmaz. Butonu yine de kilitliyoruz, tıklanan ölü buton "bozuk" gibi okunur.
        /// </summary>
        private void InviteFriends()
        {
            SteamLobby lobby = FindFirstObjectByType<SteamLobby>();
            if (lobby == null)
            {
                Debug.LogWarning("[InGameMenu] SteamLobby bulunamadı — davet penceresi açılamadı.", this);
                return;
            }

            LogInviteDiagnostics(lobby);
            lobby.OpenInviteDialog();
        }

        /// <summary>
        /// Davet penceresi açılmazsa SEBEBİNİ söyler. Üç ayrı sebep var ve üçü de dışarıdan aynı
        /// görünüyor — "hiçbir şey olmadı":
        ///   · Steam init değil          → çağrı hiç gitmiyor (build'de steam_appid.txt eksik olabilir)
        ///   · Overlay bu süreçte kapalı → çağrı gidiyor ama Steam çizemiyor
        ///   · Lobi geçersiz             → SteamLobby guard'ı çağrıyı sessizce yutuyor
        ///
        /// ÖLÇÜLDÜ (2026-08-06): Unity EDİTÖRÜNDE `IsOverlayEnabled()` **false** döner — editör
        /// süreci Steam üzerinden başlatılmadığı için overlay hook'u yok. Aynı anda MPPM sanal
        /// oyuncularında pencere açılıyor (ayrı süreçler). Yani editörde davet penceresini
        /// görememek NORMALDİR ve kodda arayacak bir hata yoktur; bu log o soruyu bir daha
        /// sordurmamak için duruyor.
        /// </summary>
        private void LogInviteDiagnostics(SteamLobby lobby)
        {
            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("[Steam] SteamManager init DEĞİL — davet penceresi açılamaz. " +
                                 "Build klasöründe .exe yanında steam_appid.txt var mı?", this);
                return;
            }

            bool overlayEnabled = SteamUtils.IsOverlayEnabled();
            bool lobbyValid = lobby.CurrentLobby.IsValid();

            Debug.Log($"[Steam] Davet isteği — overlay etkin: {overlayEnabled} · lobi geçerli: " +
                      $"{lobbyValid} ({lobby.CurrentLobby}) · host: {InstanceFinder.IsServerStarted}", this);

            if (!overlayEnabled)
            {
                Debug.LogWarning("[Steam] Overlay bu süreçte KAPALI — pencere hiçbir koşulda " +
                                 "görünmez. Kodda sorun yok; build alıp Steam üzerinden çalıştır.", this);
            }
        }

        /// <summary>
        /// Oturumdan çıkar. Menüye dönüşü NetworkSceneDirector yapıyor (bağlantı kapanınca MainMenu'yü
        /// o yüklüyor) — burada sahne yüklemeye kalkışmak onunla yarışırdı.
        /// </summary>
        private void LeaveSession()
        {
            SteamLobby lobby = FindFirstObjectByType<SteamLobby>();
            if (lobby != null)
            {
                lobby.LeaveSession();
                return;
            }

            Debug.LogWarning("[InGameMenu] SteamLobby bulunamadı — oturumdan çıkılamadı.", this);
        }
    }
}
