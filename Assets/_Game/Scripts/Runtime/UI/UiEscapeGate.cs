using UnityEngine;

namespace SunsetExpress.UI
{
    /// <summary>
    /// ESC tuşunun bir karede YALNIZ BİR KEZ tüketilmesini sağlar.
    ///
    /// Sorun: "ESC yalnız en üstteki paneli kapatır" kontratı bileşen sırasına bağlıydı.
    /// İlan panosu önce koşup paneli kapatır ve imleç talebini bırakırsa, AYNI KAREDE koşan oyun içi
    /// menü aynı <c>wasPressedThisFrame</c> değerini görüp kendini açıyordu — kullanıcı bir kez
    /// ESC'ye basmasına rağmen bir panel kapanıp diğeri açılıyordu.
    ///
    /// Çözüm sıradan bağımsız çalışır, çünkü iki koruma birlikte iş görür:
    ///   · Menü ÖNCE koşarsa: hakemde panonun talebi hâlâ durur, menü zaten geri çekilir.
    ///   · Pano ÖNCE koşarsa: ESC'yi tüketir, menü bu kapıdan geçemez.
    ///
    /// Kare numarası kullanılır çünkü tüketimi sıfırlayacak merkezî bir "kare sonu" kancası yok;
    /// <see cref="Time.frameCount"/> her kare kendiliğinden ilerlediği için sıfırlama gerekmez.
    /// </summary>
    public static class UiEscapeGate
    {
        private const int NeverConsumed = -1;

        private static int _consumedFrame = NeverConsumed;

        /// <summary>Bu karede ESC başka bir bileşen tarafından tüketildi mi.</summary>
        public static bool ConsumedThisFrame => _consumedFrame == Time.frameCount;

        /// <summary>
        /// ESC'yi bu kare için sahiplenmeyi dener. İlk çağıran <c>true</c> alır ve tuşu işleyebilir;
        /// aynı karedeki sonraki çağıranlar <c>false</c> alır.
        /// </summary>
        public static bool TryConsume()
        {
            if (ConsumedThisFrame)
                return false;

            _consumedFrame = Time.frameCount;
            return true;
        }
    }
}
