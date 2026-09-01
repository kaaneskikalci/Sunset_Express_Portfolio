using UnityEngine;

namespace SunsetExpress.Profiles
{
    /// <summary>
    /// Hareketli Zemin arketipinin PAYLAŞILAN ayarları (GDD 7, 12.3).
    ///
    /// NEDEN PROFİL: Pazarlıksız kural "tüm fizik sabitleri ScriptableObject profillerinde" der
    /// (GDD 12.3). İlk yazımda bu değerler örnek-başına serialized alanlardaydı; gerekçe "her
    /// platform kendi level bağlamına özgü" idi. Gerekçe waypoint'ler için DOĞRU ama hız/ritim
    /// için değil: aynı arketipin altı örneği levellere yerleştikten sonra "kütükler biraz hızlı"
    /// düzeltmesini altı yerde yapmak istemezsin — kuralın var oluş sebebi tam olarak budur.
    ///
    /// BÖLÜNME (ekip kararı 2026-08-04):
    ///   PROFİLDE  → ritim ve his (hız, bekleme, dönüş hızı)
    ///   ÖRNEKTE   → level geometrisi (waypoint'ler, dönüş ekseni, güzergah modu)
    /// Farklı hisli bir kütük istiyorsan yeni bir profil asset'i üret — tasarımcı kod yazmaz.
    /// </summary>
    [CreateAssetMenu(fileName = "MovingPlatformProfile", menuName = "Sunset Express/Moving Platform Profile")]
    public sealed class MovingPlatformProfile : ScriptableObject
    {
        [Header("Güzergah ritmi")]
        [Tooltip("Hareket hızı (m/sn). 0 girilirse güvenli varsayılana düşülür (eski asset koruması).")]
        public float speed = 2f;

        [Tooltip("Her waypoint'te duraklama süresi (sn). Ritim buradan kurulur — GDD 7 " +
                 "'ritim, senkron hareket'. 0 geçerli bir değerdir (duraksız gidiş-geliş).")]
        public float waitAtWaypoint = 0.5f;

        [Header("Dönme")]
        [Tooltip("Dönüş hızı (derece/sn). Eksen ÖRNEKTE tanımlıdır — o level geometrisidir.\n\n" +
                 "⚠ PhysX gerçeği: kinematic bir platform üstündeki cisimlere açısal hız AKTARMAZ. " +
                 "Platform döner ama oyuncular kendi yönlerinde kalır; parent benzeri garantili yaw " +
                 "mirası yoktur. Dönüşü görsel/ritmik bir öğe olarak kullan, 'üstündeki de dönecek' " +
                 "diye tasarlama.")]
        public float rotationSpeed = 0f;
    }
}
