using FishNet;
using FishNet.Connection;
using FishNet.Object;
using SunsetExpress.Coffins;
using SunsetExpress.Networking;
using SunsetExpress.Player;
using SunsetExpress.UI;
using UnityEngine;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Teslim noktası / mezar (GDD 3.1 "Gömme"): tabut çukura indirilince kontrat tamamlanır.
    ///
    /// TESLİM = TABUTUN BIRAKILMASI. Hacme değmek YETMEZ; kimsenin tutmuyor olması ve tabutun
    /// durmuş olması gerekir. İki gerekçe: ① GDD 3.1'in lafzı "tabut çukura İNDİRİLİR",
    /// ② mekanik olarak doğrusu bu — tabutu çukurun üstünde sallandırıp kontratı bitirmek
    /// mümkün olmamalı. Ekibin hep birlikte bırakması gereken bir final anı oluyor.
    ///
    /// OTORİTE: karar YALNIZ sunucuda verilir (hasar/ceset verisi sunucu-otoriter ve client'ın
    /// kendi kopyasından türetmesi ekranlar arası tutarsızlık üretirdi). Sonuç ObserversRpc ile
    /// yayılır; "Hub'a dön" yalnız host'ta çalışır ve sunucuda ayrıca doğrulanır
    /// (<see cref="ContractBoard"/> ile aynı desen — arayüzü gizlemek yetki değildir).
    ///
    /// TAMAMLANMA HAKKI NOKTADA DEĞİL TABUTTA (<see cref="ContractClaims"/>): iki örtüşen mezar
    /// hacmi aynı tabutu aynı karede teslim edip iki rapor yayınlayabiliyordu.
    ///
    /// OTURMA ÖLÇÜMÜ FİZİK ADIMINDA: koşul <c>OnPostPhysicsSimulation</c>'da her adım ölçülür ve
    /// adım delta'sı biriktirilir. Yoklamayla (0.1 sn) ölçerken iki yoklama arasına sığan kısa
    /// tutma/çarpma/çıkış-giriş kesintileri görülmüyordu — "kesintisiz" garantisi lafzen vardı,
    /// özü yoktu. Sahnede tabut ARAMA'sı pahalı olduğu için o ayrıca seyrekleştirilir.
    ///
    /// ⚠ TETİKLEYİCİ CONVEX OLMALI: içeride mi kontrolü <c>Collider.ClosestPoint</c> ile yapılır,
    /// o da convex olmayan MeshCollider'da çalışmaz. Box/Sphere/Capsule ya da convex mesh kullan.
    /// (Enter/Exit sayacı yerine bunu seçmenin sebebi: duran tabutun Rigidbody'si UYUR ve
    /// <c>OnTriggerStay</c> uyuyan cisim için çağrılmaz — teslimin tam da beklediği an sessiz
    /// kalırdı. Yoklama bu tuzağa hiç girmez.)
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class DeliveryPoint : NetworkBehaviour
    {
        [Header("Kontrat")]
        [Tooltip("YALNIZ YEDEK. Normalde rapor, hub'daki panodan SEÇİLEN kontratı gösterir — aynı " +
                 "level farklı merhumlarla oynanır, künye sahneye gömülemez. Bu alan yalnızca " +
                 "level doğrudan Play'e alındığında (hub'dan geçmeden) devreye girer; playtest " +
                 "kolaylığı içindir. Boşsa sahne adı yazılır.")]
        [SerializeField] private ContractDefinition _contract;

        [Header("Teslim koşulu")]
        [Tooltip("Oturma eşikleri (GDD 12.3 — sabitler profilde). Boşsa güvenli varsayılanlar kullanılır.")]
        [SerializeField] private DeliveryProfile _profile;

        // Profil yoksa kullanılan varsayılanlar. Teslim, oyun döngüsünün BİTİŞİ — eksik bir ayar
        // dosyası yüzünden kontrat tamamlanamaz hâle gelmemeli (fail-soft).
        private const float DefaultSettleDuration = 1.5f;
        private const float DefaultMaxSpeed = 0.5f;
        private const float DefaultMaxAngularSpeed = 0.6f;
        private const float DefaultSearchInterval = 0.25f;

        private Collider _volume;
        private float _contractStartTime;
        private float _settledTime;
        private bool _completed;

        private Coffin _trackedCoffin;
        private float _nextSearchTime;
        private bool _warnedVolumeTooSmall;
        private bool _warnedNoBodyBounds;

        private ContractReportPanel _panel;

        // Teşhis alanları guard'sız: arama bunları koşulsuz yazıyor ve guard'lasaydım sürüm
        // derlemesinde "tanımsız alan" hatası verirdi (bu sınıfta ölçülen tuzak).
        private Vector3 _diagNearestCenter;
        private bool _diagHasNearest;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private DeliveryState _reportedState = DeliveryState.Unknown;
        private float _nextOutsideLogTime;
#endif

        private float SettleDuration => _profile != null && _profile.settleDuration > 0.01f
            ? _profile.settleDuration : DefaultSettleDuration;

        private float MaxSpeed => _profile != null && _profile.maxSettleSpeed > 0.001f
            ? _profile.maxSettleSpeed : DefaultMaxSpeed;

        private float MaxAngularSpeed => _profile != null && _profile.maxSettleAngularSpeed > 0.001f
            ? _profile.maxSettleAngularSpeed : DefaultMaxAngularSpeed;

        private float SearchInterval => _profile != null && _profile.coffinSearchInterval > 0.001f
            ? _profile.coffinSearchInterval : DefaultSearchInterval;

        private void Awake()
        {
            _volume = GetComponent<Collider>();

            // Fail-loud: tetikleyici değilse tabut çukura giremez, çarpar. Sessizce "hiç teslim
            // olmuyor" diye görünürdü — sahne kurulumunda en kolay atlanan adım bu.
            if (_volume != null && !_volume.isTrigger)
            {
                Debug.LogError("[DeliveryPoint] Collider 'Is Trigger' DEĞİL — tabut çukura giremez " +
                               "ve teslim hiçbir zaman gerçekleşmez. Inspector'dan işaretle.", this);
            }

            // `NetworkObject` unutulursa `IsServerStarted` HİÇ açılmaz ve teslim sessizce hiç
            // çalışmaz. `RequireComponent` yerine doğrulama: NetworkObject ÜST objede de
            // olabilir ve RequireComponent orada gereksiz bir ikinci tane eklerdi.
            if (GetComponentInParent<NetworkObject>() == null)
            {
                Debug.LogError("[DeliveryPoint] NetworkObject YOK (bu objede de üst objelerde de). " +
                               "Sunucu mantığı hiç başlamaz ve teslim SESSİZCE çalışmaz — " +
                               "Inspector'dan Network Object ekle.", this);
            }

            if (_profile == null)
            {
                Debug.LogWarning("[DeliveryPoint] DeliveryProfile atanmamış — güvenli varsayılanlarla " +
                                 "çalışılıyor, ayarlar kalıcı olmayacak (GDD 12.3). " +
                                 "Create → Sunset Express → Delivery Profile.", this);
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Süre sayacı level yüklenince başlar: teslim noktası sahneyle birlikte doğuyor.
            _contractStartTime = Time.time;
            _completed = false;
            _settledTime = 0f;
            _trackedCoffin = null;

            TimeManager.OnPostPhysicsSimulation += ServerPostPhysics;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            if (TimeManager != null)
                TimeManager.OnPostPhysicsSimulation -= ServerPostPhysics;
        }

        /// <summary>
        /// Oturma koşulu HER FİZİK ADIMINDA ölçülür ve adım delta'sı biriktirilir. Koşul bir adım
        /// bile bozulursa sayaç sıfırlanır — "kesintisiz" garantisi ancak böyle gerçek olur
        /// (GDD 1.4'ün okunabilir tetikleyici disiplini: anlık tek kare karar vermemeli, ama
        /// aradaki kesintiyi de kaçırmamalı).
        /// </summary>
        private void ServerPostPhysics(float delta)
        {
            if (!IsServerStarted || _completed)
                return;

            Coffin coffin = ResolveCoffin();

            if (coffin == null)
            {
                ReportState(DeliveryState.CoffinOutside);
                _settledTime = 0f;
                return;
            }

            if (IsCarriedByAnyone(coffin))
            {
                ReportState(DeliveryState.StillCarried);
                _settledTime = 0f;
                return;
            }

            if (!IsResting(coffin))
            {
                ReportState(DeliveryState.StillMoving);
                _settledTime = 0f;
                return;
            }

            if (_settledTime <= 0f)
                ReportState(DeliveryState.Settling);

            _settledTime += delta;

            if (_settledTime >= SettleDuration)
                CompleteContract(coffin);
        }

        /// <summary>
        /// Hacimdeki tabut. Sahne taraması PAHALI olduğu için seyrek yapılır; bulunan tabutun
        /// hâlâ içeride olup olmadığı ise her adımda ucuza doğrulanır.
        /// </summary>
        private Coffin ResolveCoffin()
        {
            if (_trackedCoffin != null)
            {
                if (IsBodyInsideVolume(_trackedCoffin))
                    return _trackedCoffin;

                _trackedCoffin = null;
            }

            if (Time.time < _nextSearchTime)
                return null;

            _nextSearchTime = Time.time + SearchInterval;
            _trackedCoffin = FindCoffinInside();
            return _trackedCoffin;
        }

        private Coffin FindCoffinInside()
        {
            if (_volume == null)
                return null;

            Coffin inside = null;
            float nearestSqr = float.MaxValue;

            Coffin[] coffins = FindObjectsByType<Coffin>(FindObjectsSortMode.None);
            for (int i = 0; i < coffins.Length; i++)
            {
                Coffin c = coffins[i];
                if (c == null)
                    continue;

                if (!TryGetBodyBounds(c, out Bounds body))
                {
                    // SESSİZ ELENME YASAK. Sınır kurulamadığında tabut taramadan tamamen düşüyor
                    // ve teslim hiç gerçekleşmiyor — teşhis "içeride değil" diyor ama merkez
                    // (0,0,0) çıkıyordu, yani aslında hiç ölçülmemişti. Sahada bir kez yaşandı;
                    // sebebi neyse artık kendini söylesin.
                    WarnNoBodyBounds(c);
                    continue;
                }

                // En yakın tabut teşhis için kaydedilir: "içeride değil" derken KAÇ METRE dışarıda
                // olduğunu da söyleyebilmek gerekiyor, yoksa kutuyu kör ayarlıyoruz.
                float sqr = (body.center - _volume.bounds.center).sqrMagnitude;
                if (sqr < nearestSqr)
                {
                    nearestSqr = sqr;
                    _diagNearestCenter = body.center;
                    WarnIfVolumeSmallerThanCoffin(body);
                }

                if (inside == null && IsBodyInsideVolume(c))
                    inside = c;
            }

            _diagHasNearest = nearestSqr < float.MaxValue;
            return inside;
        }

        /// <summary>
        /// Tabut GÖVDESİNİN TAMAMI hacimde mi. Önce yalnız birleşik merkez kontrol ediliyordu ve
        /// bu iki yönde de yanlıştı: tabutun büyük kısmı dışarıdayken teslim
        /// gerçekleşebiliyor, AÇIK KAPAK ise birleşik merkezi kaydırıp tersine sahte ret
        /// üretebiliyordu. Artık gövde sınırlarının SEKİZ KÖŞESİ birden doğrulanıyor ve kapak
        /// hesaba hiç katılmıyor.
        /// </summary>
        private bool IsBodyInsideVolume(Coffin coffin)
        {
            if (_volume == null || !TryGetBodyBounds(coffin, out Bounds body))
                return false;

            Vector3 min = body.min;
            Vector3 max = body.max;

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new(
                    (i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y,
                    (i & 4) == 0 ? min.z : max.z);

                if (!IsInsideVolume(corner))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tabut GÖVDESİNİN dünya sınırları — KAPAK HARİÇ. Kapak ayrı bir menteşeli parça ve
        /// açıkken tabutun kendisinden daha geniş bir hacim süpürüyor; teslim ölçütü kapağın
        /// açıklığına bağlı olamaz. Tetikleyiciler de dışarıda: onlar geometri değil.
        ///
        /// AYIRMA ÖLÇÜTÜ RIGIDBODY: gövde collider'ları tabutun KENDİ Rigidbody'sine bağlıdır,
        /// kapağınkiler kendi HingeJoint'li gövdesine. İlk yazımda ölçüt `CoffinLid` bileşeniydi
        /// ve YANLIŞTI — o bileşen kapak child'ında değil TABUTUN KÖKÜNDE duruyor
        /// (`[RequireComponent(typeof(Coffin))]`), dolayısıyla filtre tabutun BÜTÜN
        /// collider'larını eliyor, sınır hiç oluşmuyor ve tabut taramadan tamamen düşüyordu:
        /// teslim hiç gerçekleşmedi, teşhis "içeride değil" dedi ama merkez (0,0,0) çıktı.
        /// </summary>
        private static bool TryGetBodyBounds(Coffin coffin, out Bounds bounds)
        {
            bounds = default;
            bool any = false;

            if (!coffin.TryGetComponent(out Rigidbody body))
                return false;

            Collider[] colliders = coffin.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || c.isTrigger || c.attachedRigidbody != body)
                    continue;

                if (!any)
                {
                    bounds = c.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(c.bounds);
                }
            }

            return any;
        }

        /// <summary><c>ClosestPoint</c> nokta içerideyse noktanın kendisini döndürür.</summary>
        private bool IsInsideVolume(Vector3 worldPoint)
        {
            Vector3 closest = _volume.ClosestPoint(worldPoint);
            return (closest - worldPoint).sqrMagnitude < 0.0001f;
        }

        /// <summary>
        /// Hacim tabutu ALMIYORSA söyle — ölçüt artık sabit bir minimum değil, TABUTUN KENDİ
        /// BOYU. Önceki `MinVolumeSize = 0.5` sabiti tabutun sığdığını kanıtlamıyordu:
        /// 0.6 m'lik bir kutu eşiği geçiyor ama 2 m'lik tabutu asla alamıyordu ve teslim sessizce
        /// hiç gerçekleşmiyordu.
        /// </summary>
        /// <summary>Tabutun gövde sınırı hiç kurulamadı — teslim ASLA gerçekleşmez, bir kez söyle.</summary>
        private void WarnNoBodyBounds(Coffin coffin)
        {
            if (_warnedNoBodyBounds)
                return;

            _warnedNoBodyBounds = true;
            Debug.LogError("[DeliveryPoint] Tabutun gövde sınırı kurulamadı — teslim hiçbir zaman " +
                           "gerçekleşmez. Gövde collider'ları tabutun KENDİ Rigidbody'sine bağlı " +
                           "olmalı (tetikleyiciler ve kapağın kendi gövdesine bağlı collider'lar " +
                           "hesaba katılmaz). Tabut prefab'ında Rigidbody ya da collider yapısı " +
                           "değişmiş olabilir.", coffin);
        }

        private void WarnIfVolumeSmallerThanCoffin(Bounds body)
        {
            if (_warnedVolumeTooSmall || _volume == null)
                return;

            Vector3 volume = _volume.bounds.size;
            Vector3 needed = body.size;

            if (volume.x >= needed.x && volume.y >= needed.y && volume.z >= needed.z)
                return;

            _warnedVolumeTooSmall = true;

            // HANGİ EKSEN battı, KAÇ METRE eksik — iki vektörü gözle karşılaştırmak zorunda
            // kalmayalım (sahada tam bu oldu: yalnız Z 36 cm eksikti ama mesaj altı sayı basıp
            // hangisinin sorun olduğunu söylemiyordu).
            string failing = string.Empty;
            if (volume.x < needed.x) failing += $"\n  X: {volume.x:0.00} < {needed.x:0.00}  (eksik {needed.x - volume.x:0.00} m)";
            if (volume.y < needed.y) failing += $"\n  Y: {volume.y:0.00} < {needed.y:0.00}  (eksik {needed.y - volume.y:0.00} m)";
            if (volume.z < needed.z) failing += $"\n  Z: {volume.z:0.00} < {needed.z:0.00}  (eksik {needed.z - volume.z:0.00} m)";

            // Tabut SERBESTÇE dönebiliyor (GDD 6.4 yeniden tasarımı: joint'in üç açısal ekseni de
            // serbest), yani hangi yaw'da oturacağı önceden bilinmiyor. Yatay eksenlerin ikisi de
            // en az tabutun KÖŞEGENİ kadar olmalı, yoksa mezar bugün çalışır yarın çalışmaz.
            float diagonal = Mathf.Sqrt(needed.x * needed.x + needed.z * needed.z);

            Debug.LogError($"[DeliveryPoint] Mezar hacmi tabutu ALMIYOR — gövdenin TAMAMI içeride " +
                           $"olmalı, yoksa teslim hiçbir zaman gerçekleşmez.{failing}\n" +
                           $"ÖNERİ: yatay eksenleri (X ve Z) en az {diagonal:0.0} m yap — tabut " +
                           "serbestçe dönüyor, hangi yönde oturacağı belli değil. Dikey eksen " +
                           $"(Y) en az {needed.y + 0.5f:0.0} m.\n" +
                           "Not: script yassı bir zemin/taban küpündeyse objenin ÖLÇEĞİ collider'ı " +
                           "da yassılaştırır — teslim noktası KENDİ objesinde olmalı (ölçek 1,1,1).",
                           this);
        }

        /// <summary>
        /// Tabutu tutan var mı. Sunucuda <see cref="PlayerGrabber.IsCarrying"/> joint'in varlığından
        /// okunur, yani otoriter — görsel SyncVar'a değil gerçek fiziksel bağa bakılır.
        /// </summary>
        private static bool IsCarriedByAnyone(Coffin coffin)
        {
            PlayerGrabber[] grabbers = FindObjectsByType<PlayerGrabber>(FindObjectsSortMode.None);
            for (int i = 0; i < grabbers.Length; i++)
            {
                PlayerGrabber g = grabbers[i];
                if (g == null || !g.IsCarrying)
                    continue;

                if (!g.TryGetCarryGrabPoint(out _, out Transform coffinRoot) || coffinRoot == null)
                    continue;

                if (coffinRoot.GetComponentInParent<Coffin>() == coffin)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// ÇİZGİSEL VE AÇISAL hız birlikte. Yalnız çizgisel bakmak yetmiyordu: grab
        /// joint'imizde tüm açısal eksenler serbest (GDD 6.4 yeniden tasarımı), yani merkezi
        /// sabit dururken kendi etrafında dönen tabut "durmuş" sayılıp teslim ediliyordu.
        /// </summary>
        private bool IsResting(Coffin coffin)
        {
            if (!coffin.TryGetComponent(out Rigidbody body))
                return true; // Rigidbody yoksa hareket de yok

            // Uyuyan cismin iki hızı da zaten sıfırdır; ayrıca kontrol gerekmez.
            float maxSpeed = MaxSpeed;
            if (body.linearVelocity.sqrMagnitude > maxSpeed * maxSpeed)
                return false;

            float maxAngular = MaxAngularSpeed;
            return body.angularVelocity.sqrMagnitude <= maxAngular * maxAngular;
        }

        private void CompleteContract(Coffin coffin)
        {
            // Tamamlanma hakkı TABUTA ait, bu noktaya değil: iki örtüşen hacim aynı tabutu aynı
            // karede teslim edip iki rapor yayınlayabiliyordu. Claim'i kaybeden nokta
            // sessizce susar — hata değil, başka bir mezar önce davrandı.
            if (!ContractClaims.TryClaim(coffin))
            {
                _completed = true;
                return;
            }

            _completed = true;

            // Hub'da SEÇİLEN kontrat kazanır; sahnedeki alan yalnız yedek (level doğrudan Play'e
            // alındığında). Tersi sırayla çalışsaydı hangi kontratı seçersen seç raporda hep
            // aynı künye çıkardı — sahada tam bu görüldü.
            ContractDefinition contract = ActiveContract.Current != null ? ActiveContract.Current : _contract;

            ContractReport report = new()
            {
                ContractName = contract != null ? contract.ResolvedName : gameObject.scene.name,
                Brief = contract != null ? contract.brief : string.Empty,
                Duration = Time.time - _contractStartTime,
                CoffinDamage01 = ReadDamage(coffin),
                CorpseDelivered = ReadCorpseDelivered(coffin)
            };

            Debug.Log($"[DeliveryPoint] Kontrat tamamlandı: {report.ContractName} · " +
                      $"{report.Duration:0.0} sn · hasar {report.CoffinDamage01:P0} · " +
                      $"ceset {(report.CorpseDelivered ? "teslim" : "KAYIP")}.", this);

            ObserversShowReport(report);
        }

        private static float ReadDamage(Coffin coffin)
        {
            CoffinDamage damage = coffin.GetComponentInChildren<CoffinDamage>();
            return damage != null ? damage.Damage01 : 0f;
        }

        /// <summary>
        /// Ceset bileşeni YOKSA teslim edilmiş sayılır: cesetsiz test tabutu "ceset kayıp" diye
        /// raporlanmamalı. Kayıp yalnız <see cref="CorpseSlide.CorpseLost"/> açıkken bildirilir.
        /// </summary>
        private static bool ReadCorpseDelivered(Coffin coffin)
        {
            CorpseSlide corpse = coffin.GetComponentInChildren<CorpseSlide>();
            return corpse == null || !corpse.CorpseLost;
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversShowReport(ContractReport report)
        {
            ContractReportPanel panel = ResolvePanel();
            if (panel == null)
            {
                Debug.LogWarning("[DeliveryPoint] Rapor paneli bulunamadı — kontrat tamamlandı ama " +
                                 "ekran gösterilemiyor.", this);
                return;
            }

            // Hub'a dönüşü yalnız host tetikleyebilir (ContractBoard ile aynı ekip kararı).
            panel.Show(report, InstanceFinder.IsServerStarted, RequestReturnToHub);
        }

        private ContractReportPanel ResolvePanel()
        {
            if (_panel == null)
                _panel = FindFirstObjectByType<ContractReportPanel>();

            return _panel;
        }

        private void RequestReturnToHub() => ServerReturnToHub();

        /// <summary>
        /// RequireOwnership = false: teslim noktası bir SAHNE objesi, hiçbir client ona sahip değil.
        /// Yetki sahiplikle değil, sunucunun kendi doğrulamasıyla kurulur.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        private void ServerReturnToHub(NetworkConnection conn = null)
        {
            if (conn == null || !conn.IsLocalClient)
            {
                Debug.LogWarning("[DeliveryPoint] Hub'a dönüşü yalnız host başlatabilir — istek reddedildi.", this);
                return;
            }

            NetworkSceneDirector director = FindFirstObjectByType<NetworkSceneDirector>();
            if (director == null)
            {
                Debug.LogError("[DeliveryPoint] NetworkSceneDirector bulunamadı — Hub'a dönülemiyor. " +
                               "Bootstrap sahnesindeki NetworkManager'da olmalı.", this);
                return;
            }

            director.ReturnToHub();
        }

        /// <summary>
        /// Sahne ömürlü teslim noktası, kalıcı HUD'daki paneli yanında götürmez — rapor level'dan
        /// çıkarken kapatılmalı. <see cref="ContractBoard"/>'daki aynı tuzak: temizlik olmazsa
        /// panel ve imleç talebi bir sonraki sahneye TAŞINIR.
        /// </summary>
        public override void OnStopClient()
        {
            base.OnStopClient();
            ReleasePanel();
        }

        private void OnDestroy() => ReleasePanel();

        private void ReleasePanel()
        {
            if (_panel == null)
                return;

            _panel.Hide();
            _panel = null;
        }

        /// <summary>
        /// Teslim NEDEN gerçekleşmiyor — sahne kurulumunda en çok vakit yiyen soru bu ve tahminle
        /// cevaplanmamalı. Yalnız DURUM DEĞİŞİNCE yazar (koşul her fizik adımında ölçülüyor, her
        /// adımda yazsa Console'u boğardı) ve yalnız editör/geliştirme derlemesinde.
        /// </summary>
        private void ReportState(DeliveryState state)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // "İçeride değil" durumu, DEĞİŞMEDİĞİ sürece sessiz kalırsa yanıltıcı olur: level
            // açılırken basılan tek satır, saatler sonra bakıldığında "şu an dışarıda" sanılıyor
            // (sahada tam bu oldu). Tabut YAKINDAYKEN periyodik tekrarlanır — uzaktayken değil,
            // Console boğulmasın.
            bool repeat = state == DeliveryState.CoffinOutside
                          && _diagHasNearest
                          && Time.time >= _nextOutsideLogTime
                          && (_diagNearestCenter - _volume.bounds.center).sqrMagnitude < 100f;

            if (state == _reportedState && !repeat)
                return;

            _reportedState = state;

            switch (state)
            {
                case DeliveryState.CoffinOutside:
                    _nextOutsideLogTime = Time.time + 2f;
                    Debug.Log("[DeliveryPoint] Tabut mezar hacminin İÇİNDE DEĞİL.\n" +
                              $"  tabut merkezi : {_diagNearestCenter}\n" +
                              $"  hacim merkezi : {_volume.bounds.center}\n" +
                              $"  hacim boyutu  : {_volume.bounds.size}\n" +
                              "Gövdenin TAMAMI hacimde olmalı (yalnız merkezi değil) — Scene " +
                              "view'da yeşil gizmoya bak. Tetikleyici fiziği durdurmaz: altında " +
                              "KATI zemin olmalı.", this);
                    break;
                case DeliveryState.StillCarried:
                    Debug.Log("[DeliveryPoint] Tabut hacimde ama HÂLÂ TAŞINIYOR — teslim için " +
                              "herkesin bırakması gerekiyor (E).", this);
                    break;
                case DeliveryState.StillMoving:
                    Debug.Log("[DeliveryPoint] Tabut hacimde, bırakılmış ama HAREKET HÂLİNDE " +
                              "(kayıyor ya da dönüyor) — durması bekleniyor.", this);
                    break;
                case DeliveryState.Settling:
                    Debug.Log($"[DeliveryPoint] Tabut yerinde ve duruyor — {SettleDuration:0.0} sn " +
                              "sayılıyor.", this);
                    break;
            }
#endif
        }

        private enum DeliveryState
        {
            Unknown,
            CoffinOutside,
            StillCarried,
            StillMoving,
            Settling
        }

#if UNITY_EDITOR
        /// <summary>Seçili olmasa da çizilir: mezar hacmi level yerleşiminde görünür olmalı.</summary>
        private void OnDrawGizmos()
        {
            Collider volume = _volume != null ? _volume : GetComponent<Collider>();
            if (volume == null)
                return;

            Gizmos.color = new Color(0.4f, 0.9f, 0.5f, 0.35f);
            Bounds b = volume.bounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }
#endif
    }
}
