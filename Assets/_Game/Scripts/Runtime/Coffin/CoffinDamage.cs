using FishNet.Object;
using SunsetExpress.Profiles;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.Coffins
{
    /// <summary>
    /// Tabut hasar sistemi (GDD 4.6): darbe şiddetine göre server'da hasar sayacı birikir.
    /// - Hasar arttıkça kapak mandalının eşiği düşer (CoffinLid.Damage01 üzerinden).
    /// - PAZARLIKSIZ: sayaç maksimuma ulaşınca YALNIZCA KAPAK parçalanır (kalıcı yok olur);
    ///   gövde her zaman taşınabilir kalır — kontrat soft-lock'a giremez.
    /// - Görsel çatlaklar/kırık tutamaç sonraki art pass'i; skor değil komedi/ekonomi aracı.
    ///
    /// Network: hasar değeri server-only (HUD/rapor ihtiyacı doğunca kanal açılır). Kapak, kendi
    /// nested NetworkObject'idir ve maks hasarda server DESPAWN eder — client yıkımı ve geç
    /// katılan durumu FishNet spawn sistemi tarafından otomatik çözülür (RPC/state gerekmez).
    /// </summary>
    [RequireComponent(typeof(Coffin))]
    public sealed class CoffinDamage : NetworkBehaviour
    {
        [Tooltip("Kapağın NESTED NetworkObject'i (Lid child'ına eklenir) — maks hasarda despawn edilir.")]
        [SerializeField] private NetworkObject _lidNetworkObject;

        private Coffin _coffin;
        private CoffinLid _lid;
        private CorpseSlide _corpseSlide;
        private float _damage;

        /// <summary>Kapak kalıcı olarak parçalandı mı (server-otoriter).</summary>
        public bool LidDestroyed { get; private set; }

        /// <summary>Normalize hasar (0-1) — cenaze raporu/ödeme kesintisi ileride bunu okuyacak (GDD 4.6).</summary>
        public float Damage01
        {
            get
            {
                float max = _coffin.Profile != null ? _coffin.Profile.damageMax : 100f;
                return Mathf.Clamp01(_damage / Mathf.Max(1f, max));
            }
        }

        private void Awake()
        {
            _coffin = GetComponent<Coffin>();
            _lid = GetComponent<CoffinLid>();
            _corpseSlide = GetComponent<CorpseSlide>();
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServerStarted)
                return;

            CoffinProfile p = _coffin.Profile;
            float minImpulse = p != null ? p.minDamageImpulse : 300f;
            float scale = p != null ? p.damageImpulseScale : 0.05f;

            float impulse = collision.impulse.magnitude;
            if (impulse > minImpulse)
                AddDamage((impulse - minImpulse) * scale);
        }

        private void Update()
        {
            // GEÇİCİ TEST ARACI (DebugSyncJump gibi vertical slice öncesi silinir):
            // F10 = server'da +25 hasar — kapak yıkımını çarpma tekrarı olmadan test etmek için.
            if (IsServerStarted && Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame)
            {
                AddDamage(25f);
                Debug.Log($"[CoffinDamage] Debug hasar: {_damage:F0} ({Damage01:P0})");
            }
        }

        /// <summary>Server-only: hasar ekler, mandal eşiğini günceller, maksimumda kapağı parçalar.</summary>
        public void AddDamage(float amount)
        {
            if (!IsServerStarted || LidDestroyed || amount <= 0f)
                return;

            // Firavun/Lahit (GDD 5.3,): "kapak hasar sistemi devre dışı" — mühürlü profilde
            // hasar hiç birikmez, kapak asla parçalanamaz, ceset asla düşemez. Fail-closed.
            if (_corpseSlide != null && _corpseSlide.Corpse != null && _corpseSlide.Corpse.lidSealed)
                return;

            float max = _coffin.Profile != null ? _coffin.Profile.damageMax : 100f;
            _damage = Mathf.Min(_damage + amount, max);

            // Mandal eşiği hasarla düşer: ağır hasarlı tabutta kapak daha kolay açılır (GDD 4.6, 5.2).
            if (_lid != null)
                _lid.Damage01 = Damage01;

            if (_damage >= max)
                DestroyLid();
        }

        private void DestroyLid()
        {
            LidDestroyed = true;

            if (_lid != null)
                _lid.NotifyLidDestroyed();

            Debug.Log("[CoffinDamage] KAPAK PARÇALANDI — ceset düşme riski artık kalıcı (GDD 4.6).");

            // Nested NetworkObject despawn: client yıkımı + geç katılan otomatik (spawn sistemi).
            if (_lidNetworkObject != null)
                base.Despawn(_lidNetworkObject);
        }
    }
}
