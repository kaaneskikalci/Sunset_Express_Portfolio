using FishNet.Object;
using SunsetExpress.Profiles;
using UnityEngine;

namespace SunsetExpress.Coffins
{
    /// <summary>
    /// Host-authoritative tabut (GDD 12.2): fizik otoritesi HER ZAMAN host'ta; client'ta predict
    /// EDİLMEZ, NetworkTransform ile interpolasyon + hafif extrapolasyon ile gösterilir.
    /// Rigidbody sabitleri CoffinProfile'dan uygulanır (GDD 4.1, 12.3). Kapak ayrı Rigidbody +
    /// HingeJoint ile gövdeye bağlıdır, kilitli değildir (GDD 4.1, 5.2). Bkz. Docs/GDD/02-coffin-physics.md.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class Coffin : NetworkBehaviour
    {
        [Header("Profil (GDD 12.3)")]
        [SerializeField] private CoffinProfile _profile;

        [Header("Kapak (GDD 4.1, 5.2)")]
        [Tooltip("Kapak Rigidbody'si — HingeJoint ile gövdeye bağlı, kilitli değil.")]
        [SerializeField] private Rigidbody _lid;

        [Header("Grab Point'ler (GDD 4.2)")]
        [Tooltip("Tabutun köşelerinde/baş-ayak ucunda tanımlı tutma noktaları. 2 kişilik mod: baş ve ayak (2 nokta).")]
        [SerializeField] private Transform[] _grabPoints;

        private Rigidbody _body;
        private bool[] _occupied; // server-only doluluk

        /// <summary>Tabut gövdesinin Rigidbody'si — grab joint'inin connectedBody'si.</summary>
        public Rigidbody Body => _body;

        /// <summary>Fizik sabitleri profili — CorpseSlide kayma sabitlerini buradan okur (GDD 12.3).</summary>
        public CoffinProfile Profile => _profile;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
        }

        /// <summary>
        /// Yaw-özel sönüm — TimeManager.OnPrePhysicsSimulation'a bağlı: her manuel fizik adımından TAM
        /// BİR KEZ önce çalışır (FixedUpdate, TimeManager modunda birden fazla kez çalışıp aynı
        /// eski angularVelocity üzerinden torque biriktirebiliyordu — kare-hızına bağlı sönüm). Tabutun
        /// yatay dönüşü SERBEST (hareket-tabanlı rotasyon, GDD 6.4) ama sönümsüz olunca savruluyordu;
        /// sadece DÜNYA-DİKEY (yaw) bileşenine counter-torque, devrilme (X/Z slappy) etkilenmez.
        /// Yalnız server'da abone olunur → yalnız otoriter (dinamik) tabutta çalışır.
        /// </summary>
        private void ServerPrePhysics(float delta)
        {
            if (_body == null)
                return;

            float yawDamp = _profile != null ? _profile.yawDamping : 0f;
            if (yawDamp > 0f)
            {
                float yawW = Vector3.Dot(_body.angularVelocity, Vector3.up);
                _body.AddTorque(Vector3.up * (-yawW * yawDamp), ForceMode.Acceleration);
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            // Aynı sahne objesi önce client olarak başlatıldıysa (sahne yeniden yüklenmeden yeni
            // oturumda server rolü) kinematic kalmış olabilir — otoritede daima dinamik.
            _body.isKinematic = false;
            if (_lid != null)
                _lid.isKinematic = false;

            ApplyProfile();
            _occupied = new bool[_grabPoints != null ? _grabPoints.Length : 0];

            // Yaw damping'i fizik tick'ine hizala: manuel sim öncesi tam bir kez.
            TimeManager.OnPrePhysicsSimulation += ServerPrePhysics;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            if (TimeManager != null)
                TimeManager.OnPrePhysicsSimulation -= ServerPrePhysics;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // Client tabutu simüle ETMEZ (host-authoritative, GDD 12.2). Physics Mode = TimeManager
            // her instance'ta fizik adımladığı için, server olmayan tarafta rigidbody'leri kinematic
            // yaparız — yoksa yerel fizik NetworkTransform'un uyguladığı poz ile kavga eder.
            if (!IsServerStarted)
            {
                _body.isKinematic = true;
                if (_lid != null)
                    _lid.isKinematic = true;
            }
        }

        private void ApplyProfile()
        {
            if (_profile == null)
            {
                Debug.LogWarning($"{name}: CoffinProfile atanmadı, varsayılan Rigidbody değerleri kullanılıyor.");
                return;
            }

            // Toplam kütle ileride + CorpseProfile.mass ile beslenecek (GDD 4.1).
            _body.mass = _profile.baseShellMass;
            _body.solverIterations = _profile.solverIterations;
            _body.solverVelocityIterations = _profile.solverVelocityIterations;
            _body.maxAngularVelocity = _profile.maxAngularVelocity;
            if (_profile.bodyAngularDamping > 0f)
                _body.angularDamping = _profile.bodyAngularDamping;

            if (_lid != null)
            {
                _lid.solverIterations = _profile.solverIterations;
                _lid.solverVelocityIterations = _profile.solverVelocityIterations;
            }
        }

        // ---- Grab (GDD 4.2) — doluluk yönetimi server-authoritative ----

        /// <summary>Server-only: verilen konuma en yakın BOŞ grab point'i bulur ve işaretler.</summary>
        public bool TryOccupyNearest(Vector3 worldPos, out int index)
        {
            index = -1;
            if (_grabPoints == null || _occupied == null)
                return false;

            float best = float.MaxValue;
            for (int i = 0; i < _grabPoints.Length; i++)
            {
                if (_occupied[i] || _grabPoints[i] == null)
                    continue;
                float d = (_grabPoints[i].position - worldPos).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    index = i;
                }
            }

            if (index >= 0)
            {
                _occupied[index] = true;
                return true;
            }
            return false;
        }

        /// <summary>Server-only: grab point'i serbest bırakır.</summary>
        public void FreePoint(int index)
        {
            if (_occupied != null && index >= 0 && index < _occupied.Length)
                _occupied[index] = false;
        }

        /// <summary>Profil hedef ip boyu (linear limit).</summary>
        public float GrabLinearLimit => _profile != null ? _profile.jointLinearLimit : 0.08f;

        // Kopma sistemi sabitleri (GDD 4.3) — ileride CorpseProfile.breakForceMultiplier ile çarpılacak.
        public float GrabBreakForce => _profile != null ? _profile.grabBreakForce : 4500f;
        public float GrabBreakDeviation => _profile != null ? _profile.grabBreakDeviation : 0.3f;
        // Uyarı kademeleri (GDD 4.3, 13.2). 0/negatif değer GÜVENLİ VARSAYILANA düşer: yeni serialized
        // alan eski asset'lerde 0 gelir ve eşik 0 olsaydı "tension >= 0" hep doğru olup ikonu kalıcı
        // olarak en üst kademede kilitlerdi (serialized-alan tuzağı).
        public float GrabBreakWarnRatio => Ratio(_profile != null ? _profile.grabBreakWarnRatio : 0f, 0.50f);
        public float GrabBreakWarnRatioMedium => Ratio(_profile != null ? _profile.grabBreakWarnRatioMedium : 0f, 0.65f);
        public float GrabBreakWarnRatioSevere => Ratio(_profile != null ? _profile.grabBreakWarnRatioSevere : 0f, 0.80f);

        private static float Ratio(float value, float fallback) => value > 0f ? value : fallback;
        public float RegrabCooldown => _profile != null ? _profile.regrabCooldown : 0.5f;
        public float SyncJumpBreakMultiplier => _profile != null ? _profile.syncJumpBreakForceMultiplier : 2f;
        public float SyncJumpBreakWindow => _profile != null ? _profile.syncJumpBreakWindow : 0.4f;

        /// <summary>Makara süresi — grab anındaki mesafeden hedef limite küçülme (sn).</summary>
        public float HoistDuration => _profile != null ? _profile.hoistDuration : 0.6f;

        /// <summary>Grab point transform'u — el IK'sı hedefi buradan türetir (ofset, tabutun kendi
        /// eksenlerinden kurulan bir çerçevede uygulanır; bkz. PlayerArmStretchIK.BuildOffsetFrame).
        /// SALT GÖRSEL: fizik kurulumu bu erişimciyi çağırmaz — ConfigureGrabJoint connectedAnchor'ı
        /// doğrudan `_grabPoints[index]`ten, hoist ve kopma ölçümü GrabPointWorld'den okur.</summary>
        public Transform GrabPoint(int index)
        {
            return _grabPoints != null && index >= 0 && index < _grabPoints.Length
                ? _grabPoints[index]
                : null;
        }

        /// <summary>Grab point'in dünya konumu (hoist başlangıç mesafesi için).</summary>
        public Vector3 GrabPointWorld(int index)
        {
            return _grabPoints != null && index >= 0 && index < _grabPoints.Length && _grabPoints[index] != null
                ? _grabPoints[index].position
                : transform.position;
        }

        /// <summary>
        /// Oyuncunun Rigidbody'sine eklenmiş ConfigurableJoint'i GDD 4.2/12.3 spec'ine göre yapılandırır.
        /// Joint oyuncu ↔ tabut arasındadır (asla parent değil, pazarlıksız 4.1).
        /// - Linear LIMITED + yumuşak limitSpring: tabut, el anchor'ının 'ip boyu' yarıçapında HAPSOLUR —
        ///   snap hissi buradan gelir; parent olmadan oyuncular tabutun altında kalmaya zorlanır.
        /// - Angular tamamen SERBEST: hareket-tabanlı yaw (GDD 6.4) + slappy devrilme. İçinden-geçme
        ///   çarpışmada, savrulma yawDamping'de. (Serbest/limitli açısal karışımı 360°'de fırlatıyordu.)
        /// - initialLinearLimit: grab anındaki gerçek mesafe; PlayerGrabber bunu hoist ile hedefe küçültür.
        /// connectedAnchor tabut lokal uzayında olduğundan server ve owner'da aynı değeri verir (tutarlı).
        /// </summary>
        public void ConfigureGrabJoint(ConfigurableJoint joint, int pointIndex, float initialLinearLimit)
        {
            joint.connectedBody = _body;
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = _grabPoints != null && pointIndex >= 0 && pointIndex < _grabPoints.Length
                ? transform.InverseTransformPoint(_grabPoints[pointIndex].position)
                : Vector3.zero;

            // Deterministik joint çerçevesi. Çarpışma BAŞTAN açık kurulur — iç içe geçmeye karşı kalıcı
            // fiziksel sigorta. (enableCollision'ı sonradan true yapmak PhysX'te güvenilir işlemez; makara
            // fazının susturulması PlayerGrabber'da Physics.IgnoreCollision ile yapılır.)
            joint.axis = Vector3.right;
            joint.secondaryAxis = Vector3.up;
            joint.enableCollision = true;

            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Limited;
            SetLinearLimit(joint, Mathf.Max(initialLinearLimit, GrabLinearLimit));
            ApplyLinearLimitSpring(joint, 1f); // yayın ilk yazımı — SetLinearLimit artık yaya dokunmuyor

            // TÜM açısal eksenler SERBEST. Serbest (yaw) + Limited (tilt koni) KARIŞIMI, ConfigurableJoint'in
            // açı temsili yüzünden tabut ~360° yaw döndüğünde tilt limitini TEKİLLEŞTİRİP dev kuvvet
            // üretiyordu — "tam dönüş sonrası fırlama" tam olarak buydu (quaternion çift-örtüsü: 360°'de
            // rotasyon başa döner ama joint bunu tam tur dışarıda sanır). Çözüm: açısal hiç limitleme yok.
            // Tabut iki grab point'ten "serbest bilekli" tutulur; yönelimi iki noktadan + gravity'den doğar
            // (hareket-tabanlı yaw, GDD 6.4). İçinden-geçme sigortası ÇARPIŞMADA (enableCollision), aşırı
            // yaw savrulması YAW DAMPING'de (Coffin.ServerPrePhysics). Slappy devrilme artık tam serbest.
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
        }

        /// <summary>Linear limiti (ip boyunu) günceller — hoist bunu her frame küçültür.</summary>
        public void SetLinearLimit(ConfigurableJoint joint, float limit)
        {
            // contactDistance: solver sınırı bu mesafe ÖNCEDEN görmeye başlar. 0 (default) bırakılırsa
            // kısıt ancak sınıra değince aktive olur ve 50 Hz'de girip-çıkıp titrer (boundary chatter) —
            // "kopmaya yakınken titriyor" şikayetinin kök nedeni. Solver ayrıntısı olduğu için profil
            // değil mühendislik sabiti.
            // YALNIZ LİMİT — yay BU METOTTA YAZILMAZ. Eskiden burası yayı da 1× yazıyordu ve
            // makara (TickHoist) her adımda bunu çağırdığı için senkron zıplama penceresinde
            // uygulanan 3× sert yay bir sonraki adımda siliniyordu; kenar tetikleme zaten
            // "uygulandı" saydığı için geri de gelmiyordu. Yayın tek yazarı ApplyLinearLimitSpring.
            joint.linearLimit = new SoftJointLimit
            {
                limit = limit,
                contactDistance = Mathf.Min(0.05f, limit * 0.5f)
            };
        }

        /// <summary>
        /// Linear limit yayını yazar. <paramref name="multiplier"/> yalnız senkron zıplama
        /// penceresinde 1'den büyüktür (GDD 6.5).
        ///
        /// NEDEN GEREKLİ: zıplama oyuncuya `VelocityChange` olarak biniyor, tabut ise 8 cm boşluk +
        /// yumuşak yay üzerinden takip ediyor — playtest'te (2026-08) impulsun çoğu tabutu kaldırmak
        /// yerine YAYI GERMEYE gidiyordu ve dört kişi aynı anda zıplasa bile kazanılan yükseklik
        /// hissedilmiyordu. Pencerede yay sertleşince impuls tabuta aktarılır.
        ///
        /// KURAL KONTROLÜ: bu bir "taşıyıcı sayısı çarpanı" DEĞİLDİR (GDD 4.5 onu yasaklıyor).
        /// Çarpan her taşıyıcıda AYNI — bir kişi de zıplasa dört kişi de zıplasa joint aynı oranda
        /// sertleşir; birleşme fizikte olur. GDD 6.5 zaten aynı pencerede kopma eşiğinin
        /// yükseltilmesini öngörüyor, bu onun ikizi: biri kopmayı önlüyor, öteki impulsu taşıyor.
        /// </summary>
        public void ApplyLinearLimitSpring(ConfigurableJoint joint, float multiplier)
        {
            float baseSpring = _profile != null ? _profile.jointLinearLimitSpring : 15000f;
            float baseDamper = _profile != null ? _profile.jointLinearLimitDamper : 200f;
            float m = Mathf.Max(1f, multiplier);

            joint.linearLimitSpring = new SoftJointLimitSpring
            {
                spring = baseSpring * m,
                // Damper de birlikte ölçeklenir: yalnız yayı sertleştirmek sönümsüz bir zıplama
                // yayı üretir ve tabut pencereden sonra zıplayarak salınır.
                damper = baseDamper * m
            };
        }

        /// <summary>Senkron zıplama penceresindeki yay çarpanı (GDD 6.5). 0/negatifte güvenli
        /// varsayılana düşer — eski asset koruması.</summary>
        public float SyncJumpSpringMultiplier =>
            _profile != null && _profile.syncJumpSpringMultiplier > 1f ? _profile.syncJumpSpringMultiplier : 3f;
    }
}
