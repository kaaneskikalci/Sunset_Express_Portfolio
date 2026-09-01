using UnityEngine;

namespace SunsetExpress.Networking
{
    /// <summary>
    /// Sahnedeki oyuncu doğuş noktası işaretçisi. Tek işi "burada bir oyuncu doğabilir" demek;
    /// hiçbir mantık taşımaz, network objesi DEĞİLDİR.
    ///
    /// Neden sabit konum değil (GDD 12.2 host-authoritative spawn): 4 oyuncu aynı noktaya doğarsa
    /// Rigidbody'ler iç içe girer ve PhysX onları birbirinden ayırmak için fırlatır — oyun daha
    /// başlamadan kaos çıkar. Noktalar sahneye serpiştirilir, <see cref="NetworkSceneDirector"/>
    /// sırayla dağıtır.
    ///
    /// SAHİPLİK: işaretçileri sahneye yerleştirmek LEVEL işidir (Baran). Script yalnız bir etiket
    /// olduğu için taşınması/çoğaltılması güvenlidir — kod tarafında hiçbir şeye bağlı değil.
    /// </summary>
    public sealed class PlayerSpawnPoint : MonoBehaviour
    {
#if UNITY_EDITOR
        /// <summary>Sahnede görünür olsun — yoksa boş bir GameObject'i level içinde bulmak zor.</summary>
        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.9f, 0.35f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
            // Bakış yönü: oyuncu doğduğunda buraya döner.
            Gizmos.DrawRay(transform.position + Vector3.up * 0.9f, transform.forward * 0.8f);
        }
#endif
    }
}
