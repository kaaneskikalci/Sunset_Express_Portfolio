using FishNet.Object;
using SunsetExpress.Profiles;
using UnityEngine;

namespace SunsetExpress.Obstacles
{
    /// <summary>
    /// Hareketli Zemin arketipi (GDD 7): kayan kütükler, dönen platformlar. "Tek başına atlanabilir,
    /// tabutla senkron zorunlu" — platform oyuncuyu ve tabutu SÜRTÜNMEYLE taşır, hiçbir şeyi parent
    /// yapmaz. Tabutun parent'lanmaması pazarlıksız kuraldır (GDD 4.1); aynı mantığı platform için de
    /// koruyoruz: yük fiziksel temasla taşınır, hiyerarşiyle değil. Kaygan his kasıtlıdır — gerekirse
    /// zemin collider'ına yüksek sürtünmeli PhysicMaterial verilir, koda katsayı gömülmez.
    ///
    /// Otorite: host-authoritative (GDD 12.2). Hareket YALNIZ server'da üretilir, client'lar
    /// NetworkTransform'un interpolasyonunu görür — tabutla aynı model. Client'ta rigidbody kinematic
    /// bırakılır ki yerel fizik senkron pozla kavga etmesin (Coffin.cs deseni).
    ///
    /// Zamanlama: hareket `OnPrePhysicsSimulation`'da, adımın GERÇEK delta'sıyla uygulanır — platform
    /// ADIMDAN ÖNCE yer değiştirmeli ki üstündeki cisimleri o adımda itebilsin. `FixedUpdate`
    /// KULLANILMAZ: PhysicsMode = TimeManager'da FixedUpdate manuel fizik adımından kopuktur, adım
    /// başına 0/1/2 kez çalışır ve hareket düzensizleşir (reviewer notu).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(FishNet.Component.Transforming.NetworkTransform))]
    public sealed class MovingPlatform : NetworkBehaviour
    {
        public enum PathMode
        {
            /// <summary>Son noktaya varınca ters yönde geri döner (kütük gidip gelir).</summary>
            PingPong,
            /// <summary>Son noktadan ilk noktaya atlar (kapalı devre).</summary>
            Loop
        }

        [Header("Profil (GDD 12.3) — hız/ritim/dönüş hızı")]
        [Tooltip("Platformun HİSSİ buradan gelir; paylaşılır. ATANMAZSA platform hareket ETMEZ ve " +
                 "uyarı basar — sessizce yanlış hızda çalışan bir engel, duran engelden kötüdür.")]
        [SerializeField] private MovingPlatformProfile _profile;

        [Header("Level geometrisi (bu ÖRNEĞE özgü — profile taşınmaz)")]
        [Tooltip("Sahnedeki yol noktaları, sırayla. Boş/tek nokta bırakılırsa platform yer değiştirmez " +
                 "(yalnız dönme kullanılabilir). Noktalar platformun ÇOCUĞU OLMAMALI — birlikte hareket " +
                 "ederlerse hedef sürekli kaçar.")]
        [SerializeField] private Transform[] _waypoints;
        [SerializeField] private PathMode _pathMode = PathMode.PingPong;
        [Tooltip("Lokal dönme ekseni. Sıfır vektör = dönme yok. Hız profilde.")]
        [SerializeField] private Vector3 _rotationAxis = Vector3.zero;

        private Rigidbody _body;
        private int _targetIndex;
        private int _step = 1;      // PingPong yön işareti
        private float _waitTimer;

        // Başlangıç pozu — stop→start'ta "eski fizik pozu + sıfırlanmış güzergah state'i" karışımı
        // doğmasın diye poz ve güzergah BİRLİKTE sıfırlanır (aşağıya bak).
        private Vector3 _restPosition;
        private Quaternion _restRotation;

        private float Speed => _profile != null && _profile.speed > 0f ? _profile.speed : 2f;
        private float WaitAtWaypoint => _profile != null ? Mathf.Max(0f, _profile.waitAtWaypoint) : 0.5f;
        private float RotationSpeed => _profile != null ? _profile.rotationSpeed : 0f;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();

            // Platform HER ZAMAN kinematic: tabut ve oyuncular onu itememeli, o onları itmeli.
            // Server'da hareket MovePosition ile verilir, client'ta NetworkTransform yazar.
            _body.isKinematic = true;

            // Interpolate: platform 50 Hz fizik adımında hareket ediyor, ekran daha hızlı
            // çiziyor — host'ta platformun kendisi ve üstündekiler titriyordu. Remote client'ı
            // NetworkTransform zaten yumuşatıyor; bu düzeltme host görünümü içindir. Binicilerin
            // jitter'ını TEK BAŞINA çözmez, bir fizik adımı (~20 ms) görsel gecikme ekler.
            _body.interpolation = RigidbodyInterpolation.Interpolate;

            _restPosition = _body.position;
            _restRotation = _body.rotation;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_profile == null)
            {
                Debug.LogError($"{name}: MovingPlatformProfile atanmamış — platform hareket etmeyecek.", this);
                return;
            }

            // BAŞLANGIÇ POLİTİKASI: her oturum SIFIRDAN başlar. Eskiden yalnız `_targetIndex`
            // sıfırlanıyordu; `_waitTimer` ve rigidbody pozu eski değerinde kalıyordu ve stop→start'ta
            // "eski fizik pozu + sıfırlanmış güzergah state'i" karışımı doğuyordu — platform
            // beklenmedik süre duruyor ya da güzergahın yanlış yerinden waypoint 1'e gidiyordu.
            // Hibrit davranış yerine açık seçim: poz, bekleme ve yön BİRLİKTE başa alınır.
            // `_restPosition` TASARIMCININ BIRAKTIĞI konumdur, waypoint 0'a eşitlenmez — aşağıdaki
            // kontrol yalnız DOĞRULAR ve sapma varsa uyarır (eski yorum "varsayımı kodla
            // kuruyor" diyordu, yanlıştı).
            _body.position = _restPosition;
            _body.rotation = _restRotation;
            // Süreksiz reset → PhysX→Transform yazbacki elle tetiklenir; yoksa transform (ve onu
            // okuyan NetworkTransform) bir sonraki simülasyona kadar eski pozu görür
            // (mühendislik invariantları).
            _body.PublishTransform();
            _step = 1;
            _waitTimer = 0f;
            _targetIndex = HasPath() ? NextIndexFrom(0) : 0;

            // `NextIndexFrom(0)` platformun waypoint 0'DA kurulduğunu varsayar — ilk hedef 1'dir,
            // yani 0 hiç ziyaret edilmez. Sahnede platform 0'dan uzağa bırakılırsa ilk rota
            // rest→waypoint1 olur ve güzergahın ilk parçası sessizce atlanır. Varsayımı
            // kurmuyoruz (tasarımcının koyduğu yeri ezmek daha kötü olurdu), ama SÖYLÜYORUZ.
            if (HasPath() && _waypoints[0] != null)
            {
                float offset = Vector3.Distance(_restPosition, _waypoints[0].position);
                if (offset > 0.5f)
                {
                    Debug.LogWarning($"{name}: platform waypoint 0'dan {offset:0.00} m uzakta kurulmuş. " +
                                     "İlk hedef waypoint 1 olduğu için güzergahın ilk parçası atlanacak — " +
                                     "platformu waypoint 0'ın üstüne taşı ya da noktaları yeniden sırala.", this);
                }
            }

            TimeManager.OnPrePhysicsSimulation += ServerPrePhysics;
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // TimeManager null olabilir (kapanış sırası) — CorpseSlide/Coffin ile aynı guard.
            if (TimeManager != null)
                TimeManager.OnPrePhysicsSimulation -= ServerPrePhysics;
        }

        private bool HasPath() => _waypoints != null && _waypoints.Length > 1;

        /// <summary>
        /// Fizik adımından ÖNCE tam bir kez: yeni poz hesaplanır ve MovePosition/MoveRotation ile
        /// verilir. Transform'a doğrudan yazmak yerine MovePosition kullanılır — kinematic rigidbody
        /// üstündeki cisimleri ancak böyle sürükler (transform ataması temas çözümünü atlar).
        /// </summary>
        private void ServerPrePhysics(float delta)
        {
            Vector3 position = _body.position;
            Quaternion rotation = _body.rotation;

            if (RotationSpeed != 0f && _rotationAxis.sqrMagnitude > 0f)
                rotation *= Quaternion.AngleAxis(RotationSpeed * delta, _rotationAxis.normalized);

            if (HasPath())
                position = AdvanceAlongPath(position, delta);

            _body.MovePosition(position);
            _body.MoveRotation(rotation);
        }

        private Vector3 AdvanceAlongPath(Vector3 position, float delta)
        {
            if (_waitTimer > 0f)
            {
                _waitTimer -= delta;
                return position;
            }

            Transform target = _waypoints[_targetIndex];
            if (target == null)
            {
                // Tasarımcı bir noktayı silmiş olabilir — sessizce takılmak yerine sıradakine geç.
                _targetIndex = NextIndexFrom(_targetIndex);
                return position;
            }

            Vector3 toTarget = target.position - position;
            float stepLength = Speed * delta;

            // Bu adımda hedefe varılıyorsa tam noktaya OTUR (aşma yok), sonra bekleme başlat.
            if (toTarget.sqrMagnitude <= stepLength * stepLength)
            {
                _waitTimer = WaitAtWaypoint;
                _targetIndex = NextIndexFrom(_targetIndex);
                return target.position;
            }

            return position + toTarget.normalized * stepLength;
        }

        /// <summary>Sıradaki nokta — PingPong'da uçlarda yön çevirir, Loop'ta başa sarar.</summary>
        private int NextIndexFrom(int current)
        {
            if (_pathMode == PathMode.Loop)
                return (current + 1) % _waypoints.Length;

            int next = current + _step;
            if (next >= _waypoints.Length || next < 0)
            {
                _step = -_step;
                next = current + _step;
                // Tek elemanlı diziye düşmemek için kelepçe (HasPath zaten >1 garanti eder).
                next = Mathf.Clamp(next, 0, _waypoints.Length - 1);
            }
            return next;
        }

#if UNITY_EDITOR
        /// <summary>Güzergahı sahnede görünür kılar — level yerleşimi Baran'ın işi, yolu görmesi gerekir.</summary>
        private void OnDrawGizmosSelected()
        {
            if (_waypoints == null || _waypoints.Length == 0)
                return;

            Gizmos.color = Color.cyan;
            for (int i = 0; i < _waypoints.Length; i++)
            {
                if (_waypoints[i] == null)
                    continue;

                Gizmos.DrawWireSphere(_waypoints[i].position, 0.25f);

                int next = i + 1;
                if (next >= _waypoints.Length)
                {
                    if (_pathMode != PathMode.Loop)
                        break;
                    next = 0;
                }

                if (_waypoints[next] != null)
                    Gizmos.DrawLine(_waypoints[i].position, _waypoints[next].position);
            }
        }
#endif
    }
}
