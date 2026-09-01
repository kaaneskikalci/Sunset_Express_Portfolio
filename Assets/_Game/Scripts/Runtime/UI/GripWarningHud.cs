using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Kopma (elden kayma) uyarı göstergesi — GDD 4.3 + 13.2.
    ///
    /// GDD 13.2 lafzı: "Tutuş gerilim göstergesi: yalnızca eşiğin belirli bir oranı aşılınca belirir
    /// (el ikonu titrer). SÜREKLİ BAR GÖSTERİLMEZ — oyuncu tabuta baksın, HUD'a değil." Bu yüzden
    /// burada bar, yüzde, sayaç YOKTUR; tek sinyal kanalı ikonun kendisi ve titremesidir.
    /// (Oran GDD'de önce tek %70'ti; Aşama 0 ayarında üç kademeye ayrıldı — güncel %50/%65/%80,
    /// değerler CoffinProfile'da. "Sürekli bar yok" kuralı değişmedi.)
    ///
    /// Kademe modeli (ekip kararı, 2026-08): gerilim sürekli float olarak DEĞİL, 3 kademe halinde
    /// taşınır. Gerekçe pazarlıksız kural (GDD 12.2): "grab/bırakma/kapak/kopma state değil EVENT
    /// senkronu (RPC) ile taşınır." Kademe yalnızca eşik GEÇİLİRKEN yayınlandığı için event
    /// karakterini korur — tick başına akan bir tension stream'i olsaydı state senkronu olurdu.
    ///
    /// Erişilebilirlik (GDD 13.3): titreşim ayrı kapatılabilir — <see cref="RumbleEnabled"/>.
    /// Renk tek başına bilgi taşımaz (renk körlüğü güvenli): kademe ayrıca GENLİK ve ÖLÇEK ile
    /// kodlanır, yani renk hiç algılanmasa bile kademe okunur.
    ///
    /// Görsel şu an KOD İLE kurulur (prefab yok) — UI tasarımı netleşince prefab'a taşınacak;
    /// o zaman <see cref="iconSprite"/> gerçek el ikonuyla, <see cref="warningClip"/> "kaçırıyorum!"
    /// klibiyle doldurulur.
    /// </summary>
    public sealed class GripWarningHud : MonoBehaviour
    {
        public const byte LevelNone = 0;    // uyarı yok — ikon gizli
        // Eşik oranları CoffinProfile'da yaşar (GDD 12.3) — HUD yalnız kademe NUMARASINI alır.
        // Aşağıdaki yüzdeler bilgi amaçlı, profilin güncel değerleridir; profil değişirse burası
        // bayatlar ama davranış etkilenmez.
        public const byte LevelLight = 1;   // ~%50+ : "kayıyor"
        public const byte LevelMedium = 2;  // ~%65+ : "ciddi kayıyor"
        public const byte LevelSevere = 3;  // ~%80+ : "kopmak üzere"

        /// <summary>Erişilebilirlik anahtarı (GDD 13.3): titreşim kamera sallanmasından BAĞIMSIZ
        /// kapatılabilir olmalı. Ayarlar menüsü (Ozanay/UI) geldiğinde buraya bağlanır.</summary>
        public static bool RumbleEnabled = true;

        /// <summary>Bir kademenin görsel/dokunsal imzası. Kademe farkı üç kanaldan birden okunur
        /// (genlik + ölçek + renk) — tek kanala bağımlılık erişilebilirlik riski (GDD 13.3).</summary>
        [System.Serializable]
        public struct LevelStyle
        {
            [Tooltip("Titreme genliği (referans 1080p'de piksel).")]
            public float trembleAmplitude;
            [Tooltip("Titreme frekansı (Hz) — yükseldikçe 'kontrol kaçıyor' hissi.")]
            public float trembleFrequency;
            [Tooltip("İkon ölçeği — renk algılanmasa bile kademeyi okutan ikinci kanal.")]
            public float scale;
            public Color color;
            [Range(0f, 1f)] public float rumbleLow;
            [Range(0f, 1f)] public float rumbleHigh;
        }

        [Header("Kademe imzaları (1=hafif, 2=orta, 3=kopmak üzere)")]
        [SerializeField]
        private LevelStyle _light = new()
        {
            trembleAmplitude = 4f,
            trembleFrequency = 14f,
            scale = 1.0f,
            color = new Color(1f, 0.82f, 0.25f, 0.85f),
            rumbleLow = 0.12f,
            rumbleHigh = 0.05f
        };

        [SerializeField]
        private LevelStyle _medium = new()
        {
            trembleAmplitude = 9f,
            trembleFrequency = 22f,
            scale = 1.12f,
            color = new Color(1f, 0.55f, 0.15f, 0.93f),
            rumbleLow = 0.3f,
            rumbleHigh = 0.18f
        };

        [SerializeField]
        private LevelStyle _severe = new()
        {
            trembleAmplitude = 17f,
            trembleFrequency = 32f,
            scale = 1.28f,
            color = new Color(1f, 0.25f, 0.2f, 1f),
            rumbleLow = 0.6f,
            rumbleHigh = 0.45f
        };

        [Header("Yerleşim")]
        [Tooltip("Ekran merkezine göre ofset (referans 1080p). Merkezin biraz altı: oyuncu tabuta " +
                 "bakarken çevresel görüşle yakalar, bakışı HUD'a çekmez (GDD 13.2).")]
        [SerializeField] private Vector2 _anchoredPosition = new(0f, -180f);
        [SerializeField] private float _iconSize = 96f;

        [Header("Placeholder — UI tasarımı gelince doldurulacak")]
        [Tooltip("Gerçek el ikonu. Boşken düz kare çizilir (placeholder olduğu belli olsun diye).")]
        [SerializeField] private Sprite _iconSprite;
        [Tooltip("\"Kaçırıyorum!\" ses klibi (GDD 4.3). Asset henüz yok — geldiğinde bağlanır.")]
        [SerializeField] private AudioClip _warningClip;

        [Header("Geçiş")]
        [Tooltip("Sönme hızı. Belirme ANINDA olmalı — uyarının geç kalması adalet sütununu bozar (GDD 4.3).")]
        [SerializeField] private float _fadeOutSpeed = 6f;

        private RectTransform _iconRect;
        private Image _icon;
        private AudioSource _audio;
        private byte _level = LevelNone;
        private byte _lastActiveLevel = LevelLight; // sönerken hangi kademenin stiliyle çizileceği
        private float _visibility;      // 0-1 sönme katsayısı
        private float _noiseSeed;
        private bool _rumbleActive;
        private Gamepad _rumblePad; // titreşimi BAŞLATTIĞIMIZ cihaz — durdurma bunun üzerinden gider

        /// <summary>Şu anki uyarı kademesi (0 = uyarı yok).</summary>
        public byte Level => _level;

        private void Awake()
        {
            _noiseSeed = Random.value * 1000f;
            BuildVisuals();
            ApplyVisibility(0f);
        }

        /// <summary>
        /// Canvas + ikon hiyerarşisini kod ile kurar (prefab yok — UI tasarımı sonrası prefab'a taşınacak).
        /// GraphicRaycaster BİLİNÇLİ olarak eklenmez: bu HUD tıklanmaz, oyun girdisini yutmamalı.
        /// </summary>
        private void BuildVisuals()
        {
            Canvas canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100; // FishNet debug HUD'ının üstünde kalsın

            CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            GameObject iconGo = new("GripWarningIcon", typeof(RectTransform));
            iconGo.transform.SetParent(transform, false);

            _iconRect = (RectTransform)iconGo.transform;
            _iconRect.anchorMin = _iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            _iconRect.pivot = new Vector2(0.5f, 0.5f);
            _iconRect.sizeDelta = new Vector2(_iconSize, _iconSize);
            _iconRect.anchoredPosition = _anchoredPosition;

            _icon = iconGo.AddComponent<Image>();
            _icon.sprite = _iconSprite;
            _icon.raycastTarget = false;
            _icon.preserveAspect = true;

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f; // kendi tutuşumuzun uyarısı — 2D
        }

        /// <summary>
        /// Uyarı kademesini ayarlar. Kaynak tarafı için tek giriş noktası:
        /// <see cref="GripWarningBinder"/> besler, o da <c>PlayerGrabber.OnGripWarningChanged</c>
        /// event'ine abonedir — event köprüsü migrasyonu TAMAMLANDI, poll yolu kaldırıldı.
        /// </summary>
        public void SetLevel(byte level)
        {
            if (level == _level)
                return;

            bool wasSilent = _level == LevelNone;
            _level = level;

            // Sönme, KOPTUĞU kademenin stiliyle çizilsin diye son aktif kademe saklanır. Kopma anında
            // server AYNI SERVER ADIMINDA üst kademeyi ve 0'ı sıralar (MeasureGripTension →
            // ServerBreakGrip → ServerReleaseHeld). RPC'ler güvenilir ve çağrı sırasına sadıktır ama
            // paket sınırı / araya render girmesi GARANTİ DEĞİLDİR; pratikte çoğunlukla aynı client
            // network turunda işlenirler ve üst kademe tek kare bile render edilmez — kopma hep sarı
            // sönerdi (saha testinde görüldü). (Kademe zaten üstteyse yeni RPC gitmez, yalnız 0 gider;
            // sonuç aynı.) Artık sönme, koptuğu kademenin rengiyle görünür.
            if (level != LevelNone)
                _lastActiveLevel = level;

            // İlk kez uyarıya girişte ses (GDD 4.3 "kaçırıyorum!"). Kademe tırmanışında tekrar
            // çalmaz — üst üste binen uyarı sesi gürültüye dönüşür.
            if (wasSilent && level != LevelNone)
            {
                _visibility = 1f;   // belirme ANINDA (fade-in yok): oyuncunun tepki penceresi kutsal
                PlayWarningSound();
            }
        }

        private void PlayWarningSound()
        {
            if (_warningClip != null && _audio != null)
                _audio.PlayOneShot(_warningClip);
        }

        private void Update()
        {
            bool active = _level != LevelNone;

            if (active)
                _visibility = 1f;
            else if (_visibility > 0f)
                _visibility = Mathf.Max(0f, _visibility - _fadeOutSpeed * Time.unscaledDeltaTime);

            if (_visibility <= 0f)
            {
                ApplyVisibility(0f);
                StopRumble();
                return;
            }

            // Sönerken _level zaten 0'dır; o kademenin stili yok, bu yüzden son AKTİF kademeninki
            // kullanılır. Yoksa StyleFor'un default'u (_light) devreye girer ve her kopma sarı sönerdi.
            LevelStyle style = StyleFor(active ? _level : _lastActiveLevel);
            ApplyTremble(style);
            ApplyVisibility(_visibility, style);
            ApplyRumble(style, active);
        }

        private LevelStyle StyleFor(byte level) => level switch
        {
            LevelSevere => _severe,
            LevelMedium => _medium,
            _ => _light
        };

        /// <summary>
        /// Perlin gürültüsüyle organik "el titremesi" (GDD 4.3). Kare dalga/rastgele sıçrama yerine
        /// Perlin: titreme kasılan bir ele benzesin, dijital parazit gibi durmasın.
        /// unscaledTime — zaman ölçeği değişse de (yavaşlatma efektleri) uyarı okunur kalır.
        /// </summary>
        private void ApplyTremble(LevelStyle style)
        {
            float t = Time.unscaledTime * style.trembleFrequency;
            float ox = (Mathf.PerlinNoise(_noiseSeed + t, 0f) - 0.5f) * 2f * style.trembleAmplitude;
            float oy = (Mathf.PerlinNoise(0f, _noiseSeed + t) - 0.5f) * 2f * style.trembleAmplitude;

            _iconRect.anchoredPosition = _anchoredPosition + new Vector2(ox, oy);
            _iconRect.localScale = Vector3.one * style.scale;
        }

        private void ApplyVisibility(float visibility)
        {
            if (_icon != null)
                _icon.enabled = visibility > 0f;
        }

        private void ApplyVisibility(float visibility, LevelStyle style)
        {
            if (_icon == null)
                return;

            _icon.enabled = visibility > 0f;
            Color c = style.color;
            c.a *= visibility;
            _icon.color = c;
        }

        // ---- Titreşim (GDD 4.3 "controller titreşimi", 13.3 erişilebilirlik) ----

        private void ApplyRumble(LevelStyle style, bool active)
        {
            if (!active || !RumbleEnabled)
            {
                StopRumble();
                return;
            }

            Gamepad pad = Gamepad.current;
            if (pad == null)
            {
                // Pad çekildi/kayboldu: durdurulacak cihaz ARTIK current değil, saklanan referanstır.
                StopRumble();
                return;
            }

            // Aktif cihaz değiştiyse ESKİSİNİ sustur — yoksa Pad A titrerken Pad B "current" olunca
            // B çalışır ama A fiziksel olarak titremeye devam ederdi.
            if (_rumbleActive && _rumblePad != null && _rumblePad != pad)
                SilencePad(_rumblePad);

            pad.SetMotorSpeeds(style.rumbleLow, style.rumbleHigh);
            _rumblePad = pad;
            _rumbleActive = true;
        }

        /// <summary>Titreşimi, BAŞLATILAN cihaz üzerinden durdurur. Gamepad.current'a güvenilmez:
        /// cihaz değişmiş veya çekilmiş olabilir ve o zaman eski pad sonsuza dek titrer.</summary>
        private void StopRumble()
        {
            if (!_rumbleActive)
                return;

            _rumbleActive = false;
            SilencePad(_rumblePad);
            _rumblePad = null;
        }

        private static void SilencePad(Gamepad pad)
        {
            // added: cihaz çekilmişse pad objesi hayatta ama bağlı değildir — çağrı boşa gider, zararsız.
            if (pad != null && pad.added)
                pad.SetMotorSpeeds(0f, 0f);
        }

        // Motorları AÇIK bırakmak pad'i fiziksel olarak titrer halde bırakır — oyundan çıkışta,
        // sahne geçişinde ve obje yok edilirken kesinlikle susturulmalı.
        private void OnDisable() => StopRumble();

        private void OnDestroy() => StopRumble();

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                StopRumble();
        }
    }
}
