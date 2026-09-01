using System.Text;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using Steamworks;
using UnityEngine;

namespace SunsetExpress.Player
{
    /// <summary>
    /// Oyuncunun görünen adı (Steam persona adı) — ekip farkındalığı için isim etiketi.
    ///
    /// ⚠ GDD 13.2'ye SONRADAN eklendi (2026-08). O bölüm "minimal" ilkesiyle yazılmış
    /// ("oyuncu tabuta baksın, HUD'a değil") ve isim etiketi orijinal listede yoktu. Eklenmesi
    /// ekip kararı; çizim tarafı ilkeye saygılı tutuluyor (bkz. <see cref="UI.PlayerNameTagHud"/>):
    /// kendi adın görünmez, uzakta söner, arada duvar varsa gizlenir.
    ///
    /// AKIŞ: adı OWNER kendi makinesinden okur (Steam yalnız kendi persona adını güvenilir verir),
    /// sunucuya gönderir, sunucu TEMİZLEYİP SyncVar'a yazar. Doğrudan SyncVar'a yazdırmak
    /// istemcinin herkesin ekranına sansürsüz metin basması demek olurdu.
    ///
    /// SyncVar kullanımı event-senkron tercihiyle (GDD 12.2) çelişmiyor: bu bir OLAY değil kalıcı
    /// bir DURUM ve yalnız bir kez değişir; geç katılan ve yeniden bağlanan oyuncuların adı
    /// görebilmesi için state gerekiyor — event'le taşınsa sonradan gelen kimsenin adını bilemezdi.
    /// </summary>
    public sealed class PlayerNameTag : NetworkBehaviour
    {
        /// <summary>Ad bu uzunlukta kırpılır. Steam sınırı 32; ekranda daha uzunu zaten okunmaz.</summary>
        private const int MaxNameLength = 24;

        private readonly SyncVar<string> _displayName = new();

        /// <summary>Ekranda gösterilecek ad. Henüz gelmediyse boş — çizim katmanı boş adı atlar.</summary>
        public string DisplayName => _displayName.Value;

        private Transform _visualAnchor;

        /// <summary>
        /// Etiketin bağlanacağı transform: FishNet'in tick'ler ARASINDA yumuşattığı görsel obje —
        /// kök Rigidbody DEĞİL.
        ///
        /// NEDEN ÖNEMLİ (sahada görüldü): kök her fizik tick'inde sıçrayarak ilerler, kamera ise
        /// yumuşatılmış görsel objeyi izler (<see cref="PlayerController"/> kamerayı ona bağlıyor).
        /// Etiketi köke bağlayınca ikisi FARKLI referans çerçevesinde kalıyor ve etiket kameraya
        /// göre titriyordu. İleri/geri harekette sıçrama derinlik eksenine düştüğü için fark
        /// edilmiyor, YANA harekette doğrudan ekran yatayına düşüyor ve okunmaz hâle geliyordu.
        ///
        /// Görsel obje yoksa köke düşülür — titrer ama kaybolmaz.
        /// </summary>
        public Transform VisualAnchor
        {
            get
            {
                if (_visualAnchor != null)
                    return _visualAnchor;

                Transform graphical = NetworkObject != null ? NetworkObject.GetGraphicalObject() : null;
                _visualAnchor = graphical != null ? graphical : transform;
                return _visualAnchor;
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner)
                return;

            // Adı yalnız OWNER gönderebilir ve yalnız KENDİ adını: Steam başkasının persona adını
            // güvenilir vermez (arkadaş değilse boş döner), ayrıca başkası adına isim yazdırmak
            // istemciye verilecek bir yetki değil.
            ServerSetName(ReadLocalSteamName());
        }

        /// <summary>
        /// Lokal Steam persona adı. Steam kapalıyken (lokal host playtest'i) boş döner ve sunucu
        /// "Player N" yedeğine düşer — isim etiketi yüzünden test akışı bozulmamalı.
        /// </summary>
        private static string ReadLocalSteamName()
        {
            // `SteamManager.Initialized` getter'ı Steam hiç kurulmamışsa bile güvenli (SteamLobby
            // aynı deseni kullanıyor). Try/catch yok: init edilmemişken çağrı yapılmıyor zaten.
            if (!SteamManager.Initialized)
                return string.Empty;

            return SteamFriends.GetPersonaName();
        }

        /// <summary>
        /// RequireOwnership: adı yalnız o oyuncunun sahibi gönderebilir. Sunucu yine de TEMİZLER —
        /// sahiplik "bu metin güvenli" demek değil.
        /// </summary>
        [ServerRpc]
        private void ServerSetName(string requested, NetworkConnection conn = null)
        {
            _displayName.Value = Sanitize(requested, conn);
        }

        /// <summary>
        /// İSTEMCİDEN GELEN METİN HERKESİN EKRANINA BASILIYOR — temizlik şart:
        ///
        /// · TMP ZENGİN METİN etiketleri (`&lt;color&gt;`, `&lt;size&gt;`, `&lt;sprite&gt;`) sökülür. Sökülmezse
        ///   biri adına `&lt;size=400&gt;` yazıp herkesin ekranını kaplayabilir; `&lt;sprite&gt;` ile de
        ///   atlas dışı indeks isteyip hata seli üretebilir.
        /// · Kontrol karakterleri ve satır sonları atılır — etiket tek satır.
        /// · Uzunluk kırpılır: uzun ad hem okunmaz hem bant yer.
        /// · Sonuç boşsa bağlantı numarasından "Player N" üretilir. Boş etiket, oyuncunun
        ///   kaybolduğu izlenimi verirdi.
        /// </summary>
        private static string Sanitize(string raw, NetworkConnection conn)
        {
            StringBuilder sb = new(MaxNameLength);

            if (!string.IsNullOrEmpty(raw))
            {
                bool inTag = false;

                foreach (char c in raw)
                {
                    // Zengin metin ayıklaması: '<' ile '>' arası tamamen düşer. Kapanmayan '<'
                    // kalanı yutar — bu bilinçli, yarım etiket TMP'yi de şaşırtır.
                    if (c == '<') { inTag = true; continue; }
                    if (inTag) { if (c == '>') inTag = false; continue; }

                    if (char.IsControl(c))
                        continue;

                    sb.Append(c);

                    if (sb.Length >= MaxNameLength)
                        break;
                }
            }

            string clean = sb.ToString().Trim();

            if (clean.Length > 0)
                return clean;

            int id = conn != null ? conn.ClientId : 0;
            return $"Player {id + 1}";
        }
    }
}
