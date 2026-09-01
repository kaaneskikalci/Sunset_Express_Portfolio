using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.Player
{
    /// <summary>
    /// Owner-local hibrit orbit kamera (GDD 6.8): serbest döndürülebilir, geriye çekilmiş, yüksek açılı.
    /// Networked DEĞİL — her oyuncu kendi kamerasını yönetir (GDD 6.8: dört farklı açı = dört klip).
    /// Yaw'ı PlayerController kamera-göreceli hareket için okur (Katman 1, GDD 6.2).
    /// Kamera parametreleri Aşama 0'da inline; per-level CameraProfile ScriptableObject sonra (GDD 6.8).
    /// </summary>
    /// <remarks>
    /// EXECUTION ORDER +100: kamera imleç kilidini okuyor, `CursorArbiterDriver` ise onu
    /// yazıyor ve İKİSİ DE `LateUpdate`'te. Sıra tanımsız bırakılırsa panel açıldığı kare kamera
    /// bayat kilidi okuyup bir kare dönüyor, kapanışta bir kare geç uyanıyordu. Pozitif order
    /// kamerayı driver'dan (varsayılan 0) SONRAYA alır ve dikişi kapatır.
    /// Yan fayda: kamera LateUpdate'in sonuna yaklaşınca hedefi o karede son konumundan takip eder.
    /// Driver'a negatif order vermek de aynı işi görürdü ama o Ozanay'ın dosyası — kendi tarafımdan
    /// çözülebilen bir sorunu başkasının dosyasına taşımanın anlamı yok.
    /// </remarks>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(Camera))]
    public sealed class OrbitCamera : MonoBehaviour
    {
        [Header("Hedef")]
        [Tooltip("Hedef pivotunun üstüne ofset (omuz/baş hizası).")]
        [SerializeField] private Vector3 _targetOffset = new(0f, 1.2f, 0f);

        [Header("Orbit (GDD 6.8)")]
        [Tooltip("Varsayılan pitch yüksek — GDD 6.8: standart 3. şahıstan belirgin yukarı (~30-40°).")]
        [SerializeField] private float _pitch = 35f;
        [SerializeField] private float _minPitch = 10f;
        [SerializeField] private float _maxPitch = 70f;
        [Tooltip("Kamera mesafesi — geriye çekilmiş (GDD 6.8).")]
        [SerializeField] private float _distance = 7f;
        [Tooltip("Fare deltası hassasiyeti.")]
        [SerializeField] private float _sensitivity = 0.12f;
        [Tooltip("Geniş FOV — GDD 6.8: ~75-85° yatay. Unity dikey FOV kullanır; 16:9'da ~65 dikey ≈ ~85 yatay.")]
        [SerializeField] private float _fieldOfView = 65f;

        private Transform _target;
        private Camera _cam;
        private float _yaw;

        /// <summary>Kamera-göreceli hareket için PlayerController tarafından okunur (Katman 1).</summary>
        public float Yaw => _yaw;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _cam.fieldOfView = _fieldOfView;
            _yaw = transform.eulerAngles.y;

            // Yeni kamera GLOBAL imleç durumunu EZMEZ. Bu Awake eskiden oturum başına bir
            // kez koşuyordu; Dilim 2'den beri oyuncular her sahne geçişinde despawn/respawn olduğu
            // için HER GEÇİŞTE koşuyor. Bir UI açıkken (ESC menüsü, ilan panosu) koşarsa imleci
            // kilitliyordu: panel ekranda ama tıklanamaz, üstüne kamera da fareyi okumaya başlıyor.
            // Talep sahibi varken imlece dokunmuyoruz; kapanışta hakem zaten kilidi geri veriyor.
            if (!SunsetExpress.UI.CursorArbiter.AnyoneWantsCursor)
                Cursor.lockState = CursorLockMode.Locked;
        }

        public void SetTarget(Transform target)
        {
            _target = target;
            if (target != null)
                _yaw = target.eulerAngles.y;
        }

        private void LateUpdate()
        {
            if (_target == null)
                return;

            // Kamera YALNIZ imleç yakalanmışken döner. Bir UI açıkken (ESC menüsü, ilan panosu)
            // imleç serbest bırakılır; bu guard olmadan oyuncu panelde tıklamak için fareyi
            // gezdirirken kamera da savruluyordu. Kural kendi içinde tamdır ve UI koduna bağımlı
            // değildir — "fare oyuna aitse kamera döner".
            Mouse mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 d = mouse.delta.ReadValue();
                _yaw += d.x * _sensitivity;
                _pitch = Mathf.Clamp(_pitch - d.y * _sensitivity, _minPitch, _maxPitch);
            }

            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 focus = _target.position + _targetOffset;
            Vector3 pos = focus - rot * Vector3.forward * _distance;
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
