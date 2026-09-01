using System;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using SunsetExpress.Profiles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.Player
{
    /// <summary>
    /// Networked, client-side predicted Rigidbody karakter (GDD 12.2: oyuncu kendini predict eder).
    /// Katman 1 — kamera-göreceli hareket (tank kontrolü YOK, GDD 6.2); Katman 2 — velocity yönüne
    /// görsel dönüş. Karakter Rigidbody tabanlıdır (CharacterController değil) ve devrilemez (GDD 6.1).
    /// Prediction deseni FishNet 4.7.2 RigidbodyPrediction demo'sundan alınmıştır.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PlayerController : TickNetworkBehaviour
    {
        #region Prediction data
        public struct ReplicateData : IReplicateData
        {
            /// <summary>Kamera-göreceli dünya XZ yönü; owner'da tick anında hesaplanır (deterministik reconcile için).</summary>
            public Vector2 MoveWorld;
            public bool Jump;

            public ReplicateData(Vector2 moveWorld, bool jump)
            {
                MoveWorld = moveWorld;
                Jump = jump;
                _tick = 0;
            }

            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }

        public struct ReconcileData : IReconcileData
        {
            public PredictionRigidbody PredictionRigidbody;

            public ReconcileData(PredictionRigidbody pr)
            {
                PredictionRigidbody = pr;
                _tick = 0;
            }

            private uint _tick;
            public void Dispose() { }
            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
        }
        #endregion

        [Header("Profil (GDD 12.3 — sabitler profilde)")]
        [SerializeField] private PlayerProfile _profile;

        [Header("Zemin Kontrolü")]
        [SerializeField] private LayerMask _groundMask = ~0;
        [SerializeField] private float _groundCheckRadius = 0.3f;
        [Tooltip("Rigidbody merkezinden aşağı, ayak küresi ofseti.")]
        [SerializeField] private float _groundCheckOffset = 1f;

        [Header("Görsel / Kamera")]
        [Tooltip("Velocity yönüne dönen görsel child (Katman 2). NetworkObject 'Graphical Object' smoothing hedefi de bu olmalı.")]
        [SerializeField] private Transform _graphics;
        [Tooltip("Owner'da instantiate edilecek orbit kamera prefab'ı.")]
        [SerializeField] private OrbitCamera _cameraPrefab;

        private readonly PredictionRigidbody _prediction = new();
        private Rigidbody _rb;
        private PlayerGrabber _grabber;
        private OrbitCamera _camera;
        private bool _jumpQueued;

        // Görsel dönüş hedefi: replicate edilen SON hareket girdisinin yönü. Girdi akışı state
        // forwarding ile her makineye AYNI ulaştığı için duruştaki son rotasyon da her ekranda aynı
        // olur — pozisyondan türetme, duruş anında makineler arası farklı açıda donuyordu (playtest bug'ı).
        private Vector3 _lastFacingDir;

        /// <summary>Son zıplama TICK'i (server + owner'da replicate içinde set edilir). PlayerGrabber,
        /// zıplama impuls penceresinde kopma eşiğini geçici yükseltmek için okur (GDD 6.5).
        /// Time.time DEĞİL tick: pencerenin koruduğu şey, impulsun joint'lere yayıldığı FİZİK ADIMI
        /// sayısıdır. Kare takılıp aynı frame'de iki catch-up tick koşarsa Time.time ikisinde
        /// de aynı okunur ve pencere hedeflenenden az adımı korurdu.
        ///
        /// ARTIK OWNER DA OKUYOR (eski yorum "yalnız server okur" diyordu, bayattı): senkron zıplama
        /// penceresinde joint yayı sertleşiyor ve o hesap owner'da da yapılıyor. Bu yüzden damga ile
        /// karşılaştırılan "şimdi" AYNI TICK ALANINDAN gelmeli — bkz. <see cref="CurrentSimTick"/>.</summary>
        public uint LastJumpTick { get; private set; }

        /// <summary>
        /// ŞU AN SİMÜLE EDİLEN tick — kaynağı ROLE GÖRE seçilir (aşağıda). Tek kuralı şu:
        /// damga ile "şimdi" HER ZAMAN aynı alandan okunur.
        ///
        /// Client'ta `TimeManager.Tick` KULLANILMAZ: reconcile replay'inde o GÜNCEL tick'tir, oysa
        /// replay tarihsel bir adımı yeniden koşar.
        /// `LastJumpTick`'i tarihsel `rd.GetTick()` ile damgalayıp pencereyi güncel tick'e göre
        /// ölçmek İKİ FARKLI TICK ALANINI karşılaştırmaktı: tick 92'deki zıplama tick 115'te replay
        /// edilirse pencere `115-92` görüp erken kapanıyor, owner'da yay tarihsel adımda hiç
        /// sertleşmiyordu. Başlangıç da "şimdi" de artık bu alandan okunur.
        ///
        /// KAYNAK ROLE GÖRE DEĞİŞİR ve bu bilinçli: server'da `TimeManager.Tick` (gerçek fizik
        /// adımı), client'ta `rd.GetTick()` (replicate tick'i). Server'da `rd.GetTick()` kullanmak
        /// pencereyi CLIENT'ın tick alanına taşırdı — server bir adımda birden fazla buffered input
        /// tüketebildiği için "20 fizik adımı" garantisi bozulurdu. Host'ta `IsServerStarted` true
        /// olduğu için server dalı seçilir ve `Debug_ExternalJump`'ın damgasıyla da uyumlu kalır.
        /// </summary>
        public uint CurrentSimTick { get; private set; }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            // Karakter devrilemez — denge bozulması yalnızca tabuttan gelir (GDD 6.1). Yön görsel child'da.
            _rb.constraints = RigidbodyConstraints.FreezeRotation;

            // Joint zinciri stabilitesi: tabutu taşırken oyuncu da zincirin parçası — solver iterasyonu
            // tabutla eşit olmalı, yoksa zincirin zayıf halkası titrer (GDD 12.3).
            if (_profile != null && _profile.SolverIterations > 0)
            {
                _rb.solverIterations = _profile.SolverIterations;
                _rb.solverVelocityIterations = Mathf.Max(1, _profile.SolverVelocityIterations);
            }

            _grabber = GetComponent<PlayerGrabber>();
            _prediction.Initialize(_rb);
        }

        public override void OnStartNetwork()
        {
            // Rigidbody prediction tick + postTick ister.
            SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            // Server yeniden başlarsa TimeManager.Tick sıfırlanır ama bu obje yaşıyorsa LastJumpTick
            // eski (büyük) değerde kalır: yeni sayaç o değere yaklaşınca ZIPLAMA OLMADAN kopma
            // penceresi ~20 tick açılırdı. Sıfırlama OnStartNetwork'te DEĞİL burada: OnStartNetwork
            // obje ağda herhangi bir rolde yaşadığı sürece bir kez koşar — server durup client rolü
            // sürerse yeniden çağrılmaz ve rol-örtüşmeli stop→start'ı kaçırırdı.
            // Tek okuyucu (PlayerGrabber kopma ölçümü) zaten server-gated.
            LastJumpTick = 0;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (IsOwner && _cameraPrefab != null)
            {
                _camera = Instantiate(_cameraPrefab);
                // Kamera, root rigidbody'i (tick başına zıplayarak ilerler) değil FishNet'in tick'ler
                // arası smooth ettiği Graphics child'ını takip etmeli — yoksa görüntü titrer.
                _camera.SetTarget(_graphics != null ? _graphics : transform);
            }
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (_camera != null)
                Destroy(_camera.gameObject);
        }

        private void Update()
        {
            // Varış kontrolü owner'da, her karede — `Rigidbody.position`'dan okunur (transform'dan
            // değil; mühendislik invariantları).
            if (IsOwner)
                TickReviveArrival();

            // Jump girişi frame'de yakalanır, tick'te tüketilir (tick != frame; kaçırmamak için).
            if (IsOwner && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                _jumpQueued = true;

            // Katman 2: görsel gövde SON hareket girdisinin yönüne döner (GDD 6.2). Kaynak, replicate
            // edilen input akışı (_lastFacingDir, PerformReplicate'te set edilir) — her makinede aynı
            // veri, dolayısıyla duruştaki son rotasyon dahil her ekranda aynı yön.
            //
            // NOT: "tabut tutarken gövde grab point'e kilitlenir" (GDD 6.2) DENENDİ VE GERİ ALINDI —
            // oynanış sekansını bozuyordu (2026-08 playtest, Kaan). Kilit gerekirse üst gövde IK
            // pass'iyle birlikte yeniden ele alınmalı; tam gövde kilidi bu kontrol şemasına oturmadı.
            if (_graphics != null && _profile != null && _lastFacingDir != Vector3.zero)
            {
                Quaternion target = Quaternion.LookRotation(_lastFacingDir, Vector3.up);
                _graphics.rotation = Quaternion.Slerp(_graphics.rotation, target, _profile.RotationLerpSpeed * Time.deltaTime);
            }
        }

        protected override void TimeManager_OnTick()
        {
            PerformReplicate(BuildMoveData());
        }

        protected override void TimeManager_OnPostTick()
        {
            CreateReconcile();
        }

        private ReplicateData BuildMoveData()
        {
            // Yalnızca controller (owner) input üretir.
            if (!IsOwner)
                return default;

            // ÖLÜYKEN GİRDİ YOK — ve kapı BURADA, `PerformReplicate`'te DEĞİL. Sebep determinizm:
            // ölüm durumu owner-lokaldir (TargetRpc ile gelir), server'ın replicate'inde o bayrak
            // yoktur. Simülasyonda gate'leseydik owner ile server FARKLI girdi işler ve reconcile
            // sonsuza dek düzeltme üretirdi. Kaynakta boş girdi üretmek ikisini de aynı veriyle
            // besler. Yan fayda: owner artık ışınlamaya karşı yürümeye çalışmıyor, yeniden doğuş
            // düzeltmesi temiz oturuyor.
            if (IsLocallyDead)
            {
                _jumpQueued = false;
                return new ReplicateData(Vector2.zero, false);
            }

            Vector2 raw = ReadMoveAxis();

            // Katman 1: WASD kameraya göre kuvvet uygular (W = kameranın baktığı yön). Dünya yönü owner'da
            // hesaplanıp replicate verisine gömülür ki reconcile replay'inde kamera yaw'ı gerekmesin.
            float yaw = _camera != null ? _camera.Yaw : transform.eulerAngles.y;
            Vector3 world = Quaternion.Euler(0f, yaw, 0f) * new Vector3(raw.x, 0f, raw.y);
            world = Vector3.ClampMagnitude(world, 1f);

            bool jump = _jumpQueued;
            _jumpQueued = false;

            return new ReplicateData(new Vector2(world.x, world.z), jump);
        }

        private static Vector2 ReadMoveAxis()
        {
            Keyboard k = Keyboard.current;
            if (k == null)
                return Vector2.zero;

            float x = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
            float y = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
        }

        [Replicate]
        private void PerformReplicate(ReplicateData rd, ReplicateState state = ReplicateState.Invalid, Channel channel = Channel.Unreliable)
        {
            // "Şimdi" damgası. ROL AYRIMI ŞART: `rd.GetTick` server'da, client-owned bir obje
            // için CLIENT'ın replicate tick'idir — server'ın fizik tick'i DEĞİL, ve server'a
            // çevrilmez. Server bir adımda birden fazla buffered input tüketebildiği için o alanda
            // sayaç iki ilerleyebilir ve "20 fizik adımı koruma" penceresi kısalırdı. Her rol kendi
            // alanında kalır ve damga ile "şimdi" aynı kaynaktan gelir:
            //   server → TimeManager.Tick  (gerçek fizik adımı; kopma eşiği penceresinin anlamı bu)
            //   client → rd.GetTick()      (replicate tick'i; reconcile replay'inde tarihsel adım doğru)
            // Profil null olsa bile yazılır: pencere hesabı buna bağlı, bayat kalması sessiz hata.
            CurrentSimTick = IsServerStarted ? TimeManager.Tick : rd.GetTick();

            if (_profile == null)
                return;

            // Server-side girdi doğrulaması: MoveWorld client'ta üretilir, dürüst client clamp'ler ama
            // buna güvenilmez — modifiye client NaN/Infinity veya dev vektörle otoriter Rigidbody'yi
            // bozabilir. Sonlu olmayan girdi sıfırlanır, büyüklük 1'e kelepçelenir.
            Vector2 mw = rd.MoveWorld;
            if (!float.IsFinite(mw.x) || !float.IsFinite(mw.y))
                mw = Vector2.zero;
            mw = Vector2.ClampMagnitude(mw, 1f);

            Vector3 worldDir = new(mw.x, 0f, mw.y);

            // Görsel dönüş hedefi: son anlamlı girdi yönü. Replicate her makinede (server, owner,
            // state forwarding ile spectator) aynı veriyle çalışır → yön her ekranda aynı.
            if (worldDir.sqrMagnitude > 0.01f)
                _lastFacingDir = worldDir.normalized;
            bool grounded = IsGrounded();
            float control = grounded ? 1f : _profile.AirControl;

            // ═══ KAS MODELİ: hız hedefi + SABİT KUVVET TAVANI (playtest 2026-08) ═══
            // Eskiden hareket `ForceMode.VelocityChange` ile uygulanıyordu ve bu KÜTLEDEN BAĞIMSIZ:
            // her tick oyuncunun hızını hedefe doğru zorla itiyordu, joint yayının o tick'te ne
            // yaptığına bakmadan. Sonuç: tabutun kütlesi ekibi yavaşlatamıyordu — matematiksel
            // olarak imkânsızdı. Tabut 500 kg olsa da hiçbir şey değişmezdi. Playtest bunu
            // "tabut çok hafif hissettiriyor" diye bildirdi.
            //
            // Çözüm GDD 4.5'in LAFZINDA yazıyor: "Karakterin hareket kuvveti SABİTTİR; yavaşlama
            // tabutun kütlesinden ve sürtünmesinden FİZİKSEL OLARAK doğar." Yani taşırken hız
            // çarpanı uygulamak yasak (o yapay olurdu) ama kuvvet tavanı koymak kuralın ta kendisi.
            //
            // Model: "bu tick'te hedefe varmak için gereken kuvveti uygula, ama kasın gücünü ASLA
            // aşma." Yüksüzken tavan hiç devreye girmez ve davranış eskisiyle BİREBİR aynıdır;
            // yüklüyken yay geri çeker, tavan yetmez ve oyuncu gerçekten yavaşlar. Taşıyıcı
            // sayısına bakan hiçbir mantık yok — 4 kişide yük bölüşüldüğü için ekip kendiliğinden
            // hızlanır, GDD 4.5'in vaadi aynen bu.
            Vector3 horiz = _rb.linearVelocity;
            horiz.y = 0f;
            Vector3 desired = worldDir * _profile.MoveMaxSpeed;
            Vector3 velError = desired - horiz;

            // Doyumsuz haldeki katsayı: kütle/delta → tam olarak eski VelocityChange davranışı.
            float delta = (float)TimeManager.TickDelta;
            Vector3 wanted = velError * (_rb.mass / Mathf.Max(0.0001f, delta));

            // Kuvvet tavanı. 0/negatif asset koruması (Unity tuzağı): eski profillerde bu
            // alan 0 gelir ve oyuncu HİÇ hareket edemezdi.
            float maxForce = _profile.MoveMaxForce > 0f ? _profile.MoveMaxForce : 2800f;

            // İvme tavanı. YÜKSÜZ hissin korunması buna bağlı ve KOŞULLU: yalnız
            // `maxForce >= MoveAccelPerTick * kütle / delta` ise ivme tavanı önce bağlar ve davranış
            // eski VelocityChange ile birebir aynı olur. Tersi olursa kuvvet tavanı yüksüzken de
            // bağlar ve serbest hareket sessizce yavaşlar (ilk yazımda tam bu olmuştu,
            // 1400 N yüksüz ivmelenmeyi yarıya düşürüyordu). Sınır profil tooltip'inde yazılı.
            float accelCapForce = _profile.MoveAccelPerTick * (_rb.mass / Mathf.Max(0.0001f, delta));
            Vector3 force = Vector3.ClampMagnitude(wanted, Mathf.Min(maxForce, accelCapForce)) * control;

            // Taşırken + input yokken tam frenleme YAPMA (GDD 6.1: tabut/ekip oyuncuyu joint üzerinden
            // sürükleyebilmeli). Tam fren, joint çekişine karşı her tick hız sıfırlar → server'da
            // stick-slip titremesi üretir. Kısık fren = sürüklenirken sendeleme, titreme yok.
            if (_grabber != null && _grabber.IsCarrying && worldDir.sqrMagnitude < 0.01f)
                force *= _profile.CarryIdleBrakeFactor;

            _prediction.AddForce(force, ForceMode.Force);

            if (rd.Jump && grounded)
            {
                // VelocityChange kütleden bağımsızdır: karakter kütlesini (ör. 70 kg) artırıp tabutu
                // itebilmesini sağlarken zıplama yüksekliği sabit kalır. JumpForce = zıplama hızı (m/s).
                // Tutarken ÇOK ZAYIF hop (GDD 6.3/6.5): tek taşıyıcı engel aşamaz; tüm taşıyıcılar aynı
                // anda zıplarsa ivmeler joint'ler üzerinden birleşir — 4'lü senkron zıplama mekaniği.
                float factor = _grabber != null && _grabber.IsCarrying ? _profile.CarryJumpFactor : 1f;
                _prediction.AddForce(Vector3.up * (_profile.JumpForce * factor), ForceMode.VelocityChange);
                // Damga, bu adımı simüle eden rolün KENDİ tick'i — `CurrentSimTick` (server'da
                // TimeManager.Tick, client'ta rd.GetTick()). Damgayı ve "şimdi"yi ayrı ayrı yazmak
                // bu turun hatasıydı: client'ta tarihsel bir zıplama girdisi güncel tick'le
                // damgalanınca owner'da ESKİ bir pencere ŞİMDİ açılmış görünüyordu. Değer eskiden
                // yalnız server'ın kopma eşiğini etkiliyordu ve fark edilmiyordu; artık
                // PlayerGrabber owner tarafında joint yayını da buna göre sertleştiriyor, yani
                // yüksek gecikmeli bir reconcile owner ile server'da FARKLI yay üretebilirdi.
                // Damga ile "şimdi" LİTERAL OLARAK aynı kaynaktan: ikisi de CurrentSimTick.
                // Ayrı ayrı yazmak (biri rd.GetTick(), öteki TimeManager.Tick) bu turda tam olarak
                // yediğimiz hataydı — iki tick alanını karşılaştırmak.
                LastJumpTick = CurrentSimTick; // kopma eşiği + zıplama yayı penceresi (GDD 6.5 fizik notu)
            }

            _prediction.Simulate();
        }

        /// <summary>
        /// Owner'da ölüm sinyali — parametre kalan bekleme süresi (sn). Ölüm ekranı bunu dinler
        /// (GDD 3.4). Kopma uyarısı köprüsüyle aynı desen: sunucu karar verir, owner'a TargetRpc
        /// gider, arayüz event'e abone olur.
        /// </summary>
        public event Action<float> OnLocalDeath;

        /// <summary>
        /// Owner'da DİRİLME sinyali — ölüm ekranı karartmayı BUNUNLA kaldırır.
        ///
        /// ⚠ ANLAMI DEĞİŞTİ (eski adı `OnLocalRespawn`): artık server "ışınladım" dediğinde DEĞİL,
        /// owner GERÇEKTEN yeni pozuna ulaştığında ateşlenir. Sebep: `TargetRespawned` GÜVENİLİR
        /// bir RPC, pozu taşıyan reconcile ise UNRELIABLE — paket kaybında karartma açılırken
        /// oyuncu hâlâ uçurumda düşüyor olabiliyordu. Ad bilerek değiştirildi ki bu
        /// semantik kayması sessizce geçmesin.
        /// </summary>
        public event Action OnLocalRevived;

        /// <summary>Owner-lokal ölüm durumu. Girdi kapısı ve ölüm ekranı bunu okur. Server'da
        /// ANLAMSIZDIR (yalnız owner'a TargetRpc gider) — simülasyonda kullanma, bkz. BuildMoveData.</summary>
        public bool IsLocallyDead { get; private set; }

        /// <summary>Dirilmenin beklendiği owner-lokal zaman (`Time.time` tabanlı). Server MUTLAK
        /// zaman göndermez, SÜRE gönderir — iki makinenin saati tutmaz.</summary>
        public float LocalReviveAt { get; private set; }

        /// <summary>Server'ın ışınlayacağını bildirdiği hedef. Owner buraya ULAŞANA kadar ölü sayılır.</summary>
        public Vector3 LocalRespawnPoint { get; private set; }

        /// <summary>Hedefe "ulaşmış" sayılma yarıçapı (m). CÖMERT tutulur: hassas bir eşik kendisi
        /// kilit kaynağına döner, asıl güvence aşağıdaki zaman aşımıdır.</summary>
        private const float ReviveArrivalRadius = 1f;

        /// <summary>Varış beklemenin ÜST SINIRI (sn). PAZARLIK KONUSU DEĞİL: tek bir paket kaybı
        /// oyuncuyu siyah ekranda kilitlerse ölüm ekranının kendisi soft-lock kaynağı olur.
        /// Bu projede aynı sınıftan (açık kalan bayrak → kalıcı kilit) beş hata çıktı.</summary>
        private const float ReviveArrivalTimeout = 2f;

        private bool _awaitingReviveArrival;
        private float _reviveArrivalDeadline;

        /// <summary>Server-only: owner'a öldüğünü ve ne kadar bekleyeceğini bildirir.
        /// Tutuşu da BIRAKIR — ölü oyuncu tabuta bağlı kalırsa ışınlanırken tabutu peşinden
        /// sürükler (tabutu taşırken uçuruma düşmek bu oyunun en sık ölüm biçimi olacak).</summary>
        public void ServerNotifyDeath(float delay)
        {
            if (!IsServerStarted)
                return;

            if (_grabber != null)
                _grabber.ServerForceRelease();

            TargetDied(Owner, delay);
        }

        /// <summary>Server-only: owner'a dirildiğini ve NEREYE ışınlandığını bildirir.
        /// Hedef güvenilir kanaldan taşınır ki owner "hangi düzeltmeyi beklediğini" bilsin;
        /// karartma o poza ULAŞILINCA kalkar.</summary>
        public void ServerNotifyRespawn(Vector3 point)
        {
            if (IsServerStarted)
                TargetRespawned(Owner, point);
        }

        /// <summary>
        /// Server-only: BEKLEMEDEN dirilt. Işınlama YAPILAMADIĞI hâller için — güvenli zemin
        /// bulunamadıysa beklenecek bir hedef de yoktur.
        ///
        /// Ayrı bir metot olması bilinçli: aynı işi `ServerNotifyRespawn(mevcut poz)` ile de
        /// yapabilirdik ama o, "varış" kanalını "vazgeçtim" anlamında kullanmak olurdu — okuyan
        /// kişi neden orayı hedef verdiğimizi anlamazdı. Burada niyet adında yazıyor.
        ///
        /// ⚠ Bu dalda oyuncu HÂLÂ DÜŞÜYOR olabilir: karartma kalkınca düşen gövdeyi görür ve
        /// muhtemelen tekrar ölür. Kabul edilen davranış — alternatifi siyah ekranda kilitlenmek.
        /// </summary>
        public void ServerNotifyReviveImmediate()
        {
            if (IsServerStarted)
                TargetRevivedImmediate(Owner);
        }

        [TargetRpc]
        private void TargetDied(NetworkConnection conn, float delay)
        {
            // Alan event'ten ÖNCE yazılır: HUD owner'ını ararken ölüm gelirse event
            // yutuluyordu. Artık HUD `Bind` sonrası durumu HEMEN okuyabiliyor — kopma uyarısının
            // `GripWarningLevel`'ı abonelikten sonra okumasıyla birebir aynı desen.
            IsLocallyDead = true;
            LocalReviveAt = Time.time + delay;
            _awaitingReviveArrival = false;

            OnLocalDeath?.Invoke(delay);
        }

        [TargetRpc]
        private void TargetRespawned(NetworkConnection conn, Vector3 point)
        {
            // BURADA DİRİLMİYORUZ — yalnız hedefi öğreniyoruz. Ölü durumu, owner gerçekten oraya
            // ulaşınca (ya da zaman aşımında) kalkar; karartmanın düşen gövdeyi göstermesini
            // engelleyen şey bu.
            LocalRespawnPoint = point;
            _awaitingReviveArrival = true;
            _reviveArrivalDeadline = Time.time + ReviveArrivalTimeout;
        }

        [TargetRpc]
        private void TargetRevivedImmediate(NetworkConnection conn)
        {
            _awaitingReviveArrival = false;
            IsLocallyDead = false;
            OnLocalRevived?.Invoke();
        }

        /// <summary>
        /// Owner yeni pozuna ulaştı mı — ulaştıysa ölü durumunu kaldırır.
        ///
        /// Kararı OYNANIŞ katmanı verir, ölüm ekranı DEĞİL: `IsLocallyDead` aynı zamanda girdi
        /// kapısını sürüyor ve oynanışın UI'a bağımlı olması istenmez (aynı gerekçeyle kamera da
        /// imleç hakemini okur, ona yazmaz). Ekran yalnız `OnLocalRevived`'i dinler.
        /// </summary>
        private void TickReviveArrival()
        {
            if (!_awaitingReviveArrival)
                return;

            bool arrived = (_rb.position - LocalRespawnPoint).sqrMagnitude
                           <= ReviveArrivalRadius * ReviveArrivalRadius;
            bool timedOut = Time.time >= _reviveArrivalDeadline;

            if (!arrived && !timedOut)
                return;

            if (timedOut && !arrived)
            {
                Debug.LogWarning("[Player] Yeniden doğuş pozu zamanında ulaşmadı — karartma yine de " +
                                 "kaldırılıyor (fail-safe).", this);
            }

            _awaitingReviveArrival = false;
            IsLocallyDead = false;
            OnLocalRevived?.Invoke();
        }

        /// <summary>
        /// SERVER-ONLY ışınlama — yeniden doğuş için (GDD 3.4). Owner'a ayrı bir RPC ile
        /// bildirilmez: `CreateReconcile` zaten her tick rigidbody durumunu gönderiyor, yani
        /// sunucudaki konum değişimi normal reconcile yoluyla owner'a geçer ve orada snap'lenir.
        ///
        /// `ClearPendingForces` ŞART: o tick için kuyruğa alınmış hareket kuvvetleri ışınlamadan
        /// sonra da uygulanırdı ve oyuncu yeni konumunda eski momentumuyla fırlardı. Hızlar da
        /// sıfırlanır — düşerken ölen oyuncu, doğduğu yerde düşme hızıyla devam etmemeli.
        ///
        /// Rotasyona DOKUNULMAZ: gövde `FreezeRotation` ile kilitli ve görsel yönelim
        /// `_graphics` üzerinden yürüyor (Katman 2, GDD 6.2).
        /// </summary>
        public void ServerTeleport(Vector3 position)
        {
            if (!IsServerStarted)
                return;

            _prediction.ClearPendingForces();
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = position;

            // Transform da GÜNCELLENİR — okuma tarafı için şart. Physics Mode = TimeManager'da fizik
            // adımı manuel atıldığı için `_rb.position` PhysX'e yazılır ama `transform.position`
            // BİR SONRAKİ simülasyona kadar eski değerde kalır. Bu arada transform'u okuyan
            // herkes oyuncuyu hâlâ eski yerinde görür: yeniden doğuş taraması ışınlanmış oyuncuyu
            // düşüyor sanıp aynı karede tekrar öldürüyordu (sahada 0.0 sn'lik ikinci ölüm).
            //
            // `transform.position`'a ELLE yazmak yerine `PublishTransform`: PhysX→Transform
            // yazbackini AÇIKÇA tetikleyen API budur, pozu VE rotasyonu birlikte taşır ve daha
            // verimlidir. Tam eşdeğer DEĞİL: child transform'ları recursive yayınlamaz — burada
            // sorun değil, kök rigidbody ışınlanıyor. `Physics.SyncTransforms()` BU İŞE YARAMAZ:
            // o TERS yönde (Transform→PhysX) çalışır. Bkz. mühendislik invariantları.
            _rb.PublishTransform();
        }

        public override void CreateReconcile()
        {
            PerformReconcile(new ReconcileData(_prediction));
        }

        /// <summary>
        /// YALNIZCA TEST ARACI (DebugSyncJump, GDD 15.1 impuls testi): server'da dış zıplama impulsu.
        /// Normal zıplama yolu replicate içindedir; bu, tek test makinesinde 4'lü senkronu simüle eder.
        /// </summary>
        public void Debug_ExternalJump()
        {
            if (_profile == null)
                return;
            float factor = _grabber != null && _grabber.IsCarrying ? _profile.CarryJumpFactor : 1f;
            _rb.AddForce(Vector3.up * (_profile.JumpForce * factor), ForceMode.VelocityChange);
            // Damga MEVCUT Tick — "+1" DEĞİL. FishNet Tick'i fizik adımından SONRA artırır
            // (TimeManager.TickUpdate: PrePhysics → SimulatePhysics → PostPhysics → Tick++).
            // Update bu döngüden sonra koştuğu için TimeManager.Tick zaten SIRADAKİ adımın tick'idir —
            // impuls tam o adımda çözülür. +1 damgalamak pencereyi bir adım geciktirip en kritik
            // ilk impuls adımını korumasız bırakıyordu.
            LastJumpTick = TimeManager.Tick;
        }

        [Reconcile]
        private void PerformReconcile(ReconcileData rd, Channel channel = Channel.Unreliable)
        {
            _prediction.Reconcile(rd.PredictionRigidbody);
        }

        private bool IsGrounded()
        {
            Vector3 feet = _rb.position + Vector3.down * _groundCheckOffset;
            return UnityEngine.Physics.CheckSphere(feet, _groundCheckRadius, _groundMask, QueryTriggerInteraction.Ignore);
        }
    }
}
