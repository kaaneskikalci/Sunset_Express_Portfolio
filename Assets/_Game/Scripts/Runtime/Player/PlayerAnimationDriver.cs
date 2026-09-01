using UnityEngine;

namespace SunsetExpress.Player
{
    /// <summary>
    /// Görsel katman: Animator parametrelerini fizikten türetir — animasyon fiziğe asla karışmaz
    /// (GDD 4.2'deki IK ayrımıyla aynı ilke). Network component'i değildir; tüm instance'larda çalışır
    /// çünkü okuduğu kaynaklar zaten senkronludur: Speed → rb velocity (prediction/state forwarding),
    /// Carrying → PlayerGrabber.CarryingVisible (owner'da anlık joint, diğerlerinde SyncVar).
    ///
    /// Animator parametreleri: "Speed" (Float, yatay m/s), "Carrying" (Bool).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PlayerAnimationDriver : MonoBehaviour
    {
        [Tooltip("Model child'ındaki Animator (Graphics altındaki FBX instance'ı).")]
        [SerializeField] private Animator _animator;

        private static readonly int SpeedParam = Animator.StringToHash("Speed");
        private static readonly int CarryingParam = Animator.StringToHash("Carrying");

        private PlayerGrabber _grabber;
        private Vector3 _lastSamplePos;
        private float _smoothedSpeed;

        private void Awake()
        {
            _grabber = GetComponent<PlayerGrabber>();
        }

        private void OnEnable()
        {
            _lastSamplePos = SamplePosition();
            _smoothedSpeed = 0f;
        }

        /// <summary>Hız kaynağı Rigidbody.linearVelocity DEĞİL: remote kopyalarda velocity tick'ler
        /// arası sıfır/tutarsız okunur (Baran'ın ekranında idle→run çalışmama bug'ı). Animator'ın
        /// oturduğu smoothed görsel pozisyonun frame-farkı her makinede aynı davranır.</summary>
        private Vector3 SamplePosition()
        {
            return _animator != null ? _animator.transform.position : transform.position;
        }

        private void Update()
        {
            if (_animator == null || Time.deltaTime <= 0f)
                return;

            Vector3 pos = SamplePosition();
            Vector3 delta = pos - _lastSamplePos;
            _lastSamplePos = pos;
            delta.y = 0f;

            float rawSpeed = delta.magnitude / Time.deltaTime;
            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, rawSpeed, 12f * Time.deltaTime);

            _animator.SetFloat(SpeedParam, _smoothedSpeed);
            _animator.SetBool(CarryingParam, _grabber != null && _grabber.CarryingVisible);
        }
    }
}
