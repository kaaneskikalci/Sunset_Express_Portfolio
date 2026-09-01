using FishNet;
using UnityEngine;

namespace SunsetExpress.UI
{
    /// <summary>
    /// <see cref="CursorArbiter"/>'ı her kare çalıştıran tek sürücü. Kalıcı HUD'da yaşar
    /// (<see cref="HudBootstrap"/>), yani sahne değişse de imleç politikası kesintisiz uygulanır.
    ///
    /// Neden ayrı bir sürücü: imleci dayatma işi eskiden oyun içi menünün içindeydi. Ama menü
    /// "bir panel"dir, imleç politikasının sahibi değil — panel kapalıyken bile kuralın işlemesi
    /// gerekiyor (ör. ilan panosu açıkken kamera doğar ve imleci ezmeye çalışır).
    ///
    /// LateUpdate BİLİNÇLİ: tüm <c>Update</c>'ler talebini bildirdikten SONRA koşar, yani aynı kare
    /// içinde açılan/kapanan paneller doğru sonuçlanır ve bileşen sırasına bağımlılık kalmaz.
    /// </summary>
    public sealed class CursorArbiterDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            // Oturum dışında (ana menü) imleç kilitlenmez — orada fare zaten UI'ın.
            bool sessionActive = InstanceFinder.IsServerStarted || InstanceFinder.IsClientStarted;
            CursorArbiter.Enforce(sessionActive);
        }
    }
}
