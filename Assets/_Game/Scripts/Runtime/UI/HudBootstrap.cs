using UnityEngine;

namespace SunsetExpress.UI
{
    /// <summary>
    /// HUD'ı çalışma anında ayağa kaldırır — sahneye ve prefab'a HİÇ dokunmadan.
    ///
    /// Neden bu yol: <c>TestScene</c> Kaan'ın sahibi olduğu dosya ve "aynı sahneye aynı gün iki kişi
    /// dokunmaz" kuralı var. Kod ile bootstrap, HUD'ın Kaan'ın sırasını beklemeden
    /// geliştirilip test edilmesini sağlar ve sahne/prefab YAML çakışması riskini sıfırlar.
    ///
    /// GEÇİCİ: UI tasarımı netleşince HUD prefab'a taşınacak (UI prefab'ları → Ozanay). O noktada bu
    /// bootstrap ya prefab'ı Instantiate eder ya da tamamen kalkıp Canvas sahneye/prefab'a gömülür.
    ///
    /// </summary>
    public static class HudBootstrap
    {
        // Alanlar da guard içinde: server derlemesinde gövde dışarıda kaldığı için burada kalsalardı
        // "hiç kullanılmıyor" uyarısı (CS0169/CS0414) üretirlerdi.
#if !UNITY_SERVER
        private const string RootName = "[SunsetExpress HUD]";

        private static GameObject _root;
#endif

        /// <summary>
        /// AfterSceneLoad: sahne yüklendikten sonra koşar, her oyun instance'ında (MPPM klonları dahil)
        /// bir kez. HUD ağdan bağımsız yaşar — lokal owner'ı <see cref="GripWarningBinder"/> bulur.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Headless/dedicated server'da HUD'ın hiçbir işlevi yok: Canvas, AudioSource ve Binder'ın
            // periyodik owner taraması boşa maliyet olurdu. Derleme zamanı guard'ı gövdeyi
            // tamamen dışarıda bırakır (erişilemeyen kod uyarısı üretmez); batchMode ise editörden
            // veya normal build'den headless koşan durumları yakalar.
#if !UNITY_SERVER
            if (Application.isBatchMode)
                return;

            // Domain reload kapalıyken (Enter Play Mode Options) statik alan önceki oturumdan
            // kalabilir; isim kontrolü ikinci bir HUD kurulmasını önler.
            if (_root != null)
                return;

            _root = new GameObject(RootName);
            Object.DontDestroyOnLoad(_root);

            // ÖNCE altyapı: EventSystem sahibi ve imleç sürücüsü, arayüz bileşenlerinden önce
            // kurulur ki hiçbiri "EventSystem var mı" diye kendi başına karar vermek zorunda
            // kalmasın (o lazy kurulum sıra bağımlıydı ve iki yönde de bozuluyordu).
            _root.AddComponent<UiEventSystemOwner>();
            _root.AddComponent<CursorArbiterDriver>();

            _root.AddComponent<GripWarningHud>();
            _root.AddComponent<GripWarningBinder>();

            // Oturum içi çıkış menüsü (ESC). Hub'a geçilince MainMenu sahnesi değiştiği için lobi
            // paneli yok oluyor; oyuncunun oturumdan çıkacak başka yolu kalmıyor. Sahne başına
            // kurmak yerine kalıcı HUD'da yaşar — her gameplay sahnesinde gerekli.
            _root.AddComponent<InGameMenu>();

            // İlan panosu arayüzü (yaklaşma ipucu + kontrat listesi). Panonun KENDİSİ sahne
            // objesidir; arayüzü burada yaşar ki hub geometrisi değişince (Baran'ın alanı) yeniden
            // kurulması gerekmesin.
            _root.AddComponent<ContractBoardPanel>();

            // Ekip arkadaşı isim etiketleri (GDD 13.2 eklemesi). Oyuncular sahneden sahneye
            // yeniden doğduğu için kalıcı HUD'da yaşar; etiket sahiplerini kendisi tarar.
            _root.AddComponent<PlayerNameTagHud>();

            // Ölüm ekranı (GDD 3.4) — lokal owner'ın ölüm/dirilme sinyallerini dinler.
            _root.AddComponent<DeathScreenHud>();

            // Cenaze raporu (GDD 3.1 "Gömme"). Teslim noktası SAHNE objesidir ve level'dan level'a
            // değişir; rapor her level'da yeniden kurulmasın diye kalıcı HUD'da yaşar.
            _root.AddComponent<ContractReportPanel>();

            // Oyuncu ölümü/yeniden doğuşu (GDD 3.4). UI değil ama sahnesiz ve kalıcı yaşaması
            // gereken sunucu mantığı — her level'a ayrıca kurulmasın diye burada.
            _root.AddComponent<GameLoop.PlayerRespawnCoordinator>();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Playtest aracı (F5 — oyuncuları yeniden doğur). UI değil ama kalıcı ve sahnesiz
            // yaşaması gereken tek nesne burası; sürüm derlemesine hiç girmiyor.
            _root.AddComponent<GameLoop.PlaytestResetHotkey>();
#endif
#endif
        }
    }
}
