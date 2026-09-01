using SunsetExpress.Player;
using UnityEngine;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Ölümcül bölge (GDD 3.4): uçurum dibi, nehir akıntısı sonu, lav. İçine giren oyuncu
    /// <see cref="PlayerRespawnCoordinator"/>'a bildirilir ve tabutun yanında yeniden doğar.
    ///
    /// TASARIMCI ARACI: level'a elle yerleştirilen tetikleyici hacimler asıl yöntemdir — "ölümcül"
    /// olan yer tasarım kararıdır, uçurum dibiyle nehrin sonu aynı Y'de olmak zorunda değil.
    /// Koordinatörde ayrıca bir Y eşiği güvenlik ağı var: hacim koymayı unutan level yüzünden
    /// oyuncu sonsuza kadar düşmesin.
    ///
    /// SUNUCU-OTORİTER: karar yalnız server'da verilir. Client'ta tetikleyici ateşlense bile
    /// hiçbir şey yapılmaz — ölüm kararı istemciye bırakılmaz.
    ///
    /// Collider `Is Trigger` OLMALI. Tabut da bu bölgeye düşebilir ama tabut kaybı AYRI bir yol
    /// (ekip son checkpoint'e döner, hasar artar — GDD 3.4); o dinlenme mezarlığı sistemiyle
    /// birlikte gelecek, burada yalnız oyuncu ele alınır.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class LethalZone : MonoBehaviour
    {
        private PlayerRespawnCoordinator _coordinator;

        private void OnTriggerEnter(Collider other)
        {
            // Koordinatör kalıcı HUD kökünde yaşıyor ve bu sahneden önce ya da sonra kurulmuş
            // olabilir; bulunana kadar aranır.
            if (_coordinator == null)
            {
                _coordinator = FindFirstObjectByType<PlayerRespawnCoordinator>();
                if (_coordinator == null)
                    return;
            }

            // Çarpan collider oyuncunun ÇOCUĞU olabilir (kapsül kökte ama ileride değişebilir).
            PlayerController player = other.GetComponentInParent<PlayerController>();
            if (player == null)
                return;

            _coordinator.ReportLethalContact(player);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Collider col = GetComponent<Collider>();
            if (col == null)
                return;

            // Ölümcül hacimler level'da GÖRÜNÜR olmalı — Baran yerleştirirken nerede olduğunu
            // bilmeli. Seçili değilken de çizilir, bilinçli.
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.25f);
            Bounds b = col.bounds;
            Gizmos.DrawCube(b.center, b.size);
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.8f);
            Gizmos.DrawWireCube(b.center, b.size);
        }
#endif
    }
}
