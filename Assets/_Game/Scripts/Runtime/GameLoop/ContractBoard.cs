using FishNet;
using FishNet.Connection;
using FishNet.Object;
using SunsetExpress.Networking;
using SunsetExpress.Player;
using SunsetExpress.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Hub'daki ilan panosu: yaklaş, E'ye bas, kontrat seç, ekip o levele gider (GDD 3.1, 8.1, 13.1).
    ///
    /// TUŞ: E — GDD 6.3 tablosunda "Tut / Etkileşim" olarak tanımlı, uydurulmadı. AMA aynı tabloda
    /// tabut tutarken E "Bırak (her zaman anında çalışır — panik butonu)" ve bu PAZARLIKSIZ bir
    /// kuraldır. Bu yüzden pano, oyuncu tabut TAŞIRKEN hiç devreye girmez: ipucu bile göstermez.
    /// Bırakma tuşunun önüne hiçbir koşulda geçilmez.
    ///
    /// OTORİTE: seçim client'ta yapılır ama sahneyi SUNUCU yükler. Client yalnızca DİZİN gönderir,
    /// sahne adı göndermez — sahne adına güvenmek istemci girdisiyle rastgele sahne yükletmek olurdu.
    /// Sunucu dizini kendi listesine karşı doğrular.
    ///
    /// TEK ADIM (ekip kararı): GDD 13.1 hub'da ilan panosu (seçim) ile garaj kapısını (çıkış) AYIRIR.
    /// Şimdilik birleştirildi — seçim doğrudan yükler. Kabul edilen bedel: bir oyuncu bütün ekibi
    /// levele çekebilir. Hub'a gerçek geometri gelince garaj kapısı ikinci adım olarak eklenecek.
    /// </summary>
    public sealed class ContractBoard : NetworkBehaviour
    {
        [Header("Kontratlar (GDD 8.1 — katalog)")]
        [Tooltip("Panoda listelenecek kontrat asset'leri. Sahne adı boş olanlar gösterilmez.")]
        [SerializeField] private ContractDefinition[] _contracts;

        [Header("Etkileşim")]
        [Tooltip("Oyuncunun panoyu kullanabilmesi için gereken yakınlık (m).")]
        [SerializeField] private float _interactRange = 3f;
        [Tooltip("Lokal oyuncu bulunana kadar tarama aralığı (sn) — oyuncu sahneye geç spawn olur.")]
        [SerializeField] private float _rebindInterval = 0.5f;

        private PlayerGrabber _localPlayer;
        private ContractBoardPanel _panel;
        private float _nextRebindTime;
        private bool _promptShown;

        /// <summary>
        /// Pano SAHNE ömürlü, panel ise KALICI HUD'da yaşıyor — pano öldüğünde açtığı arayüzü
        /// yanında götürmüyor. Temizlik olmadan şu oluyordu: remote oyuncunun paneli AÇIKKEN
        /// host kontratı başlatır, pano bir sonraki Update'ten önce deinitialize olur ve panel,
        /// `_onSelect` delegesi ve imleç talebi LEVEL'A TAŞINIR. Yalnız ipucu açıksa "E — Contract
        /// Panel" yazısı level'da asılı kalır.
        ///
        /// `OnStopClient` temel ağ sınırı, <see cref="OnDestroy"/> güvenlik ağı (sahne unload gibi
        /// ağ olayı üretmeyen yollar için).
        /// </summary>
        /// <summary>
        /// Pano sunucuda doğdu = HUB'dayız = aktif kontrat yok. Temizlik burada yapılır çünkü
        /// "hub'a döndük" olayının tek güvenilir işareti panonun kendisi: F6 playtest reseti,
        /// rapordan dönüş ve normal akış hepsi buradan geçer. Temizlenmezse level'ı doğrudan
        /// Play'e alarak test eden biri bir önceki oturumun kontratını görürdü.
        /// </summary>
        public override void OnStartServer()
        {
            base.OnStartServer();
            ActiveContract.Clear();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            ReleasePanel();
        }

        private void OnDestroy() => ReleasePanel();

        /// <summary>
        /// Sahip olunan arayüzü bırakır. Panel paylaşılan bir kaynak: bugün sahnede tek pano var,
        /// birden fazla olursa "paneli ben mi açtım" takibi gerekir — o gün gelirse burası değişir.
        /// </summary>
        private void ReleasePanel()
        {
            if (_panel == null)
                return;

            // Close() imleç talebini de bırakıyor (CursorArbiter.Release), yani level'a taşınan
            // serbest imleç sorunu da burada kapanıyor.
            if (_panel.IsOpen)
                _panel.Close();

            _panel.HidePrompt();
            _promptShown = false;
            _panel = null;
        }

        private void Update()
        {
            // Panel kalıcı HUD'da yaşıyor ve pano sahnesinden ÖNCE ya da SONRA kurulmuş olabilir;
            // bulunana kadar aranır.
            if (_panel == null)
            {
                _panel = FindFirstObjectByType<ContractBoardPanel>();
                if (_panel == null)
                    return;
            }

            if (!IsUsableByLocalPlayer())
            {
                // Menzilden çıkıldıysa açık paneli de kapat — oyuncu uzaklaşmışken ekranda kalmasın.
                if (_panel.IsOpen)
                    _panel.Close();

                SetPrompt(false);
                return;
            }

            // İpucu YALNIZ panel kapalıyken görünür. Eskiden sadece "menzilde miyiz" izleniyordu:
            // panel açılınca ShowPrompt kendini bastırıyordu ama bayrak true kalıyordu, panel
            // kapanınca koşul değişmediği için ipucu bir daha çizilmiyordu — menzilden çıkıp
            // yeniden girmek gerekiyordu (sahada görüldü).
            SetPrompt(!_panel.IsOpen);

            if (Keyboard.current == null)
                return;

            // Panel açıkken ESC'yi BURASI karşılar ve paneli kapatır; InGameMenu bu sırada ESC'yi
            // bilerek yok sayıyor (CursorArbiter.AnyoneElseWantsCursor). Böylece iki panel asla
            // üst üste binmez ve ESC her yerde "geri" anlamına gelir.
            //
            // `UiEscapeGate` sıra bağımsızlığını tamamlıyor: biz menüden ÖNCE koşarsak paneli
            // kapatıp talebi bırakırız ve menü aynı karede ESC'yi kendi lehine yorumlayabilirdi —
            // tuşu tüketerek bunu engelliyoruz.
            if (_panel.IsOpen && Keyboard.current.escapeKey.wasPressedThisFrame
                && UiEscapeGate.TryConsume())
            {
                _panel.Close();
                return;
            }

            if (!Keyboard.current.eKey.wasPressedThisFrame)
                return;

            if (_panel.IsOpen)
            {
                _panel.Close();
                return;
            }

            // ÜSTÜMÜZDE AÇIK BAŞKA UI VARSA AÇMA. ESC menüsü açıkken panonun yanında E'ye
            // basmak iki paneli birden açıyordu; sonraki ESC'de hangisinin kapanacağı `Update`
            // sırasına kalıyordu. `UiEscapeGate` yalnız "ilk çağıran kazanır" der, ÖNCELİK kurmaz —
            // öncelik burada, açılış anında kuruluyor: menü açıkken oyun dünyasıyla etkileşim yok.
            if (CursorArbiter.AnyoneElseWantsCursor(_panel))
                return;

            _panel.Open(_contracts, InstanceFinder.IsServerStarted, RequestContract);
        }

        /// <summary>İpucunu yalnız DURUM DEĞİŞİNCE günceller — her kare SetActive çağırmamak için.</summary>
        private void SetPrompt(bool visible)
        {
            if (visible == _promptShown)
                return;

            _promptShown = visible;

            if (visible)
                _panel.ShowPrompt("E — Contract Panel");
            else
                _panel.HidePrompt();
        }

        /// <summary>
        /// Lokal oyuncu panoyu kullanabilir durumda mı: canlı, yakında ve ELİ BOŞ.
        /// Tabut taşırken false döner — E o an bırakma tuşudur (pazarlıksız, GDD 6.3/6.5).
        /// </summary>
        private bool IsUsableByLocalPlayer()
        {
            if (!IsLocalOwner(_localPlayer))
            {
                _localPlayer = null;

                if (Time.unscaledTime < _nextRebindTime)
                    return false;

                _nextRebindTime = Time.unscaledTime + _rebindInterval;

                PlayerGrabber[] players = FindObjectsByType<PlayerGrabber>(FindObjectsSortMode.None);
                foreach (PlayerGrabber p in players)
                {
                    if (!IsLocalOwner(p))
                        continue;
                    _localPlayer = p;
                    break;
                }

                if (_localPlayer == null)
                    return false;
            }

            if (_localPlayer.IsCarrying)
                return false;

            return (_localPlayer.transform.position - transform.position).sqrMagnitude
                   <= _interactRange * _interactRange;
        }

        /// <summary>
        /// NetworkObject null kontrolü ŞART: FishNet'in IsSpawned/IsOwner property'leri iç
        /// _networkObjectCache alanını null kontrolü OLMADAN dereference eder
        /// (NetworkBehaviour.cs:28) ve o alan ancak preinitialize sırasında atanır.
        /// </summary>
        private static bool IsLocalOwner(PlayerGrabber p)
        {
            return p != null && p.NetworkObject != null && p.IsSpawned && p.IsOwner;
        }

        private void RequestContract(int index) => ServerStartContract(index);

        /// <summary>
        /// RequireOwnership = false: pano bir SAHNE objesi, hiçbir client ona sahip değil. Yetki
        /// kontrolü sahiplikle değil, sunucunun kendi listesiyle doğrulamayla yapılır.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        private void ServerStartContract(int index, NetworkConnection conn = null)
        {
            // YALNIZ HOST başlatabilir (ekip kararı). Arayüzde butonu gizlemek yeterli değil —
            // istemci arayüzü atlatabilir, yetki kontrolü sunucuda olmalı. `IsLocalClient`,
            // sunucuda "bu bağlantı sunucunun kendi client'ı mı" sorusunu cevaplar
            // (NetworkConnection.QOL.cs:23), yani host modunda tam olarak host'u tanır.
            if (conn == null || !conn.IsLocalClient)
            {
                Debug.LogWarning("[ContractBoard] Kontratı yalnız host başlatabilir — istek reddedildi.", this);
                return;
            }

            if (_contracts == null || index < 0 || index >= _contracts.Length)
            {
                Debug.LogWarning($"[ContractBoard] Geçersiz kontrat dizini: {index}.", this);
                return;
            }

            ContractDefinition contract = _contracts[index];
            if (contract == null || !contract.IsPlayable)
            {
                Debug.LogWarning($"[ContractBoard] Kontrat {index} oynanabilir değil (sahne adı boş).", this);
                return;
            }

            NetworkSceneDirector director = FindFirstObjectByType<NetworkSceneDirector>();
            if (director == null)
            {
                Debug.LogError("[ContractBoard] NetworkSceneDirector bulunamadı — sahne geçişi yapılamıyor. " +
                               "Bootstrap sahnesindeki NetworkManager'da olmalı.", this);
                return;
            }

            // Seçimi sahne geçişinin ÖTESİNE taşı: level'daki mezar raporu buradan okuyacak.
            // Aynı level farklı merhumlarla oynanır, o yüzden künye level sahnesine gömülemez.
            ActiveContract.Set(contract);

            Debug.Log($"[ContractBoard] Kontrat başlıyor: {contract.ResolvedName} → '{contract.sceneName}'.", this);
            director.LoadNetworkScene(contract.sceneName);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, _interactRange);
        }
#endif
    }
}
