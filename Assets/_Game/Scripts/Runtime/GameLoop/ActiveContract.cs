namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Hub'da SEÇİLEN kontrat — sahne geçişi boyunca taşınır (GDD 3.1).
    ///
    /// NEDEN VAR: aynı level farklı merhumlarla oynanır. Her merhuma ayrı level yapılmaz; kontrat
    /// "kimi taşıyorsun"u, level "nereden taşıyorsun"u belirler. Rapor kontrattan okunmalı, level
    /// sahnesine gömülü bir alandan değil — yoksa hangi kontratı seçersen seç aynı künye çıkar
    /// (sahada görüldü).
    ///
    /// SUNUCU-YEREL, senkronlanmaz: raporu zaten sunucu derleyip <c>ObserversRpc</c> ile yayıyor
    /// (bkz. <see cref="DeliveryPoint"/>), yani client'ın seçili kontratı bilmesine gerek yok.
    /// Fazladan bir senkron kanalı açmak GDD 12.2'nin event-senkron tercihine de aykırı olurdu.
    ///
    /// STATİK OLMASININ SEBEBİ: bilginin sahne geçişinden SAĞ ÇIKMASI gerekiyor ve seçim (Hub'daki
    /// pano) ile tüketim (level'daki mezar) iki ayrı sahnede. Kalıcı bir sahne objesine bağlamak
    /// da olurdu ama o obje <c>NetworkSceneDirector</c> olurdu — Kaan'ın dosyası, ve tek bir
    /// ScriptableObject referansı için oraya alan eklemek gereksiz.
    ///
    /// ScriptableObject referansı tutmak güvenli: profil/kontrat asset'leri sahne ömürlü değil.
    /// </summary>
    internal static class ActiveContract
    {
        /// <summary>Şu an oynanan kontrat; hub'daysak ya da level doğrudan açıldıysa null.</summary>
        internal static ContractDefinition Current { get; private set; }

        /// <summary>Pano kontratı başlatırken çağırır — sahne YÜKLENMEDEN önce.</summary>
        internal static void Set(ContractDefinition contract) => Current = contract;

        /// <summary>
        /// Hub'a dönüldü: aktif kontrat yok. Temizlenmezse, level'ı doğrudan Play'e alarak test
        /// eden biri bir önceki oturumun kontratını görürdü — bayat veri sessizce doğru görünür.
        /// </summary>
        internal static void Clear() => Current = null;
    }
}
