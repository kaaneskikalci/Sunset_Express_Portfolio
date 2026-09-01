using UnityEngine;

namespace SunsetExpress.Profiles
{
    /// <summary>
    /// Karakter hareket sabitleri. GDD 12.3 gereği fizik sabitleri ScriptableObject'te.
    /// Bkz. Docs/GDD/04-controls-camera.md (6.1, 6.2). Aşama 0'da tune edilecek başlangıç değerleri.
    /// </summary>
    [CreateAssetMenu(menuName = "Sunset Express/Player Profile", fileName = "PlayerProfile")]
    public sealed class PlayerProfile : ScriptableObject
    {
        [Header("Hareket (GDD 6.1)")]
        [Tooltip("Yatay hedef hız (m/s).")]
        public float MoveMaxSpeed = 5f;

        [Tooltip("Tick başına maksimum hız değişimi. YÜKSÜZ haldeki ivmelenme hissini bu belirler — " +
                 "kısa ama sıfır olmayan ivmelenme (GDD 6.1).")]
        // Initializer AKTİF ASSET ile eşit tutulur: 1.2'de kalmıştı ve yeni oluşturulan bir
        // profilde yüksüz eşik 4200 N olurdu — 2800'lük varsayılan kuvvet tavanı yüksüzken de bağlar,
        // yani yeni profil sessizce hantal başlardı. İki kadranın kenetli olmasının somut riski bu.
        public float MoveAccelPerTick = 0.8f;

        [Tooltip("KAS GÜCÜ TAVANI (Newton). GDD 4.5'in 'karakterin hareket kuvveti SABİTTİR' maddesinin " +
                 "koddaki karşılığı — taşırken hız çarpanı uygulamak yasaktır (yapay olur), ama kuvvetin " +
                 "sınırlı olması kuralın kendisidir: yavaşlama tabutun kütlesinden fiziksel olarak doğar.\n\n" +
                 "⚠ YÜKSÜZ HİSSİ BOZMAMASI İÇİN ALT SINIR VAR: bu değer `MoveAccelPerTick × kütle / " +
                 "tickDelta`'dan KÜÇÜK olursa tavan yüksüzken de devreye girer ve serbest hareket " +
                 "yavaşlar. 70 kg / 50 Hz / 0.8 için o sınır 2800 N. İlk yazımda 1400 girilmişti ve " +
                 "yüksüz ivmelenmeyi sessizce YARIYA düşürüyordu — playtest bunu " +
                 "'yeni model hantal' diye yorumlardı. 2800'de tavan yalnız YÜKLÜYKEN bağlar.\n\n" +
                 "⚠ AŞAĞIDAKİ TABLO KABA YAKLAŞIMDIR, kararlı hız değildir. 'Etkin kütle' " +
                 "oyuncu ile tabutun RİJİT bağlı olduğunu varsayar; oysa bağ 8 cm boşluklu bir YAY. " +
                 "Gerçekte oyuncu önce yayı gerer, sonra yay direnir; yatay direnç yoksa sistem " +
                 "geçici salınımdan sonra yine hedef hıza (5 m/s) ulaşır. Tablo, YAY GERİLDİKTEN VE " +
                 "yük aktarımı başladıktan SONRAKİ kaba geçici EKİP ivmesidir — gerçek ilk anda " +
                 "(8 cm boşluk tüketilirken) yalnız oyuncu ~40 m/s² hızlanır, tabut henüz yoktur. " +
                 "2800 N'lik sürekli direnç yay bölgesinde ~0.19 m ek sapma demektir; direnç kas " +
                 "gücünü aşarsa hedef hıza hiç ulaşılmaz.\n" +
                 "Ölçek (oyuncu 70 kg, tabut 100 gövde + 80 ceset, 2800 N):\n" +
                 "    yüksüz          →  70 kg  → 40.0 m/s²  (ivme tavanı bağlar — eskisiyle AYNI)\n" +
                 "    tek taşıyıcı    → 250 kg  → 11.2 m/s²  (belirgin ağır)\n" +
                 "    4 taşıyıcı      → 115 kg  → 24.3 m/s²  (yük bölüşülüyor, ekip hızlanıyor)\n" +
                 "    cesetsiz, tek   → 170 kg  → 16.5 m/s²  (gerçekten hafifledi)\n\n" +
                 "DAHA FAZLA AĞIRLIK İSTİYORSAN buradan değil `CoffinProfile.baseShellMass`'ten ayarla — " +
                 "kuvvet tavanı sayesinde tabut kütlesi ARTIK GERÇEKTEN İŞE YARIYOR, bu değişikliğin " +
                 "bütün amacı oydu. Bu değeri düşürmek yüksüz hareketi de bozar.\n" +
                 "0 girilirse güvenli varsayılana düşülür (eski asset koruması).")]
        public float MoveMaxForce = 2800f;

        [Range(0f, 1f)]
        [Tooltip("Havada kontrol — cimri tutulur (GDD 6.1: düşüşün taahhüt hissi korunur).")]
        public float AirControl = 0.25f;

        [Header("Zıplama (GDD 6.5)")]
        [Tooltip("Serbest zıplama hızı (m/s, VelocityChange — kütleden bağımsız).")]
        public float JumpForce = 5f;

        [Range(0f, 1f)]
        [Tooltip("Tutarken zıplama çarpanı. 1'de bile 'tek kişi = zayıf hop' FİZİKTEN doğar: tek zıplayanın " +
                 "momentumu tabut kütlesi + yerdeki taşıyıcıların çapasına yenilir; herkes AYNI ANDA zıplarsa " +
                 "momentum birleşir ve tabut belirgin yükselir (GDD 6.5). Düşük değerler grup zıplamasını da " +
                 "öldürür (0.4'te 2 kişi ~4 cm kaldırır — işe yaramaz). Exploit kontrolü level tasarımının işi.\n\n" +
                 "PLAYTEST (2026-08): 1.0'daydı, yani taşırken zıplama HİÇ zayıflamıyordu — GDD 6.5 " +
                 "'tek oyuncu yalnızca zayıf bir hop yapar (engel aşamaz)' diyor, oysa tam zıplama " +
                 "yapılıyordu. 0.65'e çekildi. Grup zıplamasının ETKİSİZ kalması ayrı bir sorundu ve " +
                 "ayrı çözüldü: impuls yayı germeye gidiyordu, `CoffinProfile.syncJumpSpringMultiplier` " +
                 "ile pencerede joint sertleştirildi. İkisini birlikte ayarla — bunu düşürüp ötekini " +
                 "yükseltmek 'solo zayıf / grup güçlü' eğrisini keskinleştirir.")]
        public float CarryJumpFactor = 0.65f;

        [Header("Görsel Dönüş (Katman 2, GDD 6.2)")]
        [Tooltip("Görsel gövdenin yatay velocity yönüne yumuşak dönüş hızı.")]
        public float RotationLerpSpeed = 12f;

        [Range(0f, 1f)]
        [Tooltip("Taşırken input yokken frenleme yetkisi çarpanı. GDD 6.1: 'tabut aşırı eğilirse joint " +
                 "üzerinden oyuncuyu sürükler' — sürüklenme TASARIM GEREĞİ. 1 = tam fren (joint'e karşı " +
                 "halat çekişmesi titremesi üretir), düşük = sürüklenirken sendeleme. Serbest gezerken etkisiz.\n\n" +
                 "PLAYTEST (2026-08): 0.15'te sürüklenme fazla geldi — ötekiler tabutu çekince duran " +
                 "oyuncu tamamen kargo oluyor, oyuna hiçbir etkisi kalmıyordu. Sürüklenmeyi KALDIRMIYORUZ " +
                 "(GDD 6.1'in istediği his o), ama oyuncu ayağını yere basabilmeli: 0.45. Bu 'ayak direme' " +
                 "yetkisi — ekip yanlış yöne çekerken sen frene basabilirsin, ama tek başına kazanamazsın.\n" +
                 "1'e yaklaştırma: joint'e karşı her tick hız sıfırlamak server'da stick-slip titremesi üretir.")]
        public float CarryIdleBrakeFactor = 0.45f;

        [Header("Tutma (GDD 4.2)")]
        [Tooltip("Tabutu tutabilmek için maksimum mesafe (m). Küçük = grab point'e yakın durmak zorunda " +
                 "(uzaktan tutup esnetmeyi önler). GDD 4.2.")]
        public float GrabRange = 1.2f;

        [Tooltip("Grab menzili ölçüm yüksekliği (m) — menzil ayaklardan (pivot) değil bu hizadan " +
                 "(göğüs/uzanma) ölçülür. Başkalarının taşıdığı baş üstü tabuta yerden uzanabilmek için.")]
        public float GrabReachHeight = 1.0f;

        [Tooltip("Baş üstü taşıma yüksekliği (m) — joint anchor'ının ayak pivotundan yukarı ofseti. SABİT: " +
                 "fare tekeriyle kaldır/indir (GDD 6.3) playtest sonrası ekip kararıyla oyundan KALDIRILDI " +
                 "(2026-08) — karakterin kol boyu baş üstü aralığını 1-2 kademeye sıkıştırıyordu ve dar " +
                 "aralık anlamlı his üretmiyordu. Kol uzaması artık yükseklikten değil KOPMA GERİLİMİNDEN " +
                 "türüyor (PlayerArmStretchIK).  KISIT: bu değer kolun DOĞAL erişimi içinde olmalı — " +
                 "uzamanın normalde SIFIR olup yalnız kopmaya yaklaşınca devreye girmesi buna bağlı. " +
                 "Ayrıca tabutun kafayı sıyırması için karakter boyu + 0.175 üstünde olmalı " +
                 "(1.42 m karakterde taban 1.595).")]
        public float CarryHeight = 1.7f;

        [Header("Fizik (GDD 12.3)")]
        [Tooltip("Joint zinciri stabilitesi: taşıma sırasında oyuncu + tabut tek bağlı sistemdir; zincirin " +
                 "EN DÜŞÜK iterasyonlu üyesi titrer. Tabutla aynı seviyede tutulur (12+). 0 = Unity default.")]
        public int SolverIterations = 12;

        [Tooltip("Solver velocity iterations (4+). 0 = Unity default.")]
        public int SolverVelocityIterations = 4;
    }
}
