#if UNITY_EDITOR || DEVELOPMENT_BUILD
using FishNet;
using SunsetExpress.Networking;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// PLAYTEST ARACI — iki kademeli reset. İhtiyaç sahadan geldi: bir oyuncu haritadan düşünce
    /// ya da tabut uçuruma gidince oyundan çıkıp yeniden girmek gerekiyordu; bu, 15 dakikalık bir
    /// playtest turunun yarısını yiyor.
    ///
    /// ═══ F5 — HIZLI RESET ═══
    /// Oyuncular spawn noktalarına, tabutlar başlangıç pozlarına. Anında, sahne yüklemesi yok.
    /// SINIRI: ceset kaybı, hasar sayacı ve engel durumları YERİNDE KALIR.
    ///
    /// ═══ F6 — TAM RESET ═══
    /// Hub'a döner; panodan kontratı yeniden seçmek level'ı SIFIRDAN yükler ve her şeyi temizler
    /// (ceset, hasar, engeller). Yavaş ama eksiksiz.
    ///
    /// İkisi ayrı duruyor çünkü ihtiyaç da ayrı: çoğu sıkışma F5 ile çözülür, yalnız ceset düşünce
    /// ya da hasar birikince F6 gerekir. Tek tuşta birleştirmek her sıkışmada sahne yüklemesi
    /// bekletirdi.
    ///
    /// CESEDİ YERİNDE DİRİLTMEK bilinçli olarak yapılmadı: `CorpseSlide`'ın kayıp bayrağı, ragdoll
    /// temizliği, tabut kütlesine cesedin geri eklenmesi ve CoM defteri işin içinde — o dosya
    /// "CoM'a yalnızca tek script dokunur" kuralının sahibi. Kütle muhasebesini yanlış geri almak
    /// sonradan bambaşka görünen fizik hataları üretir. F6 aynı sonucu güvenli yoldan veriyor.
    /// (Not: "kayıp KALICIDIR" pazarlıksız kuralı OYNANIŞI bağlar; bu araç sürüm derlemesine
    /// girmediği için ihlal değil — ama konusu o maddeye değdiği için burada açıkça yazılı.)
    ///
    /// Tuşlar F5/F6: F8 (kopma uyarısı debug), F9 (DebugSyncJump), F10 (CoffinDamage) dolu.
    ///
    /// TÜM DOSYA `UNITY_EDITOR || DEVELOPMENT_BUILD` guard'ı içinde: sürüm derlemesine hiç
    /// girmez, oyuncunun elinde "herkesi ışınla" tuşu olmaz.
    /// </summary>
    public sealed class PlaytestResetHotkey : MonoBehaviour
    {
        private void Update()
        {
            if (Keyboard.current == null || !InstanceFinder.IsServerStarted)
                return;

            bool quickReset = Keyboard.current.f5Key.wasPressedThisFrame;
            bool fullReset = Keyboard.current.f6Key.wasPressedThisFrame;

            if (!quickReset && !fullReset)
                return;

            NetworkSceneDirector director = FindFirstObjectByType<NetworkSceneDirector>();
            if (director == null)
            {
                Debug.LogWarning("[Playtest] NetworkSceneDirector bulunamadı — reset yapılamadı.", this);
                return;
            }

            if (quickReset)
            {
                Debug.Log("[Playtest] F5 — oyuncular ve tabutlar başa alınıyor (hızlı reset).", this);
                director.PlaytestReset();
                return;
            }

            Debug.Log("[Playtest] F6 — Hub'a dönülüyor. Panodan kontratı yeniden seçmek level'ı " +
                      "SIFIRDAN yükler: ceset, hasar ve engeller de temizlenir.", this);
            director.ReturnToHub();
        }
    }
}
#endif
