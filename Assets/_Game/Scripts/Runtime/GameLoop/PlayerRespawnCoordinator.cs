using System.Collections.Generic;
using FishNet;
using SunsetExpress.Coffins;
using SunsetExpress.Networking;
using SunsetExpress.Player;
using UnityEngine;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Oyuncu ölümü ve yeniden doğuşu (GDD 3.4) — SUNUCU-OTORİTER.
    ///
    /// GDD'nin kuralı birebir: *"oyuncu TABUTUN YANINDA (tabuta snap'lenmiş güvenli zeminde)
    /// yeniden doğar... yeniden doğma 3-5 saniyelik bekleme süresine bağlıdır ve yalnızca tabutun
    /// yakınındaki güvenli zeminde gerçekleşir — kasıtlı ölüm, uçurum atlama kısayoluna dönüşemez."*
    ///
    /// ÇAPA TABUTTUR, CHECKPOINT DEĞİL. Checkpoint yalnız TABUT düştüğünde devreye girer (ekip son
    /// mezarlığa döner) — o ayrı bir yol ve dinlenme mezarlığı sistemiyle gelecek. Oyuncu ölümü
    /// ucuzdur: ekipten kopmazsın, yalnız birkaç saniye kaybedersin.
    ///
    /// BEKLEME SÜRESİ EXPLOIT SİGORTASIDIR, cezalandırma değil: gecikme olmasaydı uçurumdan
    /// atlamak, tabuta yetişmenin en hızlı yolu olurdu.
    ///
    /// İKİ TETİKLEYİCİ:
    ///   · <see cref="LethalZone"/> hacimleri — asıl yöntem, tasarımcı yerleştirir
    ///   · Y eşiği — güvenlik ağı; hacim koymayı unutan level yüzünden oyuncu sonsuza düşmesin
    ///
    /// KALAN (GDD 3.4'ten): *"Doğmadan 1 saniye önce doğum noktası blink efektiyle telegraf
    /// edilir."* Telegraf henüz yok — görsel işaretçinin tüm client'larda belirmesi ağ üzerinden
    /// yayın ister ve bu bileşen `NetworkBehaviour` değil. Mekanik çalışıyor, cila eksik.
    /// </summary>
    public sealed class PlayerRespawnCoordinator : MonoBehaviour
    {
        /// <summary>
        /// Ayarların TEK kaynağı. Bu bileşen çalışma anında yaratıldığı için (HudBootstrap)
        /// Inspector'dan referans verilemiyor; profil <c>Resources</c>'tan ada göre yüklenir.
        /// Bkz. <see cref="RespawnProfile"/>.
        /// </summary>
        private const string ProfileResourcePath = "RespawnProfile";

        private RespawnProfile _profile;

        /// <summary>Doğuştan sonra bu süre içinde ölmek, doğum noktasının kötü olduğuna işarettir.</summary>
        private const float ReDeathWarnWindow = 3f;

        private readonly Dictionary<PlayerController, float> _pending = new();
        private readonly Dictionary<PlayerController, (float Time, Vector3 Point)> _lastRespawn = new();
        private readonly List<PlayerController> _finished = new();
        private readonly List<PlayerController> _stale = new();
        private float _nextScanTime;

        private void Awake()
        {
            _profile = Resources.Load<RespawnProfile>(ProfileResourcePath);

            if (_profile == null)
            {
                // FAIL-SOFT: yeniden doğuş oyunu oynanabilir kılan bir sistem; eksik bir ayar
                // dosyası yüzünden oyuncular sonsuza düşmemeli. Varsayılanlarla devam edilir,
                // ama sessizce değil — asset eksikse bilinsin.
                _profile = ScriptableObject.CreateInstance<RespawnProfile>();
                Debug.LogWarning($"[Respawn] Profil bulunamadı (Resources/{ProfileResourcePath}). " +
                                 "Varsayılan değerlerle çalışılıyor; ayarlar kalıcı olmayacak. " +
                                 "Create → Sunset Express → Respawn Profile ile " +
                                 "Assets/_Game/Resources/RespawnProfile.asset oluştur.", this);
            }
        }

        /// <summary>
        /// Oyuncu ölümcül bir yere değdi. Aynı oyuncu için ikinci çağrı YOK SAYILIR — bir hacimde
        /// yuvarlanan oyuncu her karede tetikleyebilir, bekleme süresi sürekli sıfırlanmamalı.
        /// </summary>
        public void ReportLethalContact(PlayerController player)
        {
            if (player == null || !InstanceFinder.IsServerStarted)
                return;

            if (_pending.ContainsKey(player))
                return;

            // Doğuş dokunulmazlığı: ışınlamanın oturmasına izin verilir. Bu pencere olmadan tek
            // bir bayat pozisyon okuması oyuncuyu anında yeniden öldürüyordu.
            if (IsInRespawnGrace(player))
                return;

            WarnIfDiedRightAfterRespawn(player);

            _pending[player] = Time.time + _profile.respawnDelay;

            // Owner'a bildir: ölüm ekranı karartmayı açar ve geri sayımı gösterir. Bildirim
            // ışınlamadan ÖNCE gitmeli — karartmanın amacı düşen gövdeyi örtmek.
            player.ServerNotifyDeath(_profile.respawnDelay);
        }

        private void Update()
        {
            // Karar YALNIZ sunucuda verilir. Client'ta bu bileşen sessizce boşta durur.
            if (!InstanceFinder.IsServerStarted)
                return;

            ScanForFallenPlayers();
            TickPending();
        }

        /// <summary>Y eşiği güvenlik ağı — ölümcül hacim konulmamış level'da da kurtarır.</summary>
        private void ScanForFallenPlayers()
        {
            if (Time.time < _nextScanTime)
                return;

            _nextScanTime = Time.time + _profile.scanInterval;

            PruneRespawnHistory();

            PlayerController[] players = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            for (int i = 0; i < players.Length; i++)
            {
                PlayerController p = players[i];
                if (p == null || p.transform.position.y > _profile.fallThresholdY)
                    continue;

                ReportLethalContact(p);
            }
        }

        /// <summary>
        /// Doğuş geçmişi yalnız "hemen sonra öldü mü" sorusu için tutulur; penceresi geçen ya da
        /// yok edilmiş oyuncuların kayıtları sözlüğü büyütmemeli (oturum boyu sızıntı olurdu).
        /// </summary>
        private void PruneRespawnHistory()
        {
            if (_lastRespawn.Count == 0)
                return;

            _stale.Clear();

            foreach (KeyValuePair<PlayerController, (float Time, Vector3 Point)> entry in _lastRespawn)
            {
                if (entry.Key == null || Time.time - entry.Value.Time > ReDeathWarnWindow)
                    _stale.Add(entry.Key);
            }

            for (int i = 0; i < _stale.Count; i++)
                _lastRespawn.Remove(_stale[i]);
        }

        private void TickPending()
        {
            if (_pending.Count == 0)
                return;

            _finished.Clear();

            foreach (KeyValuePair<PlayerController, float> entry in _pending)
            {
                // Oyuncu bu arada yok edildiyse (disconnect, sahne geçişi) kaydı düşür.
                if (entry.Key == null)
                {
                    _finished.Add(entry.Key);
                    continue;
                }

                if (Time.time >= entry.Value)
                    _finished.Add(entry.Key);
            }

            // Sözlük üstünde dolaşırken değiştirmemek için ayrı turda uygulanır.
            for (int i = 0; i < _finished.Count; i++)
            {
                PlayerController player = _finished[i];
                _pending.Remove(player);

                if (player != null)
                    Respawn(player);
            }
        }

        private void Respawn(PlayerController player)
        {
            if (!TryFindRespawnPoint(player, out Vector3 point))
            {
                Debug.LogWarning("[Respawn] Güvenli zemin bulunamadı — oyuncu ışınlanmadı. " +
                                 "Tabutun çevresi tamamen ölümcül/boşluk olabilir.", this);

                // Karartmayı AÇIK BIRAKMA: ışınlama başarısız olsa bile ekran geri gelmeli,
                // yoksa oyuncu siyah ekranda kilitli kalır. Işınlama OLMADIĞI için beklenecek bir
                // hedef de yok — "varış" kanalı yerine açık "beklemeden dirilt" ucu kullanılır.
                player.ServerNotifyReviveImmediate();
                return;
            }

            player.ServerTeleport(point);
            player.ServerNotifyRespawn(point);

            _lastRespawn[player] = (Time.time, point);
        }

        /// <summary>
        /// Oyuncu doğuş dokunulmazlığı penceresinde mi. Pencere KISADIR: amacı ölümü ucuzlatmak
        /// değil, ışınlamanın tamamlanmasına izin vermek. Gerçekten ölümcül bir noktaya doğulduysa
        /// pencere kapanır kapanmaz normal ölüm işler — ve <see cref="WarnIfDiedRightAfterRespawn"/>
        /// bunu Console'a yazar.
        /// </summary>
        private bool IsInRespawnGrace(PlayerController player)
        {
            if (!_lastRespawn.TryGetValue(player, out (float Time, Vector3 Point) last))
                return false;

            // 0-tuzağı sigortası: alan sonradan eklendi, eski asset'lerde 0 gelebilir.
            float grace = _profile.respawnGrace > 0.01f ? _profile.respawnGrace : 0.75f;
            return Time.time - last.Time < grace;
        }

        /// <summary>
        /// Doğum noktası KÖTÜYSE bunu tahminle değil kanıtla bilmek istiyoruz. Oyuncu doğduktan
        /// hemen sonra tekrar ölüyorsa seçilen nokta güvenli değildi (kayan bir eğim, dar bir
        /// sütun tepesi, ölümcül hacmin kıyısı) — koordinatıyla birlikte Console'a yazılır.
        ///
        /// Sahada "geri sayım iki kez çıkıyor" diye görünen şey buydu: arayüz iki kez çizmiyordu,
        /// oyuncu gerçekten iki kez ölüyordu.
        /// </summary>
        private void WarnIfDiedRightAfterRespawn(PlayerController player)
        {
            if (!_lastRespawn.TryGetValue(player, out (float Time, Vector3 Point) last))
                return;

            _lastRespawn.Remove(player);

            float alive = Time.time - last.Time;
            if (alive > ReDeathWarnWindow)
                return;

            Debug.LogWarning($"[Respawn] Oyuncu doğduktan {alive:0.0} sn sonra tekrar öldü — " +
                             $"seçilen doğum noktası güvenli değildi: {last.Point}. " +
                             "Muhtemel sebep: nokta dar/eğimli bir yüzeyde ya da ölümcül hacmin " +
                             "kıyısında. Profilde maxHeightDifference ve clearanceRadius'a bak.",
                             this);
        }

        /// <summary>
        /// Doğum noktasını bulur. Öncelik TABUT (GDD 3.4); tabut yoksa (ör. Hub) spawn
        /// işaretçilerine düşülür.
        /// </summary>
        private bool TryFindRespawnPoint(PlayerController player, out Vector3 point)
        {
            PlayerCapsule capsule = PlayerCapsule.For(player, _profile);

            Coffin coffin = FindFirstObjectByType<Coffin>();
            if (coffin != null && TryFindSafeGroundNear(coffin.transform.position, capsule, out point))
                return true;

            // Tabutsuz sahne (Hub) ya da tabutun çevresi tamamen ölümcül: spawn işaretçisi yedeği.
            // İşaretçiler de DOĞRULANIR: eskiden ilk işaretçi koşulsuz kabul ediliyordu,
            // yani ölümcül hacme taşınmış ya da başka oyuncuyla dolu bir spawn point sonsuz ölüm
            // döngüsü üretebiliyordu. Yedek yol da güvenli olmak zorunda.
            PlayerSpawnPoint[] markers = FindObjectsByType<PlayerSpawnPoint>(FindObjectsSortMode.None);
            for (int i = 0; i < markers.Length; i++)
            {
                if (markers[i] == null)
                    continue;

                Vector3 candidate = markers[i].transform.position;
                if (!HasClearance(candidate, capsule) || IsInsideLethalZone(candidate, capsule))
                    continue;

                point = candidate;
                return true;
            }

            // Hiçbir işaretçi doğrulanamadıysa yine de İLKİNE düş: doğmamaktansa riskli bir
            // noktada doğmak iyidir — oyuncu hiç dönmezse oturum onun için biter.
            if (markers.Length > 0 && markers[0] != null)
            {
                Debug.LogWarning("[Respawn] Hiçbir spawn işaretçisi güvenli doğrulanamadı; " +
                                 "ilkine düşülüyor. İşaretçiler ölümcül hacimde ya da dolu olabilir.", this);
                point = markers[0].transform.position;
                return true;
            }

            point = default;
            return false;
        }

        /// <summary>
        /// Oyuncunun GERÇEK kapsül ölçüleri. Boşluk kontrolü sabit bir küreyle yapılıyordu ve o
        /// yalnız yerden ~1.1 m yüksekliğe bakıyordu: 1.15-1.42 m arası alçak bir tavan
        /// kapsülün ÜSTÜNÜ geometriye gömüyor, oyuncu duvarın içinde doğuyordu. Ölçüler
        /// karakterden okunur; bulunamazsa profildeki yedek değerler kullanılır.
        /// </summary>
        private readonly struct PlayerCapsule
        {
            public readonly float Radius;
            public readonly float Height;

            private PlayerCapsule(float radius, float height)
            {
                Radius = radius;
                Height = height;
            }

            public static PlayerCapsule For(PlayerController player, RespawnProfile profile)
            {
                float radius = profile != null && profile.clearanceRadius > 0.01f ? profile.clearanceRadius : 0.55f;
                float height = profile != null && profile.clearanceHeight > 0.01f ? profile.clearanceHeight : 1.8f;

                if (player != null)
                {
                    CapsuleCollider c = player.GetComponentInChildren<CapsuleCollider>();
                    if (c != null)
                    {
                        // Ölçek dahil: karakter prefab'ı ölçeklenmişse collider'ın nominal
                        // değerleri gerçek boyutu vermez.
                        Vector3 s = c.transform.lossyScale;
                        radius = c.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                        height = Mathf.Max(c.height * Mathf.Abs(s.y), radius * 2f);
                    }
                }

                return new PlayerCapsule(radius, height);
            }
        }

        /// <summary>
        /// Verilen noktanın çevresinde, oyuncunun sığdığı ve ÖLÜMCÜL OLMAYAN bir zemin arar.
        /// İçten dışa doğru taranır: tabuta en yakın güvenli nokta tercih edilir ("tabuta
        /// snap'lenmiş", GDD 3.4).
        ///
        /// Ölümcül hacim kontrolü ŞART: bulunan nokta bir <see cref="LethalZone"/> içindeyse
        /// oyuncu doğar doğmaz yeniden ölür ve sonsuz ölüm döngüsüne girer.
        ///
        /// YÖN RASTGELEDİR, MESAFE DEĞİL: taramanın başlangıç açısı her ölümde kaydırılır, ama
        /// halkalar hâlâ içten dışa denenir. Yani "tabuta en yakın güvenli zemin" garantisi
        /// (GDD 3.4) bozulmadan, oyuncu her seferinde tabutun farklı bir yanında doğar — sabit
        /// tarama hep aynı noktayı buluyordu ve bu robotik hissettiriyordu. Rastgelelik yalnız
        /// sunucuda üretilir; sonuç zaten ışınlama olarak yayılıyor, senkron gerekmez.
        /// </summary>
        private bool TryFindSafeGroundNear(Vector3 center, PlayerCapsule capsule, out Vector3 point)
        {
            // Tam turun kesri olarak başlangıç kayması (0..1).
            float spin = _profile.randomizeDirection ? Random.value : 0f;

            // 0-tuzağı sigortası: alanlar sonradan eklendi, eski asset'lerde 0 gelebilir ve
            // 0 olursa hiçbir aday kabul edilmezdi (bilinen tuzak).
            float maxDrop = _profile.maxHeightDifference > 0.01f ? _profile.maxHeightDifference : 2.5f;
            float maxRise = _profile.maxRiseFromCoffin > 0.01f ? _profile.maxRiseFromCoffin : 1f;

            // IŞIN TABUTUN HEMEN ÜSTÜNDEN BAŞLAR. Önceden sabit 6 m yukarıdan başlıyordu ve
            // tabutun üstünde bir platform varsa ışın önce ONA çarpıp orayı "zemin" sayıyordu:
            // tabut aşağıda kalırken oyuncu üst kata doğuyordu (sahada görüldü). Başlangıcı
            // yukarı payıyla sınırlamak, üstteki katı taramanın görüş alanından tamamen çıkarır —
            // filtreye kalmadan, kaynağında.
            float rayStart = maxRise + 0.5f;
            float rayLength = rayStart + maxDrop + 0.5f;

            // Halkalar MİNİMUM MESAFEDEN başlar, merkezden değil. Merkezden başlayınca ışın ilk
            // olarak TABUTUN KENDİSİNE çarpıyor ve "güvenli zemin" diye tabutun üstünü buluyordu:
            // oyuncu tepede doğup kayıyor, düşüyor ve tekrar ölüyordu (sahada görüldü — geri sayım
            // iki kez çıkıyordu, çünkü gerçekten iki kez ölünüyordu).
            int rings = Mathf.Max(1, _profile.searchRings);
            float minRadius = Mathf.Max(0.01f, _profile.minDistanceFromCoffin);
            float maxRadius = Mathf.Max(minRadius, _profile.searchRadius);

            for (int ring = 0; ring <= rings; ring++)
            {
                float radius = Mathf.Lerp(minRadius, maxRadius, ring / (float)rings);
                int samples = Mathf.Max(3, _profile.samplesPerRing);

                for (int i = 0; i < samples; i++)
                {
                    // Her halkada açı ayrıca kaydırılır — aynı yönler üst üste denenmesin, tabutun
                    // etrafı daha eşit taransın.
                    float turns = spin + i / (float)samples + ring * 0.5f / samples;
                    float angle = turns * Mathf.PI * 2f;
                    Vector3 probe = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                    if (!Physics.Raycast(probe + Vector3.up * rayStart, Vector3.down,
                                         out RaycastHit hit, rayLength, _profile.groundMask,
                                         QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    // Zemin ARANIYOR: tabutun ya da başka bir oyuncunun üstü zemin değildir.
                    // Tabutun üstüne doğmak yukarıdaki döngüyü, oyuncunun üstüne doğmak da
                    // ikisinin birbirini fırlatmasını üretir.
                    if (hit.collider.GetComponentInParent<Coffin>() != null)
                        continue;
                    if (hit.collider.GetComponentInParent<PlayerController>() != null)
                        continue;

                    // "Tabutun YANI" aynı kat demektir. Bu kontrol olmadan, dar bir köprüde yanda
                    // boşluk olduğunda ışın 6 m aşağıdaki SÜTUNUN TEPESİNİ "zemin" sayıyordu:
                    // oyuncu köprünün altına doğuyor, kayıyor, düşüyor ve TEKRAR ölüyordu — geri
                    // sayımın iki kez çıkmasının ikinci sebebi buydu (sahada görüldü).
                    // Yükseklik payı ASİMETRİKTİR — yukarı ve aşağı aynı şey değil.
                    // Aşağı: bir basamak inişine izin var, alt kata inmeye yok.
                    // Yukarı: çok daha dar. Tabutun ÜSTÜNDEKİ platform "tabutun yanı" sayılmaz;
                    // orası ayrı bir kattır ve oyuncu tabuttan koparak orada doğardı.
                    float dy = hit.point.y - center.y;
                    if (dy > maxRise || dy < -maxDrop)
                        continue;

                    // ZEMİN EĞİMİ. Doğrulanmadığında dik bir yamaç da "zemin" sayılıyordu:
                    // ışın çarpıyor, yükseklik payı tutuyor, ama oyuncu doğar doğmaz kayıp
                    // düşüyor ve tekrar ölüyordu. Tam da kapatmaya çalıştığımız döngü.
                    float maxSlope = _profile.maxGroundSlope > 0.01f ? _profile.maxGroundSlope : 40f;
                    if (Vector3.Angle(hit.normal, Vector3.up) > maxSlope)
                        continue;

                    Vector3 candidate = hit.point + Vector3.up * 0.05f;

                    if (!HasClearance(candidate, capsule))
                        continue;

                    if (IsInsideLethalZone(candidate, capsule))
                        continue;

                    point = candidate;
                    return true;
                }
            }

            point = default;
            return false;
        }

        /// <summary>
        /// Oyuncu bu noktaya SIĞIYOR mu — gerçek kapsül ölçüleriyle. Önce yalnız yerden bir küre
        /// bakılıyordu ve o ~1.1 m yüksekliğe kadar görüyordu: 1.15-1.42 m arası alçak bir
        /// tavan kapsülün üstünü geometriye gömüyor, oyuncu duvarın içinde doğuyordu.
        /// </summary>
        private bool HasClearance(Vector3 groundPoint, PlayerCapsule capsule)
        {
            GetCapsulePoints(groundPoint, capsule, out Vector3 bottom, out Vector3 top);
            return !Physics.CheckCapsule(bottom, top, capsule.Radius, _profile.groundMask,
                                         QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Nokta bir ölümcül hacmin içinde mi — sonsuz ölüm döngüsü sigortası. Oyuncunun BOYU
        /// kadar kontrol edilir, tek nokta değil: ayak ucu hacmin dışında ama başı içinde kalan
        /// bir noktada oyuncu doğar doğmaz ölürdü. <see cref="LethalZone"/> collider'ın üstünde
        /// değil ÜST OBJEDE de olabilir — tetikleyicinin kendisi de aynı esnekliği kullanıyor.
        /// </summary>
        private static bool IsInsideLethalZone(Vector3 groundPoint, PlayerCapsule capsule)
        {
            GetCapsulePoints(groundPoint, capsule, out Vector3 bottom, out Vector3 top);

            Collider[] overlaps = Physics.OverlapCapsule(bottom, top, capsule.Radius, ~0,
                                                         QueryTriggerInteraction.Collide);
            for (int i = 0; i < overlaps.Length; i++)
            {
                if (overlaps[i] != null && overlaps[i].GetComponentInParent<LethalZone>() != null)
                    return true;
            }
            return false;
        }

        /// <summary>Zemin noktasının üstünde duran kapsülün iki küre merkezi.</summary>
        private static void GetCapsulePoints(Vector3 groundPoint, PlayerCapsule capsule,
            out Vector3 bottom, out Vector3 top)
        {
            float radius = Mathf.Max(0.05f, capsule.Radius);
            float height = Mathf.Max(radius * 2f, capsule.Height);

            bottom = groundPoint + Vector3.up * radius;
            top = groundPoint + Vector3.up * (height - radius);
        }
    }
}
