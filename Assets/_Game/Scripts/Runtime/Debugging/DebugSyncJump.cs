using FishNet;
using SunsetExpress.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SunsetExpress.Debugging
{
    /// <summary>
    /// Aşama 0 test aracı (GDD 15.1: 4'lü senkron zıplama impuls testi). Tek test makinesinde birden
    /// fazla pencereye aynı anda Space basılamaz; F9, server'da TABUTU TUTAN tüm taşıyıcılara aynı
    /// tick'te zıplama impulsu uygular — mükemmel senkronu simüle eder. Oyun mekaniği DEĞİLDİR;
    /// vertical slice öncesi silinir.
    /// </summary>
    public sealed class DebugSyncJump : MonoBehaviour
    {
        private void Update()
        {
            if (Keyboard.current == null || !Keyboard.current.f9Key.wasPressedThisFrame)
                return;
            if (!InstanceFinder.IsServerStarted)
                return; // impuls yalnızca otoritede anlamlı (host penceresinde bas)

            int count = 0;
            foreach (PlayerGrabber g in FindObjectsByType<PlayerGrabber>(FindObjectsSortMode.None))
            {
                if (!g.IsCarrying)
                    continue;
                PlayerController pc = g.GetComponent<PlayerController>();
                if (pc != null)
                {
                    pc.Debug_ExternalJump();
                    count++;
                }
            }
            Debug.Log($"[DebugSyncJump] {count} taşıyıcıya senkron zıplama impulsu uygulandı.");
        }
    }
}
