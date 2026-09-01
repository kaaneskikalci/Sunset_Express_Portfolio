using System.Collections.Generic;
using SunsetExpress.Player;
using TMPro;
using UnityEngine;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Ekip arkadaşlarının isim etiketleri (GDD 13.2 eklemesi, 2026-08).
    ///
    /// ⚠ 13.2 "MİNİMAL" İLKESİYLE YAZILMIŞ: *"oyuncu tabuta baksın, HUD'a değil"*. İsim etiketi o
    /// listede yoktu, sonradan ekip kararıyla eklendi. Bu yüzden çizim bilinçli olarak
    /// KISITLIDIR — sürekli parlayan dört etiket, GDD'nin tam da kaçındığı şey olurdu:
    ///
    ///   · Kendi adın ASLA görünmez (kimsin zaten biliyorsun).
    ///   · Ekran dışındaysa çizilmez. (Ekran dışı ekip farkındalığı GDD'de YÖN OKLARININ işi —
    ///     ayrı bir madde, ayrı bir çözüm; etiket onun yerine geçmeye çalışmaz.)
    ///   · Uzakta söner, tamamen kaybolur.
    ///   · Arada duvar varsa gizlenir: isim etiketi duvar arkasını görme aracı DEĞİL.
    ///
    /// TEK CANVAS, oyuncu başına world-space canvas DEĞİL: dört ayrı canvas hem pahalı hem de
    /// mesafeyle boyut/okunabilirlik kontrolünü zorlaştırır. Etiketler ekran uzayında, dünya
    /// pozisyonundan projeksiyonla konumlanır — boyut her mesafede aynı ve okunur kalır.
    ///
    /// sortingOrder 120: kopma uyarısının (100) üstünde, panellerin (150+) altında. Etiket bir
    /// arayüz değil, dünyaya ait bir işaret — hiçbir paneli örtmemeli.
    /// </summary>
    public sealed class PlayerNameTagHud : MonoBehaviour
    {
        [Tooltip("Etiketin oyuncunun ayağından ne kadar yukarıda durduğu (m).")]
        [SerializeField] private float _headHeight = 2.1f;

        [Tooltip("Bu mesafeden sonra etiket tamamen görünmez (m).")]
        [SerializeField] private float _maxDistance = 22f;

        [Tooltip("Sönmenin başladığı mesafe (m). Bununla max arası kademeli kaybolur.")]
        [SerializeField] private float _fadeStartDistance = 14f;

        [Tooltip("Oyuncu listesini yenileme aralığı (sn) — her kare aramak gereksiz.")]
        [SerializeField] private float _rescanInterval = 1f;

        [Tooltip("Arada duvar varsa etiketi gizle. Kapatılırsa isimler duvar arkasından görünür.")]
        [SerializeField] private bool _hideWhenOccluded = true;

        [Tooltip("Görüş çizgisi kontrolünde engel sayılacak katmanlar.")]
        [SerializeField] private LayerMask _occlusionMask = ~0;

        [Header("Test")]
        [Tooltip("SADECE TEST İÇİN: kendi adını da göster. Normalde gizli (GDD 13.2 'minimal' — " +
                 "kim olduğunu zaten biliyorsun). Tek başına oynarken sistemin çalıştığını " +
                 "doğrulamanın tek yolu bu; oyuna böyle bırakılmaz.")]
        [SerializeField] private bool _debugShowOwnTag;

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private float _nextRescanTime;
        private Camera _camera;
        private float _nextCameraSearchTime;

        private readonly List<PlayerNameTag> _tags = new();
        private readonly Dictionary<PlayerNameTag, TextMeshProUGUI> _labels = new();
        private readonly List<PlayerNameTag> _stale = new();

        private void Start()
        {
            // interactive: false — etiketler tıklanmaz ve oyun girdisini YUTMAMALI.
            _canvas = UiFactory.CreateOverlayCanvas(transform, "NameTagCanvas", 120, interactive: false);
            _canvasRect = (RectTransform)_canvas.transform;
        }

        private void LateUpdate()
        {
            // LateUpdate: kamera bu karede hareketini tamamladıktan SONRA projeksiyon alınır.
            // Update'te alsaydık etiketler kamera hareketinin bir kare gerisinde sürüklenirdi.
            Camera cam = ResolveCamera();
            if (cam == null)
            {
                HideAll();
                return;
            }

            RescanIfDue();

            for (int i = 0; i < _tags.Count; i++)
                UpdateLabel(_tags[i], cam);
        }

        /// <summary>
        /// Projeksiyonu yapacak kamera. `Camera.main` TEK BAŞINA YETMİYOR ve buna güvenmek bu
        /// sınıfın ilk sürümünün hiç çalışmamasına sebep oldu: oyunun kamerası
        /// (`PlayerCamera` prefab'ı) **`Untagged`**, yani `Camera.main` null dönüyor ve her karede
        /// `HideAll()` çağrılıyordu — hiçbir etiket çizilmedi.
        ///
        /// Prefab'a `MainCamera` etiketi eklemek de bir çözümdü ama o Kaan'ın prefab'ı ve
        /// `Camera.main` proje genelinde kurulmamış bir varsayım; doğrusu, kameraya BAŞKA
        /// yollardan da ulaşabilmek. Sıra: etiketli kamera → lokal orbit kamera → sahnedeki ilk
        /// etkin kamera.
        ///
        /// Sonuç önbelleklenir (`Camera.main` her çağrıda etiket taraması yapar) ve yalnız kamera
        /// kaybolunca yeniden aranır; arama da seyreltilir ki kamerasız bir karede her frame
        /// sahne taraması yapılmasın.
        /// </summary>
        private Camera ResolveCamera()
        {
            if (_camera != null && _camera.isActiveAndEnabled)
                return _camera;

            if (Time.unscaledTime < _nextCameraSearchTime)
                return null;

            _nextCameraSearchTime = Time.unscaledTime + 0.25f;

            Camera tagged = Camera.main;
            if (tagged != null && tagged.isActiveAndEnabled)
            {
                _camera = tagged;
                return _camera;
            }

            // Owner'ın orbit kamerası: oyunun gerçekten baktığı kamera bu.
            OrbitCamera orbit = FindFirstObjectByType<OrbitCamera>();
            if (orbit != null)
            {
                Camera fromOrbit = orbit.GetComponentInChildren<Camera>();
                if (fromOrbit != null && fromOrbit.isActiveAndEnabled)
                {
                    _camera = fromOrbit;
                    return _camera;
                }
            }

            // Son çare: sahnedeki ilk etkin kamera. Menüde/yükleme sırasında kamera hiç
            // olmayabilir — o zaman null döner ve etiketler gizlenir, bu doğru davranış.
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].isActiveAndEnabled)
                {
                    _camera = all[i];
                    return _camera;
                }
            }

            _camera = null;
            return null;
        }

        /// <summary>Sahnedeki oyuncuları seyrek tarar; ölen referansların etiketlerini yok eder.</summary>
        private void RescanIfDue()
        {
            if (Time.unscaledTime < _nextRescanTime)
                return;

            _nextRescanTime = Time.unscaledTime + _rescanInterval;

            _tags.Clear();
            _tags.AddRange(FindObjectsByType<PlayerNameTag>(FindObjectsSortMode.None));

            // Yok edilmiş oyuncuların etiketleri ekranda asılı kalmamalı (fake-null tuzağı:
            // sözlük anahtarı hâlâ orada ama nesne ölü).
            _stale.Clear();
            foreach (KeyValuePair<PlayerNameTag, TextMeshProUGUI> entry in _labels)
            {
                if (entry.Key == null || !_tags.Contains(entry.Key))
                    _stale.Add(entry.Key);
            }

            for (int i = 0; i < _stale.Count; i++)
            {
                if (_labels.TryGetValue(_stale[i], out TextMeshProUGUI label) && label != null)
                    Destroy(label.transform.parent.gameObject);

                _labels.Remove(_stale[i]);
            }
        }

        private void UpdateLabel(PlayerNameTag tag, Camera cam)
        {
            if (tag == null)
                return;

            // KENDİ adını gösterme. `IsOwner` yerine `NetworkObject` kontrolüyle birlikte:
            // spawn öncesi FishNet property'leri güvenli değil (proje genelinde aynı guard).
            bool isSelf = tag.NetworkObject != null && tag.IsSpawned && tag.IsOwner;
            string name = tag.DisplayName;

            if ((isSelf && !_debugShowOwnTag) || string.IsNullOrEmpty(name))
            {
                SetLabelVisible(tag, false);
                return;
            }

            // Kök Rigidbody DEĞİL, yumuşatılmış görsel obje — kamera da onu izliyor. İkisi farklı
            // çerçevede kalınca etiket kameraya göre titriyordu (bkz. PlayerNameTag.VisualAnchor).
            Vector3 head = tag.VisualAnchor.position + Vector3.up * _headHeight;
            Vector3 viewport = cam.WorldToViewportPoint(head);

            // z <= 0: nokta kameranın ARKASINDA. Bu kontrol olmadan projeksiyon aynalanır ve
            // arkandaki oyuncunun adı önünde beliriverir.
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
            {
                SetLabelVisible(tag, false);
                return;
            }

            float distance = Vector3.Distance(cam.transform.position, head);
            if (distance > _maxDistance)
            {
                SetLabelVisible(tag, false);
                return;
            }

            if (_hideWhenOccluded && IsOccluded(cam, head, tag))
            {
                SetLabelVisible(tag, false);
                return;
            }

            TextMeshProUGUI label = EnsureLabel(tag);
            label.text = name;

            // Uzakta kademeli sönme: etiketin varlığı hatırlatma olmalı, ilan değil.
            float fade = _maxDistance > _fadeStartDistance
                ? 1f - Mathf.Clamp01((distance - _fadeStartDistance) / (_maxDistance - _fadeStartDistance))
                : 1f;
            label.alpha = fade;

            RectTransform rect = (RectTransform)label.transform.parent;
            rect.anchoredPosition = new Vector2(
                (viewport.x - 0.5f) * _canvasRect.rect.width,
                (viewport.y - 0.5f) * _canvasRect.rect.height);

            SetLabelVisible(tag, true);
        }

        /// <summary>
        /// Kamera ile oyuncunun BAŞI arasında katı bir engel var mı. İsim etiketi duvar arkasını
        /// görme aracı değil — kapatılırsa ekip birbirinin yerini geometriden bağımsız bilir ve
        /// parkurun saklanma/şaşırma anları değersizleşir.
        /// </summary>
        private bool IsOccluded(Camera cam, Vector3 head, PlayerNameTag tag)
        {
            Vector3 origin = cam.transform.position;
            Vector3 delta = head - origin;
            float dist = delta.magnitude;

            if (dist < 0.01f)
                return false;

            if (!Physics.Raycast(origin, delta / dist, out RaycastHit hit, dist,
                                 _occlusionMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            // Oyuncunun KENDİ collider'ına çarpmak engel sayılmaz — ışın zaten ona gidiyor.
            return hit.collider.GetComponentInParent<PlayerNameTag>() != tag;
        }

        private TextMeshProUGUI EnsureLabel(PlayerNameTag tag)
        {
            if (_labels.TryGetValue(tag, out TextMeshProUGUI existing) && existing != null)
                return existing;

            GameObject go = new("NameTag", typeof(RectTransform));
            go.transform.SetParent(_canvas.transform, false);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(300f, 40f);

            TextMeshProUGUI label = UiFactory.CreateLabel(go.transform, "Label", string.Empty, 18f);
            _labels[tag] = label;
            return label;
        }

        private void SetLabelVisible(PlayerNameTag tag, bool visible)
        {
            if (!_labels.TryGetValue(tag, out TextMeshProUGUI label) || label == null)
                return;

            GameObject root = label.transform.parent.gameObject;
            if (root.activeSelf != visible)
                root.SetActive(visible);
        }

        private void HideAll()
        {
            foreach (KeyValuePair<PlayerNameTag, TextMeshProUGUI> entry in _labels)
            {
                if (entry.Value != null)
                    entry.Value.transform.parent.gameObject.SetActive(false);
            }
        }
    }
}
