using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

namespace SunsetExpress.UI
{
    /// <summary>
    /// Projedeki TEK EventSystem'i sahiplenir ve kalıcı HUD ömrü boyunca yaşatır.
    ///
    /// Neden gerekli: EventSystem eskiden oyun içi menünün <c>Start</c>'ında "yoksa kur"
    /// diye lazy kuruluyordu ve <see cref="GameLoop.BootstrapLoader"/> ile sırası GARANTİ DEĞİLDİ.
    /// İki yönde de bozuluyordu:
    ///   · Önce MainMenu yüklenirse → sahnedeki EventSystem bulunur, kalıcı olan KURULMAZ; MainMenu
    ///     unload olunca Hub ve level butonları EventSystem'siz kalır (tıklama ölür).
    ///   · Önce kalıcı olan kurulursa → MainMenu kendi kopyasını getirir; İKİ aktif EventSystem,
    ///     editörde sürekli uyarı, hangisinin girdiyi işlediği belirsiz.
    ///
    /// Çözüm sıradan bağımsız: kendi EventSystem'imizi DETERMİNİSTİK olarak (Awake'te) kurarız ve
    /// her sahne yüklemesinde yabancı kopyaları eleriz. Böylece sahnelerden EventSystem silmek
    /// zorunda kalmayız — Baran ileride sahne eklerken Canvas'la birlikte gelen kopya da elenir.
    /// </summary>
    public sealed class UiEventSystemOwner : MonoBehaviour
    {
        private EventSystem _owned;

        private void Awake()
        {
            _owned = CreateOwnedEventSystem();

            // Açılış sahnesinde zaten bir kopya varsa hemen ele — sceneLoaded olayı o sahne için
            // artık ateşlenmeyecek.
            RemoveForeignEventSystems();

            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode) => RemoveForeignEventSystems();

        private EventSystem CreateOwnedEventSystem()
        {
            GameObject go = new("EventSystem (Persistent)", typeof(EventSystem));
            go.transform.SetParent(transform, false);

            // Yeni Input System modülü — proje her yerde UnityEngine.InputSystem okuyor.
            InputSystemUIInputModule module = go.AddComponent<InputSystemUIInputModule>();
            module.AssignDefaultActions();

            return go.GetComponent<EventSystem>();
        }

        /// <summary>
        /// Bizimki dışındaki tüm EventSystem'leri yok eder. Sahnelerin kendi kopyalarını getirmesi
        /// normaldir (Unity, Canvas oluştururken otomatik ekler) — burada sessizce toplanır.
        /// </summary>
        private void RemoveForeignEventSystems()
        {
            // INACTIVE kopyalar da taranır: `FindObjectsByType`'ın varsayılanı yalnız aktif
            // objeleri bulur. Kapalı gelen bir EventSystem elenmezdi ve sonradan etkinleştirilince
            // yine iki sistem olurdu — tam da bu sınıfın önlemeye çalıştığı durum.
            EventSystem[] all = FindObjectsByType<EventSystem>(FindObjectsInactive.Include,
                                                               FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                EventSystem candidate = all[i];
                if (candidate == null || candidate == _owned)
                    continue;

                // ÖNCE devre dışı bırak, SONRA yok et: `Destroy` kare sonuna ertelenir, yani
                // yabancı sistem yok edilene kadar AYNI KAREDE bizimkiyle birlikte girdi işlemeye
                // devam ederdi. Kapatmak anında etki eder ve o pencereyi kapatır.
                candidate.enabled = false;
                candidate.gameObject.SetActive(false);
                Destroy(candidate.gameObject);
            }
        }
    }
}
