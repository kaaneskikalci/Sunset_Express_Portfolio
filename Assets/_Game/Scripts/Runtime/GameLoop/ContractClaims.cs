using System.Collections.Generic;
using SunsetExpress.Coffins;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Bir tabutun teslimini KİM tamamladı — tabut başına tek seferlik, sunucu-otoriter kayıt.
    ///
    /// NEDEN VAR: tamamlanma bayrağı <see cref="DeliveryPoint"/> ÖRNEĞİNE aitti. Aynı
    /// tabut iki örtüşen mezar hacmindeyse iki nokta da aynı karede tamamlıyor ve İKİ ayrı
    /// buffered rapor yayınlıyordu. Bugün yalnız ekran iki kez çizilir; ücret/ödül sistemi gelince
    /// aynı kontrat İKİ KEZ ÖDENİR. Bayrak noktada değil, tabutta olmalı.
    ///
    /// ATOMİKLİK: Unity tek iş parçacıklıdır ve tüm teslim noktaları aynı ana döngüde koşar, yani
    /// "kontrol et + sahiplen" bölünemez. İlk çağıran kazanır, ikincisi false alır ve hiçbir şey
    /// yayınlamaz.
    ///
    /// YAŞAM DÖNGÜSÜ — açık sıfırlama YOK, bilerek: kayıt yalnız Coffin REFERANSI tutar ve her
    /// level yüklemesi yeni bir Coffin örneği doğurur. Yok edilmiş tabutların kayıtları
    /// budandığı için eski bir claim yeni bir kontratı asla bloklayamaz. Sıfırlama çağrısı
    /// eklemek, çağrılmayı unutulabilecek bir adım eklemek olurdu.
    /// </summary>
    internal static class ContractClaims
    {
        private static readonly List<Coffin> Claimed = new();

        /// <summary>
        /// Bu tabutun teslimini sahiplenmeyi dener. İlk çağıran <c>true</c>, sonrakiler
        /// <c>false</c> alır. YALNIZ SUNUCUDA çağrılmalı.
        /// </summary>
        internal static bool TryClaim(Coffin coffin)
        {
            if (coffin == null)
                return false;

            PruneDestroyed();

            // Unity fake-null: yok edilmiş tabut `== null` döner ama listede referans olarak
            // durur; budama bunu zaten temizledi, o yüzden burada düz karşılaştırma güvenli.
            if (Claimed.Contains(coffin))
                return false;

            Claimed.Add(coffin);
            return true;
        }

        /// <summary>Yok edilmiş tabutların kayıtlarını düşürür — liste oturum boyu büyümesin.</summary>
        private static void PruneDestroyed()
        {
            for (int i = Claimed.Count - 1; i >= 0; i--)
            {
                if (Claimed[i] == null)
                    Claimed.RemoveAt(i);
            }
        }
    }
}
