using SunsetExpress.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Kopma uyarısı HUD'ının VERİ KAYNAĞI — lokal owner'ın <see cref="PlayerGrabber"/>'ını bulur,
    /// <see cref="PlayerGrabber.OnGripWarningChanged"/>'e abone olur ve kademeyi
    /// <see cref="GripWarningHud.SetLevel"/>'e aktarır (GDD 4.3, 13.2).
    ///
    /// Kademeleme SERVER'da yapılır (eşikler CoffinProfile'da: şu an 0.50 / 0.65 / 0.80 — profil
    /// değeridir, buradaki sayı yalnız bilgi amaçlıdır) ve yalnız kademe
    /// DEĞİŞİNCE `ObserversRpc(BufferLast = true)` ile yayınlanır — yani event HER gözlemci kopyasında
    /// ateşlenir. Bu HUD'ı etkilemez: burada yalnız LOKAL OWNER'ın grabber'ına abone olunur, dolayısıyla
    /// başkasının uyarı ikonu senin ekranında belirmez. Observer'a çıkarılmasının sebebi el IK'sıdır —
    /// kol uzaması aynı kademeden beslenir ve komedi başkasının debelenmesini izlemektedir.
    /// Burada tension POLL EDİLMEZ: tick başına akan bir gerilim
    /// değeri kopmayı state senkronuna çevirirdi, oysa pazarlıksız kural grab/bırakma/kapak/kopmanın
    /// EVENT senkronuyla taşınmasını şart koşar (GDD 12.2). <see cref="PlayerGrabber.GripTension"/>
    /// hâlâ public ama SERVER-ONLY'dir — HUD onu okumaz.
    ///
    /// Ömür farkı: bu bileşen DontDestroyOnLoad ile kalıcıdır, grabber ise sahne/ağ ömürlüdür.
    /// Abonelik bu yüzden tek noktadan (<see cref="Bind"/>/<see cref="Unbind"/>) yönetilir — aksi
    /// halde kalıcı HUD ölü grabber'ı tutar (sızıntı) ya da ikon ekranda asılı kalır.
    /// </summary>
    [RequireComponent(typeof(GripWarningHud))]
    public sealed class GripWarningBinder : MonoBehaviour
    {
        [Header("Owner arama")]
        [Tooltip("Lokal oyuncu bulunana kadar tarama aralığı (sn). Oyuncu ağ üzerinden geç spawn olabilir.")]
        [SerializeField] private float _rebindInterval = 0.5f;

        [Header("Debug")]
        [Tooltip("F8: kademeleri 0→1→2→3→0 döndürür. Ağ olmadan saf UI iterasyonu için " +
                 "(F9 = DebugSyncJump, F10 = CoffinDamage — çakışmasın diye F8).")]
        [SerializeField] private bool _debugCycleKeyEnabled = true;

        private GripWarningHud _hud;
        private PlayerGrabber _grabber;
        private bool _bound;        // _grabber fake-null olsa DA aboneliğin varlığını bilmek için
        private byte _liveLevel;    // event'ten gelen son kademe (debug override sırasında da birikir)
        private float _nextRebindTime;
        private bool _debugOverride;
        private byte _debugLevel;

        private void Awake()
        {
            _hud = GetComponent<GripWarningHud>();
        }

        private void Update()
        {
            if (_debugCycleKeyEnabled)
                PollDebugKey();

            EnsureGrabber();

            // Override açıkken canlı kademe uygulanmaz; _liveLevel'da birikir ve F8 ile
            // override kapandığında geri yüklenir.
            if (_debugOverride)
                _hud.SetLevel(_debugLevel);
        }

        /// <summary>
        /// Aboneliğin doğru grabber'da olmasını sağlar. Kaynak kaybında (despawn, sahiplik devri,
        /// sahne değişimi) ÖNCE abonelikten çıkılır, SONRA yeni kaynak aranır.
        /// </summary>
        private void EnsureGrabber()
        {
            if (IsLocalOwner(_grabber))
                return;

            // Doğrudan "_grabber = null" YAPILMAZ: referansı bırakmadan önce abonelik kopmalı,
            // yoksa kalıcı HUD sahne ömürlü grabber'a bağlı kalır. Unbind aynı zamanda ikonu
            // söndürür — kaynak gidince ekranda "kopmak üzere" asılı kalmasın.
            Unbind();

            if (Time.unscaledTime < _nextRebindTime)
                return;

            _nextRebindTime = Time.unscaledTime + _rebindInterval;

            // Her karede değil, aralıklı tarama: oyuncu sayısı küçük (2-4) ve bu yol yalnız
            // bağlanana kadar koşar.
            PlayerGrabber[] grabbers = FindObjectsByType<PlayerGrabber>(FindObjectsSortMode.None);
            foreach (PlayerGrabber g in grabbers)
            {
                if (!IsLocalOwner(g))
                    continue;

                Bind(g);
                return;
            }
        }

        private void Bind(PlayerGrabber grabber)
        {
            _grabber = grabber;
            _bound = true;
            grabber.OnGripWarningChanged += HandleWarningChanged;

            // Abone olur olmaz mevcut kademeyi çek. Event yalnız DEĞİŞİMDE ateşlendiği için,
            // abonelikten önce yükselmiş bir kademe (geç bağlanma, sahiplik devri, HUD'ın
            // grabber'dan sonra ayağa kalkması) bir daha yayınlanmaz — ikon hiç belirmezdi.
            ApplyLiveLevel(grabber.GripWarningLevel);
        }

        /// <summary>Abonelikten çıkar ve ikonu söndürür. Çağrılması güvenlidir (idempotent).</summary>
        private void Unbind()
        {
            if (!_bound)
                return;

            // Fake-null: grabber yok edildiyse C# event'i zaten onunla öldü, -= gereksiz ve
            // destroyed obje üzerinde erişim riski. _bound sayesinde bu durumu yine de yakalarız,
            // yoksa "_grabber != null" false döner, unbind atlanır ve ikon ekranda asılı kalırdı.
            if (_grabber != null)
                _grabber.OnGripWarningChanged -= HandleWarningChanged;

            _grabber = null;
            _bound = false;
            ApplyLiveLevel(GripWarningHud.LevelNone);
        }

        private void HandleWarningChanged(byte level) => ApplyLiveLevel(level);

        private void ApplyLiveLevel(byte level)
        {
            _liveLevel = level;

            // _hud null kontrolü: OnDestroy sırasında bileşen zaten yok edilmiş olabilir.
            if (!_debugOverride && _hud != null)
                _hud.SetLevel(level);
        }

        /// <summary>
        /// Bu grabber lokal oyuncunun mu — ağ katmanı güvenli okunabilir haldeyken.
        /// NetworkObject null kontrolü ŞART: FishNet'in <c>IsSpawned</c>/<c>IsOwner</c>
        /// property'leri iç <c>_networkObjectCache</c> alanını null kontrolü OLMADAN dereference
        /// eder (NetworkBehaviour.cs:28) ve o alan ancak preinitialize sırasında atanır (a.g.e.:159).
        /// FindObjectsByType henüz initialize olmamış bir grabber döndürebildiği için doğrudan
        /// IsSpawned okumak NullReferenceException riski taşır.
        /// </summary>
        private static bool IsLocalOwner(PlayerGrabber g)
        {
            return g != null && g.NetworkObject != null && g.IsSpawned && g.IsOwner;
        }

        private void PollDebugKey()
        {
            if (Keyboard.current == null || !Keyboard.current.f8Key.wasPressedThisFrame)
                return;

            _debugLevel = (byte)((_debugLevel + 1) % 4);
            _debugOverride = _debugLevel != GripWarningHud.LevelNone;

            // Override kapanınca canlı kademeye ANINDA dön: override sırasında gelen event'ler
            // _liveLevel'da birikti, HUD'a uygulanmamıştı.
            if (!_debugOverride && _hud != null)
                _hud.SetLevel(_liveLevel);

            Debug.Log($"[GripWarningHud] Debug kademe: {_debugLevel}" +
                      (_debugOverride ? " (override AÇIK)" : " (override kapandı, canlı veriye dönüldü)"));
        }

        // Kalıcı HUD, sahne ömürlü grabber'a abone: kapanış yollarında abonelik MUTLAKA kopmalı.
        private void OnDisable() => Unbind();

        private void OnDestroy() => Unbind();
    }
}
