using UnityEngine;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Teslim (gömme) karar sabitleri — GDD 12.3: "tüm fizik sabitleri ScriptableObject
    /// profillerinde tutulur".
    ///
    /// İlk yazımda bu değerler <see cref="DeliveryPoint"/>'in Inspector'ındaydı ve göreve
    /// "ikinci teslim noktası gelince taşınır" notu düşülmüştü. Bu kuralı KAPATMIYOR:
    /// hız/süre eşikleri fizik karar sabitidir, level geometrisi değil. Bölünme engellerdeki
    /// kararla aynı: TUNING profile, GEOMETRİ sahne örneğinde (mezar hacminin yeri ve boyutu
    /// <see cref="DeliveryPoint"/>'in kendi collider'ında kalır).
    ///
    /// Profil atanmamışsa kod güvenli varsayılanlarla çalışır ve bir kez uyarır: teslim, oyun
    /// döngüsünün BİTİŞİ — eksik bir ayar dosyası yüzünden kontrat tamamlanamaz hâle gelmemeli.
    /// </summary>
    [CreateAssetMenu(fileName = "DeliveryProfile", menuName = "Sunset Express/Delivery Profile")]
    public sealed class DeliveryProfile : ScriptableObject
    {
        [Header("Oturma")]
        [Tooltip("Tabutun bırakılmış ve durmuş hâlde bu kadar KESİNTİSİZ beklemesi gerekir (sn). " +
                 "Kısaltmak çukurun üstünden geçen tabutu teslim saymaya yaklaştırır.")]
        [Range(0.2f, 10f)]
        public float settleDuration = 1.5f;

        [Tooltip("Bu ÇİZGİSEL hızın altındaki tabut durmuş sayılır (m/sn).")]
        public float maxSettleSpeed = 0.5f;

        [Tooltip("Bu AÇISAL hızın altındaki tabut durmuş sayılır (rad/sn). Çizgisel hız TEK BAŞINA " +
                 "yetmez: grab joint'imizde tüm açısal eksenler serbest (GDD 6.4 yeniden tasarımı), " +
                 "yani merkezi sabit dururken kendi etrafında dönen tabut 'durmuş' görünürdü.")]
        public float maxSettleAngularSpeed = 0.6f;

        [Header("Maliyet")]
        [Tooltip("Tabut ARAMA aralığı (sn). Oturma koşulu her fizik adımında ölçülür — bu yalnız " +
                 "sahnede tabut arayan pahalı taramanın sıklığı.")]
        public float coffinSearchInterval = 0.25f;
    }
}
