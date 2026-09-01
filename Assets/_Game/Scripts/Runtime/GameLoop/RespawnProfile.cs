using UnityEngine;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Yeniden doğuş sabitleri (GDD 3.4). Projedeki diğer profillerle aynı felsefe (GDD 12.3):
    /// sabitler koda değil asset'e yazılır, tasarımcı kod olmadan ayarlar.
    ///
    /// ⚠ ASSET'İN YERİ FARKLI: diğer profiller `ScriptableObjects/` altında ve sahnedeki/prefab'daki
    /// bileşene Inspector'dan bağlanıyor. <see cref="PlayerRespawnCoordinator"/> ise ÇALIŞMA ANINDA
    /// yaratılıyor (HudBootstrap), yani bağlanacak bir Inspector alanı yok. Bu yüzden asset
    /// `Resources` altında durur ve koddan yüklenir:
    ///
    ///     Assets/_Game/Resources/RespawnProfile.asset
    ///
    /// Dosya adı BUDUR ve değişemez — yükleme ada göre yapılıyor.
    ///
    /// Asset yoksa kod güvenli varsayılanlarla çalışmaya devam eder (fail-soft) ve bir kez uyarır:
    /// yeniden doğuş oyunu oynanabilir kılan bir sistem, eksik bir ayar dosyası yüzünden
    /// durmamalı.
    /// </summary>
    [CreateAssetMenu(fileName = "RespawnProfile", menuName = "Sunset Express/Respawn Profile")]
    public sealed class RespawnProfile : ScriptableObject
    {
        [Header("Bekleme (GDD 3.4: 3-5 sn)")]
        [Tooltip("Ölümle yeniden doğuş arası. Kısaltmak uçurumu kısayola çevirir (exploit " +
                 "sigortası), uzatmak oyuncuyu ekipten koparır. GDD bandı 3-5 sn.")]
        [Range(1f, 10f)]
        public float respawnDelay = 4f;

        [Header("Güvenli zemin araması")]
        [Tooltip("Tabuta bu mesafeden YAKIN doğulmaz. Çok düşürülürse oyuncu tabutun üstünde/içinde " +
                 "doğar, kayıp düşer ve tekrar ölür.")]
        public float minDistanceFromCoffin = 1.6f;

        [Tooltip("Tabuttan bu yarıçapa kadar güvenli zemin aranır.")]
        public float searchRadius = 4f;

        [Tooltip("AŞAĞI yön payı: aday zemin tabuttan bu kadar ALÇAKTAysa reddedilir. Dar bir " +
                 "köprüde yanda boşluk varsa ışın aşağıdaki sütunun tepesini buluyordu; oyuncu " +
                 "köprünün altına doğup kayıyor ve tekrar ölüyordu. Bir basamak/rampa inişine " +
                 "izin verecek kadar geniş olmalı, alt kata inecek kadar değil.")]
        public float maxHeightDifference = 2.5f;

        [Tooltip("YUKARI yön payı — kasten çok daha DAR. Tabutun üstündeki bir platform " +
                 "\"tabutun yanı\" değildir: tabut aşağıda kalırken oyuncu üst kata doğuyordu. " +
                 "Yalnız bir basamak çıkışına izin verecek kadar açık. Büyütmek o hatayı geri " +
                 "getirir; asıl çözüm zaten ışının tabutun çok üstünden başlamaması.")]
        public float maxRiseFromCoffin = 1f;

        [Tooltip("Halka sayısı — içten dışa taranır, tabuta en yakın güvenli nokta tercih edilir.")]
        [Range(1, 8)]
        public int searchRings = 3;

        [Tooltip("Her halkada denenecek yön sayısı.")]
        [Range(3, 24)]
        public int samplesPerRing = 8;

        [Tooltip("Oyuncunun sığması için gereken boşluk yarıçapı. Oyuncunun GERÇEK CapsuleCollider " +
                 "yarıçapı bulunabilirse o kullanılır; bu yalnız yedek değerdir.")]
        public float clearanceRadius = 0.55f;

        [Tooltip("Boşluk kontrolünde kullanılacak yedek oyuncu boyu (m). Gerçek CapsuleCollider " +
                 "bulunabilirse onun yüksekliği kullanılır.")]
        public float clearanceHeight = 1.8f;

        [Tooltip("Aday zeminin en fazla bu kadar eğimli olmasına izin verilir (derece). Eğim " +
                 "doğrulanmadığında DİK BİR YAMAÇ 'zemin' sayılıyordu: oyuncu doğar doğmaz " +
                 "kayıp düşüyor ve tekrar ölüyordu.")]
        [Range(0f, 89f)]
        public float maxGroundSlope = 40f;

        [Tooltip("Zemin sayılacak katmanlar.")]
        public LayerMask groundMask = ~0;

        [Tooltip("Açık: doğum yönü her ölümde rastgele kaydırılır — hep aynı noktada doğmak " +
                 "robotik hissettiriyordu. Tabuta yakınlık korunur, yalnız YÖN değişir.")]
        public bool randomizeDirection = true;

        [Tooltip("Doğuştan sonra bu süre boyunca oyuncu ölümsüzdür. Bir karelik bayat okuma ya da " +
                 "ışınlamanın fiziğe işlemesindeki gecikme, oyuncuyu ANINDA tekrar öldürebiliyordu " +
                 "(geri sayım iki kez, ölüm 8 sn). Kısa tutulur: amaç hile değil, doğuşun " +
                 "tamamlanmasına izin vermek. 0 yapmak o hatayı geri getirir.")]
        [Range(0f, 3f)]
        public float respawnGrace = 0.75f;

        [Header("Güvenlik ağı")]
        [Tooltip("Bu Y'nin altına düşen oyuncu, ölümcül hacim olmasa bile ölmüş sayılır.")]
        public float fallThresholdY = -50f;

        [Tooltip("Y eşiği taraması aralığı (sn) — her kare gerekmiyor.")]
        public float scanInterval = 0.5f;
    }
}
