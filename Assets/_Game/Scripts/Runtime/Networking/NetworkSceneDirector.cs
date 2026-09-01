using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using SunsetExpress.Coffins;
using SunsetExpress.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace SunsetExpress.Networking
{
    /// <summary>
    /// Ağ üzerinden sahne geçişinin TEK giriş noktası (menü → Hub → level) ve oyuncu doğuşu.
    /// NetworkManager'ın üstünde yaşar, dolayısıyla sahneler arası kalıcıdır.
    ///
    /// NEDEN UNITY'NİN SceneManager'I YETMEZ: `LoadScene` yalnız çağrıldığı makinede çalışır —
    /// host geçer, client'lar menüde kalır. FishNet'in GLOBAL sahne yüklemesi hem herkesi taşır
    /// hem de SONRADAN katılan client'ı otomatik oraya çeker (saklanan SceneLoadData'dan).
    ///
    /// ═══ TASARIM: OYUNCU PERSIST ETMEZ, DESPAWN/RESPAWN ═══
    /// İlk tasarım oyuncuyu sahneler arası taşıyıp unload'dan önce tutuşları bırakan bir "bariyer"
    /// kuruyordu. Reddedildi: `SceneLoadData.MovedNetworkObjects` VARSAYILAN OLARAK BOŞ,
    /// yani `ReplaceOption.All` sırasında oyuncular zaten eski sahneyle yok olurdu — persistence
    /// varsayımı hiç gerçekleşmiyordu. Despawn yolu mevcut yaşam döngüsünü kullanır:
    /// `PlayerGrabber.OnStopServer` zaten `ServerReleaseHeld()` çağırıyor ve tutuş/doluluk/
    /// CarryVisual/uyarı kademesi/joint'i tek uçtan SENKRON temizliyor (FishNet
    /// `Deinitialize → OnStopServer` senkron çalışır, tick beklemek gerekmez). Ekstra public
    /// "release" ucuna veya "yeni grab'i engelle" kapısına gerek kalmıyor.
    ///
    /// ═══ ÜÇ TETİKLEYİCİ AYRI İŞ YAPAR ═══
    ///   OnClientLoadedStartScenes  → ilk Hub geçişini başlatır (yalnız asServer + LOKAL client)
    ///   OnLoadEnd                  → server'da spawn marker'larını toplar
    ///   OnClientPresenceChangeEnd  → o bağlantının Player'ını doğurur
    /// Ham "server başladı" tetikleyicisi KULLANILMAZ: SteamLobby'de server, host client'ından
    /// ÖNCE başlıyor — o an geçiş başlatmak lokal client'ı arkada bırakır.
    ///
    /// ═══ MENÜYE DÖNÜŞ: DÖRT ABONELİK, TEK KARAR NOKTASI ═══
    /// Koşulun kendisi kolay, YENİDEN DEĞERLENDİRİLMESİ zor. Üç sessiz tuzak var:
    ///   • Yalnız client olayını dinlemek YETMEZ. SteamLobby önce client'ı durduruyor;
    ///     FishySteamworks senkron `Stopped` veriyor ve o an server HÂLÂ ÇALIŞIYOR → koşul false.
    ///     Tugboat server sonra kapanıyor ama dinleyen yoksa host menüye HİÇ dönemiyor.
    ///   • "Sahne kuyruğu boş" POLL EDİLEMEZ (public property değil) ve `OnLoadEnd` kuyruk
    ///     boşalmadan ateşleniyor — gerçek sınır `OnQueueEnd`.
    ///   • Server kuyruk KOŞARKEN durursa FishNet kuyruğu `ResetValues()` ile temizliyor ama
    ///     `OnQueueEnd` YAYINLAMIYOR. Mandal true'da asılı kalır, menüye dönülmez ve
    ///     `_hubTransitionStarted` sıfırlanmadığı için SONRAKİ OTURUM HUB'A HİÇ GEÇMEZ.
    ///     Bu yüzden tam duruşta mandallar olayla değil UZLAŞTIRMAYLA temizlenir.
    /// Dört olay da (client state, server state, queue start, queue end) aynı
    /// <see cref="TryReturnToMenu"/> noktasına gider; hangisi sonra gelirse gelsin doğru çalışır.
    ///
    /// "Durdu mu" ölçütü <see cref="TransportState"/>'ten okunur, `IsClientStarted`/
    /// `IsServerStarted` bayraklarından DEĞİL — FishNet o bayrakları `Stopping` sırasında da
    /// false yapıyor ve bu oturumda aynı hata iki kez yapıldı.
    /// </summary>
    public sealed class NetworkSceneDirector : MonoBehaviour
    {
        [Header("Sahneler (Build Settings'te ekli olmalı)")]
        [Tooltip("Lobi kurulunca herkesin çekileceği sahne.")]
        [SerializeField] private string _hubSceneName = "Hub_Test";

        [Tooltip("Oturum kapanınca dönülecek OFFLINE sahne. Ağ yok, Unity'nin kendi yükleyicisi kullanılır.")]
        [SerializeField] private string _menuSceneName = "MainMenu";

        [Header("Oyuncu")]
        [Tooltip("Spawn edilecek Player prefab'ı. NetworkManager'ın Spawnable Prefabs listesinde " +
                 "KAYITLI olmalı, yoksa spawn sessizce başarısız olur.")]
        [SerializeField] private NetworkObject _playerPrefab;

        [Tooltip("Hiç PlayerSpawnPoint bulunamazsa kullanılacak yedek konum. Sahneye işaretçi " +
                 "koymayı unutmak oyunu başlatılamaz hale getirmesin (fail-soft).")]
        [SerializeField] private Vector3 _fallbackSpawnPosition = new(0f, 2f, 0f);

        private NetworkManager _nm;
        private Multipass _multipass;
        private bool _subscribed;

        private bool _hubTransitionStarted;  // oturum başına bir kez
        private bool _transitionActive;      // LoadNetworkScene kilidi — çift geçiş isteğini reddeder
        private bool _returningToMenu;       // menü yüklemesi bir kez; sahne GERÇEKTEN yüklenince açılır
        private bool _sceneQueueActive;      // OnQueueStart/End mandalı (kuyruk durumu poll edilemez)
        private bool _sessionWasActive;      // hiç oturum açıldı mı — açılmadan "menüye dön" tetiklenmesin
        private int _pendingMenuSceneHandle; // bekleyen menü yüklemesi — ADLA değil HANDLE ile eşleşir

        /// <summary>Marker'lar SAHNE BAŞINA tutulur. Tek global liste yanlıştı: additive bir
        /// sahne yüklenince önceki sahnenin geçerli marker'ları siliniyordu, ve spawn edilen obje
        /// `args.Scene`'e yerleştirilirken BAŞKA sahnenin marker pozu seçilebiliyordu.</summary>
        private readonly Dictionary<int, List<Transform>> _spawnPoints = new();
        private readonly Dictionary<int, int> _nextSpawnIndex = new();

        /// <summary>Sahne başına tabut rigidbody'lerinin başlangıç pozu — playtest reset'i için.</summary>
        private readonly Dictionary<int, List<RigidbodyRestPose>> _coffinRestPoses = new();

        private void Awake()
        {
            _nm = GetComponentInParent<NetworkManager>();
            if (_nm == null)
                _nm = FindFirstObjectByType<NetworkManager>();

            // NetworkManager'ın `DefaultExecutionOrder(short.MinValue)`'ı sıralamayı garanti ediyor,
            // yani normal akışta alt manager'lar hazır. AMA NetworkManager doğrulama/duplicate
            // yüzünden kendi Awake'inden ERKEN ÇIKARSA alt manager'lar kurulmamış olur —
            // o durumda null-reference yerine fail-loud çıkılır.
            if (_nm == null || _nm.SceneManager == null || _nm.ClientManager == null || _nm.ServerManager == null)
            {
                Debug.LogError("[SceneDirector] NetworkManager (veya alt manager'ları) hazır değil — " +
                               "sahne geçişi devre dışı.", this);
                enabled = false;
                return;
            }

            _multipass = TransportState.GetMultipass(_nm);
            if (_multipass == null)
            {
                Debug.LogError("[SceneDirector] TransportManager.Transport bir Multipass değil — kurulum eksik.", this);
                enabled = false;
                return;
            }

            if (_playerPrefab == null)
                Debug.LogError("[SceneDirector] Player prefab atanmamış — Hub'da kimse doğmayacak.", this);

            Subscribe();
        }

        private void OnDestroy() => Unsubscribe();

        /// <summary>Tüm abonelikler TEK yerde — dağınık `+=`'ler eşleşmeyen `-=`'lere yol açıyor.</summary>
        private void Subscribe()
        {
            if (_subscribed || _nm == null)
                return;
            _subscribed = true;

            _nm.SceneManager.OnClientLoadedStartScenes += HandleClientLoadedStartScenes;
            _nm.SceneManager.OnLoadEnd += HandleLoadEnd;
            _nm.SceneManager.OnClientPresenceChangeEnd += HandleClientPresenceChangeEnd;
            _nm.SceneManager.OnQueueStart += HandleQueueStart;
            _nm.SceneManager.OnQueueEnd += HandleQueueEnd;
            _nm.ClientManager.OnClientConnectionState += HandleClientConnectionState;
            _nm.ServerManager.OnServerConnectionState += HandleServerConnectionState;
            UnitySceneManager.sceneLoaded += HandleUnitySceneLoaded;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _nm == null)
                return;
            _subscribed = false;

            _nm.SceneManager.OnClientLoadedStartScenes -= HandleClientLoadedStartScenes;
            _nm.SceneManager.OnLoadEnd -= HandleLoadEnd;
            _nm.SceneManager.OnClientPresenceChangeEnd -= HandleClientPresenceChangeEnd;
            _nm.SceneManager.OnQueueStart -= HandleQueueStart;
            _nm.SceneManager.OnQueueEnd -= HandleQueueEnd;
            _nm.ClientManager.OnClientConnectionState -= HandleClientConnectionState;
            _nm.ServerManager.OnServerConnectionState -= HandleServerConnectionState;
            UnitySceneManager.sceneLoaded -= HandleUnitySceneLoaded;
        }

        // ---------------- Public kontrat (Ozanay'ın ilan panosu bunu çağırır) ----------------

        /// <summary>
        /// Herkesi <paramref name="sceneName"/>'e taşır. SERVER'da çağrılır; mevcut sahneleri
        /// değiştirir ve sonradan katılanlar da otomatik oraya düşer.
        ///
        /// Ozanay: ilan panosu etkileşimi ve level'a geçiş için gereken tek uç budur —
        /// `ReplaceOption`, `SceneLoadData`, despawn sırası vs. burada kapalıdır.
        /// Devam eden bir geçiş varken ikinci çağrı REDDEDİLİR (tıklama tekrarı güvenli).
        /// </summary>
        public void LoadNetworkScene(string sceneName)
        {
            if (_nm == null || !_nm.IsServerStarted)
            {
                Debug.LogWarning($"[SceneDirector] LoadNetworkScene('{sceneName}') yalnız SERVER'da " +
                                 "çağrılabilir — yok sayıldı.", this);
                return;
            }
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                Debug.LogError("[SceneDirector] Sahne adı boş — geçiş yapılmadı.", this);
                return;
            }

            // Geçiş kilidi: iki ilan panosu isteği aynı anda gelirse iki `ReplaceOption.All`
            // işlemi kuyruğa girer, oyuncular İKİ KEZ sökülür ve iki sahne geçişi art arda koşar.
            if (_transitionActive)
            {
                Debug.LogWarning($"[SceneDirector] Zaten bir sahne geçişi sürüyor — '{sceneName}' isteği " +
                                 "yok sayıldı.", this);
                return;
            }

            // DOĞRULAMA DESPAWN'DAN ÖNCE: sahne adı Build Settings'te yoksa yükleme hiç
            // başlamaz, ama oyuncular çoktan sökülmüş olur ve yeni presence olayı gelmediği için
            // BİR DAHA DOĞMAZLAR. Yazım hatası tüm oturumu boş bir dünyada bırakırdı.
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[SceneDirector] '{sceneName}' Build Settings'te yok veya kapalı — " +
                               "geçiş İPTAL (oyunculara dokunulmadı).", this);
                return;
            }

            // Hedef zaten yüklüyse geçiş NO-OP değil, ZARARLIDIR: FishNet sahneyi yeniden
            // yüklenebilir saymıyor ama kuyruk başlangıcını yine yayınlıyor ve ReplaceAll presence
            // kayıtlarını yeniliyor — yani oyuncular boşuna bir despawn/respawn turundan geçerdi.
            // Gerçekten "sahneyi yeniden başlat" gerekirse ayrı ve açık bir kontrat yazılır.
            Scene existing = UnitySceneManager.GetSceneByName(sceneName);
            if (existing.IsValid() && existing.isLoaded)
            {
                Debug.LogWarning($"[SceneDirector] '{sceneName}' zaten yüklü — geçiş yok sayıldı " +
                                 "(gereksiz despawn/respawn turu önlendi).", this);
                return;
            }

            _transitionActive = true;

            // Oyuncuları ÖNCE despawn et. Tabut eski sahneyle unload edilirken oyuncu hâlâ ona
            // joint'le bağlıysa OnStopServer hiç koşmaz ve joint bir fizik çözümü boyunca
            // connectedBody null ile canlı kalır (oyuncu bir an dünyaya bağlıymış gibi çekilir).
            DespawnAllPlayers();

            // ReplaceOption.All: MainMenu Unity'nin kendi yükleyicisiyle açılmış OFFLINE bir sahne;
            // OnlineOnly onu bırakırdı. Geç katılan client'ta da doğru — FishNet saklanan global
            // SceneLoadData'dan ReplaceScenes'i kopyalayıp gönderiyor.
            SceneLoadData data = new(sceneName) { ReplaceScenes = ReplaceOption.All };
            _nm.SceneManager.LoadGlobalScenes(data);
        }

        // ---------------- Geçiş ----------------

        /// <summary>
        /// Oturumun GERÇEKTEN hazır olduğu an: lokal client başlangıç sahnelerini bitirdi.
        /// Ham "server başladı" değil — SteamLobby'de server host client'ından önce başlıyor.
        /// Uzaktan bağlanan client'lar için `IsLocalClient` false olduğundan geçişi TEKRAR
        /// başlatmazlar; mevcut global sahneyi server'dan zaten alırlar.
        /// Bu callback'ten `LoadGlobalScenes` çağırmak güvenli: FishNet iç içe yükleme yapmıyor,
        /// isteği `QueueOperation` ile sıraya alıyor.
        /// </summary>
        private void HandleClientLoadedStartScenes(NetworkConnection conn, bool asServer)
        {
            if (!asServer)
                return;

            _sessionWasActive = true;

            if (!conn.IsLocalClient || _hubTransitionStarted)
                return;

            // DOĞRUDAN HUB'DA PLAY'E BASIP HOST OLMA: Hub zaten yüklüyse aşağıdaki geçiş
            // "zaten yüklü" diye reddedilir, ama `_hubTransitionStarted` true kalır ve sahne
            // FishNet üzerinden yüklenmediği için `OnClientPresenceChangeEnd` HİÇ gelmez —
            // yani HİÇ OYUNCU DOĞMAZ ve bir daha denenmez. Genel "zaten yüklü" uyarısı bu sonucu
            // anlatmıyor, o yüzden burada özel olarak reddediyoruz.
            // Bu akış BİLİNÇLİ OLARAK desteklenmiyor: mevcut sahneyi FishNet'e sonradan "global"
            // kaydettirmek marker toplamayı da ayrıca çözmeyi gerektirirdi. Giriş noktası Bootstrap.
            Scene hub = UnitySceneManager.GetSceneByName(_hubSceneName);
            if (hub.IsValid() && hub.isLoaded)
            {
                Debug.LogError($"[SceneDirector] '{_hubSceneName}' sahnesinden doğrudan host olunamaz — " +
                               "oyuncu doğmaz. Oyunu BOOTSTRAP sahnesinden (build index 0) başlat.", this);
                return;
            }

            _hubTransitionStarted = true;
            LoadNetworkScene(_hubSceneName);
        }

        /// <summary>
        /// Server'da yüklenen sahnelerin spawn işaretçilerini toplar — SAHNE BAŞINA.
        /// `QueueData.AsServer` filtresi ŞART: host'ta bu olay client tarafı için de
        /// ateşleniyor ve o çağrıda `LoadedScenes` BOŞ geliyor (FishNet host'ta sahneyi tekrar
        /// yüklemiyor). Filtresiz sürüm marker listesini o boş çağrıda siliyordu — herkes yedek
        /// konumda üst üste doğardı.
        /// </summary>
        private void HandleLoadEnd(SceneLoadEndEventArgs args)
        {
            if (_nm == null || !_nm.IsServerStarted)
                return;
            if (!args.QueueData.AsServer)
                return;

            for (int i = 0; i < args.LoadedScenes.Length; i++)
            {
                Scene scene = args.LoadedScenes[i];
                if (!scene.IsValid())
                    continue;

                List<Transform> points = new();
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    // includeInactive: true — işaretçiler çoğu zaman kapalı boş objeler olur.
                    PlayerSpawnPoint[] found = root.GetComponentsInChildren<PlayerSpawnPoint>(true);
                    for (int p = 0; p < found.Length; p++)
                        points.Add(found[p].transform);
                }

                _spawnPoints[scene.handle] = points;
                _nextSpawnIndex[scene.handle] = 0;
                SnapshotCoffins(scene);

                if (points.Count == 0)
                {
                    Debug.LogWarning($"[SceneDirector] '{scene.name}' sahnesinde PlayerSpawnPoint yok — " +
                                     $"oyuncular yedek konumda ({_fallbackSpawnPosition}) doğacak.", this);
                }
            }

            PruneDeadScenes();
        }

        /// <summary>Kapanan sahnelerin marker kayıtları birikmesin — sahne handle'ları geri kullanılır.</summary>
        private void PruneDeadScenes()
        {
            // `GetSceneByHandle` public API'de yok — yüklü sahneleri gezip handle kümesi kurulur.
            HashSet<int> alive = new();
            for (int i = 0; i < UnitySceneManager.sceneCount; i++)
            {
                Scene s = UnitySceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded)
                    alive.Add(s.handle);
            }

            List<int> dead = new();
            foreach (KeyValuePair<int, List<Transform>> kv in _spawnPoints)
            {
                if (!alive.Contains(kv.Key))
                    dead.Add(kv.Key);
            }
            foreach (int handle in dead)
            {
                _spawnPoints.Remove(handle);
                _nextSpawnIndex.Remove(handle);
                _coffinRestPoses.Remove(handle);
            }
        }

        /// <summary>
        /// Oyuncu doğuşu. `OnClientPresenceChangeEnd` seçildi çünkü "client sahneyi yükledi AMA
        /// henüz observer değil" penceresini kapatan TEK olay budur — `AddConnectionToScene`
        /// sırası: presence start → observer rebuild → presence end.
        /// </summary>
        private void HandleClientPresenceChangeEnd(ClientPresenceChangeEventArgs args)
        {
            if (_nm == null || !_nm.IsServerStarted || !args.Added)
                return;

            NetworkConnection conn = args.Connection;
            if (conn == null || !conn.IsActive || !conn.IsAuthenticated)
                return;

            // Karşılıklı dışlama: FishNet demo PlayerSpawner "bu bağlantının zaten oyuncusu
            // var mı" diye BAKMIYOR ve owner'ı default sahneye ekliyor — o ekleme bu handler'ı
            // tetikliyor. Doğrudan TestScene akışında iki yol birden koşarsa çift Player doğardı.
            // Demo dosyasını değiştirmiyoruz; kontrolü biz yapıyoruz.
            if (HasPlayer(conn))
                return;

            SpawnPlayerFor(conn, args.Scene);
        }

        private static bool HasPlayer(NetworkConnection conn)
        {
            // FirstObject'in Player OLDUĞU GARANTİ DEĞİL — yalnız kümedeki ilk objedir. Önce onu
            // dene (ucuz), sonra tüm sahipli objeleri tara.
            if (conn.FirstObject != null && conn.FirstObject.GetComponent<PlayerGrabber>() != null)
                return true;

            foreach (NetworkObject nob in conn.Objects)
            {
                if (nob != null && nob.GetComponent<PlayerGrabber>() != null)
                    return true;
            }
            return false;
        }

        private void SpawnPlayerFor(NetworkConnection conn, Scene scene)
        {
            if (_playerPrefab == null)
                return;

            GetSpawnPose(scene, out Vector3 position, out Quaternion rotation);

            NetworkObject nob = Instantiate(_playerPrefab, position, rotation);
            _nm.ServerManager.Spawn(nob, conn, scene);
        }

        /// <summary>
        /// Round-robin, OYUNCUNUN GİRDİĞİ SAHNENİN kendi marker'ları arasında: 4 oyuncu aynı
        /// noktaya doğarsa Rigidbody'ler iç içe girer ve PhysX onları ayırmak için fırlatır
        /// (GDD 6.1 — kaosun kaynağı tabut olmalı, spawn değil).
        /// </summary>
        private void GetSpawnPose(Scene scene, out Vector3 position, out Quaternion rotation)
        {
            if (_spawnPoints.TryGetValue(scene.handle, out List<Transform> points) && points.Count > 0)
            {
                int index = _nextSpawnIndex.TryGetValue(scene.handle, out int i) ? i : 0;

                // Yok edilmiş marker'ları atlayarak ilerle.
                for (int guard = 0; guard < points.Count; guard++)
                {
                    Transform point = points[index % points.Count];
                    index++;

                    if (point != null)
                    {
                        _nextSpawnIndex[scene.handle] = index;
                        position = point.position;
                        rotation = point.rotation;
                        return;
                    }
                }
                _nextSpawnIndex[scene.handle] = index;
            }

            position = _fallbackSpawnPosition;
            rotation = Quaternion.identity;
        }

        /// <summary>
        /// Ekibi Hub'a geri götürür — PLAYTEST ARACI, TAM RESET yolu.
        ///
        /// <see cref="PlaytestReset"/> hızlıdır ama kısmi: ceset kaybı, hasar sayacı ve engel
        /// durumları yerinde kalır. Hub'a dönüp panodan kontratı yeniden seçmek level'ı SIFIRDAN
        /// yükler ve her şeyi temizler — ve bunu mevcut, denenmiş sahne yolundan yapar.
        /// Yeni bir "sahneyi yeniden başlat" makinesi kurmaya gerek kalmıyor: director farklı bir
        /// sahneye geçmeyi zaten destekliyor, yalnız MEVCUT sahneyi reddediyor.
        /// </summary>
        public void ReturnToHub() => LoadNetworkScene(_hubSceneName);

        /// <summary>
        /// PLAYTEST RESET: oyuncuları spawn noktalarına, tabutları başlangıç pozlarına geri alır.
        ///
        /// SAHNE YENİDEN YÜKLENMEZ, bilinçli. <see cref="LoadNetworkScene"/> mevcut sahneyi bilerek
        /// reddediyor (FishNet onu yeniden yüklenebilir saymıyor; gerçek reset unload→load sıralaması
        /// ister ve o asenkron zincir ayrı bir kontrat olarak yazılmalı — yorumunda böyle diyor).
        /// Buradaki ihtiyaç ondan dar: "sıkıştık, bizi başa al".
        ///
        /// SIRA ÖNEMLİ: önce oyuncular sökülür, SONRA tabut ışınlanır. Ters sırada tabut, hâlâ
        /// bağlı ConfigurableJoint'lerle taşınırken yer değiştirir ve oyuncuları savurur ya da
        /// joint'i patlatırdı. Oyuncular despawn olunca joint'ler onlarla birlikte ölür, tabut
        /// serbest kalır.
        ///
        /// SINIRI: ceset durumu (Mod B'de düşmüş ceset) geri gelmez — "kayıp KALICIDIR" pazarlıksız
        /// kuralı (GDD 3.4/5.1) burada da geçerli. Hasar sayacı da sıfırlanmaz.
        ///
        /// Bağlantıların sahnesi despawn'DAN ÖNCE toplanır: oyuncu yok edilince onun sahnesini
        /// okuyacak bir referans kalmıyor.
        /// </summary>
        public void PlaytestReset()
        {
            if (_nm == null || !_nm.IsServerStarted)
            {
                Debug.LogWarning("[SceneDirector] PlaytestReset yalnız SERVER'da çağrılabilir.", this);
                return;
            }

            // Sahne geçişi sürerken karışma: geçiş zaten despawn/respawn yapıyor, araya girmek
            // çift spawn üretirdi.
            if (_transitionActive)
            {
                Debug.LogWarning("[SceneDirector] Sahne geçişi sürüyor — respawn isteği yok sayıldı.", this);
                return;
            }

            Dictionary<NetworkConnection, Scene> scenes = new();
            foreach (NetworkConnection conn in _nm.ServerManager.Clients.Values)
            {
                if (conn == null)
                    continue;

                foreach (NetworkObject nob in conn.Objects)
                {
                    if (nob == null || nob.GetComponent<PlayerGrabber>() == null)
                        continue;

                    scenes[conn] = nob.gameObject.scene;
                    break;
                }
            }

            DespawnAllPlayers();

            // Joint'ler oyuncularla birlikte öldü — tabut artık serbest, güvenle ışınlanabilir.
            int coffins = ResetCoffins();

            foreach (KeyValuePair<NetworkConnection, Scene> pair in scenes)
            {
                NetworkConnection conn = pair.Key;
                if (conn == null || !conn.IsActive || !conn.IsAuthenticated)
                    continue;

                Scene target = pair.Value.IsValid() && pair.Value.isLoaded
                    ? pair.Value
                    : UnitySceneManager.GetActiveScene();

                SpawnPlayerFor(conn, target);
            }

            Debug.Log($"[SceneDirector] Playtest reset — {scenes.Count} oyuncu yeniden doğuruldu, " +
                      $"{coffins} rigidbody başlangıç pozuna alındı.", this);
        }

        /// <summary>
        /// Sahne yüklenince tabutların başlangıç pozunu kaydeder. Tabutun ALTINDAKİ TÜM
        /// Rigidbody'ler alınır (gövde + kapak): kapak ayrı bir Rigidbody ve HingeJoint ile bağlı,
        /// yalnız gövdeyi ışınlamak kapağı joint'ten asılı bırakıp sertçe savururdu.
        ///
        /// `Coffin.cs`'e DOKUNULMUYOR: pozlar dışarıdan, bileşen üzerinden okunuyor. O dosya
        /// pazarlıksız kuralların merkezi, oraya playtest aracı için alan eklemek istemedik.
        /// </summary>
        private void SnapshotCoffins(Scene scene)
        {
            List<RigidbodyRestPose> poses = new();

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Coffin[] found = root.GetComponentsInChildren<Coffin>(true);
                for (int i = 0; i < found.Length; i++)
                {
                    Rigidbody[] bodies = found[i].GetComponentsInChildren<Rigidbody>(true);
                    for (int b = 0; b < bodies.Length; b++)
                        poses.Add(new RigidbodyRestPose(bodies[b]));
                }
            }

            _coffinRestPoses[scene.handle] = poses;
        }

        /// <summary>Kaydedilmiş tabut pozlarını geri yükler ve hızları sıfırlar. Kaç rigidbody
        /// geri alındığını döndürür (log için).</summary>
        private int ResetCoffins()
        {
            int count = 0;

            foreach (KeyValuePair<int, List<RigidbodyRestPose>> entry in _coffinRestPoses)
            {
                foreach (RigidbodyRestPose pose in entry.Value)
                {
                    if (!pose.Restore())
                        continue;
                    count++;
                }
            }

            return count;
        }

        /// <summary>Bir Rigidbody'nin başlangıç dünya pozu. Yok edilmiş gövdeyi sessizce atlar.</summary>
        private readonly struct RigidbodyRestPose
        {
            private readonly Rigidbody _body;
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;

            public RigidbodyRestPose(Rigidbody body)
            {
                _body = body;
                _position = body.position;
                _rotation = body.rotation;
            }

            public bool Restore()
            {
                if (_body == null)
                    return false;

                // Hızlar ÖNCE sıfırlanır: ışınlanmış ama hâlâ hızlı bir gövde, bir sonraki fizik
                // adımında eski hızıyla fırlar ve reset anlamsızlaşır.
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.position = _position;
                _body.rotation = _rotation;

                // Transform'a da yazback: manuel fizik adımında `Rigidbody.position` PhysX'e
                // yazılır ama `transform` BİR SONRAKİ simülasyona kadar eski kalır. Aynı karede
                // `coffin.transform`'u okuyan bir sistem (teslim kontrolü, kamera, IK) tabutu hâlâ
                // uçurumda görürdü. Oyuncu ışınlamasında bu hata sahada ölçüldü; aynı politikayı
                // burada da uyguluyoruz. Bkz. ışınlama politikası.
                _body.PublishTransform();
                return true;
            }
        }

        private void DespawnAllPlayers()
        {
            // Snapshot ŞART: despawn `conn.Objects` kümesini değiştirir, üstünde dolaşırken
            // despawn etmek koleksiyonu bozar. Host'un kendi oyuncusu da bu kümededir.
            List<NetworkObject> players = new();
            foreach (NetworkConnection conn in _nm.ServerManager.Clients.Values)
            {
                if (conn == null)
                    continue;

                foreach (NetworkObject nob in conn.Objects)
                {
                    if (nob != null && nob.GetComponent<PlayerGrabber>() != null)
                        players.Add(nob);
                }
            }

            foreach (NetworkObject nob in players)
            {
                if (nob != null && nob.IsSpawned)
                    _nm.ServerManager.Despawn(nob);
            }
        }

        // ---------------- Menüye dönüş ----------------

        /// <summary>
        /// Menü yüklemesi PLANLANDI ama daha gerçekleşmedi. Bu aralıkta YENİ OTURUM AÇILAMAZ
        /// (SteamLobby bunu okuyup Host/Join'i reddediyor).
        ///
        /// Neden zorunlu: Unity'nin `LoadScene`'i kare sonuna ertelenir ve **iptal API'si
        /// YOKTUR**. Son transport `Stopped` olunca menü yüklemesini planlıyoruz; aynı karede bir
        /// davet kabul edilip yeni oturum başlarsa, bekleyen `LoadSceneMode.Single` bir sonraki
        /// karede TAZE ağ dünyasını söker. Director'ın DontDestroyOnLoad olması sahne objelerini
        /// korumaz. Yüklemeyi iptal edemediğimize göre tek doğru çözüm, o pencerede oturum
        /// başlatmayı kapatmak.
        /// </summary>
        public bool IsReturningToMenu => _returningToMenu;

        private void HandleQueueStart() => _sceneQueueActive = true;

        private void HandleQueueEnd()
        {
            _sceneQueueActive = false;
            _transitionActive = false;
            TryReturnToMenu();
        }

        private void HandleClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
                _sessionWasActive = true;

            TryReturnToMenu();
        }

        private void HandleServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                _sessionWasActive = true;
                return;
            }

            // Client olayını dinlemek TEK BAŞINA yetmiyor (sınıf özeti, 1. tuzak) — ve server
            // TAMAMEN durduysa kuyruk mandallarını UZLAŞTIRMAK gerekiyor (3. tuzak): FishNet
            // `ResetValues()` ile kuyruğu temizliyor ama `OnQueueEnd` yayınlamıyor, yani kuyruk
            // koşarken kapanan bir oturumda mandal sonsuza dek true kalır ve menüye dönülmez.
            if (TransportState.IsServerFullyStopped(_multipass))
            {
                _sceneQueueActive = false;
                _transitionActive = false;
            }

            TryReturnToMenu();
        }

        /// <summary>
        /// Dört koşul da sağlandıysa offline menüye dön. Tek karar noktası — ilgili her olaydan
        /// çağrılır, bu yüzden olaylar hangi sırayla gelirse gelsin doğru çalışır.
        /// </summary>
        private void TryReturnToMenu()
        {
            if (_returningToMenu || !_sessionWasActive || _sceneQueueActive)
                return;
            if (!TransportState.IsSessionFullyStopped(_multipass))
                return;

            // Oturum bitti: bir sonraki lobi taze başlasın.
            _hubTransitionStarted = false;
            _sessionWasActive = false;
            _transitionActive = false;
            _spawnPoints.Clear();
            _nextSpawnIndex.Clear();

            // DOĞRULAMA BAYRAKTAN ÖNCE: `_returningToMenu` Host/Join'i KİLİTLİYOR ve
            // yalnız menü gerçekten yüklenince açılıyor. Sahne adı yanlışsa/Build Settings'te
            // yoksa yükleme hiç olmaz, `sceneLoaded` hiç gelmez ve dört giriş noktası SONSUZA DEK
            // reddedilir — oyun yeniden başlatılmadan lobi kurulamaz. Kilidi kurmadan önce
            // yükleyebileceğimizden emin oluyoruz; olamıyorsak fail-loud edip kilidi HİÇ kurmuyoruz
            // (oturum kapanmış olarak kalır ama en azından yeni lobi kurulabilir).
            if (!Application.CanStreamedLevelBeLoaded(_menuSceneName))
            {
                Debug.LogError($"[SceneDirector] Menü sahnesi '{_menuSceneName}' Build Settings'te yok " +
                               "veya kapalı — menüye dönülemiyor. Oturum kapandı, sahne değişmedi.", this);
                return;
            }

            Debug.Log("[SceneDirector] Oturum kapandı — ana menüye dönülüyor.");

            try
            {
                // Dönen `Scene`'in HANDLE'ı saklanır, adı DEĞİL: alana tam path yazılırsa
                // yükleme başarılı olur ama `scene.name` kısa ad döner ve ad karşılaştırması
                // TUTMAZ — kilit yine açılmazdı. Handle her iki yazımda da doğru eşleşir.
                Scene pending = UnitySceneManager.LoadScene(_menuSceneName,
                                                           new LoadSceneParameters(LoadSceneMode.Single));
                _pendingMenuSceneHandle = pending.handle;
                _returningToMenu = true;
            }
            catch (System.Exception e)
            {
                // Senkron hata: kilidi HİÇ kurmadık, kurtarmaya gerek yok — yalnız görünür olsun.
                Debug.LogError($"[SceneDirector] Menü yüklenemedi ('{_menuSceneName}'): {e.Message}", this);
            }
        }

        /// <summary>Menü GERÇEKTEN yüklendi — dönüş kilidi ancak burada açılır.</summary>
        private void HandleUnitySceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_returningToMenu && scene.handle == _pendingMenuSceneHandle)
            {
                _returningToMenu = false;
                _pendingMenuSceneHandle = 0;
            }
        }
    }
}
