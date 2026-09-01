using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using Steamworks;
using UnityEngine;

// NOT: Projede asmdef KULLANILMIYOR (karar: 2026-08, Assembly yapısı). Tüm oyun
// kodu Assembly-CSharp.ta derlenir; sebebi Steamworks.NET ve FishySteamworks.ün asmdef.siz
// olması ve Unity.de asmdef → Assembly-CSharp referansının mümkün olmaması.
namespace SunsetExpress.Networking
{
    /// <summary>
    /// Steam lobi akışı (GDD 12.1: arkadaş daveti tek tık). Multipass üzerinde iki dünya:
    /// - Tugboat  → lokal geliştirme/MPPM (Steam KAPALIYKEN de çalışır — yalnız Tugboat index'i başlatılır)
    /// - FishySteamworks → uzak oturumlar (Steam relay). Test AppID 480.
    ///
    /// Yaşam döngüsü tek state makinesinde: tüm çıkış yolları merkezi/idempotent LeaveSession()'a
    /// iner. Join sonucu, Valve'ın öngördüğü gibi JoinLobby'nin call handle'ına bağlı
    /// CallResult&lt;LobbyEnter_t&gt; ile alınır — global LobbyEnter callback'i yok; böylece gecikmiş/
    /// eski denemelerin sonuçları yeni denemeye karışamaz ve LobbyCreated/LobbyEnter sıra
    /// belirsizliği sorun olmaktan çıkar.
    /// </summary>
    public sealed class SteamLobby : MonoBehaviour
    {
        private enum LobbyState
        {
            Idle,       // oturum yok — Host/Join serbest
            Creating,   // CreateLobby isteği uçuşta
            Hosting,    // server + kendi client'ımız ayakta (lobi Steam yolundaysa geçerli)
            Joining,    // davet kabul edildi, JoinLobby/bağlantı uçuşta
            Connected,  // client olarak bağlı
            Leaving,    // çıkış istendi, transport'ların GERÇEKTEN durması bekleniyor

            /// <summary>Lokal server başlatma isteği gönderildi, BAŞLADIĞI henüz doğrulanmadı.
            /// `StartConnection` "istek kabul edildi" döner; bind sonucu asenkron gelir. Bu aralıkta
            /// kendi client'ımızı BAĞLAMIYORUZ — port doluysa mevcut host'a bağlanıp kendimizi host
            /// sanardık (Ozanay'ın bulgusu, 2026-08-06).</summary>
            StartingLocalHost
        }

        private const string HostAddressKey = "SunsetExpress_HostId";

        [Tooltip("Lobi kapasitesi (GDD: 2-4 oyuncu).")]
        [SerializeField] private int _maxPlayers = 4;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Panel ve alanı BİRLİKTE derleme dışı kalır: release build'e yanlışlıkla Host Local /
        // Client Local test butonlarıyla gitmeyi imkansız kılar. Alan da guard içinde,
        // yoksa release'de "atanmış ama hiç okunmamış" (CS0414) uyarısı kalırdı.
        [Tooltip("Ekranda basit Host/Davet/Ayrıl butonları çizilsin mi (Aşama 0 test paneli). " +
                 "Yalnız editör ve development build'de vardır.")]
        [SerializeField] private bool _drawDebugPanel = true;
#endif

        private NetworkManager _nm;
        private Multipass _multipass;
        private global::FishySteamworks.FishySteamworks _steamTransport;

        private Callback<GameLobbyJoinRequested_t> _joinRequested;

        // Uçuştaki CreateLobby denemeleri — join ile aynı gerekçe (aşağıdaki _pendingJoins yorumu).
        private readonly System.Collections.Generic.List<CallResult<LobbyCreated_t>> _pendingCreates = new();

        // Her join denemesi KENDİ CallResult'ını taşır: tek nesneyi Set ile
        // yeniden kullanmak eski isteği izsiz bırakıyordu — iptal edilen deneme sonradan başarılı
        // dönerse lobide hayalet üyelik kalıyordu. Uçuştakiler burada tutulur, OnDestroy'da dispose.
        private readonly System.Collections.Generic.List<CallResult<LobbyEnter_t>> _pendingJoins = new();

        private NetworkSceneDirector _director; // aynı GameObject'te; yoksa null (opsiyonel bağımlılık)
        private LobbyState _state = LobbyState.Idle;
        private bool _awaitingClientStop;   // Leaving sırasında: client transport'unun Stopped'ı bekleniyor mu
        private bool _awaitingServerStop;   // Leaving sırasında: server transport'larının Stopped'ı bekleniyor mu
        private int _generation;            // LeaveSession her çağrıldığında artar — eski akışlar geçersizleşir
        private CSteamID _expectedLobby;    // Joining sırasında beklenen lobi

        public CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;

        /// <summary>
        /// Şu anda YENİ bir oturum (Host/Join) başlatılabilir mi. UI bunu butonun `interactable`
        /// durumunda okumalı: menü panel durumunu `InstanceFinder.IsServerStarted/
        /// IsClientStarted`'dan okuyor, ama FishNet o bayrakları `Stopping` sırasında da false
        /// yapıyor — yani kapanış sürerken menü "Create Lobby"yi açıyor, basılınca `HostSteam`
        /// isteği reddediyor ve buton ÖLÜ görünüyor. Genelde tek kare, transport kapanışı
        /// uzarsa daha fazla.
        /// </summary>
        public bool CanStartSession => _state == LobbyState.Idle && _nm != null && _multipass != null
                                       && !MenuLoadPending;

        /// <summary>
        /// Sahne yöneticisi menüye dönüş yüklemesini PLANLADI mı. Planladıysa yeni oturum
        /// açılamaz: Unity `LoadScene`'i kare sonuna erteler ve İPTAL EDİLEMEZ, yani o pencerede
        /// kurulan taze bir oturum bir sonraki karede sökülür. Director yoksa (ör.
        /// doğrudan TestScene akışı) kapı hep açıktır — bekleyen bir yükleme de yoktur.
        /// </summary>
        private bool MenuLoadPending
        {
            get
            {
                if (_director == null)
                    return false;
                return _director.IsReturningToMenu;
            }
        }

        /// <summary>Oturum başlatma girişlerinin ortak reddi — dört giriş noktası da bunu sorar.</summary>
        private bool RejectedByPendingMenuLoad(string what)
        {
            if (!MenuLoadPending)
                return false;

            Debug.LogWarning($"[SteamLobby] Menüye dönüş yüklemesi beklerken {what} yok sayıldı — " +
                             "menü yüklendikten sonra tekrar dene.");
            return true;
        }

        private void Awake()
        {
            _nm = GetComponentInParent<NetworkManager>();
            if (_nm == null)
                _nm = FindFirstObjectByType<NetworkManager>();

            _multipass = _nm != null ? _nm.TransportManager.Transport as Multipass : null;
            if (_multipass == null)
            {
                Debug.LogError("[SteamLobby] TransportManager.Transport bir Multipass değil — kurulum eksik.");
                enabled = false;
                return;
            }

            // Opsiyonel: yalnız "menü yüklemesi bekliyor mu" kapısı için. Yoksa kapı hep açık.
            _director = GetComponent<NetworkSceneDirector>();

            _steamTransport = _multipass.GetTransport<global::FishySteamworks.FishySteamworks>();
            if (_steamTransport == null)
                Debug.LogError("[SteamLobby] Multipass listesinde FishySteamworks yok — Steam yolu devre dışı, " +
                               "lokal (Tugboat) butonları çalışmaya devam eder.");

            _nm.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _nm.ServerManager.OnServerConnectionState += OnServerConnectionState;
        }

        private void Start()
        {
            if (!SteamManager.Initialized)
            {
                Debug.LogWarning("[SteamLobby] Steam başlatılamadı (Steam açık mı? steam_appid.txt var mı?). " +
                                 "Lokal (Tugboat) akış çalışmaya devam eder.");
                return;
            }

            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
        }

        private void OnApplicationQuit()
        {
            // Steam API burada hâlâ kesin ayakta (teardown'dan önce çalışır) — lobi çıkışının güvenli
            // tek noktası. OnDestroy'da HİÇBİR Steam çağrısı yapılmaz: SteamManager.Initialized
            // getter'ı bile instance yok edilmişse YENİ SteamManager yaratıp ikinci init exception'ı
            // üretebilir.
            if (CurrentLobby.IsValid())
            {
                SteamMatchmaking.LeaveLobby(CurrentLobby);
                CurrentLobby = CSteamID.Nil;
            }
        }

        private void OnDestroy()
        {
            if (_nm != null)
            {
                _nm.ClientManager.OnClientConnectionState -= OnClientConnectionState;
                _nm.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            }

            // Burada Steamworks API çağrısı YOK (lobi çıkışı OnApplicationQuit'te) — yalnız
            // abonelik/dispose temizliği.
            _joinRequested?.Dispose();
            foreach (var pending in _pendingJoins)
                pending?.Dispose();
            _pendingJoins.Clear();
            foreach (var pending in _pendingCreates)
                pending?.Dispose();
            _pendingCreates.Clear();
        }

        // ---------------- Merkezi çıkış (idempotent) ----------------

        /// <summary>
        /// Tüm çıkış/iptal yollarının ortak ucu: lobiden çık, başlamış server/client bağlantılarını
        /// durdur (kısmi başlatmalar dahil), state'i sıfırla, eski akış neslini geçersiz kıl.
        /// İki kez çağrılması güvenlidir.
        /// </summary>
        public void LeaveSession()
        {
            if (_state == LobbyState.Leaving)
                return; // kapanış zaten yürüyor — ikinci istek yeni bir çıkış başlatmaz

            // Kurulum guard'ı: `Awake` bozuk kurulumda component'i `enabled = false` yapıyor
            // ama DISABLED bir component'in public metotları dışarıdan yine çağrılabilir. Kapanış
            // ölçütü artık fail-closed olduğu için (Multipass okunamıyorsa "durdu" DENMEZ), böyle
            // bir çağrı `Leaving`'e girip oradan hiç çıkamazdı — Host/Join kalıcı olarak kilitlenirdi.
            // Kapanış akışına hiç girmemek doğru davranış: ortada durdurulacak bir oturum da yok.
            if (_nm == null || _multipass == null)
            {
                Debug.LogError("[SteamLobby] Kurulum eksik (NetworkManager/Multipass yok) — " +
                               "LeaveSession yok sayıldı.");
                return;
            }

            _generation++;

            // State ÖNCE Leaving'e (eskiden doğrudan Idle'dı): StopConnection'lar
            // OnClientConnectionState'i tetikler; handler Idle/Leaving görünce yeniden temizlik
            // yapmaz (reentrancy koruması aynen korunuyor).
            //
            // NEDEN IDLE DEĞİL: Idle "Host/Join serbest" demek, ama transport'lar o anda
            // henüz durmamış olabiliyor — Tugboat `Stopped` olayını KUYRUĞA koyuyor, olay sonraki
            // transport iterasyonunda geliyor. Ayrıl→hemen yeni Host dizisinde eski transport'un
            // gecikmiş `Stopped`'ı yeni oturumun üstüne düşüyor ve `OnClientConnectionState`
            // (state artık Idle değil) TAZE oturumu iptal ediyordu. Leaving, "kapanış istendi ama
            // bitmedi" aralığını temsil eder ve o aralıkta yeni oturum açılmasını engeller.
            _state = LobbyState.Leaving;
            _expectedLobby = CSteamID.Nil;

            if (CurrentLobby.IsValid())
            {
                SteamMatchmaking.LeaveLobby(CurrentLobby);
                CurrentLobby = CSteamID.Nil;
            }

            // Mandallar stop İSTEKLERİNDEN ÖNCE ve KÖTÜMSER kurulur. Sebep ince: FishySteamworks
            // senkron kapanıyor, yani `OnClientConnectionState(Stopped)` `StopConnection()`'ın
            // İÇİNDE ateşlenebilir. Mandalları sonradan kursaydık o an ikisi de false olurdu ve
            // handler'ın çağırdığı TryFinishLeave "beklenen yok" deyip server HİÇ DURDURULMADAN
            // Idle'a geçerdi. Kötümser başlayınca senkron olay yalnız kendi mandalını düşürür,
            // öteki hâlâ bekleniyor olduğu için kapanış erken bitmez.
            _awaitingClientStop = true;
            _awaitingServerStop = true;

            // Sıralamayı (client önce, server sonra) BİLEREK değiştirmiyoruz: Multipass'in kendi
            // Shutdown'ı da aynı sırayı kullanıyor ve server'ı öne almak "client Stopped geldiğinde
            // server kesin durmuştur" garantisini zaten vermiyor (iki olay ayrı transport
            // callback'lerinden geçiyor). Kapanışın bittiğini OLAYLARDAN öğreniyoruz.
            _nm.ClientManager.StopConnection();
            _nm.ServerManager.StopConnection(true);

            // Uzlaştırma: HİÇ BAŞLATILMAMIŞ taraf için olay gelmez (zaten Stopped'dı), o yüzden
            // kötümser mandal burada gerçek transport durumuna göre düşürülür. Olaylar senkron
            // gelmişse bu satırlar no-op'tur. İkisi birlikte, "sonsuza dek Leaving" ve "erken Idle"
            // uçlarının ikisini de kapatır.
            if (IsTransportFullyStopped(false))
                _awaitingClientStop = false;
            if (IsTransportFullyStopped(true))
                _awaitingServerStop = false;

            TryFinishLeave();
        }

        /// <summary>
        /// Kapanış GERÇEKTEN bitti mi: beklenen tarafların hepsi `Stopped` olayını verdiyse Idle'a düş.
        /// Tek nokta — hem LeaveSession'ın sonundan hem de gecikmeli client/server state olaylarından
        /// çağrılır, bu yüzden olaylar hangi sırayla gelirse gelsin doğru çalışır.
        ///
        /// ÖLÇÜT `ClientManager.Started` DEĞİL: FishNet o bayrağı
        /// `Started = state == LocalConnectionState.Started` diye yazıyor, yani **Stopping sırasında
        /// da false**. Bayrağa bakan sürüm, transport hâlâ kapanırken "ikisi de durdu" deyip erken
        /// Idle'a geçiyordu — düzeltmeye çalıştığı yarışı aynen bırakıyordu. Artık GERÇEK `Stopped`
        /// olayları mandala alınıyor; hiç başlamamış taraf için baştan beklenmiyor.
        /// </summary>
        private void TryFinishLeave()
        {
            if (_state != LobbyState.Leaving)
                return;
            if (_awaitingClientStop || _awaitingServerStop)
                return;

            _state = LobbyState.Idle;
            Debug.Log("[SteamLobby] Oturum tamamen kapandı — Host/Join yeniden serbest.");
        }

        /// <summary>Bir transport'un ŞU ANDA gerçekten durmuş olup olmadığı — `Stopping` durduğa
        /// sayılmaz. Mantık <see cref="TransportState"/>'te: sahne yöneticisi de aynı soruyu
        /// soracak ve bu ince ayrımı iki yerde kopyalamak, aynı hatayı iki yerde yapmak demekti
        ///. İki state makinesi bağımsız kalır, yalnız bu saf sorgu paylaşılır.</summary>
        private bool IsTransportFullyStopped(bool server)
            => server ? TransportState.IsServerFullyStopped(_multipass)
                      : TransportState.IsClientFullyStopped(_multipass);

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;

            // Kapanış sürüyorsa bu olay YENİ bir temizlik başlatmaz, yalnız kapanışı tamamlar.
            if (_state == LobbyState.Leaving)
            {
                _awaitingClientStop = false;
                TryFinishLeave();
                return;
            }

            if (_state != LobbyState.Idle)
            {
                Debug.Log("[SteamLobby] Bağlantı kapandı — oturum temizleniyor.");
                LeaveSession();
            }
        }

        /// <summary>Server tarafı da kapanışın parçası — client'tan sonra durabiliyor, bu yüzden
        /// Idle'a düşme kararı iki olaydan hangisi SONRA gelirse ondan tetiklenir.</summary>
        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            // Lokal host'un ikinci adımı: server'ın GERÇEKTEN başlamasını burada öğreniyoruz.
            if (_state == LobbyState.StartingLocalHost)
            {
                if (args.ConnectionState == LocalConnectionState.Started)
                {
                    CompleteLocalHost();
                    return;
                }
                if (args.ConnectionState == LocalConnectionState.Stopped)
                {
                    // Bind başarısız (port dolu, ikinci instance). Client'ı HİÇ bağlamadık —
                    // kendini host sanan client sorunu burada kesiliyor.
                    Debug.LogError("[SteamLobby] Lokal server ayağa kalkamadı (port dolu olabilir) — " +
                                   "oturum iptal. Aynı makinede ikinci host açıyorsan onun yerine " +
                                   "Client (Lokal) ile bağlan.");
                    LeaveSession();
                    return;
                }
                return; // Starting — beklemeye devam
            }

            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;

            // Multipass'te birden fazla server transport'u var (Steam + Tugboat aggregate). Bu olay
            // BİRİNİN kapandığını söyler, hepsinin değil — o yüzden mandalı olayın kendisiyle değil,
            // tüm transport'ların durumunu okuyarak kaldırıyoruz.
            if (IsTransportFullyStopped(true))
                _awaitingServerStop = false;

            TryFinishLeave();
        }

        // ---------------- Steam yolu ----------------

        public void HostSteam()
        {
            if (!SteamManager.Initialized || _steamTransport == null)
            {
                Debug.LogError("[SteamLobby] Steam init değil veya transport eksik — HostSteam kullanılamaz.");
                return;
            }
            if (_state != LobbyState.Idle)
            {
                Debug.LogWarning($"[SteamLobby] {_state} durumunda yeni host isteği yok sayıldı — önce Ayrıl.");
                return;
            }
            if (RejectedByPendingMenuLoad("yeni host isteği"))
                return;

            _state = LobbyState.Creating;
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, _maxPlayers);
            if (call == SteamAPICall_t.Invalid)
            {
                // Invalid handle'da CallResult kaydedilmez → callback asla gelmez, state Creating'de
                // asılı kalırdı.
                Debug.LogError("[SteamLobby] CreateLobby isteği başlatılamadı (invalid call handle).");
                LeaveSession();
                return;
            }

            // Her create denemesi KENDİ CallResult'ını ve İSTEK ANINDAKİ neslini taşır — join yoluyla
            // aynı desen. Eskiden tek bir `_lobbyCreated` nesnesi `Set` ile yeniden
            // kullanılıyordu ve iki hata birden vardı: ① `Set()` önceki handle'ı UNREGISTER ediyor,
            // yani Ayrıl→hemen yeni Host dizisinde ilk isteğin callback'i hiç gelmiyordu; başarıyla
            // dönen o lobide Steam bizi otomatik üye yaptığı için görünmeyen hayalet üyelik kalıyordu.
            // ② Nesil callback BAŞLARKEN okunuyordu, yani "bu callback eski bir isteğe mi ait"
            // sorusunu hiç cevaplamıyor, yalnız callback'in kendi içinde abort olup olmadığına
            // bakıyordu. Şimdi nesil istek gönderilirken kapanıyor ve handler her zaman kayıtlı.
            int gen = _generation;
            CallResult<LobbyCreated_t> attempt = null;
            attempt = CallResult<LobbyCreated_t>.Create((created, ioFailure) =>
            {
                _pendingCreates.Remove(attempt);
                attempt.Dispose();
                HandleLobbyCreated(created, ioFailure, gen);
            });
            _pendingCreates.Add(attempt);
            attempt.Set(call);
        }

        private void HandleLobbyCreated(LobbyCreated_t result, bool ioFailure, int requestGeneration)
        {
            int gen = requestGeneration;
            CSteamID created = new CSteamID(result.m_ulSteamIDLobby);

            // Beklenmeyen/iptal edilmiş istek: kurulmuşsa lobiyi terk et. Nesil kontrolü ARTIK BURADA,
            // çünkü iptal edilmiş bir deneme sonradan BAŞARIYLA dönebilir — state o sırada yeni bir
            // oturum yüzünden tekrar Creating olsa bile bu lobi bize ait değildir.
            if (requestGeneration != _generation || _state != LobbyState.Creating)
            {
                if (created.IsValid())
                    SteamMatchmaking.LeaveLobby(created);
                return;
            }

            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Debug.LogError($"[SteamLobby] Lobi kurulamadı: {result.m_eResult}");
                LeaveSession();
                return;
            }

            CurrentLobby = created;

            // Her adım başarıya bağlı: biri düşerse merkezi abort — yarım oturum "hazır" olmaz.
            if (!SteamMatchmaking.SetLobbyData(CurrentLobby, HostAddressKey, SteamUser.GetSteamID().ToString()))
            {
                Debug.LogError("[SteamLobby] Lobi metadata yazılamadı — oturum iptal.");
                LeaveSession();
                return;
            }

            // Steam host: aggregate başlatma bilinçli — Tugboat da dinler (aynı makinede MPPM client
            // katılabilsin); herhangi biri başarısızsa oturum iptal.
            if (!_nm.ServerManager.StartConnection())
            {
                Debug.LogError("[SteamLobby] Server başlatılamadı — oturum iptal.");
                LeaveSession();
                return;
            }

            if (!ConnectSteamClient(SteamUser.GetSteamID().ToString()))
            {
                Debug.LogError("[SteamLobby] Host client bağlantısı başlatılamadı — oturum iptal.");
                LeaveSession();
                return;
            }

            if (gen != _generation)
                return; // adımlar sırasında abort olduysa hazır ilan etme

            _state = LobbyState.Hosting;
            SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
            Debug.Log($"[SteamLobby] Lobi hazır: {CurrentLobby} — davet penceresi açıldı (Shift+Tab).");
        }

        /// <summary>
        /// Steam davet penceresini açar. HOST DA CLIENT DA çağırabilir: Steam'de lobiye üye olan
        /// herkes arkadaş davet edebilir ve `CurrentLobby` katılma yolunda client'ta da doluyor
        /// (bkz. JoinLobby). Eskiden yalnız `Hosting` kabul ediliyordu, bu yüzden client'ın davet
        /// düğmesi ölüydü — arayüzde gizlemek zorunda kalınmıştı.
        ///
        /// Kontratı BAŞLATMA yetkisiyle karıştırılmamalı: orası bilinçli olarak host-only kalıyor
        /// (Tasarım sapmaları ②). Davet zararsız, başlatma değil.
        /// </summary>
        public void OpenInviteDialog()
        {
            bool inLobby = _state == LobbyState.Hosting || _state == LobbyState.Connected;
            if (inLobby && CurrentLobby.IsValid())
                SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            if (_state != LobbyState.Idle)
            {
                Debug.LogWarning($"[SteamLobby] {_state} durumunda davet yok sayıldı — önce Ayrıl.");
                return;
            }
            if (RejectedByPendingMenuLoad("davet"))
                return;

            _state = LobbyState.Joining;
            _expectedLobby = request.m_steamIDLobby;

            // Join sonucu, Valve'ın öngördüğü gibi bu isteğin CALL HANDLE'ına bağlanır. Her deneme
            // KENDİ CallResult'ını taşır: iptal edilen deneme sonradan başarılı dönerse handler'ı
            // hâlâ kayıtlıdır ve hayalet lobi üyeliğini kendisi temizler.
            SteamAPICall_t call = SteamMatchmaking.JoinLobby(request.m_steamIDLobby);
            if (call == SteamAPICall_t.Invalid)
            {
                Debug.LogError("[SteamLobby] JoinLobby isteği başlatılamadı (invalid call handle).");
                LeaveSession();
                return;
            }

            int gen = _generation;
            CallResult<LobbyEnter_t> attempt = null;
            attempt = CallResult<LobbyEnter_t>.Create((entered, ioFailure) =>
            {
                _pendingJoins.Remove(attempt);
                attempt.Dispose();
                HandleJoinResult(entered, ioFailure, gen);
            });
            _pendingJoins.Add(attempt);
            attempt.Set(call);
        }

        private void HandleJoinResult(LobbyEnter_t entered, bool ioFailure, int requestGeneration)
        {
            CSteamID lobby = new CSteamID(entered.m_ulSteamIDLobby);
            var response = (EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse;
            bool success = !ioFailure && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;

            // Eski nesil / iptal edilmiş deneme: state'e dokunma; başarıyla girildiyse Steam
            // tarafında hayalet üyelik bırakma.
            if (requestGeneration != _generation || _state != LobbyState.Joining || lobby != _expectedLobby)
            {
                if (success && lobby.IsValid() && lobby != CurrentLobby)
                    SteamMatchmaking.LeaveLobby(lobby);
                return;
            }

            if (!success)
            {
                Debug.LogError($"[SteamLobby] Lobiye giriş başarısız: ioFailure={ioFailure}, response={response}");
                LeaveSession();
                return;
            }

            CurrentLobby = lobby;
            _expectedLobby = CSteamID.Nil;

            string hostId = SteamMatchmaking.GetLobbyData(CurrentLobby, HostAddressKey);
            if (!ulong.TryParse(hostId, out ulong hostSteamId) || hostSteamId == 0)
            {
                Debug.LogError($"[SteamLobby] Lobi verisindeki host id geçersiz: '{hostId}' — oturum iptal.");
                LeaveSession();
                return;
            }

            if (!ConnectSteamClient(hostSteamId.ToString()))
            {
                Debug.LogError("[SteamLobby] Client bağlantısı başlatılamadı — oturum iptal.");
                LeaveSession();
                return;
            }

            _state = LobbyState.Connected;
        }

        private bool ConnectSteamClient(string hostSteamId)
        {
            if (_steamTransport == null)
            {
                Debug.LogError("[SteamLobby] FishySteamworks transport yok — bağlanılamıyor.");
                return false;
            }
            _multipass.SetClientTransport<global::FishySteamworks.FishySteamworks>();
            _steamTransport.SetClientAddress(hostSteamId);
            return _nm.ClientManager.StartConnection();
        }

        // ---------------- Lokal yol (Tugboat — MPPM/günlük iterasyon) ----------------

        private int GetTransportIndex<T>() where T : Transport
        {
            for (int i = 0; i < _multipass.Transports.Count; i++)
            {
                if (_multipass.Transports[i] is T)
                    return i;
            }
            return -1;
        }

        public void HostLocal()
        {
            if (_state != LobbyState.Idle)
            {
                Debug.LogWarning($"[SteamLobby] {_state} durumunda HostLocal yok sayıldı — önce Ayrıl.");
                return;
            }
            if (RejectedByPendingMenuLoad("HostLocal"))
                return;

            // Yalnız Tugboat index'i başlatılır: aggregate başlatma Steam kapalıyken
            // FishySteamworks'te başarısız olur ve abort Tugboat'ı da kapatırdı — "lokal yol Steam'siz
            // çalışır" garantisi bozulurdu.
            int tugboatIndex = GetTransportIndex<FishNet.Transporting.Tugboat.Tugboat>();
            if (tugboatIndex < 0)
            {
                Debug.LogError("[SteamLobby] Multipass listesinde Tugboat yok — lokal host kurulamıyor.");
                return;
            }

            if (!_multipass.StartConnection(true, tugboatIndex))
            {
                Debug.LogError("[SteamLobby] Lokal server başlatılamadı.");
                LeaveSession();
                return;
            }

            // CLIENT HENÜZ BAĞLANMAZ (Ozanay'ın bulgusu, 2026-08-06). `StartConnection` "server
            // BAŞLADI" demez, "istek KABUL EDİLDİ" der — Tugboat hemen `Starting`'e geçer, bind
            // sonucu ASENKRON gelir. Port doluysa (ikinci bir instance Host Lokal derse) server
            // sessizce başlayamaz AMA client 127.0.0.1:7770'e, yani MEVCUT HOST'a bağlanırdı ve
            // biz `Hosting` yazardık: kendini host sanan bir client. Host-only arayüzler
            // (davet butonu) o pencerede yanlış aktifleşiyordu.
            // Artık server'ın GERÇEKTEN `Started` olması beklenir; sonucu OnServerConnectionState verir.
            _state = LobbyState.StartingLocalHost;
        }

        /// <summary>
        /// Lokal host'un ikinci adımı: server gerçekten ayağa kalktı, şimdi kendi client'ımızı bağla.
        /// Ayrı bir adım olmasının sebebi yukarıda — "istek kabul edildi" ≠ "başladı".
        /// </summary>
        private void CompleteLocalHost()
        {
            _multipass.SetClientTransport<FishNet.Transporting.Tugboat.Tugboat>();
            if (!_nm.ClientManager.StartConnection())
            {
                Debug.LogError("[SteamLobby] Lokal host client başlatılamadı — oturum iptal.");
                LeaveSession();
                return;
            }
            _state = LobbyState.Hosting; // lobisiz lokal oturum — CurrentLobby Nil kalır
            Debug.Log("[SteamLobby] Lokal host hazır.");
        }

        public void ConnectLocal()
        {
            if (_state != LobbyState.Idle)
            {
                Debug.LogWarning($"[SteamLobby] {_state} durumunda ConnectLocal yok sayıldı — önce Ayrıl.");
                return;
            }
            if (RejectedByPendingMenuLoad("ConnectLocal"))
                return;
            _multipass.SetClientTransport<FishNet.Transporting.Tugboat.Tugboat>();
            if (!_nm.ClientManager.StartConnection())
            {
                Debug.LogError("[SteamLobby] Lokal client başlatılamadı.");
                return;
            }
            _state = LobbyState.Connected;
        }

        // ---------------- Aşama 0 test paneli ----------------

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!_drawDebugPanel)
                return;

            GUILayout.BeginArea(new Rect(Screen.width - 190f, 10f, 180f, 220f));
            bool steamOk = SteamManager.Initialized && _steamTransport != null;

            if (_state == LobbyState.Idle)
            {
                GUI.enabled = steamOk;
                if (GUILayout.Button("Host (Steam)"))
                    HostSteam();

                GUI.enabled = true;
                if (GUILayout.Button("Host (Lokal)"))
                    HostLocal();
                if (GUILayout.Button("Client (Lokal)"))
                    ConnectLocal();

                if (!steamOk)
                    GUILayout.Label("Steam: KAPALI (lokal mod)");
            }
            else
            {
                GUILayout.Label($"Durum: {_state}");

                if (_state == LobbyState.Hosting && CurrentLobby.IsValid())
                {
                    if (GUILayout.Button("Davet Et"))
                        OpenInviteDialog();
                }

                if (GUILayout.Button("Ayrıl / Durdur"))
                    LeaveSession();
            }

            GUILayout.EndArea();
        }
#endif
    }
}
