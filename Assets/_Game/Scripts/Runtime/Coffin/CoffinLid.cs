using FishNet.Object;
using SunsetExpress.Profiles;
using UnityEngine;

namespace SunsetExpress.Coffins
{
    /// <summary>
    /// Kod-tabanlı kapak mandalı (GDD 5.2): kapak artık pasif fizik kazası değil, kontrollü mekanizma.
    /// Kapalıyken hinge kilitlidir (limit 0); server her fizik adımının sonunda iki tetiği ölçer —
    /// yatış açısı eşiği VEYA sert darbe — ve mandalı bırakır. Tabut dike döner + kapak kapanırsa
    /// mandal otomatik yeniden kilitlenir (kapak krizi atlatılabilir panik anıdır, kalıcı ceza değil;
    /// kalıcı ceza katmanı Mod B'dedir).
    ///
    /// Okunabilir kaos (GDD 5.4/1.4): eşikler profilden, rastgelelik yok — oyuncu "45°'yi geçersem
    /// kapak gelir" diye ÖĞRENİR. Hasar eşiği düşürür (GDD 4.6, Damage01'i hasar sistemi doldurur).
    /// Firavun (lidSealed) mandalı asla bırakmaz (GDD 5.3).
    ///
    /// Network: mandal yalnız SERVER fiziğinde yaşar — client kapağı zaten kinematik + NT ile izler.
    /// Durum değişimi state değil EVENT olarak duyurulur (GDD 12.2) — ses/görsel kanca noktası.
    /// </summary>
    [RequireComponent(typeof(Coffin))]
    public sealed class CoffinLid : NetworkBehaviour
    {
        [Tooltip("Kapak child'ındaki HingeJoint. Editördeki limitleri (ör. 0-110) 'açık' limitler " +
                 "olarak cache'lenir; mandal kilidi limitleri 0'a çeker.")]
        [SerializeField] private HingeJoint _lidHinge;

        private Coffin _coffin;
        private CorpseSlide _corpseSlide;
        private JointLimits _openLimits;
        private float _nextLatchChangeTime;
        private float _pendingImpactImpulse;
        private float _pendingImpactTime = float.NegativeInfinity;
        private bool _lidDestroyed; // kalıcı: parçalanmış kapakta mandal mantığı bir daha çalışmaz

        /// <summary>Mandal kilitli mi (server-otoriter). Mod B tetiği ve hasar sistemi okur.</summary>
        public bool IsLatched { get; private set; } = true;

        /// <summary>Normalize hasar (0-1) — CoffinDamage doldurur (Adım 2); eşik hasarla düşer (GDD 4.6).</summary>
        public float Damage01 { get; set; }

        /// <summary>Kapağın anlık FİZİKSEL açıklığı (derece). Mod B, mandal durumundan bağımsız olarak
        /// kapağın gerçekten açık olduğunu bununla doğrular (mandal açık ≠ kapak açık).
        /// Hinge yoksa (kapak parçalanmış) tam açık sayılır.</summary>
        public float CurrentOpenAngle => _lidHinge != null ? Mathf.Abs(_lidHinge.angle) : 180f;

        private void Awake()
        {
            _coffin = GetComponent<Coffin>();
            _corpseSlide = GetComponent<CorpseSlide>();
            if (_lidHinge != null)
                _openLimits = _lidHinge.limits;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Parçalanmış kapak KALICIDIR (pazarlıksız 4.6): aynı sahne objesi server stop→start
            // yaşarsa mandal mekanizması DİRİLMEZ. FishNet nested NetworkObject'ları destroy etmek
            // yerine deinitialize edebildiği için _lidHinge non-null kalabiliyor ve buradaki
            // ApplyLatch(true) otoriter IsLatched'i sessizce true yapardı. CorpseSlide'ın
            // CorpseLost guard'ıyla aynı desen.
            if (_lidDestroyed)
                return;

            // Fizik adımına hizalanma (Coffin/CorpseSlide/PlayerGrabber ile aynı desen).
            // Abonelik koşulsuz — iptalle simetrik kalsın; handler hinge yoksa zaten çıkar.
            TimeManager.OnPostPhysicsSimulation += ServerPostPhysics;

            if (_lidHinge == null)
            {
                Debug.LogError($"{name}: CoffinLid'e hinge atanmadı — mandal devre dışı.");
                return;
            }
            ApplyLatch(true, silent: true);
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (TimeManager != null)
                TimeManager.OnPostPhysicsSimulation -= ServerPostPhysics;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServerStarted)
                return;

            // Darbe zaman damgasıyla tutulur (bayat impuls gecikmeli mandal açamaz —
            // okunabilir sebep→sonuç). Hafıza penceresi içindeki en güçlü darbe kazanır;
            // süresi dolmuş kayıt yeni darbeyle koşulsuz değişir.
            float impulse = collision.impulse.magnitude;
            float memory = _coffin.Profile != null ? _coffin.Profile.lidImpactMemory : 0.2f;
            if (Time.time - _pendingImpactTime > memory || impulse > _pendingImpactImpulse)
            {
                _pendingImpactImpulse = impulse;
                _pendingImpactTime = Time.time;
            }
        }

        /// <summary>
        /// Mandal kararı — fizik adımından SONRA: OnCollisionEnter darbeyi adımın İÇİNDE biriktirir,
        /// karar da aynı adımın sonunda tüketir. FixedUpdate'te (TimeManager modunda adımdan kopuk)
        /// darbe bir sonraki adıma sarkabiliyor veya iki kez yoklanıp boşa tüketilebiliyordu.
        /// </summary>
        private void ServerPostPhysics(float delta)
        {
            if (_lidDestroyed || !IsServerStarted || _lidHinge == null)
                return;

            ApplyHingeStiffness();

            // Firavun/Lahit: kapak MÜHÜRLÜ — mandal hiçbir koşulda bırakılmaz (GDD 5.3).
            if (_corpseSlide != null && _corpseSlide.Corpse != null && _corpseSlide.Corpse.lidSealed)
            {
                _pendingImpactImpulse = 0f;
                return;
            }

            // Cooldown yalnız DURUM GEÇİŞİNİ bekletir (darbe bağışıklığı değil); darbe hafızası ise
            // yaş penceresiyle sınırlı — cooldown biterken yalnız GÜNCEL (≤ lidImpactMemory sn) darbe
            // karara girebilir, bayat darbe gecikmeli mandal açamaz.
            if (Time.time < _nextLatchChangeTime)
                return;

            CoffinProfile p = _coffin.Profile;
            float impactMemory = p != null ? p.lidImpactMemory : 0.2f;
            float impact = Time.time - _pendingImpactTime <= impactMemory ? _pendingImpactImpulse : 0f;
            _pendingImpactImpulse = 0f;
            float tilt = Vector3.Angle(transform.up, Vector3.up);

            if (IsLatched)
            {
                float baseThreshold = p != null ? p.lidOpenAngleThreshold : 45f;
                float damageFactor = p != null ? p.damageLidThresholdFactor : 0.6f;
                float impulseThreshold = p != null ? p.lidImpactImpulseThreshold : 400f;

                // Hasar eşiği düşürür: ağır hasarlı tabutta kapak daha kolay açılır (GDD 4.6).
                float threshold = baseThreshold * (1f - Mathf.Clamp01(Damage01) * damageFactor);

                if (tilt > threshold || impact > impulseThreshold)
                    ApplyLatch(false, silent: false);
            }
            else
            {
                float relatchAngle = p != null ? p.lidRelatchAngle : 12f;
                float closedAngle = p != null ? p.lidClosedAngle : 8f;

                // Yeniden kilit: tabut dik VE kapak kapalı açıda (hinge kendi açısını bilir).
                if (tilt < relatchAngle && Mathf.Abs(_lidHinge.angle) < closedAngle)
                    ApplyLatch(true, silent: false);
            }
        }

        /// <summary>
        /// Menteşe sıkılığını hasara göre yazar (GDD 4.6). SAĞLAM tabutta kapak sıkı oturur ve
        /// sallanmaz; hasar arttıkça yay gevşer ve kapak giderek daha rahat savrulur.
        ///
        /// Playtest (2026-08): "menteşe biraz sıkılabilir, böylece tabut düştükçe/hasar aldıkça
        /// gevşeme hissi daha iyi hissettirilir." Şikâyetin sebebi ayarın düşük olması DEĞİLDİ —
        /// prefab'da `useSpring` KAPALIYDI, yani menteşe hiç yay taşımıyordu. Kapak 0-110° arası
        /// tamamen serbest sallanıyordu; ne sıkılık vardı ne de gevşeyecek bir şey.
        /// Hasar da yalnız MANDAL EŞİĞİNİ düşürüyordu, menteşenin kendisine dokunmuyordu.
        ///
        /// Her adımda yazılıyor ama ucuz (iki float): hasar sürekli değişebilir ve kenar tetikleme
        /// için ayrı bir "son hasar" alanı tutmak, hasar sisteminin yazma anıyla senkron kalmayı
        /// gerektirirdi — bu, sessizce bayat kalabilecek bir bağ olurdu.
        /// </summary>
        private void ApplyHingeStiffness()
        {
            CoffinProfile p = _coffin.Profile;
            float baseSpring = p != null && p.lidHingeSpring > 0f ? p.lidHingeSpring : 120f;
            float baseDamper = p != null && p.lidHingeDamper > 0f ? p.lidHingeDamper : 12f;
            float loosen = p != null ? Mathf.Clamp01(p.lidHingeDamageLoosen) : 0.8f;

            // Hasarda yay ZAYIFLAR; damper de birlikte iner, yoksa gevşemiş kapak sönümlü kalır
            // ve "hurdaya dönüyor" hissi çıkmaz.
            float scale = 1f - Mathf.Clamp01(Damage01) * loosen;

            _lidHinge.useSpring = true;
            _lidHinge.spring = new JointSpring
            {
                spring = baseSpring * scale,
                damper = baseDamper * scale,
                targetPosition = 0f // kapalı konum
            };
        }

        private void ApplyLatch(bool latched, bool silent)
        {
            IsLatched = latched;
            _nextLatchChangeTime = Time.time +
                (_coffin.Profile != null ? _coffin.Profile.lidRelatchCooldown : 0.75f);

            // Kilit = hinge limitleri 0'a çekilir (kapak kapalı pozda kilitli);
            // bırakma = editörde ayarlanan açık limitler geri gelir.
            _lidHinge.limits = latched ? new JointLimits { min = 0f, max = 0.01f } : _openLimits;
            _lidHinge.useLimits = true;

            if (!silent)
                ObserversLatchChanged(latched);
        }

        /// <summary>Hasar sistemi çağırır (GDD 4.6): kapak kalıcı olarak parçalandı — mandal işleyişi
        /// biter, kapak "sonsuza dek açık" sayılır (Mod B tetiği IsLatched=false + LidDestroyed okur).</summary>
        public void NotifyLidDestroyed()
        {
            IsLatched = false;
            _lidDestroyed = true;
            enabled = false;

            // enabled=false C# event aboneliğini DURDURMAZ: kapak bir çarpışma callback'i
            // sırasında parçalanırsa nested obje Destroy için frame sonuna kadar yaşar ve aynı adımın
            // PostPhysics handler'ı yine çağrılır — cooldown bitmiş + tabut dikse parçalanmış kapağı
            // "yeniden kilitledi" sanıp sahte event yayınlardı. Aboneliği hemen kes (OnStopServer'daki
            // ikinci -= idempotenttir); _lidDestroyed ise ikinci savunma katmanı (fail-closed).
            if (TimeManager != null)
                TimeManager.OnPostPhysicsSimulation -= ServerPostPhysics;
        }

        [ObserversRpc]
        private void ObserversLatchChanged(bool latched)
        {
            // v1: log. Ses/görsel kanca buraya bağlanır: menteşe gıcırtısı, mandal "klik" sesi
            // (GDD 14.2 — kopma uyarısının diegetic dili kapak için de geçerli).
            Debug.Log(latched ? "[Kapak] Mandal yeniden kilitlendi." : "[Kapak] Mandal bırakıldı!");
        }
    }
}
