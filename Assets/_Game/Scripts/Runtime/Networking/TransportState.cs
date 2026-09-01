using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;

namespace SunsetExpress.Networking
{
    /// <summary>
    /// "Transport GERÇEKTEN durdu mu" sorusunun tek doğru cevabı.
    ///
    /// NEDEN AYRI BİR YARDIMCI: bu soruya `ClientManager.Started == false` /
    /// `InstanceFinder.IsServerStarted == false` diye cevap vermek YANLIŞTIR ve iki ayrı yerde
    /// aynı hatayı üretti. FishNet o bayrakları `Started = state == LocalConnectionState.Started`
    /// diye yazıyor — yani bayrak **Stopping sırasında da false**. Bayrağa bakan kod, transport
    /// hâlâ kapanırken "durdu" der; ardından açılan yeni oturumun üstüne eski transport'un
    /// gecikmiş `Stopped` olayı düşer ve taze oturumu iptal eder.
    ///
    /// Bu yüzden ölçüt her zaman transport'un KENDİ durumudur. İki tüketici var ve ikisi de
    /// kendi state makinesini BAĞIMSIZ tutar (birbirine bağlanmazlar), yalnız bu saf sorguyu
    /// paylaşırlar:
    ///   • <see cref="SteamLobby"/> — "yeni oturum açılabilir mi" (Leaving → Idle geçişi)
    ///   • sahne yöneticisi — "offline MainMenu yüklenebilir mi"
    ///
    /// FAIL-CLOSED: kurulum okunamıyorsa (Multipass yok, teardown'da fake-null) "durdu" DENMEZ.
    /// Kanıtlanamayan kapanışı kapanmış saymak, çalışan bir socket'in üstüne yeni oturum açmak
    /// demektir. Çağıranlar bu durumda zaten kapanış akışına hiç girmemelidir.
    /// </summary>
    public static class TransportState
    {
        /// <summary>Multipass'i çıkarır; kurulum beklenen şekilde değilse null.</summary>
        public static Multipass GetMultipass(NetworkManager nm)
        {
            // TransportManager de null olabilir: NetworkManager doğrulama/duplicate yüzünden
            // kendi Awake'inden ERKEN ÇIKARSA alt manager'ları kurulmamış olur. Fail-closed'ın
            // anlamlı olması için buranın null-reference atmaması şart.
            if (nm == null || nm.TransportManager == null)
                return null;
            return nm.TransportManager.Transport as Multipass;
        }

        /// <summary>
        /// Seçili CLIENT transport'u tam olarak durdu mu. `Stopping` durdu SAYILMAZ.
        /// </summary>
        public static bool IsClientFullyStopped(Multipass multipass)
        {
            if (multipass == null)
                return false; // fail-closed — bkz. sınıf özeti
            return multipass.GetConnectionState(false) == LocalConnectionState.Stopped;
        }

        /// <summary>
        /// TÜM server transport'ları tam olarak durdu mu — biri bile kapanmamışsa false.
        /// Multipass server tarafını tek çağrıda vermiyor (`GetConnectionState(true)` hata loglayıp
        /// `Stopped` döndürür, yani sessizce YANLIŞ cevap verir), o yüzden index index dolaşılır.
        /// Aggregate başlatma yüzünden birden fazla server socket'i aynı anda açık olabiliyor
        /// (Steam + Tugboat), dolayısıyla tek bir `Stopped` olayı "hepsi kapandı" demek değildir.
        /// </summary>
        public static bool IsServerFullyStopped(Multipass multipass)
        {
            if (multipass == null)
                return false; // fail-closed

            for (int i = 0; i < multipass.Transports.Count; i++)
            {
                if (multipass.GetConnectionState(true, i) != LocalConnectionState.Stopped)
                    return false;
            }
            return true;
        }

        /// <summary>Hem client hem tüm server transport'ları durdu mu — ağ oturumunun
        /// gerçekten kapandığı an. Sahne yöneticisi menüye dönmeden önce bunu bekler.</summary>
        public static bool IsSessionFullyStopped(Multipass multipass)
            => IsClientFullyStopped(multipass) && IsServerFullyStopped(multipass);
    }
}
