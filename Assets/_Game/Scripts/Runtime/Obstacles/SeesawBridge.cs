using FishNet.Object;
using SunsetExpress.Profiles;
using UnityEngine;

namespace SunsetExpress.Obstacles
{
    /// <summary>
    /// Tahterevalli Köprü arketipi (GDD 7): "Ağırlık dağılımına göre eğilen platform. Tabut + 4
    /// oyuncunun pozisyonu dengeyi belirler; bir kişi panikleyip koşarsa herkes uçar." Test ettiği
    /// beceri: pozisyon disiplini, sakin kalma.
    ///
    /// Eğilme KODLA HESAPLANMAZ — HingeJoint etrafında dönen bir Rigidbody'ye yükün kendisi tork
    /// uygular, denge fizikten doğar. Bu bilinçli: GDD 4.5'in "yapay çarpan yok, zorluk fizikten
    /// doğar" kuralı burada da geçerli. Script'in işi yalnızca eklemi profil değerlerinden kurmak
    /// ve otorite modelini korumaktır; bu yüzden tick başına koşan bir mantığı YOKTUR.
    ///
    /// OTORİTE — FAIL-CLOSED YAŞAM DÖNGÜSÜ:
    ///   Awake         → daima kinematic (kimse simüle etmez)
    ///   OnStartServer → dynamic + state sıfırla + eklemi kur (YALNIZ otorite simüle eder)
    ///   OnStopServer  → tekrar kinematic + hızları sıfırla
    /// Eskiden Awake hiç dokunmuyor, kinematic'i yalnız `OnStartClient` yapıyordu ve
    /// `OnStopServer` HİÇ YOKTU. İki delik vardı: ① Awake ile OnStartClient arasında saf client
    /// yerel olarak eğilip sonra NetworkTransform'a geri çekiliyordu (görünür sıçrama);
    /// ② host'ta server durup client rolü yaşamaya devam edince köprü DYNAMIC kalıyor ve otorite
    /// kapandıktan sonra da yerel fizik simülasyonuna devam ediyordu — sonraki server başlangıcı
    /// bayat açı/hızla açılıyordu. Bu düzen ayrıca "Inspector'da Is Kinematic kapalı olmalı"
    /// kontratını da öldürür: otoriteyi Inspector değil script belirler.
    ///
    /// Sabitler <see cref="SeesawBridgeProfile"/>'da (GDD 12.3). Hinge anchor/axis ve köprünün
    /// kütlesi ÖRNEKTE kalır — onlar level geometrisidir, paylaşılan his değil.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(HingeJoint))]
    [RequireComponent(typeof(FishNet.Component.Transforming.NetworkTransform))]
    public sealed class SeesawBridge : NetworkBehaviour
    {
        [Header("Profil (GDD 12.3)")]
        [Tooltip("Köprünün hissi buradan gelir. ATANMAZSA köprü eklemi KURMAZ ve uyarı basar — " +
                 "sessizce yanlış davranan bir engel, çalışmayan bir engelden kötüdür.")]
        [SerializeField] private SeesawBridgeProfile _profile;

        private Rigidbody _body;
        private HingeJoint _hinge;

        // Başlangıç pozu: server yeniden başlarsa köprü bayat açıda değil, kurulduğu yerde açılır.
        private Vector3 _restPosition;
        private Quaternion _restRotation;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _hinge = GetComponent<HingeJoint>();

            _restPosition = _body.position;
            _restRotation = _body.rotation;

            // Fail-closed: kimse aksini söyleyene kadar hiçbir makine bu köprüyü simüle etmez.
            _body.isKinematic = true;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_profile == null)
            {
                Debug.LogError($"{name}: SeesawBridgeProfile atanmamış — köprü eğilmeyecek.", this);
                return;
            }

            // Stop→start'ta bayat açı/hız kalmasın: poz ve hızlar başlangıca çekilir.
            _body.isKinematic = false;
            _body.position = _restPosition;
            _body.rotation = _restRotation;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;

            // Süreksiz reset → PhysX→Transform yazbacki elle tetiklenir; poz, rotasyon ve hızlar
            // yazıldıktan SONRA tek çağrı (mühendislik invariantları).
            _body.PublishTransform();

            ApplyJointSetup();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // Otorite kapandı → simülasyon da kapanır. Yoksa host'ta server durup client rolü
            // devam ederken köprü kendi başına eğilmeye devam ederdi.
            if (_body != null)
            {
                _body.isKinematic = true;
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Menteşeyi profil değerlerinden kurar. Sahnede elle ayarlanmış limitleri EZER — tek
        /// yazar burasıdır, böylece "prefab'da başka, sahnede başka" ikiliği doğmaz (Coffin'in
        /// ConfigureGrabJoint deseniyle aynı gerekçe). 0/negatif değerlerde güvenli varsayılana
        /// düşülür: yeni alan eklenmiş eski asset'lerde alan 0 gelir (Unity tuzağı).
        /// </summary>
        private void ApplyJointSetup()
        {
            float tiltLimit = _profile.tiltLimit > 0f ? _profile.tiltLimit : 25f;
            float angularDrag = _profile.angularDrag > 0f ? _profile.angularDrag : 1.5f;

            _body.angularDamping = angularDrag;

            _hinge.useLimits = true;
            _hinge.limits = new JointLimits
            {
                min = -tiltLimit,
                max = tiltLimit,
                // bounciness'te 0 GEÇERLİ ve istenen varsayılan — sıfır-tuzağı koruması UYGULANMAZ.
                bounciness = Mathf.Max(0f, _profile.limitBounciness)
            };

            _hinge.useSpring = _profile.useReturnSpring;
            if (_profile.useReturnSpring)
            {
                _hinge.spring = new JointSpring
                {
                    spring = _profile.returnSpring > 0f ? _profile.returnSpring : 40f,
                    damper = _profile.returnDamper > 0f ? _profile.returnDamper : 12f,
                    targetPosition = 0f // yatay
                };
            }
        }
    }
}
