using UnityEngine;

namespace SunsetExpress.Profiles
{
    /// <summary>
    /// Tahterevalli Köprü arketipinin PAYLAŞILAN ayarları (GDD 7, 12.3).
    ///
    /// Bölünme <see cref="MovingPlatformProfile"/> ile aynı mantıkta:
    ///   PROFİLDE → köprünün HİSSİ (eğim sınırı, toparlama yayı, sönümler)
    ///   ÖRNEKTE  → level geometrisi (hinge anchor/axis, köprünün boyu ve kütlesi)
    ///
    /// ⚠ TUNING NOTU: bu değerlerin kritik sönümlü olup olmadığı köprünün KÜTLESİ ve
    /// atalet tensörü olmadan hesaplanamaz — `ζ ≈ c / (2√(kI))`. Ritmik oyuncu hareketi
    /// rezonans benzeri salınım üretebilir. Profil ayarlanırken köprü kütlesi/ataleti ile
    /// yerleşme süresi BİRLİKTE ölçülmeli; tek başına spring/damper oynamak yanıltır.
    /// </summary>
    [CreateAssetMenu(fileName = "SeesawBridgeProfile", menuName = "Sunset Express/Seesaw Bridge Profile")]
    public sealed class SeesawBridgeProfile : ScriptableObject
    {
        [Header("Eğilme sınırı")]
        [Tooltip("Menteşenin izin verdiği maksimum eğim (± derece). Küçük değer = bağışlayıcı köprü. " +
                 "0 girilirse güvenli varsayılana düşülür (eski asset koruması).")]
        public float tiltLimit = 25f;

        [Tooltip("Sınıra çarpışta yumuşaklık. 0 = sert duvar; yüksek = yaylı. 0 GEÇERLİ bir " +
                 "değerdir ve varsayılandır — limitte enerji geri eklemez.")]
        public float limitBounciness = 0f;

        [Header("Kendini toparlama")]
        [Tooltip("Açık: köprü boşalınca yavaşça yatay konuma döner. Kapalı: kaldığı açıda kalır — " +
                 "daha acımasız, Zirve tarzı kontratlar için.")]
        public bool useReturnSpring = true;

        [Tooltip("Yatay konuma dönme kuvveti. Yüksek değer köprüyü fiilen sabitler ve GDD 7'nin " +
                 "'ağırlık dağılımı dengeyi belirler' amacını öldürür — ölçülü tut.\n" +
                 "Ölçek fikri: 250 kg yük menteşeden 2 m uzaktayken ~4.9 kN·m tork üretir; " +
                 "spring 40 ise 25°'de ~1.0 kN·m toparlama verir, yani yükü düz tutmaz. Doğru oran budur.")]
        public float returnSpring = 40f;

        [Tooltip("Salınım sönümü. Düşük değer köprüyü zıplatır ve okunamaz yapar (adalet sütunu, GDD 1.4).")]
        public float returnDamper = 12f;

        [Header("Sönümleme")]
        [Tooltip("Açısal sürüklenme — köprünün savrulma hızını sınırlar. 0 bırakılırsa ani yük " +
                 "değişiminde köprü kamçı gibi savrulur ve oyuncular okunmayan biçimde fırlar.")]
        public float angularDrag = 1.5f;
    }
}
