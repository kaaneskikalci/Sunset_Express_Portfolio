using UnityEngine;
using UnityEngine.SceneManagement;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Bootstrap sahnesinin tek görevi: NetworkManager'ı (ve üzerindeki SteamLobby'yi) ayağa
    /// kaldırıp hemen ana menüye geçmek. Kalıcı sistemlerin doğduğu yer burasıdır — build index 0.
    ///
    /// Neden ayrı bir bootstrap sahnesi (ekip kararı, Kaan onaylı): NetworkManager önceden
    /// TestScene'in içinde yaşıyordu, yani "oyun TestScene açılarak başlar" varsayımı vardı. Menü
    /// giriş noktası olunca o varsayım kırılıyor. NetworkManager'da `_dontDestroyOnLoad = 1` olduğu
    /// için Bootstrap'ta doğan instance sonraki tüm sahnelerde yaşamaya devam eder.
    ///
    /// TestScene'deki NetworkManager BİLEREK duruyor: Kaan fizik iterasyonunda doğrudan TestScene'e
    /// Play'e basabilsin diye. Çakışmaz, çünkü persistence = DestroyNewest — Bootstrap'tan gelinirse
    /// TestScene'inki (yeni olan) kendini yok eder; doğrudan TestScene açılırsa tek instance kalır.
    ///
    /// Sahne geçişi burada Unity'nin kendi SceneManager'ıyla yapılır, FishNet'inkiyle DEĞİL: bu an
    /// henüz ağ oturumu yok, ortada senkronlanacak bir şey yok. Ağ üzerinden ortak sahne geçişleri
    /// (menü → hub → level) ayrı bir iş ve FishNet SceneManager ister.
    /// </summary>
    public sealed class BootstrapLoader : MonoBehaviour
    {
        [Tooltip("Bootstrap'tan sonra açılacak sahne. Build Settings'te ekli olmalı.")]
        [SerializeField] private string _firstSceneName = "MainMenu";

        /// <summary>
        /// Awake DEĞİL Start: Start, sahnedeki TÜM Awake'lerden sonra koşar. NetworkManager kendini
        /// DontDestroyOnLoad'a Awake zincirinde taşıdığı için, sahneyi ondan önce değiştirirsek
        /// NetworkManager Bootstrap sahnesiyle birlikte yok olurdu.
        /// </summary>
        private void Start()
        {
            if (string.IsNullOrWhiteSpace(_firstSceneName))
            {
                Debug.LogError("[Bootstrap] İlk sahne adı boş — Bootstrap'ta takılı kalındı.", this);
                return;
            }

            SceneManager.LoadScene(_firstSceneName, LoadSceneMode.Single);
        }
    }
}
