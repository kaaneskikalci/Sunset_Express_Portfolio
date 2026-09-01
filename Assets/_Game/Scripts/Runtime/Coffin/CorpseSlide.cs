using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using SunsetExpress.Profiles;
using UnityEngine;

namespace SunsetExpress.Coffins
{
    /// <summary>
    /// Ceset Mod A — tabut içi 1D kayma simülasyonu (GDD 5.1) + ağırlık merkezi manipülasyonu (GDD 4.4).
    ///
    /// PAZARLIKSIZ KURALLAR:
    /// - Rigidbody.centerOfMass'e YALNIZCA BU script dokunur; güncelleme her fizik adımında TAM BİR KEZ,
    ///   kayma simülasyonundan SONRA tek noktadan yapılır (GDD 12.3). Başka hiçbir script CoM'a yazamaz.
    ///   Kural "FixedUpdate" diye yazılmıştı; Physics Mode = TimeManager'da fizik adımının gerçek sınırı
    ///   OnPrePhysicsSimulation'dır — kuralın ÖZÜ (adım başına bir kez, kaymadan sonra, tek yazar) korunur.
    /// - Ceset tabut içinde tam ragdoll simüle EDİLMEZ: görsel mesh + tek ağırlık noktası;
    ///   senkron maliyeti tek float (GDD 5.1, 12.2).
    ///
    /// Pozitif geri besleme döngüsü (GDD 4.4): tabut eğilir → ceset lokal Z'de kayar → CoM kayar →
    /// tabut daha da eğilmek ister. "Düz tutun lan!" panik anlarının motoru.
    ///
    /// NOT: Bu component, Coffin component'inin ALTINDA durmalı (Inspector sırası) — kütle kurulumu
    /// Coffin.ApplyProfile'dan sonra çalışır.
    /// </summary>
    [RequireComponent(typeof(Coffin))]
    public sealed class CorpseSlide : NetworkBehaviour
    {
        [Header("Ceset Varyantı (GDD 5.3 — varyant = profil asset'i)")]
        [SerializeField] private CorpseProfile _corpse;

        [Header("Görsel (fizik yok — GDD 5.1 Mod A)")]
        [Tooltip("Ceset görsel mesh'i (tabut child'ı, collider'sız). Kayma pozisyonuna göre lokal Z'de kaydırılır.")]
        [SerializeField] private Transform _corpseVisual;

        [Header("Mod B — Tabut Dışı (GDD 5.1, 3.4)")]
        [Tooltip("Lokal ragdoll prefab'ı — NETWORK OBJESİ DEĞİL: düşüş event'inde her client kendi " +
                 "kopyasını spawn eder, server durağan pozu yayınlar (GDD 5.1 kapsam notu: 'yalnızca " +
                 "düşüş anı + durağan poz senkronlanır'). İtilebilir sahne dekoru; TAŞINAMAZ.")]
        [SerializeField] private GameObject _ragdollPrefab;

        /// <summary>Mod A senkron maliyeti: tek float (GDD 12.2).</summary>
        private readonly SyncVar<float> _slideSync = new();

        private Coffin _coffin;
        private Rigidbody _body;
        private CoffinLid _lid;
        private CoffinDamage _damageSystem;
        private float _slidePos; // lokal Z, metre (+ baş ucu, - ayak ucu)
        private float _slideVel;
        private Rigidbody _localRagdoll; // bu makinede spawn edilen lokal ragdoll
        private bool _settled;
        private float _nextCorrectionTime;   // server: kök yayını zamanlayıcısı
        private float _sleepStableSince = -1f; // server: kesintisiz uyku başlangıcı (settle debounce)
        private uint _corpseSyncSeq;         // server: correction+settle ortak monoton sıra sayacı
        private Vector3 _correctionPos;      // client: son alınan kök hedefi
        private Quaternion _correctionRot;
        private bool _hasCorrection;
        private uint _lastCorpseSeq;         // client: kabul edilen son sıra — bayat paket reddi

        // Settle debounce süresi: ragdoll bu kadar süre KESİNTİSİZ uyursa mühürlenir — uyku sınırında
        // sallanan cisim reliable RPC spam'i üretmesin (ağ hijyeni sabiti; profil konsolidasyonu adayı).
        private const float SettleStabilityDuration = 0.5f;

        /// <summary>Ceset tabuttan düştü mü — KALICI, geri konamaz (GDD 3.4, pazarlıksız).
        /// Cenaze raporu/ödeme kesintisi ileride bunu okuyacak.</summary>
        public bool CorpseLost { get; private set; }

        private int _ejectHoldSteps; // fırlatma koşulu kaç adımdır kesintisiz sürüyor

        /// <summary>Anlık kayma pozisyonu (m) — ileride HUD/ses telegraf katmanı okur.</summary>
        public float SlidePosition => IsServerStarted ? _slidePos : _slideSync.Value;

        /// <summary>Ceset varyant profili — CoffinLid mühür bayrağını (Firavun), hasar sistemi
        /// kütle/kopma çarpanlarını buradan okur (GDD 5.3).</summary>
        public CorpseProfile Corpse => _corpse;

        private void Awake()
        {
            _coffin = GetComponent<Coffin>();
            _body = GetComponent<Rigidbody>();
            _lid = GetComponent<CoffinLid>();
            _damageSystem = GetComponent<CoffinDamage>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Rol örtüşmeli stop→start'ta bayat kalmasın: önceki oturumun kısmi bekleme
            // sayısı taşınırsa yeni oturumda ceset olması gerekenden erken düşerdi.
            _ejectHoldSteps = 0;

            // Toplam kütle = gövde (Coffin.ApplyProfile set etti) + ceset (GDD 4.1: toplam oyuncunun 2-3 katı).
            // CorpseLost guard'ı: aynı sahne objesi server stop→start yaşarsa kayıp cesedin
            // kütlesi geri eklenmesin — ceset yoksa ağırlığı da yok.
            if (_corpse != null && !CorpseLost)
                _body.mass += _corpse.mass;

            // Fizik tick'ine hizalanma (Coffin yaw damping ile aynı desen): Physics Mode =
            // TimeManager'da FixedUpdate manuel fizik adımından KOPUKTUR — adım başına 0, 1 veya 2 kez
            // çalışabilir. Kayma integrasyonu ve CoM yazımı bundan doğrudan zarar görür.
            TimeManager.OnPrePhysicsSimulation += ServerPrePhysics;
            TimeManager.OnPostPhysicsSimulation += ServerPostPhysics;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (TimeManager != null)
            {
                TimeManager.OnPrePhysicsSimulation -= ServerPrePhysics;
                TimeManager.OnPostPhysicsSimulation -= ServerPostPhysics;
            }
        }

        /// <summary>
        /// Mod A zinciri — her fizik adımından TAM BİR KEZ önce, adımın GERÇEK delta'sıyla.
        /// Sıra pazarlıksızdır (GDD 12.3): önce kayma simülasyonu, SONRA tek noktadan CoM yazımı.
        /// Fizik adımı öncesi çalışır çünkü CoM ve kayma, o adımın sonucunu belirler.
        /// </summary>
        private void ServerPrePhysics(float delta)
        {
            if (_body == null || CorpseLost)
                return;

            SimulateSlide(delta);
            ApplyCenterOfMass(); // kaymadan SONRA, tek nokta (GDD 12.3)
            _slideSync.Value = _slidePos;
            TryEject();
        }

        /// <summary>
        /// Mod B kök senkronu (GDD 12.2: pelvis + anahtar kemik) — fizik adımından SONRA çalışır ki
        /// yayınlanan poz, adımın TAZE sonucu olsun (pre-physics'te bir adım bayat poz gönderilirdi).
        /// Ragdoll UYUYANA kadar düşük frekanslı yayın — lokal ragdoll'ların ayrışması sınırlanır
        /// (playtest bulgusu: tek seferlik settle, client'ta farklı dinlenme noktası bırakıyordu).
        /// Uyandırılırsa (tekme) yayın kendiliğinden devam eder, tekrar uyuyunca settle yeniden mühürler.
        /// Zaman kapıları Time.time'da kalır: bunlar fizik integrasyonu değil AĞ YAYIN HIZI sınırıdır.
        /// </summary>
        private void ServerPostPhysics(float delta)
        {
            if (!CorpseLost || _localRagdoll == null)
                return;

            if (!_settled)
            {
                if (_localRagdoll.IsSleeping())
                {
                    // Settle debounce: ilk uyku karesinde değil, kararlılık süresi
                    // dolunca mühürle — sınırda sallanan cisim reliable spam üretmesin.
                    if (_sleepStableSince < 0f)
                        _sleepStableSince = Time.time;

                    if (Time.time - _sleepStableSince >= SettleStabilityDuration)
                    {
                        _settled = true;
                        ObserversSettleCorpse(++_corpseSyncSeq, _localRagdoll.position, _localRagdoll.rotation);
                    }
                }
                else
                {
                    _sleepStableSince = -1f;
                    if (Time.time >= _nextCorrectionTime)
                    {
                        float interval = _coffin.Profile != null ? _coffin.Profile.corpseSyncInterval : 0.2f;
                        _nextCorrectionTime = Time.time + Mathf.Max(0.05f, interval);
                        ObserversCorrectCorpse(++_corpseSyncSeq, _localRagdoll.position, _localRagdoll.rotation);
                    }
                }
            }
            else if (!_localRagdoll.IsSleeping())
            {
                _settled = false; // dekor dürtüldü — düzeltme yayını yeniden başlar
                _sleepStableSince = -1f;
            }
        }

        /// <summary>Mod A → Mod B geçişi (GDD 5.1): kapak açık/yok + yatış eşiği → ceset düşer, KALICI.</summary>
        private void TryEject()
        {
            // Fail-closed erken dönüşler de sayacı SIFIRLAR: yoksa kurulum eksikken ya da
            // kapak mühürlüyken birikmiş kısmi bir bekleme, koşullar sonradan sağlanınca cesedi
            // olması gerekenden erken düşürürdü.
            if (_corpse == null || _corpseVisual == null || _ragdollPrefab == null)
            {
                _ejectHoldSteps = 0;
                return;
            }

            // Firavun/Lahit savunma katmanı: mühürlü kapakta ceset HİÇBİR koşulda düşmez —
            // CoffinLid/CoffinDamage guard'ları delinse bile bu hat tutar (GDD 5.3, fail-closed).
            if (_corpse.lidSealed)
            {
                _ejectHoldSteps = 0;
                return;
            }

            CoffinProfile p = _coffin.Profile;

            // "Mandal açık" ≠ "kapak açık": mandal bırakılmış ama kapak fiziksel olarak
            // kapalı duruyorsa ceset kapalı kapaktan geçemez. Kapak parçalanmışsa tam açık sayılır.
            float minOpen = p != null ? p.lidEjectMinOpenAngle : 25f;
            bool lidOpen = (_damageSystem != null && _damageSystem.LidDestroyed) ||
                           (_lid != null && !_lid.IsLatched && _lid.CurrentOpenAngle > minOpen);

            float exitAngle = p != null ? p.corpseExitTiltAngle : 60f;
            bool tilted = Vector3.Angle(transform.up, Vector3.up) >= exitAngle;

            // SÜRE ŞARTI (playtest 2026-08): iki koşul da ANLIK okunuyordu, yani TEK BİR şiddetli
            // fizik karesi cesedi kalıcı olarak düşürüyordu. Birden fazla oyuncu tabutu aynı anda
            // kaldırınca hoist rampaları birleşiyor, tabut bir kare sert sarsılıyor ve hem kapak
            // hem eğim eşiği aynı karede aşılıyordu — oyuncunun göremediği, tepki veremediği bir
            // kayıp. "Ceset kaybı KALICIDIR" pazarlıksız olduğu için (GDD 3.4, 5.1) tetikleyicinin
            // OKUNAKLI olması şart: kaosun tahmin edilebilir olması kuralı bu (GDD 1.4).
            // Artık koşul kesintisiz sürmeli — gerçek bir devrilme sürer, sarsıntı sürmez.
            if (!lidOpen || !tilted)
            {
                _ejectHoldSteps = 0;
                return;
            }

            // Süre eşiği profilde ve SANİYE cinsinden: adım sayısı tick rate'e bağlıdır ve
            // tick rate değişirse fırlatmanın gerçek süresi sessizce kayardı. Kalıcı ceset kaybını
            // belirleyen bir gameplay eşiği olduğu için GDD 12.3 kapsamındadır.
            float holdSeconds = p != null && p.corpseEjectHoldDuration > 0f ? p.corpseEjectHoldDuration : 0.2f;
            // CeilToInt, RoundToInt DEĞİL: tooltip "bu kadar SÜRMEDEN düşmez" diyor, yani
            // asgari süre semantiği. Yuvarlama, ileride girilen bir süreyi yarım tick kadar KISA
            // uygulayıp sözü bozardı. (0.2/0.02 = 10 tam sayı, bugün fark yok.)
            int required = Mathf.Max(1, Mathf.CeilToInt(holdSeconds / Mathf.Max(0.0001f, (float)TimeManager.TickDelta)));

            _ejectHoldSteps++;
            if (_ejectHoldSteps < required)
                return;

            CorpseLost = true;

            // Cesetsiz tabut hem hafifler hem dengelenir — kütle/CoM yazımı BU scriptte kalır
            // (tek-yazar kuralı, GDD 12.3). Cesetsiz taşıma kolay ama utanç vericidir (GDD 3.4).
            _body.mass = Mathf.Max(1f, _body.mass - _corpse.mass);
            _body.centerOfMass = Vector3.zero;

            Vector3 pos = _corpseVisual.position;
            Quaternion rot = _corpseVisual.rotation;
            Vector3 vel = _body.linearVelocity;

            LocalEject(pos, rot, vel);
            ObserversEject(pos, rot, vel);
            Debug.Log("[CorpseSlide] CESET DÜŞTÜ — kayıp KALICI, tabuta geri konamaz (GDD 3.4).");
        }

        [ObserversRpc(ExcludeServer = true)]
        private void ObserversEject(Vector3 pos, Quaternion rot, Vector3 vel)
        {
            LocalEject(pos, rot, vel);
        }

        private void LocalEject(Vector3 pos, Quaternion rot, Vector3 vel)
        {
            if (_corpseVisual != null)
                _corpseVisual.gameObject.SetActive(false); // Mod A görseli kapanır

            if (_ragdollPrefab == null || _localRagdoll != null)
                return;

            GameObject go = Instantiate(_ragdollPrefab, pos, rot);
            _localRagdoll = go.GetComponent<Rigidbody>();
            if (_localRagdoll != null)
                _localRagdoll.linearVelocity = vel; // tabutun momentumuyla savrulur

            // Client'ta tabut KİNEMATİK hayalettir — sonsuz kütleyle ragdoll'u savurup yerel yörüngeyi
            // server'dan koparır (playtest bulgusu). Çarpışma kapatılır; doğru konum zaten server'ın
            // kök düzeltme yayınından gelir. Server'da gerçek (dinamik) çarpışma aynen yaşar.
            if (!IsServerStarted && _localRagdoll != null)
            {
                Collider ragdollCollider = go.GetComponent<Collider>();
                if (ragdollCollider != null)
                {
                    foreach (Collider coffinCollider in GetComponentsInChildren<Collider>())
                    {
                        if (coffinCollider != null)
                            UnityEngine.Physics.IgnoreCollision(ragdollCollider, coffinCollider, true);
                    }
                }
            }
        }

        [ObserversRpc(ExcludeServer = true)]
        private void ObserversCorrectCorpse(uint seq, Vector3 pos, Quaternion rot, Channel channel = Channel.Unreliable)
        {
            // Sıra kontrolü: unreliable paketler geri sıralanabilir ve reliable settle'ı
            // SONRADAN geçebilir — yalnız gördüğümüz son sıradan yenisi kabul edilir.
            if (seq <= _lastCorpseSeq)
                return;
            _lastCorpseSeq = seq;

            _correctionPos = pos;
            _correctionRot = rot;
            _hasCorrection = true;
        }

        [ObserversRpc(ExcludeServer = true)]
        private void ObserversSettleCorpse(uint seq, Vector3 pos, Quaternion rot)
        {
            if (_localRagdoll == null || seq <= _lastCorpseSeq)
                return;
            _lastCorpseSeq = seq;

            _hasCorrection = false; // settle mühürler — daha eski sıralı düzeltmeler artık reddedilir
            _localRagdoll.position = pos;
            _localRagdoll.rotation = rot;
            _localRagdoll.linearVelocity = Vector3.zero;
            _localRagdoll.angularVelocity = Vector3.zero;
            _localRagdoll.Sleep();

            // Uyuyan gövdede bir sonraki PhysX→Transform yazbackine GÜVENİLMEZ — gövde temas ya da
            // kuvvetle uyanana kadar simüle edilmeyebilir ve görsel ceset son pozunu almadan eski
            // yerinde donabilir. Bu yüzden son poz HEMEN yayınlanır. Yayınlama fizik durumunu
            // değiştirmez ve gövdeyi uyandırmaz, o yüzden `Sleep()`'ten sonra gelmesi doğru
            // (mühendislik invariantları).
            _localRagdoll.PublishTransform();
        }

        public override void OnSpawnServer(NetworkConnection connection)
        {
            base.OnSpawnServer(connection);
            // Geç katılan/reconnect: düşüş event'ini kaçırdıysa cesedi son bilinen pozda,
            // uyur halde görür — event modeli + spawn-anı yaması, state senkronu gerekmez.
            // Host'un kendi bağlantısı hariç: aynı instance'taki OTORİTER ragdoll'a
            // Sleep çağrılıp erken dondurulmasın.
            if (!CorpseLost || connection.IsLocalClient)
                return;

            Vector3 pos = _localRagdoll != null ? _localRagdoll.position : transform.position;
            Quaternion rot = _localRagdoll != null ? _localRagdoll.rotation : Quaternion.identity;
            TargetCorpseLost(connection, pos, rot);
        }

        [TargetRpc]
        private void TargetCorpseLost(NetworkConnection connection, Vector3 pos, Quaternion rot)
        {
            LocalEject(pos, rot, Vector3.zero);
            if (_localRagdoll != null)
                _localRagdoll.Sleep();
        }

        private void SimulateSlide(float dt)
        {
            CoffinProfile p = _coffin.Profile;
            float range = p != null ? p.corpseSlideRange : 0.6f;
            float threshold = Mathf.Sin((p != null ? p.corpseTiltThreshold : 10f) * Mathf.Deg2Rad);
            float accel = p != null ? p.corpseSlideAccel : 1.2f;
            float damping = p != null ? p.corpseSlideDamping : 2f;

            // Baş-ayak ekseninin (lokal Z) eğimi: yerçekiminin bu eksene izdüşümü. + = baş ucu aşağıda.
            float alongHead = Vector3.Dot(Vector3.down, transform.forward);

            // Statik sürtünme eşiği: eşiğin altında ceset KIMILDAMAZ — okunabilirlik (GDD 5.4: kaos
            // rastgele değil; oyuncu "10 dereceyi geçtim, kayacak" diye öğrenir).
            float drive = Mathf.Abs(alongHead) >= threshold ? alongHead : 0f;

            float mult = _corpse != null ? _corpse.slideSpeedMultiplier : 1f;
            _slideVel += drive * accel * mult * dt;
            _slideVel *= Mathf.Max(0f, 1f - damping * dt);
            _slidePos += _slideVel * dt;

            // Tabut iç duvarı: pozisyon kelepçelenir (ileride buraya "güm" sesi + hasar tetiği gelir).
            if (_slidePos > range) { _slidePos = range; _slideVel = 0f; }
            else if (_slidePos < -range) { _slidePos = -range; _slideVel = 0f; }
        }

        private void ApplyCenterOfMass()
        {
            float corpseMass = _corpse != null ? _corpse.mass : 80f;
            float bias = _corpse != null ? _corpse.comBiasStrength : 1f;
            // CoM ofseti = kayma × (ceset kütlesinin toplam içindeki payı) × varyant bias'ı, lokal Z'de.
            float frac = corpseMass / Mathf.Max(1f, _body.mass);
            _body.centerOfMass = Vector3.forward * (_slidePos * frac * bias);
        }

        private void Update()
        {
            // Mod B kök düzeltmesi (yalnız client): server yayınındaki hedefe yumuşak çekim; büyük
            // sapmada (>2 m) teleport. Yerel fizik oynamaya devam eder, düzeltme saniyeler içinde kazanır.
            if (!IsServerStarted && _localRagdoll != null && _hasCorrection)
            {
                if ((_localRagdoll.position - _correctionPos).sqrMagnitude > 4f)
                {
                    _localRagdoll.position = _correctionPos;
                    _localRagdoll.rotation = _correctionRot;
                    // Bu dal SÜREKSİZ bir sıçrama (ışınlama) — yayınlanır. Aşağıdaki Lerp dalı
                    // yayınlanmaz ve gerekmez: kaynağını `Rigidbody.position`'dan okuyor, yani
                    // bayat transform tüketmiyor; görsel yazback en fazla bir adım gecikir.
                    _localRagdoll.PublishTransform();
                }
                else
                {
                    _localRagdoll.position = Vector3.Lerp(_localRagdoll.position, _correctionPos, 8f * Time.deltaTime);
                    _localRagdoll.rotation = Quaternion.Slerp(_localRagdoll.rotation, _correctionRot, 8f * Time.deltaTime);
                }
            }

            // Görsel katman (tüm instance'larda): mesh kayma pozisyonuna çizilir, fiziğe karışmaz (GDD 5.1).
            // Exponential smoothing: SyncVar tick adımlarını gizler (fizik sabiti değil, salt görsel).
            // Hafif prosedürel sallanma sonra eklenecek.
            if (_corpseVisual == null)
                return;

            Vector3 lp = _corpseVisual.localPosition;
            lp.z = Mathf.Lerp(lp.z, SlidePosition, 10f * Time.deltaTime);
            _corpseVisual.localPosition = lp;
        }
    }
}
