using SunsetExpress.Player;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Ölüm ekranı (GDD 3.4): oyuncu ölünce ekran kararır, geri sayım görünür, dirilince açılır.
    ///
    /// NEDEN VAR: karartma olmadan ölüm bir OLAY gibi okunmuyordu — oyuncu düşmeye devam ediyor,
    /// sonra bir anda ışınlanıyordu. Karartma hem düşen gövdeyi hem ışınlanma anını örtüyor;
    /// geri sayım da oyuncuya ne kadar bekleyeceğini söylüyor (GDD 3.4'ün 3-5 sn'lik exploit
    /// sigortası, bilinmezlik değil bilinen bir bedel olmalı).
    ///
    /// Karartma DİRİLME SİNYALİYLE kalkar, kendi sayacıyla değil: sayaç bittiğinde ışınlama henüz
    /// gerçekleşmemiş olabilir (gecikme, güvenli zemin araması) ve ekran erken açılıp tam da
    /// gizlemek istediğimiz şeyi gösterirdi.
    ///
    /// Kalıcı HUD'da yaşar; lokal owner'ın <see cref="PlayerController"/>'ına
    /// <see cref="GripWarningBinder"/> ile aynı disiplinle bağlanır — sahne ömürlü bir kaynağa
    /// abone olan kalıcı bir bileşen, aboneliği tek noktadan yönetmek zorunda.
    /// </summary>
    public sealed class DeathScreenHud : MonoBehaviour
    {
        private const float FadeInSpeed = 5f;    // ~0.2 sn — hızlı olmalı, düşüşü örtüyor
        private const float FadeOutSpeed = 3f;   // ~0.35 sn — biraz yumuşak, geri dönüş sert olmasın

        [Tooltip("Lokal oyuncu bulunana kadar tarama aralığı (sn).")]
        [SerializeField] private float _rebindInterval = 0.5f;

        private Image _blackout;
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _countdown;

        private PlayerController _player;
        private bool _bound;
        private float _nextRebindTime;

        private bool _dead;
        private float _reviveAtTime;
        private float _alpha;
        private int _shownSeconds = -1;

        private void Start()
        {
            BuildVisuals();
            ApplyAlpha(0f);
        }

        private void OnDisable() => Unbind();

        private void OnDestroy() => Unbind();

        private void BuildVisuals()
        {
            // interactive: false — ölüm ekranı tıklanmaz ve girdi yutmamalı. ESC menüsü (200)
            // BUNUN ÜSTÜNDE kalır: ölüyken bile "Lobiden Ayrıl" erişilebilir olmalı.
            Canvas canvas = UiFactory.CreateOverlayCanvas(transform, "DeathScreenCanvas", 190, interactive: false);

            GameObject panel = new("Blackout", typeof(RectTransform));
            panel.transform.SetParent(canvas.transform, false);

            RectTransform rect = (RectTransform)panel.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _blackout = panel.AddComponent<Image>();
            _blackout.color = Color.black;
            _blackout.raycastTarget = false;

            _title = CreateCentered(panel.transform, "Title", "YOU DIED", UiFactory.TitleFontSize, 40f);
            _countdown = CreateCentered(panel.transform, "Countdown", string.Empty, 24f, -30f);
        }

        private static TextMeshProUGUI CreateCentered(Transform parent, string name, string text,
            float fontSize, float y)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(800f, 70f);
            rect.anchoredPosition = new Vector2(0f, y);

            return UiFactory.CreateLabel(go.transform, "Label", text, fontSize);
        }

        private void Update()
        {
            EnsurePlayer();
            TickFade();
            TickCountdown();
        }

        /// <summary>Aboneliğin lokal owner'da olmasını sağlar; kaynak kaybında önce abonelikten çıkar.</summary>
        private void EnsurePlayer()
        {
            if (IsLocalOwner(_player))
                return;

            // Oyuncu yok edildiyse (sahne geçişi, disconnect) karartma açık kalmamalı.
            Unbind();

            if (Time.unscaledTime < _nextRebindTime)
                return;

            _nextRebindTime = Time.unscaledTime + _rebindInterval;

            PlayerController[] players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach (PlayerController p in players)
            {
                if (!IsLocalOwner(p))
                    continue;

                Bind(p);
                return;
            }
        }

        private void Bind(PlayerController player)
        {
            _player = player;
            _bound = true;
            player.OnLocalDeath += HandleDeath;
            player.OnLocalRevived += HandleRespawn;

            // ABONELİKTEN HEMEN SONRA MEVCUT DURUMU OKU. Event'e abone olmak yalnız BUNDAN SONRAKİ
            // ölümleri duyurur; abone olana kadar geçen sürede gelen ölüm sinyali kimseye
            // ulaşmadan kaybolurdu. Somut yol: kötü bir doğum noktasında ilk fizik
            // adımında ölmek — HUD hâlâ owner'ı arıyor, ölüm sinyali yutuluyor ve oyuncu geri
            // sayım görmeden ışınlanıyordu.
            //
            // `GripWarningBinder`'ın `GripWarningLevel`'ı abonelikten hemen sonra okumasıyla
            // birebir aynı desen — yeni bir mekanizma değil, sahada doğrulanmış olanın tekrarı.
            //
            // Süre YENİDEN HESAPLANMAZ: `LocalReviveAt` zaten mutlak `Time.time` damgası. Kalan
            // süreyi buradan türetmek, geç bağlanan HUD'ın geri sayımı baştan başlatmasını önler.
            if (player.IsLocallyDead)
                SetDead(player.LocalReviveAt);
            else
                HandleRespawn();
        }

        private void Unbind()
        {
            if (!_bound)
                return;

            // Fake-null: obje yok edildiyse event zaten onunla öldü; `_bound` sayesinde bu durumu
            // yine de yakalar ve karartmayı kapatırız (GripWarningBinder'daki aynı tuzak).
            if (_player != null)
            {
                _player.OnLocalDeath -= HandleDeath;
                _player.OnLocalRevived -= HandleRespawn;
            }

            _player = null;
            _bound = false;
            HandleRespawn();
        }

        private static bool IsLocalOwner(PlayerController p)
        {
            return p != null && p.NetworkObject != null && p.IsSpawned && p.IsOwner;
        }

        /// <summary>Canlı ölüm sinyali — süre GECİKME olarak gelir, mutlak zamana çevrilir.</summary>
        private void HandleDeath(float delay) => SetDead(Time.time + delay);

        /// <summary>
        /// Karartmayı açar. MUTLAK zaman alır (gecikme değil) çünkü iki çağıranı var: canlı ölüm
        /// sinyali ve geç bağlanan HUD'ın durum okuması. İkincisinde "ne zaman öldü" bilgisi yok,
        /// yalnız "ne zaman dirilecek" var.
        /// </summary>
        private void SetDead(float reviveAtTime)
        {
            _dead = true;
            _reviveAtTime = reviveAtTime;
            _shownSeconds = -1;
        }

        private void HandleRespawn()
        {
            _dead = false;
            _shownSeconds = -1;

            if (_countdown != null)
                _countdown.text = string.Empty;
        }

        private void TickFade()
        {
            float target = _dead ? 1f : 0f;
            if (Mathf.Approximately(_alpha, target))
                return;

            float speed = _dead ? FadeInSpeed : FadeOutSpeed;
            _alpha = Mathf.MoveTowards(_alpha, target, speed * Time.deltaTime);
            ApplyAlpha(_alpha);
        }

        private void TickCountdown()
        {
            if (!_dead || _countdown == null)
                return;

            // Kalan süre TAVANA yuvarlanır: 3.2 sn kaldıysa "3" değil "4" göstermek yanıltıcı
            // olurdu; oyuncu 0 görüp beklemeye devam etmesin diye en az 1'de tutulur.
            int seconds = Mathf.Max(1, Mathf.CeilToInt(_reviveAtTime - Time.time));
            if (seconds == _shownSeconds)
                return;

            _shownSeconds = seconds;
            _countdown.text = $"Respawning in {seconds}…";
        }

        private void ApplyAlpha(float alpha)
        {
            if (_blackout != null)
            {
                Color c = Color.black;
                c.a = alpha;
                _blackout.color = c;
                _blackout.enabled = alpha > 0.001f;
            }

            if (_title != null)
                _title.alpha = alpha;

            if (_countdown != null)
                _countdown.alpha = alpha;
        }
    }
}
