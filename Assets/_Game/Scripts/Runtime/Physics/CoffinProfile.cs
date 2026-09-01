using UnityEngine;

namespace SunsetExpress.Profiles
{
    /// <summary>
    /// Tabut gövdesinin fizik sabitleri. GDD 12.3 gereği tüm fizik sabitleri
    /// ScriptableObject profillerinde tutulur, koda gömülmez.
    /// Bkz. Docs/GDD/02-coffin-physics.md ve 08-tech-architecture.md (12.3).
    /// Buradaki değerler başlangıç değerleridir; Aşama 0'da parametre iterasyonuyla ayarlanır.
    /// </summary>
    [CreateAssetMenu(menuName = "Sunset Express/Coffin Profile", fileName = "CoffinProfile")]
    public sealed class CoffinProfile : ScriptableObject
    {
        [Header("Rigidbody (GDD 4.1, 12.3)")]
        [Tooltip("Tabut gövdesinin (cesetsiz) kütlesi. Toplam kütle = bu + CorpseProfile.mass. " +
                 "GDD 4.1: toplam, oyuncu kütlesinin 2-3 katı hedeflenir (70 kg oyuncu -> ~150-200 kg tabut).")]
        public float baseShellMass = 100f;

        [Tooltip("Solver iteration override — joint stabilitesi için 12+. GDD 12.3.")]
        public int solverIterations = 12;

        [Tooltip("Solver velocity iteration override — 4+. GDD 12.3.")]
        public int solverVelocityIterations = 4;

        [Tooltip("Açısal hız tavanı — fizik patlaması sigortası. GDD 12.3.")]
        public float maxAngularVelocity = 7f;

        [Header("Grab Joint (GDD 4.2)")]
        [Tooltip("Linear limit — 'ip boyu' (m): tabutun grab point'i, oyuncunun el anchor'ının en fazla bu " +
                 "yarıçapında gezinebilir. Snap hissinin kaynağı. GDD 4.2: 0.1-0.2.")]
        public float jointLinearLimit = 0.08f;

        [Tooltip("linearLimitSpring — limitin yumuşaklığı. Sert joint fizik patlaması, yumuşak joint kontrollü " +
                 "esneme (GDD 4.2). Tabut, ağırlığı/spring kadar limitin ötesine sarkar (yük hissi).")]
        public float jointLinearLimitSpring = 15000f;

        [Tooltip("linearLimitSpring damper — sarkaç salınımını söndürür.")]
        public float jointLinearLimitDamper = 200f;

        [Tooltip("Tabut gövdesi açısal sönümü (Rigidbody.angularDamping). Yan taşımada tabutun yaw " +
                 "salınımını söndürür — 'römork sallanması' titremesinin ikinci ilacı. Unity default 0.05; " +
                 "0 girilirse dokunulmaz (eski asset koruması).")]
        public float bodyAngularDamping = 1f;

        [Tooltip("YAW-ÖZEL sönüm (1/sn) — yalnız yatay dönüş eksenine counter-torque; devrilme/slappy " +
                 "(X/Z) ETKİLENMEZ. Yaw serbest (hareket-tabanlı dönüş, GDD 6.4) ama sönümsüz olunca " +
                 "tabut momentum biriktirip serbest savruluyor ve 360° sonra elden kopuyordu. Bu sönüm " +
                 "dönüşü taşıyıcı hareketine bağlar: döndürünce döner, bırakınca durur. 0 = kapalı.")]
        public float yawDamping = 6f;

        [Tooltip("Makara süresi (sn): tutma anında linear limit mevcut mesafeden hedefe bu sürede küçülür — " +
                 "tabut yumuşakça baş üstüne çekilir, ani snap/fırlama olmaz (GDD 6.3 kaldır/indir dili).\n\n" +
                 "⚠ HER TAŞIYICI KENDİ RAMPASINI KOŞAR — dört kişi aynı anda tutunca dört yay birlikte " +
                 "çekiyor ve tabut sert sarsılıyor. Playtest'te (2026-08) bu sarsıntı cesedi fırlatıyordu; " +
                 "0.6'dan 0.9'a çıkarıldı. Taşıyıcı sayısına göre çarpan UYGULANMAZ (GDD 4.5) — rampanın " +
                 "kendisi yumuşatıldı, sayıya bakan bir mantık yok.")]
        public float hoistDuration = 0.9f;

        [Header("Kopma / Elden Kayma (GDD 4.3)")]
        [Tooltip("PAZARLIKSIZ: Unity breakForce KULLANILMAZ. Kopma her fizik adımının SONUNDA bu eşiğe " +
                 "karşı custom ölçülür — currentForce + anchor sapması birlikte. GDD 4.3, 12.3.")]
        public float grabBreakForce = 4500f;

        [Range(0f, 1f)]
        [Tooltip("Kopma uyarısı 1. kademe: gerilim bu orana ulaşınca ikon belirir (GDD 4.3). Merdiven kopma penceresine göre ayarlanır — uzama 3. kademede devreye girdiği için Severe ile 1.0 arasında tepki payı kalmalı.")]
        public float grabBreakWarnRatio = 0.50f;

        [Range(0f, 1f)]
        [Tooltip("Kopma uyarısı 2. kademe — 'kayıyor'. HUD burada titremeyi ve rengi sertleştirir " +
                 "(GDD 13.2). 0 girilirse güvenli varsayılana düşülür (eski asset koruması).")]
        public float grabBreakWarnRatioMedium = 0.65f;

        [Range(0f, 1f)]
        [Tooltip("Kopma uyarısı 3. kademe — 'kopmak üzere'. Son uyarı penceresi. " +
                 "0 girilirse güvenli varsayılana düşülür (eski asset koruması).")]
        public float grabBreakWarnRatioSevere = 0.80f;

        [Tooltip("Kopma anchor sapması eşiği (m): el ↔ grab point mesafesi ip boyunu bu kadar aşarsa tutuş " +
                 "kopar. currentForce tek başına güvenilmez — ikisi BİRLİKTE doğrulanır (GDD 4.3, 12.3).\n\n" +
                 "⚠ BU DEĞER `jointLinearLimitSpring` VE TOPLAM KÜTLEYE BAĞLIDIR, bağımsız ayarlanamaz. " +
                 "Tabut durağan haldeyken yerçekimi zaten bir sapma üretir:\n" +
                 "    durağan sapma = toplam ağırlık / spring\n" +
                 "Bu sapma kopma bütçesinin bir kısmını KALICI olarak tüketir — ceset ne kadar ağırsa " +
                 "tutuş o kadar kopmaya yakın başlar. Bu bir yan etki değil, GDD 5.3'ün ceset varyantı " +
                 "mekaniğinin yönü (ağır ceset = dar kopma marjı).\n\n" +
                 "Kural: durağan haldeki gerilim ~0.4-0.5 olsun →  grabBreakDeviation ≈ 2 × (ağırlık / spring)\n" +
                 "Ölçüm (2026-08): kütle 180 kg (100 gövde + 80 ceset) → ağırlık ~1766 N, spring 15000 → " +
                 "TEK taşıyıcıda durağan sapma 0.118 m. Seçilen 0.30 ile eşikler ağırlığın katı olarak:\n" +
                 "    durağan 0.118 m (1.00×) · uyarı-1 0.150 m (1.27×) · uyarı-2 0.195 m (1.66×)\n" +
                 "    uyarı-3 0.240 m (2.04×) · KOPMA 0.300 m (2.55×)   → tek taşıyıcı durağan gerilim 0.39\n" +
                 "Not: yük taşıyıcılara BÖLÜNÜR — 2 kişide durağan sapma ~0.059 m (gerilim ~0.20), yani ağır " +
                 "ceset 2 kişiyi durağan haldeyken zorlamaz; zorluk HAREKETTE (ivme, dönüş, tümsek) çıkar. " +
                 "Ceset varyantı zorluğunun kalibrasyonu playtest işidir, bu tablodan türetilemez.\n" +
                 "Spring veya kütle değişirse BU SAYIYI YENİDEN HESAPLA.")]
        public float grabBreakDeviation = 0.30f;

        [Tooltip("Kopma sonrası yeniden tutabilme cooldown'ı (sn). GDD 4.3.")]
        public float regrabCooldown = 0.5f;

        [Tooltip("Senkron zıplama impuls penceresinde kopma eşiği bu çarpanla geçici yükseltilir — " +
                 "her 4'lü zıplamada toplu elden kayma tetiklenmesini önler. GDD 6.5.")]
        public float syncJumpBreakForceMultiplier = 2f;

        [Tooltip("Zıplama impuls penceresi süresi (sn): taşıyıcının zıplamasından sonra kopma eşiğinin " +
                 "ve joint sertliğinin yüksek kaldığı süre. GDD 6.5 fizik notu.")]
        public float syncJumpBreakWindow = 0.4f;

        [Tooltip("Zıplama penceresinde linear limit YAYININ çarpanı (damper de birlikte ölçeklenir).\n\n" +
                 "Playtest (2026-08): 4'lü senkron zıplama teoride çalışıyor ama kazanılan yükseklik " +
                 "hissedilmiyordu — zıplama oyuncuya VelocityChange olarak biniyor, tabut ise 8 cm boşluk + " +
                 "yumuşak yay üzerinden takip ediyor, yani impulsun çoğu tabutu kaldırmak yerine yayı " +
                 "germeye gidiyordu. Pencerede yay sertleşince impuls tabuta aktarılır.\n\n" +
                 "TAŞIYICI SAYISI ÇARPANI DEĞİLDİR (GDD 4.5): çarpan her taşıyıcıda aynı, bir kişi de " +
                 "zıplasa dört kişi de zıplasa joint aynı oranda sertleşir — birleşme fizikte olur. " +
                 "Yükselt = zıplama daha 'kaskatı' ve yüksek; düşür = daha yumuşak ama etkisiz. " +
                 "1 veya altı girilirse güvenli varsayılana düşülür.")]
        public float syncJumpSpringMultiplier = 3f;

        [Header("Ceset Mod A — Kayma & CoM (GDD 4.4, 5.1)")]
        [Tooltip("Cesedin tabut içinde baş-ayak ekseninde (lokal Z) kayabileceği maksimum mesafe (± m).")]
        public float corpseSlideRange = 0.6f;

        [Tooltip("Kaymanın başladığı eğim (derece) — statik sürtünme eşiği. GDD 6.8: 'o 10 derece, cesedin " +
                 "kaymaya başladığı andır' — okunabilirlik eşiği, altında ceset kımıldamaz.")]
        public float corpseTiltThreshold = 10f;

        [Tooltip("Kayma ivmesi tabanı (m/s², tam dik eğimde). Tam kayma ~1.5-2 sn hedefi (GDD 4.4: yavaş, " +
                 "oyuncular fark edip tepki verebilsin). Ceset varyantı slideSpeedMultiplier ile çarpar.")]
        public float corpseSlideAccel = 1.2f;

        [Tooltip("Kayma sönümü (1/sn) — düşük = kaygan ceset, yüksek = yapışkan.")]
        public float corpseSlideDamping = 2f;

        [Tooltip("Mod B çıkış eşiği (derece): kapak AÇIKKEN (mandal bırakılmış veya kapak parçalanmış) " +
                 "tabut bu kadar yatarsa ceset düşer — kayıp KALICIDIR (GDD 5.1, 3.4). Mandal eşiğinden " +
                 "(45°) belirgin yüksek olmalı ki 'kapak açıldı' ile 'ceset gitti' arasında tepki penceresi kalsın.")]
        public float corpseExitTiltAngle = 60f;

        [Tooltip("Mod B kök (pelvis) düzeltme yayını aralığı (sn): server, ragdoll UYUYANA kadar bu " +
                 "aralıkla poz yayınlar; client yerel ragdoll'unu yumuşakça hedefe çeker (GDD 12.2: " +
                 "yalnız pelvis + anahtar kemik senkronu). 0.2 = 5 Hz — dekor için bol yeterli.")]
        public float corpseSyncInterval = 0.2f;

        [Header("Kapak Mandalı (GDD 5.2)")]
        [Tooltip("Mandalın bırakıldığı tabut yatış açısı (derece). Zayıf mandal — okunabilir eşik. GDD 5.2.")]
        public float lidOpenAngleThreshold = 45f;

        [Tooltip("Mandalı bırakan darbe impulsu (N·s) — 'sert darbe alırsa kapak açılır' (GDD 5.2). " +
                 "Referans: 180 kg tabutun ~1 m düşüşü ≈ 800 N·s.")]
        public float lidImpactImpulseThreshold = 400f;

        [Tooltip("Yeniden kilitlenme yatış açısı (derece): tabut bu kadar dike döndüyse VE kapak kapalı " +
                 "açıdaysa mandal otomatik kilitlenir — kapak krizi atlatılabilir panik anıdır, kalıcı ceza değil.")]
        public float lidRelatchAngle = 12f;

        [Tooltip("Mandal durum değişimleri arası minimum süre (sn) — eşik sınırında aç-kapa titremesini önler.")]
        public float lidRelatchCooldown = 0.75f;

        [Tooltip("Darbe hafızası (sn): bir darbe ancak bu kadar süre içinde mandal kararına girebilir. " +
                 "Okunabilirlik sigortası — 0.7 sn önceki bayat darbe ortam sakinken mandalı 'gecikmeli' açamaz.")]
        public float lidImpactMemory = 0.2f;

        [Tooltip("Ceset fırlatma koşulunun KESİNTİSİZ sürmesi gereken süre (sn). Kapak açık + eğim " +
                 "eşik üstü koşulu bu kadar sürmeden ceset düşmez; koşul kesilirse sayaç sıfırlanır.\n\n" +
                 "Playtest (2026-08): iki koşul da ANLIK okunuyordu, yani TEK BİR şiddetli fizik karesi " +
                 "cesedi kalıcı olarak düşürüyordu — birden fazla oyuncu aynı anda kaldırınca hoist " +
                 "rampaları birleşip tabutu bir kare sarsıyor ve ikisi birden aşılıyordu. Oyuncunun " +
                 "göremediği, tepki veremediği bir kayıp. 'Ceset kaybı KALICIDIR' pazarlıksız olduğu " +
                 "için (GDD 3.4, 5.1) tetikleyici OKUNAKLI olmalı (GDD 1.4).\n\n" +
                 "Saniye cinsinden tutulur, tick'e çevrilir — adım sayısı olarak gömülürse tick rate " +
                 "değişince gerçek süre sessizce kayardı. 0 girilirse güvenli varsayılana düşülür.")]
        public float corpseEjectHoldDuration = 0.2f;

        [Tooltip("Kapak menteşesinin yay sertliği (SAĞLAM tabutta). Kapağın kapalı konuma dönme isteği — " +
                 "yüksek = sıkı oturur, sallanmaz; düşük = lasgın.\n\n" +
                 "Playtest (2026-08): prefab'da menteşe yayı KAPALIYDI, kapak 0-110° arası tamamen serbest " +
                 "sallanıyordu — ne sıkılık vardı ne gevşeyecek bir şey. 0 girilirse güvenli varsayılana düşülür.")]
        public float lidHingeSpring = 120f;

        [Tooltip("Kapak menteşesi sönümü. Düşük = kapak zıplayarak salınır (okunamaz), yüksek = ağır ve " +
                 "sünger gibi. 0 girilirse güvenli varsayılana düşülür.")]
        public float lidHingeDamper = 12f;

        [Range(0f, 1f)]
        [Tooltip("Menteşenin hasarla NE KADAR gevşeyeceği (GDD 4.6). 0 = hasar menteşeyi hiç etkilemez; " +
                 "0.8 = tam hasarda yay %20'sine düşer, kapak lasgın savrulur; 1 = tam hasarda menteşe " +
                 "tamamen serbest.\n\n" +
                 "Kapağın 'hurdaya dönme' eğrisi budur — tabut düştükçe ve darbe aldıkça kapağın giderek " +
                 "daha rahat savrulması buradan gelir. Mandal EŞİĞİNİN ayrı bir düşüşü var " +
                 "(damageLidThresholdFactor); ikisi birlikte 'kapak artık tutmuyor' hissini kurar.")]
        public float lidHingeDamageLoosen = 0.8f;

        [Range(0f, 1f)]
        [Tooltip("Hasar 1.0'dayken açılma eşiğinin düşme oranı (GDD 4.6: hasarlı tabutta kapak kolay açılır). " +
                 "0.6 = tam hasarda eşik %60 düşer. Hasar sistemi Damage01'i doldurunca etkinleşir.")]
        public float damageLidThresholdFactor = 0.6f;

        [Tooltip("Kapağın 'kapalı' sayıldığı hinge açısı (derece) — re-latch bu açının altında kilitler.")]
        public float lidClosedAngle = 8f;

        [Tooltip("Ceset düşüşü (Mod B) için kapağın FİZİKSEL olarak en az bu kadar açık olması gerekir " +
                 "(derece). Mandal bırakılmış ama kapak kapalı duruyorsa ceset kapalı kapaktan geçemez.")]
        public float lidEjectMinOpenAngle = 25f;

        [Header("Hasar (GDD 4.6)")]
        [Tooltip("PAZARLIKSIZ: Tabut hiçbir zaman tamamen parçalanmaz. Bu sayaç maks olunca YALNIZCA " +
                 "kapak parçalanır; gövde her zaman taşınabilir kalır (soft-lock imkansız). GDD 4.6.")]
        public float damageMax = 100f;

        [Tooltip("Hasar saymaya başlayan minimum darbe impulsu (N·s) — altındaki temaslar (oyuncu " +
                 "sürtünmesi, hafif çarpma) hasar üretmez. Referans: 1 m düşüş ≈ 800 N·s.")]
        public float minDamageImpulse = 300f;

        [Tooltip("İmpuls → hasar çevrim katsayısı: hasar = (impuls - min) × bu değer. " +
                 "0.05'te 1 m'lik düşüş ≈ 25 hasar → 4 sert düşüş = maks (kapak parçalanır).")]
        public float damageImpulseScale = 0.05f;

        [Header("Sal Modu (GDD 4.7)")]
        [Tooltip("Batmış hacme orantılı kaldırma kuvveti katsayısı. GDD 4.7.")]
        public float buoyancyCoefficient = 1f;

        [Tooltip("Sala aynı anda binebilecek maks oyuncu. GDD 4.7: 2 (Şamandıra Kiti modülüyle 3 — GDD 9.3).")]
        public int maxRaftRiders = 2;
    }
}
