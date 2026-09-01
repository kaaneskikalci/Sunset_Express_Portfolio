using UnityEngine;

namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Tek bir kontrat (level) tanımı — ilan panosunda bir satır.
    ///
    /// GDD 8.1: "Oyun tek bir hikaye anlatmaz. Her level bağımsız bir kontrattır; hub'daki ilan
    /// panosundan seçilir." Kontratlar bu yüzden koda gömülmez, ASSET olarak yaşar: yeni kontrat
    /// eklemek yeni bir asset oluşturmaktır, kod değişmez (GDD 12.3'ün "tasarımcı kod olmadan
    /// varyant üretir" felsefesi).
    ///
    /// Şimdilik minimum alan seti tutuldu. GDD 8.2'deki zorluk kademesi burada var; varsayılan
    /// ceset (CorpseProfile), ücret ve lisans kilidi ileride ekonomi/ceset sistemleri bağlanınca
    /// eklenecek — bugün eklemek onları kullanmayan ölü alanlar üretirdi.
    /// </summary>
    [CreateAssetMenu(menuName = "Sunset Express/Kontrat", fileName = "Contract_")]
    public sealed class ContractDefinition : ScriptableObject
    {
        [Tooltip("Panoda görünen ad. Ör: \"Dağ Yolu\"")]
        public string displayName = "İsimsiz Kontrat";

        [Tooltip("Kısa kara mizah brief'i (GDD 3.1). Panoda ad altında gösterilir.")]
        [TextArea(2, 4)]
        public string brief;

        [Tooltip("GDD 8.2 zorluk kademesi (1 = tutorial, 5 = Zirge Tırmanışı).")]
        [Range(1, 5)]
        public int difficulty = 1;

        [Tooltip("Yüklenecek sahnenin ADI. Build Settings listesinde EKLİ olmalı, yoksa geçiş sessizce " +
                 "başarısız olur. Ayrıca sahnede PlayerSpawnPoint bulunmalı — yoksa herkes yedek " +
                 "konumda üst üste doğar.")]
        public string sceneName;

        /// <summary>Panoda gösterilecek ad — alan boş bırakılırsa asset adına düşer.</summary>
        public string ResolvedName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;

        /// <summary>Yüklenebilir mi — sahne adı boş bir kontrat panoda gösterilmemeli.</summary>
        public bool IsPlayable => !string.IsNullOrWhiteSpace(sceneName);
    }
}
