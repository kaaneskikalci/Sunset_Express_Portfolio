using UnityEngine;

namespace SunsetExpress.Profiles
{
    /// <summary>
    /// Ceset varyantı = profil asset'i (GDD 12.3). Her varyant bir mekanik modifikatördür;
    /// tasarımcı kod olmadan yeni varyant üretir. Bkz. Docs/GDD/03-corpse-system.md (5.3 tablosu).
    /// Değerler başlangıç değerleridir; Aşama 0'da ayarlanır.
    /// </summary>
    [CreateAssetMenu(menuName = "Sunset Express/Corpse Profile", fileName = "CorpseProfile")]
    public sealed class CorpseProfile : ScriptableObject
    {
        [Header("Kimlik")]
        public string displayName = "Standart Merhum";

        [Header("Kütle & Ağırlık Merkezi (GDD 4.4, 5.3)")]
        [Tooltip("Ceset kütlesi (kg). Tabut toplam kütlesine eklenir. " +
                 "GDD 5.3: standart 80, obez ~180, sumo ağır, yaşlı kadın ~45.")]
        public float mass = 80f;

        [Tooltip("Mod A 1D kayma hızı çarpanı. GDD 4.4/5.3: sumo yüksek; Yumuşak İç Döşeme modülü düşürür (GDD 9.3).")]
        public float slideSpeedMultiplier = 1f;

        [Tooltip("CoM'un baş-ayak ekseninde uca ne kadar yüklendiği. Sumo = aşırı yüklü CoM. GDD 5.3.")]
        public float comBiasStrength = 1f;

        [Header("Kopma Modifikatörü (GDD 5.3)")]
        [Tooltip("CoffinProfile.grabBreakForce bununla çarpılır. Kaygan/terli obez = 0.7 (-%30); " +
                 "Firavun taş halka tutamaç = standarttan düşük. GDD 5.3.")]
        public float breakForceMultiplier = 1f;

        [Header("Varyant Bayrakları (GDD 5.3)")]
        [Tooltip("Firavun/Lahit: kapak MÜHÜRLÜ — ceset ASLA düşmez, kapak hasar sistemi devre dışı. GDD 5.3.")]
        public bool lidSealed = false;

        [Tooltip("Basketbolcu: tabuta sığmaz — kapak kapanmaz, ceset düşme riski kalıcı. GDD 5.3.")]
        public bool fitsInCoffin = true;

        [Tooltip("Rüzgar bölgelerinde yelken etkisi çarpanı. Yaşlı kadın hafif = yüksek savrulma. GDD 5.3.")]
        public float windSailFactor = 1f;

        [Header("'Ölmemiş Olabilir' Tekmesi (GDD 5.3)")]
        [Tooltip("Belirli ÖNGÖRÜLEBİLİR tetikleyicilerde tabuta anlık impuls uygular (rastgele DEĞİL). GDD 5.3, 5.4.")]
        public bool canKick = false;

        [Tooltip("Tekme impuls şiddeti. Buz Kutusu modülü sıklığı yarıya indirir (GDD 9.3).")]
        public float kickImpulse = 0f;
    }
}
