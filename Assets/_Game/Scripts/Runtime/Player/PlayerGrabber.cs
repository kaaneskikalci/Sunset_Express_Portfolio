using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using SunsetExpress.Coffins;
using SunsetExpress.Profiles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.Player
{
    /// <summary>
    /// Tabut tutma sistemi (GDD 4.2). Oyuncu ↔ tabut bağlantısı ConfigurableJoint'tir (asla parent,
    /// pazarlıksız 4.1). Joint yalnızca SERVER'da (otoriter fizik) ve OWNER'da (prediction) kurulur;
    /// spectator client'larda kurulmaz (onlar senkron transform'u zaten görür). Grab/bırak event
    /// senkronudur (RPC, GDD 12.2). Bırak (E) her koşulda ANINDA çalışır — owner joint'i lokal olarak
    /// hemen yok eder, sonra server'a bildirir (panik butonu, pazarlıksız 6.5).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PlayerGrabber : NetworkBehaviour
    {
        [Header("Grab (GDD 4.2, 12.3)")]
        [Tooltip("Fizik/ergonomi sabitleri profili (GDD 12.3): grab menzili, uzanma hizası ve taşıma " +
                 "yüksekliği buradan okunur. PlayerController ile aynı PlayerProfile asset'ine bağlanır.")]
        [SerializeField] private PlayerProfile _profile;
        [Tooltip("Tabut collider'larının layer'ı (performans için daralt; boşsa Everything). Layer/sahne " +
                 "konfigürasyonu — fizik sabiti değil, bilinçli olarak profile'a taşınmaz.")]
        [SerializeField] private LayerMask _coffinMask = ~0;

        // Profil-destekli sabitler (GDD 12.3). Profil yoksa güvenli fallback (mevcut Coffin.cs deseni).
        private float GrabRange => _profile != null ? _profile.GrabRange : 1.2f;
        private float GrabReachHeight => _profile != null ? _profile.GrabReachHeight : 1.0f;
        /// <summary>Baş üstü taşıma yüksekliği — SABİT. Fare tekeriyle kaldır/indir (GDD 6.3) playtest
        /// sonrası ekip kararıyla oyundan tamamen kaldırıldı (2026-08): dar aralıkta anlamlı his
        /// üretmiyordu ve karakterin kol boyu baş üstü aralığı zaten sıkıştırıyordu. Kol uzaması artık
        /// yükseklikten değil KOPMA GERİLİMİNDEN türüyor (aşağıya bak).</summary>
        private float CarryHeight => _profile != null ? _profile.CarryHeight : 1.7f;

        private Rigidbody _rb;
        private Collider[] _myColliders;
        private PlayerController _controller;
        private ConfigurableJoint _joint;
        private Coffin _heldCoffin;
        private int _heldIndex = -1;
        private float _hoistStartLimit;
        private float _hoistElapsed;
        private bool _syncJumpSpringApplied; // zıplama penceresi yayı şu an uygulanmış mı (kenar tetikleme)
        private float _nextGrabAllowedTime;   // owner-lokal regrab kilidi (kopma sonrası)
        private float _serverNextGrabTime;    // server-otoriter regrab kilidi
        /// <summary>Kademe düşerken uygulanan ölü bant (gerilim oranı). Sınırda titremeyi ve ona bağlı
        /// RPC spam'ini önler. Mühendislik sabiti — tasarımcı ayarı değil, profile taşınmaz.</summary>
        private const float WarnLevelHysteresis = 0.06f;

        private byte _warnLevel;        // SERVER: son yayınlanan kademe (tekrar RPC atmamak için)
        private byte _visualWarnLevel;  // HER MAKİNEDE: HUD (owner) + kol uzaması (herkes) bunu okur

        // Server-otoriter tutuş kaydı: release/kopma/disconnect yollarında client parametrelerine
        // GÜVENİLMEZ; server yalnızca kendi kaydını serbest bırakır.
        // Owner-lokal _heldCoffin/_heldIndex'ten ayrıdır — host'ta panik bırakma önce lokal joint'i
        // temizlediği için iki kayıt bilinçli olarak ayrık tutulur.
        private Coffin _serverHeldCoffin;
        private int _serverHeldIndex = -1;

        /// <summary>Şu an tabut taşıyor mu — PlayerController frenleme yetkisini buna göre kısar (GDD 6.1).</summary>
        public bool IsCarrying => _joint != null;

        /// <summary>Salt GÖRSEL taşıma bilgisi: hangi tabutun hangi grab point'inden tutulduğu.
        /// Coffin == null → taşımıyor. El IK'sı bunu hedef olarak okur (kollar tutamağa uzanır).</summary>
        public struct CarryVisual
        {
            public Coffin Coffin;
            public int PointIndex;

            /// <summary>Her başarılı tutuşta artan görsel tutuş NESLİ. Coffin+PointIndex ikilisi
            /// aynı noktayı yeniden tutmayı AYIRT EDEMEZ: SyncVar eşit değeri "kirli" saymaz ve
            /// gözlemcide bırak→tut aynı ağ turuna denk gelirse hiç değişim görünmez.
            /// Nesil sayesinde aynı tabutun aynı köşesine regrab bile yeni bir tutuş olarak okunur —
            /// el IK'sı önceki tutuşun uzamasını devralmaz.</summary>
            public ushort Generation;
        }

        /// <summary>Taşıma durumunun her client'ta görünür hali. Joint yalnız server+owner'da var;
        /// spectator'lar (diğer oyuncuların ekranı) taşıma animasyonunu ve EL IK HEDEFİNİ bu SyncVar'dan
        /// öğrenir. GDD 12.2 notu: grab/bırak OYNANIŞI event senkronlu (RPC) kalır; bu alan oynanış
        /// state'i değil, salt görsel katman (animasyon + el IK + geç katılan/reconnect pozu) içindir.
        /// Başlangıçta tek bool'du; grab point index'i eklendi çünkü index olmadan spectator kopyalarda
        /// eller nereye uzanacağını bilemiyordu (4 karakterin 3'ünde kollar havada kalıyordu).
        /// Bant maliyeti aynı sınıfta: yalnız grab/bırak anında değişir.</summary>
        private readonly SyncVar<CarryVisual> _carryingSync = new();

        /// <summary>Tutuş nesli sayaçları — biri server'ın yayınladığı, biri owner/server'ın LOKAL
        /// bildiği tutuş için. İkisi sayısal olarak eşleşmek zorunda değil: tüketici (el IK'sı) yalnız
        /// "geçen kareye göre DEĞİŞTİ mi" diye bakar, o da her makinede kendi kanalından okur.</summary>
        private ushort _serverCarryGen;
        private ushort _localCarryGen;

        /// <summary>Görsel tutuş nesli. Her yeni tutuşta değişir — aynı tabutun aynı köşesini yeniden
        /// tutmak dahil. El IK'sı bunu "yeni tutuş" kenarı olarak kullanır; boolean bir "tutuyor mu"
        /// bayrağı gözlemcide bırak+tut aynı ağ turunda gelirse kenarı kaçırıyordu.</summary>
        public ushort CarryGeneration => (IsOwner || IsServerStarted) ? _localCarryGen : _carryingSync.Value.Generation;

        /// <summary>Animasyon sürücüsü bunu okur. Owner ve server joint'i LOKAL bilir — hem tutma hem
        /// bırakma anında tepki verir (OR'lu hali bırakmada SyncVar round-trip'i kadar geç kalıyordu,
        ///). Spectator'lar SyncVar'dan öğrenir.</summary>
        public bool CarryingVisible
        {
            get
            {
                if (IsOwner || IsServerStarted)
                    return _joint != null;

                // IsSpawned, IK hedefiyle AYNI ölçüt: pooling/deactivation'da referans null
                // olmayabiliyor — el IK'sı sönerken spectator taşıma animasyonu açık kalırdı.
                Coffin coffin = _carryingSync.Value.Coffin;
                return coffin != null && coffin.IsSpawned;
            }
        }

        /// <summary>
        /// El IK hedefi: tutulan grab point'in TRANSFORM'u (dünya konumu değil — IK, ofseti tabuttan
        /// türetilen bir çerçevede uygulayabilsin diye; bkz. ikinci aşırı yükleme ve
        /// PlayerArmStretchIK.BuildOffsetFrame).
        /// Her instance'ta çalışır — owner/server lokal kaydından, spectator SyncVar'dan okur.
        /// Salt görsel: IK her makinede lokal çözülür, senkronlanan tek şey "hangi nokta" bilgisidir
        /// (GDD 12.2 event/görsel ayrımı korunur).
        /// </summary>
        public bool TryGetCarryGrabPoint(out Transform grabPoint) => TryGetCarryGrabPoint(out grabPoint, out _);

        /// <summary>El IK hedefi + tabut kökü. Kök, IK'nın "dışa doğru" yönünü hesaplaması için gerekir:
        /// grab point'ler tabutun DÖRT köşesinde ve hepsi identity rotasyonlu, dolayısıyla sabit bir
        /// lokal ofset iki köşede dışarı, diğer ikisinde İÇERİ iter. Yön kökten türetilince dört köşede
        /// de doğru olur.</summary>
        public bool TryGetCarryGrabPoint(out Transform grabPoint, out Transform coffinRoot)
        {
            Coffin coffin;
            int index;

            if (IsOwner || IsServerStarted)
            {
                coffin = _heldCoffin;
                index = _heldIndex;
            }
            else
            {
                CarryVisual v = _carryingSync.Value;
                coffin = v.Coffin;
                index = v.PointIndex;
            }

            // IsSpawned kontrolü: despawn edilmiş ama Unity tarafından henüz yok edilmemiş
            // (pooling/deactivation) tabut referansı fake-null'a yakalanmaz — eller hayalet bir
            // tabuta uzanırdı. Görsel tüketicide fail-closed davran.
            if (coffin == null || !coffin.IsSpawned)
            {
                grabPoint = null;
                coffinRoot = null;
                return false;
            }

            grabPoint = coffin.GrabPoint(index);
            coffinRoot = grabPoint != null ? coffin.transform : null;
            return grabPoint != null;
        }

        /// <summary>Server'da ölçülen tutuş gerilimi (0-1+). 1'de kopar. YALNIZ SERVER'da anlamlıdır
        /// (saf client'ta hep 0) — HUD bunu değil OnGripWarningChanged'i dinlemeli.</summary>
        public float GripTension { get; private set; }

        /// <summary>
        /// Kopma uyarısı kademesi değişti — HER MAKİNEDE ateşlenir (GDD 4.3, 13.2).
        /// 0 = uyarı yok · 1 = ~%50+ · 2 = ~%65+ · 3 = ~%80+ (eşikler CoffinProfile.da, koda gömülü değil)
        ///
        /// Kademeleme SERVER.da yapılır ve yalnız kademe DEĞİŞİNCE ObserversRpc gider — tick başına
        /// tension yayını YAPILMAZ, çünkü o kopmayı state senkronuna çevirirdi (pazarlıksız GDD 12.2:
        /// grab/bırakma/kapak/kopma event senkronuyla taşınır). Bu, mevcut event'in çözünürlüğünü
        /// artırmaktır; bant maliyeti eski bool ile aynı sınıftadır.
        /// </summary>
        public event System.Action<byte> OnGripWarningChanged;

        /// <summary>Mevcut kademe. HUD abone olurken bunu okur (event kaçırma sigortası); el IK.sı da
        /// uzama miktarını buradan türetir — her oyuncu KENDİ grabber.ından okuduğu için doğru.</summary>
        public byte GripWarningLevel => _visualWarnLevel;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _myColliders = GetComponentsInChildren<Collider>();
            _controller = GetComponent<PlayerController>();
            if (_profile == null)
                Debug.LogWarning($"{name}: PlayerProfile atanmadı — grab sabitleri güvenli fallback " +
                                 "sabitleriyle çalışıyor (yanlış yapılandırılmış prefab/varyant olabilir). " +
                                 "Fail-closed dönüşümü.");
        }

        /// <summary>
        /// Taşıyıcı ↔ tabut çarpışma çiftlerini aç/kapa. Yalnızca MAKARA fazında susturulur (tabut
        /// gövdeye sürtünmeden yukarı çekilsin); taşıma sırasında çarpışma AÇIK — iç içe geçme sigortası.
        /// Physics.IgnoreCollision runtime'da güvenilirdir (joint.enableCollision toggle'ının aksine).
        /// </summary>
        private void SetCoffinCollisionIgnored(Coffin coffin, bool ignored)
        {
            if (coffin == null || _myColliders == null)
                return;

            foreach (Collider theirs in coffin.GetComponentsInChildren<Collider>())
            {
                if (theirs == null)
                    continue;
                foreach (Collider mine in _myColliders)
                {
                    if (mine != null)
                        UnityEngine.Physics.IgnoreCollision(mine, theirs, ignored);
                }
            }
        }

        /// <summary>
        /// Fizik işlerini tick'e hizala (Coffin/CorpseSlide ile aynı desen): Physics Mode =
        /// TimeManager'da FixedUpdate manuel fizik adımından kopuktur. Abonelik OnStartNetwork'te —
        /// hem server hem owner joint tutar, host'ta ise TEK KEZ çalışır (OnStartServer + OnStartClient
        /// ikilisi host'ta çift abone ederdi).
        /// </summary>
        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            TimeManager.OnPrePhysicsSimulation += PrePhysicsHoist;
            TimeManager.OnPostPhysicsSimulation += PostPhysicsGrip;
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            if (TimeManager != null)
            {
                TimeManager.OnPrePhysicsSimulation -= PrePhysicsHoist;
                TimeManager.OnPostPhysicsSimulation -= PostPhysicsGrip;
            }
        }

        /// <summary>Makara: joint kurulduğu her instance'ta (server + owner) ip boyunu hedefe küçültür —
        /// tabut grab anındaki mesafeden yumuşakça baş üstüne çekilir, snap/fırlama olmaz. Fizik adımından
        /// ÖNCE ve adımın delta'sıyla: eskiden Update/Time.deltaTime idi, server ve owner farklı kare
        /// hızlarında rampayı farklı hızda indirip 0.6 sn boyunca ayrışıyordu.</summary>
        private void PrePhysicsHoist(float delta)
        {
            TickHoist(delta);
            TickSyncJumpSpring();
        }

        /// <summary>
        /// Senkron zıplama penceresinde joint yayını sertleştirir (GDD 6.5). KENAR TETİKLEMELİ:
        /// yalnız pencere AÇILDIĞINDA ve KAPANDIĞINDA yazılır, her adımda değil — solver'a aynı
        /// değeri tekrar tekrar yazmak gereksiz, ve makara (TickHoist) da yayı yazdığı için
        /// koşulsuz yazım ikisini birbirine karıştırırdı.
        ///
        /// Neden gerekli: playtest'te 4'lü zıplamanın kazandırdığı yükseklik hissedilmiyordu —
        /// impuls oyuncuya biniyor ama tabut yumuşak yay üzerinden takip ettiği için enerjinin
        /// çoğu yayı germeye gidiyordu. Pencerede yay sertleşince impuls tabuta aktarılır.
        /// </summary>
        private void TickSyncJumpSpring()
        {
            if (_joint == null || _heldCoffin == null)
                return;

            bool inWindow = IsInSyncJumpWindow();
            if (inWindow == _syncJumpSpringApplied)
                return;

            _syncJumpSpringApplied = inWindow;
            _heldCoffin.ApplyLinearLimitSpring(_joint, inWindow ? _heldCoffin.SyncJumpSpringMultiplier : 1f);
        }

        /// <summary>Kopma ölçümü — fizik adımından SONRA: joint.currentForce solver tarafından ADIM
        /// SIRASINDA yazılır, adım öncesi okumak bir adım bayat kuvvet demektir (GDD 4.3, 12.2).</summary>
        private void PostPhysicsGrip(float delta)
        {
            if (!IsServerStarted)
                return;

            // Tabut ALTIMIZDAN despawn olduysa (kontrat bitişi, sahne geçişi, host'un objeyi kaldırması)
            // tutuşu server otoriter olarak kapat. Coffin tarafında "kim taşıyor" kaydı yok, bu yüzden
            // kontrol tutan tarafta. Yapılmazsa joint, taşıma kaydı ve uyarı kademesi asılı kalır —
            // owner'ın ekranında ikon sonsuza dek durur. _serverHeldIndex, referans Unity
            // tarafından yok edilip fake-null'a düştüğünde bile "tutuyorduk" bilgisini korur.
            if (_serverHeldIndex >= 0 && (_serverHeldCoffin == null || !_serverHeldCoffin.IsSpawned))
            {
                ServerReleaseHeld(); // idempotent; FreePoint zaten null-guard'lı
                return;
            }

            if (_joint != null && _heldCoffin != null)
                MeasureGripTension();
        }

        private void Update()
        {
            if (!IsOwner || Keyboard.current == null)
                return;

            // Tek tuş (E): tutuyorsa bırak (anında), tutmuyorsa tut.
            if (Keyboard.current.eKey.wasPressedThisFrame)
            {
                if (_joint != null)
                    Release();
                else
                    TryGrab();
            }

        }

        /// <summary>
        /// GDD 4.3/12.3: Unity breakForce KULLANILMAZ. Gerilim custom ölçülür — currentForce tek başına
        /// güvenilmez, anchor sapmasıyla BİRLİKTE doğrulanır (ikisinin minimumu; currentForce sıfır/yoksa
        /// sapma tek başına konuşur). Profildeki kademe oranlarında uyarı event'i, 1.0'da temiz
        /// "elden kaydı" + cooldown.
        /// Ölçüm HER FİZİK ADIMINDA tam bir kez, yalnız otoritede (server) yapılır (GDD 4.3, 12.2).
        /// </summary>
        /// <summary>
        /// Zıplama impuls penceresi içinde miyiz (GDD 6.5 fizik notu)? Pencere TICK ile ölçülür,
        /// Time.time ile değil: koruduğu şey, impulsun joint'lere yayıldığı FİZİK ADIMI
        /// sayısıdır — kare takılıp iki catch-up tick aynı frame'de koşarsa Time.time ikisinde de
        /// aynı okunur ve pencere hedeflenenden az adım korurdu.
        /// LastJumpTick varsayılanı 0'dır; tick 0'da kimse tabut tutmuyor olsa da uint çıkarma
        /// taşmasına karşı açık guard'lanır (hiç zıplanmadıysa pencere kapalı).
        /// </summary>
        private bool IsInSyncJumpWindow()
        {
            if (_controller == null || _heldCoffin == null)
                return false;

            uint jumpTick = _controller.LastJumpTick;
            if (jumpTick == 0)
                return false; // hiç zıplanmadı

            // "Şimdi" damgayla AYNI TICK ALANINDAN okunur: `TimeManager.Tick` reconcile
            // replay'inde GÜNCEL tick'i verir, damga ise tarihsel adımın tick'i. İkisini
            // karşılaştırmak replay'de pencereyi erken kapatıp owner'da yayı sertleştirmiyordu.
            uint now = _controller.CurrentSimTick;
            if (now == 0)
                return false; // henüz hiç replicate koşmadı

            if (now < jumpTick)
                return false; // saat geri sardı (yeniden bağlanma) — pencereyi açma

            uint windowTicks = TimeManager.TimeToTicks(_heldCoffin.SyncJumpBreakWindow);
            return now - jumpTick < windowTicks;
        }

        private void MeasureGripTension()
        {
            // Zıplama impuls penceresi (GDD 6.5 fizik notu): dört anlık impuls joint'lere aynı anda
            // bindiğinden, taşıyıcı zıpladıktan sonraki kısa pencerede kopma eşikleri geçici yükseltilir —
            // her senkron zıplamada toplu "elden kayma" tetiklenmesin.
            float breakForce = _heldCoffin.GrabBreakForce;
            float breakDeviation = _heldCoffin.GrabBreakDeviation;
            if (IsInSyncJumpWindow())
            {
                breakForce *= _heldCoffin.SyncJumpBreakMultiplier;
                breakDeviation *= _heldCoffin.SyncJumpBreakMultiplier;
            }

            // Anchor sapması: el ↔ grab point mesafesinin ip boyunu aşan kısmı (yayın gerilmesi = kuvvet).
            Vector3 handWorld = transform.TransformPoint(_joint.anchor);
            Vector3 grabWorld = _heldCoffin.GrabPointWorld(_heldIndex);
            float deviation = Vector3.Distance(handWorld, grabWorld) - _joint.linearLimit.limit;
            float devRatio = Mathf.Max(0f, deviation) / Mathf.Max(0.01f, breakDeviation);

            Vector3 cf = _joint.currentForce;
            float tension;
            if (cf.sqrMagnitude > 1f)
            {
                // İki sinyal birlikte doğrulanır: kopma için ikisi de eşiği aşmalı (min).
                float forceRatio = cf.magnitude / Mathf.Max(1f, breakForce);
                tension = Mathf.Min(forceRatio, devRatio);
            }
            else
            {
                tension = devRatio; // currentForce güvenilmez/sıfır — sapma tek başına ölçüt
            }

            GripTension = tension;

            // Kademe YALNIZ DEĞİŞİNCE yayınlanır — tick başına değil (event karakteri, GDD 12.2).
            ServerPublishWarnLevel(ComputeWarnLevel(tension));

            // BİLİNEN DAVRANIŞ (hata değil): kopma adımında yukarıdaki yayın ile aşağıdaki
            // ServerBreakGrip → ServerReleaseHeld → ServerPublishWarnLevel(0) AYNI server adımında
            // sıralanır. RPC'ler güvenilir ve çağrı sırasına sadıktır, ama paket sınırı ve araya render
            // girip girmeyeceği GARANTİ DEĞİLDİR — pratikte çoğunlukla aynı client network turunda
            // işlenirler ve en üst kademe hiç render edilmez. (Kademe zaten 3 ise yeni bir 3 RPC'si
            // gitmez, yalnız 0 gider — o durumda da sonuç aynı.) Araya suni gecikme KOYULMAZ: kopma,
            // tension 1'i aştığı adımda olmalı (GDD 4.3). Telafi HUD tarafındadır — sönme, koptuğu
            // kademenin stiliyle çizilir (Ozanay, GripWarningHud._lastActiveLevel).
            if (tension >= 1f)
                ServerBreakGrip();
        }

        /// <summary>
        /// Gerilimi kademeye çevirir. Eşikler profilden gelir (GDD 12.3) ve okunurken ARTAN SIRAYA
        /// normalize edilir: tasarımcı Medium'u Light'ın altına girerse (elle YAML düzenlemesi, kopyala-
        /// yapıştır profil) düşük gerilimde yüksek kademe çıkardı — "yüksekten aşağı kontrol" yalnız
        /// etiket önceliğini belirliyordu, sırayı düzeltmiyordu.
        /// Eşiğin 1'in üzerinde olması bilinçli olarak serbest: o kademeyi kapatmanın yolu budur
        /// (tension 1'e ulaştığında zaten kopma tetiklenir).
        ///
        /// HİSTEREZİS: kademe DÜŞERKEN eşiğin `WarnLevelHysteresis` kadar altına inilmesi
        /// gerekir. Durağan gerilim ile ilk eşik arasında yalnız ~3 cm sapma marjı var; sönümsüz
        /// salınımda gerilim eşiğin iki yanında gezinir ve her geçişte ObserversRpc atılırdı —
        /// hem ikon titrer hem bant boşa gider. Yükselme anında (histerezissiz) çünkü uyarının
        /// GEÇ KALMAMASI gerekir (GDD 4.3: adalet sütunu).
        /// </summary>
        private byte ComputeWarnLevel(float tension)
        {
            if (_heldCoffin == null)
                return 0;

            float light = _heldCoffin.GrabBreakWarnRatio;
            float medium = Mathf.Max(light, _heldCoffin.GrabBreakWarnRatioMedium);
            float severe = Mathf.Max(medium, _heldCoffin.GrabBreakWarnRatioSevere);

            // Mevcut kademeyi korumak için eşiğin biraz altı yeter; yükselmek için tam eşik gerekir.
            float hold = WarnLevelHysteresis;
            if (tension >= severe || (_warnLevel >= 3 && tension >= severe - hold))
                return 3;
            if (tension >= medium || (_warnLevel >= 2 && tension >= medium - hold))
                return 2;
            if (tension >= light || (_warnLevel >= 1 && tension >= light - hold))
                return 1;
            return 0;
        }

        /// <summary>Server-only: kademe değiştiyse TÜM GÖZLEMCİLERE bildirir. Başlangıçta owner-only
        /// TargetRpc'ydi; kol uzaması da aynı kademeden beslendiği için ObserversRpc'ye çıkarıldı —
        /// komedi başkasının debelenmesini izlemekte, owner-only iken kimse ötekinin kollarının
        /// gerildiğini göremiyordu (onaylı istisna). HUD yalnız lokal owner'ın grabber'ına
        /// abone olduğu için başkasının ikonu senin ekranında belirmez.
        /// Event karakteri korunur: yalnız kademe DEĞİŞİNCE yayınlanır, tick başına akış yoktur.</summary>
        private void ServerPublishWarnLevel(byte level)
        {
            if (level == _warnLevel)
                return;

            _warnLevel = level;
            ObserversGripWarning(level);
        }

        private void ServerBreakGrip()
        {
            float cooldown = _serverHeldCoffin != null ? _serverHeldCoffin.RegrabCooldown : 0.5f;

            _serverNextGrabTime = Time.time + cooldown;
            GripTension = 0f;

            // TargetGripBroken zaten owner'da DestroyJoint çağırıyor (+ cooldown taşıyor) — çift RPC yok.
            ServerReleaseHeld(notifyOwner: false); // kademe 0'ı yine de gönderir
            TargetGripBroken(Owner, cooldown);
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversGripWarning(byte level)
        {
            SetVisualWarnLevel(level);
        }

        /// <summary>Kademeyi yazar ve DEĞİŞTİYSE event.i ateşler. İki tüketici var: HUD (yalnız lokal
        /// owner.ın grabber.ına abone olur, GDD 4.3/13.2) ve el IK.sı (her oyuncunun kendi kolları).</summary>
        private void SetVisualWarnLevel(byte level)
        {
            if (_visualWarnLevel == level)
                return;

            _visualWarnLevel = level;
            OnGripWarningChanged?.Invoke(level);
        }

        [TargetRpc]
        private void TargetGripBroken(NetworkConnection conn, float cooldown)
        {
            _nextGrabAllowedTime = Time.time + cooldown;
            DestroyJoint(); // owner tarafındaki joint (host'ta server yolu zaten yok etti — guard'lı)
            Debug.Log("Elden kaydı!");
        }

        private void TryGrab()
        {
            if (Time.time < _nextGrabAllowedTime)
                return; // kopma sonrası cooldown (GDD 4.3)

            // Menzil, ayaklardan değil göğüs/uzanma hizasından ölçülür — başkalarının taşıdığı
            // baş üstü tabuta yerden uzanılabilsin. Mesafe de tabutun MERKEZİNE değil collider'ın
            // en yakın noktasına göre: 2 m'lik tabutun ucundan tutmak merkez ölçümüyle cezalanmasın.
            Vector3 reach = _rb.position + Vector3.up * GrabReachHeight;
            Collider[] hits = UnityEngine.Physics.OverlapSphere(
                reach, GrabRange, _coffinMask, QueryTriggerInteraction.Ignore);

            Coffin nearest = null;
            float best = float.MaxValue;
            foreach (Collider h in hits)
            {
                Coffin c = h.GetComponentInParent<Coffin>();
                if (c == null)
                    continue;
                float d = (h.ClosestPoint(reach) - reach).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = c;
                }
            }

            if (nearest != null)
                ServerRequestGrab(nearest);
        }

        [ServerRpc]
        private void ServerRequestGrab(Coffin coffin)
        {
            if (Time.time < _serverNextGrabTime)
                return; // kopma cooldown'ı server-otoriter de doğrulanır

            // Çift grab isteği (RTT içinde ikinci E) ikinci bir noktayı işaretleyip sızdırmasın:
            // server zaten tutuyor kaydediyorsa reddet.
            if (_serverHeldCoffin != null || coffin == null)
                return;

            // Mesafe server'da yeniden doğrulanır — client'ın bildirdiği tabuta güvenilmez
            // (modifiye client uzaktaki tabuta joint kurup fırlatamasın). Latency toleransı 1.5x.
            Vector3 reach = _rb.position + Vector3.up * GrabReachHeight;
            float maxDist = GrabRange * 1.5f;
            bool inRange = false;
            foreach (Collider c in coffin.GetComponentsInChildren<Collider>())
            {
                // Trigger/kapalı collider menzil kanıtı sayılmaz — client tarafı da OverlapSphere'de
                // trigger'ları yok sayıyor (QueryTriggerInteraction.Ignore); davranış eşitlenir.
                if (c == null || !c.enabled || c.isTrigger)
                    continue;
                if ((c.ClosestPoint(reach) - reach).sqrMagnitude <= maxDist * maxDist)
                {
                    inRange = true;
                    break;
                }
            }
            if (!inRange)
                return;

            // Doluluk server-authoritative: en yakın boş grab point'i server seçer/işaretler.
            if (!coffin.TryOccupyNearest(_rb.position, out int index))
                return;

            _serverHeldCoffin = coffin;
            _serverHeldIndex = index;
            // Görsel katman: tüm client'larda taşıma pozu + el IK hedefi.
            _carryingSync.Value = new CarryVisual { Coffin = coffin, PointIndex = index, Generation = ++_serverCarryGen };
            CreateJoint(coffin, index);            // server: otoriter fizik
            TargetCreateJoint(Owner, coffin, index); // owner: prediction
        }

        [TargetRpc]
        private void TargetCreateJoint(NetworkConnection conn, Coffin coffin, int index)
        {
            CreateJoint(coffin, index);
        }

        private void CreateJoint(Coffin coffin, int index)
        {
            if (_joint != null || coffin == null)
                return; // host'ta çift kurulmayı önle (server + target aynı instance)

            _heldCoffin = coffin;
            _heldIndex = index;
            _localCarryGen++; // owner/server tarafının kendi tutuş nesli (bkz. CarryVisual.Generation)
            _joint = gameObject.AddComponent<ConfigurableJoint>();
            // Anchor oyuncunun BAŞ ÜSTÜ el noktası (kollar yukarıda, GDD 6.3).
            _joint.anchor = new Vector3(0f, CarryHeight, 0f);

            // İp boyu, grab anındaki GERÇEK el↔grab point mesafesinden başlar (ani snap yok);
            // TickHoist bunu hoistDuration içinde profil hedefine küçültür.
            Vector3 handWorld = transform.TransformPoint(_joint.anchor);
            _hoistStartLimit = Vector3.Distance(handWorld, coffin.GrabPointWorld(index));
            _hoistElapsed = 0f;

            coffin.ConfigureGrabJoint(_joint, index, _hoistStartLimit);

            // Makara fazı: çarpışma geçici susturulur ki tabut gövdeye sürtünmeden yukarı çekilsin.
            // Tabut zaten baş üstündeyse (ör. ikinci taşıyıcı) makara yok — sigorta anında aktif kalır.
            if (_hoistStartLimit > coffin.GrabLinearLimit)
                SetCoffinCollisionIgnored(coffin, true);
        }

        private void TickHoist(float delta)
        {
            if (_joint == null || _heldCoffin == null)
                return;

            float target = _heldCoffin.GrabLinearLimit;
            if (_joint.linearLimit.limit <= target)
                return;

            _hoistElapsed += delta;
            float duration = Mathf.Max(0.01f, _heldCoffin.HoistDuration);
            float t = _hoistElapsed / duration;
            float newLimit = Mathf.Lerp(Mathf.Max(_hoistStartLimit, target), target, t);
            _heldCoffin.SetLinearLimit(_joint, newLimit);

            // Makara bitti → susturulan çarpışma çiftleri geri açılır. İç içe geçmeye karşı kalıcı
            // sigorta: açısal koni davranışı şekillendirir ama sert engel çarpışmanın kendisidir.
            if (t >= 1f)
                SetCoffinCollisionIgnored(_heldCoffin, false);
        }

        private void Release()
        {
            // Owner: joint'i ANINDA yok et (panik butonu, GDD 6.3). Server'a parametre GÖNDERİLMEZ —
            // server kendi otoriter kaydından bırakır (client sahte coffin/index ile başkasının
            // noktasını boşaltamaz).
            DestroyJoint();
            ServerRequestRelease();
        }

        [ServerRpc]
        private void ServerRequestRelease()
        {
            // Owner joint'i zaten lokal olarak yok etti (panik butonu) — geri bildirim gereksiz.
            ServerReleaseHeld(notifyOwner: false);
        }

        /// <summary>
        /// Server-otoriter bırakma: kayıtlı grab point'i boşaltır, server joint'ini yok eder.
        /// Release/kopma/despawn/ownership/disconnect yollarının ortak ucu — iki kez çağrılması güvenlidir.
        ///
        /// <paramref name="notifyOwner"/>: remote owner'ın KENDİ joint'ini de yok etmesi için TargetRpc
        /// gönderilsin mi. Owner'ın kendi başlattığı bırakma (E) ve kopma yollarında owner joint'i zaten
        /// lokal olarak yok ediyor; oralarda false geçilir. Server-başlatmalı yollarda (tabut despawn'ı)
        /// ZORUNLU: yoksa owner'da ConfigurableJoint asılı kalır, `_joint != null` yüzünden oyuncu
        /// sonsuza dek "taşıyor" sayılır — hareket freni sürer ve yeni tabut tutamaz.
        /// </summary>
        /// <summary>
        /// Server-only, dışarıdan çağrılabilir bırakma. Bugün tek çağıranı ölüm:
        /// <see cref="PlayerController.ServerNotifyDeath"/>.
        ///
        /// Neden gerekli: ölen oyuncu tabuta bağlı KALIYORDU. `ServerTeleport` onu tabutun yanına
        /// ışınlarken joint hâlâ canlı olduğu için tabut da peşinden sürükleniyordu — tabutu
        /// taşırken uçuruma düşmek bu oyunun en sık ölüm biçimi olacak, yani bu her turda çıkardı.
        ///
        /// `notifyOwner: true`: bırakmayı SERVER başlatıyor, owner'ın kendi joint'ini yok etmesi
        /// için TargetRpc şart — yoksa owner'da joint asılı kalır ve oyuncu sonsuza dek "taşıyor"
        /// sayılır. İki kez çağrılması güvenlidir.
        /// </summary>
        public void ServerForceRelease()
        {
            if (IsServerStarted)
                ServerReleaseHeld();
        }

        private void ServerReleaseHeld(bool notifyOwner = true)
        {
            bool wasHolding = _serverHeldIndex >= 0;

            if (_serverHeldCoffin != null)
                _serverHeldCoffin.FreePoint(_serverHeldIndex);
            _serverHeldCoffin = null;
            _serverHeldIndex = -1;
            _carryingSync.Value = default; // Coffin == null → taşımıyor

            // Uyarıyı KAPAT. Bu, server-başlatmalı her çıkış yolunu kapsar: kopma, tabut despawn'ı,
            // ownership değişimi, disconnect (OnStopServer). Gönderilmezse owner'da ikon ekranda
            // asılı kalır — event yolunda "uyarı bitti" bildirimi başka hiçbir yerden gelmiyor.
            ServerPublishWarnLevel(0);
            DestroyJoint(); // server tarafındaki joint (host'ta zaten yoksa guard'lı)

            // Boşa RPC atma: yalnız gerçekten tutuluyorken ve owner geçerliyken.
            if (notifyOwner && wasHolding && Owner.IsValid)
                TargetReleaseJoint(Owner);
        }

        /// <summary>Owner'a "joint'ini yok et" der. Idempotent — DestroyJoint zaten guard'lı, owner
        /// bırakmayı kendi başlatmışsa bu no-op olur.</summary>
        [TargetRpc]
        private void TargetReleaseJoint(NetworkConnection conn)
        {
            DestroyJoint();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            // Oyuncu tabutu tutarken disconnect/despawn olursa doluluk sızıntısı olmasın:
            // joint objeyle birlikte ölür ama Coffin._occupied[index] ancak burada temizlenir.
            ServerReleaseHeld();
        }

        /// <summary>
        /// Sahiplik devri — tutuş server-otoriter olarak bırakılır. Devretmeseydik: yeni owner'ın
        /// joint'i olmadan "taşıyor" görünürdü, _warnLevel eski değerde takılı kaldığı için yeni owner'a
        /// hiç kademe RPC'si gitmezdi, eski owner'ın ikonu da ekranda asılı kalırdı.
        /// </summary>
        public override void OnOwnershipServer(NetworkConnection prevOwner)
        {
            base.OnOwnershipServer(prevOwner);
            ServerReleaseHeld(); // _warnLevel'i de 0'a çeker → yeni owner ilk kademeyi taze alır
        }

        /// <summary>Sahipliği KAYBEDEN client: server'ın bırakma/kademe RPC'leri artık ona gitmez
        /// (yeni owner'a gider), bu yüzden joint ve uyarı lokal olarak temizlenir. Joint yalnız
        /// server+owner'da yaşadığı için diğer client'larda bu no-op'tur.</summary>
        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);

            DestroyJoint();

            // Kademe AYRICA sıfırlanır: callback ownership DEĞİŞTİKTEN sonra koşuyor, yani eski
            // owner'da IsOwner artık false ve DestroyJoint'in içindeki IsOwner guard'ı kademeyi
            // temizlemiyordu — ikon eski owner'ın ekranında asılı kalırdı. Tüm gözlemcilerde
            // çağrılması güvenli; zaten 0 olanlarda no-op.
            SetVisualWarnLevel(0);
        }

        /// <summary>Obje client'ta deinitialize oluyor (despawn, bağlantı kopması, pooling).
        /// Temizlenmezse aynı örnek yeniden kullanıldığında BAYAT joint/kademe ile başlar.</summary>
        public override void OnStopClient()
        {
            base.OnStopClient();

            SetVisualWarnLevel(0);

            // Joint YALNIZ server rolü de bitmişse yok edilir. Host'ta client rolü durup server rolü
            // yaşamaya devam edebiliyor; oradaki joint server'ın OTORİTER joint'idir. Koşulsuz yok
            // etmek onu kaldırırken `_serverHeld*` kaydını dolu bırakır — ölçüm durur ama tutuş kaydı
            // asılı kalır. Server tarafının bırakması OnStopServer/OnOwnershipServer'ın işi.
            if (!IsServerStarted)
                DestroyJoint();
        }

        private void DestroyJoint()
        {
            // Makara ortasında bırakma/kopma olursa susturulmuş çiftler asılı kalmasın —
            // bırakılan tabut oyuncuya yine çarpabilmeli.
            if (_heldCoffin != null)
                SetCoffinCollisionIgnored(_heldCoffin, false);

            if (_joint != null)
                Destroy(_joint);
            _joint = null;
            _heldCoffin = null;
            _heldIndex = -1;
            GripTension = 0f;

            // Kenar tetikleme mandalı sıfırlanmalı: yoksa pencere açıkken bırakıp yeniden tutan
            // oyuncuda bayrak "uygulanmış" kalır ve yeni joint'e sert yay HİÇ yazılmaz.
            _syncJumpSpringApplied = false;

            // Owner'da uyarı ANINDA söner — server'ın 0 kademesini beklemeden. E ile bırakma bir
            // panik butonudur (pazarlıksız 6.3/6.5); RTT boyunca ekranda "kopmak üzere" ikonu
            // durması o vaadi bozardı. Server yolu ayrıca 0 gönderir; ikinci 0 no-op'tur.
            if (IsOwner)
                SetVisualWarnLevel(0);
        }
    }
}
