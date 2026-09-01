using UnityEngine;

namespace SunsetExpress.Player
{
    /// <summary>
    /// El IK'sı + gerilim uzaması (GDD 4.2 IK ayrımı, 4.3 kopma uyarısı): tabut tutulurken eller
    /// grab point'e KİLİTLENİR. Kollar ise yalnız KOPMAYA YAKLAŞINCA uzar — komik bir "elimden
    /// kayıyor" sinyali.
    ///
    /// Uzama kaynağı MESAFE DEĞİL GERİLİMDİR (ekip kararı 2026-08). Eskiden `mesafe / doğal kol boyu`
    /// idi ve bir geometri açığını kapatıyordu: kol yetişmiyordu, biz esnetiyorduk; oyuncuya hiçbir
    /// şey anlatmıyor, sürekli gerili duruyordu. Artık kopma uyarısı kademesinden türüyor, yani HUD
    /// ikonuyla AYNI sinyali paylaşır. Fare tekeriyle kaldır/indir aynı kararla oyundan kaldırıldı;
    /// `CarryHeight` sabittir ve kolun doğal erişimi içinde olmalıdır.
    ///
    /// PAZARLIKSIZ AYRIM: animasyon/IK fiziğe ASLA karışmaz. Bu script yalnızca kemik transform'larına
    /// yazar; joint, kütle, CoM veya kopma ölçümüne dokunmaz. Fizik tarafı grab point'i zaten
    /// bilir (ConfigurableJoint anchor'ı) — eller onu görsel olarak takip eder, tersi değil.
    ///
    /// NETWORK: IK her makinede LOKAL çözülür. İKİ AYRI GÖRSEL KANAL kullanır:
    /// • "hangi grab point" → `PlayerGrabber.CarryVisual` **SyncVar** (görsel STATE; mühendislik invariantlarında
    ///     belgelenmiş event-senkron istisnası — yalnız grab/bırak anında değişir)
    ///   • "uyarı kademesi" → `ObserversRpc` **event** (yalnız kademe DEĞİŞİNCE yayınlanır)
    /// Uzamanın ara değerleri hiçbir kanaldan geçmez, lokal yumuşatılır (GDD 12.2). Spectator
    /// kopyalarda da çalışır — hem eller havada kalmasın hem BAŞKASININ kolları uzarken görünsün.
    ///
    /// ÇALIŞMA ANI: LateUpdate — Animator kemikleri yazdıktan SONRA ezer. FishNet'in Graphics
    /// smoothing'i Update'te olduğu için omuz dünya konumu bu noktada güncel ve doğrudur.
    /// </summary>
    public sealed class PlayerArmStretchIK : MonoBehaviour
    {
        /// <summary>Tek kolun IK zinciri. Twist bone'lar bu zincire GİRMEZ — onlar ana kemiklerin
        /// child'ı olarak scale'i miras alır (Baran'a verilen rig kuralı).</summary>
        [System.Serializable]
        public sealed class ArmChain
        {
            [Tooltip("Üst kol kemiği (omuz eklemi). Zincirin kökü.")]
            public Transform UpperArm;
            [Tooltip("Ön kol kemiği (dirsek eklemi).")]
            public Transform ForeArm;
            [Tooltip("El kemiği — IK ucu. Hedefe bu oturur.")]
            public Transform Hand;

            [Tooltip("El yöneliminin, tabut çerçevesine göre düzeltme açısı (Euler, derece).\n" +
                     "IK kolu hedefe doğrultarken kemiğin KENDİ EKSENİ etrafındaki dönüşünü (roll) " +
                     "kontrol etmez — el o kontrolsüz roll'u miras alıp çarpık durur. Bu yüzden elin " +
                     "yönelimi ayrıca sürülür: taban yönelim tabutun çerçevesinden gelir, bu açı da " +
                     "avuç içini doğru tarafa çevirir.\n" +
                     "Sol ve sağ el AYNA olduğu için değerleri genelde farklıdır — Play modunda " +
                     "kaydırarak bul. (0,0,0) = ham çerçeve yönelimi.")]
            public Vector3 HandRotationOffset;

            [Tooltip("ZORUNLU. Dirsek pole target'ı (rig'deki Pole_Elbow_L/R) — dirseğin hangi yöne " +
                     "kırılacağını belirler. Rig'de yaşadığı için karakterle birlikte döner ve poz " +
                     "değiştikçe doğru kalır. Atanmazsa bu kol IK'sı ÇALIŞMAZ (fail-closed): yanlış " +
                     "yöne kırılmış dirsek yerine dokunulmamış animasyon tercih edilir.")]
            public Transform ElbowPole;

            [Tooltip("Elin grab point'e göre ofseti (m), TABUTA GÖRE tanımlı bir çerçevede:\n" +
                     "  X = DIŞA doğru (tabut merkezinden uzağa) — eli gövdenin dışına iter\n" +
                     "  Y = tabutun yukarısı — eli alt/üst kenara kaydırır\n" +
                     "  Z = tabutun boyu (baş-ayak) — iki eli birbirinden ayırmak için, sol/sağda zıt işaret\n" +
                     "DIŞA yönü her köşe için otomatik hesaplanır: grab point'ler dört köşede ve hepsi " +
                     "identity rotasyonlu olduğu için sabit lokal ofset iki köşede dışarı, ikisinde İÇERİ " +
                     "iterdi. Aynı değer artık dört köşede de doğru davranır.")]
            public Vector3 HandLocalOffset;

            [System.NonSerialized] public bool MissingPoleReported; // hata bir kez loglanır, her karede değil

            // Bind pose ölçümleri — İLK kullanımdan önce, scale yazılmadan alınır.
            [System.NonSerialized] public float RestUpperLength;
            [System.NonSerialized] public float RestForeLength;
            [System.NonSerialized] public int LengthAxisIndex; // üst kolun uzunluk ekseni (0=X,1=Y,2=Z)
            [System.NonSerialized] public Vector3 RestUpperLocalScale; // bind scale — uzama BUNUN ÜSTÜNE çarpılır
            [System.NonSerialized] public Vector3 RestHandLocalScale;  // el, uzamayı miras almasın diye
            [System.NonSerialized] public bool RestCaptured;
        }

        [Header("Mod")]
        [Tooltip("AÇIK  = tam IK: eller grab point'e kilitlenir, kol açıları ve el yönelimi kodla sürülür " +
                 "(animasyonun kol pozu EZİLİR). Eller tutamağı birebir takip eder ama iki el aynı noktaya " +
                 "gittiği için üst üste binebilir; pole/ofset/rotasyon ayarı gerektirir.\n\n" +
                 "KAPALI = yalnız uzama: kol pozu, el yönelimi ve parmaklar tamamen ANİMASYONDAN gelir; " +
                 "kod sadece kopmaya yaklaşınca kolu uzatır. Joint, grab point'i el anchor'ının 8 cm " +
                 "yarıçapında tuttuğu için iyi bir taşıma klibi elleri zaten doğru yere koyar. Eller " +
                 "tutamağı birebir takip etmez (~10-15 cm şaşabilir) ama üst üste binme, el rotasyonu ve " +
                 "mesh'e gömülme sorunları kaybolur.\n\n" +
                 "İkisini Play modunda karşılaştırıp karar ver.")]
        [SerializeField] private bool _solveArmIK = false;

        [Header("Kol Zincirleri (Baran rig'i)")]
        [SerializeField] private ArmChain _leftArm = new();
        [SerializeField] private ArmChain _rightArm = new();

        [Header("Uzama — kopma gerilimi görselleştirmesi (GDD 4.3)")]
        [Tooltip("Kopma uyarısı kademesi başına uzama çarpanı. Sıra: [0]=uyarı yok, [1]=~%50, " +
                 "[2]=~%65, [3]=~%80 (kopmak üzere) — eşikler CoffinProfile'da, burada değil.\n\n" +
                 "TASARIM (2026-08): uzama YALNIZ SON KADEMEDE devreye girer — [0][1][2] hepsi 1.0. " +
                 "Sebep sinyal ayrımı: HUD ikonu kademeli uyarıyı taşır ('dikkat'), kol ise son alarmı " +
                 "('gitti gidiyor'). İkisi aynı şeyi tekrarlamak yerine farklı şey söyler; kolun ani " +
                 "uzaması böylece belirgin bir VURUŞ olur, sürekli değişen bir gösterge değil.\n\n" +
                 "Sıçramanın sertliğini `_stretchLerpSpeed` belirler: 6'da ~0.17 sn'de tam uzar " +
                 "(hızlı ama görünür). Daha ani istiyorsan büyüt, daha yumuşak istiyorsan küçült.")]
        [SerializeField] private float[] _stretchByWarnLevel = { 1.0f, 1.0f, 1.0f, 2.0f };

        [Tooltip("Uzamanın hedefe yaklaşma hızı (1/sn). Kademe basamaklı gelir; bu yumuşatma onu " +
                 "akıcı yapar — ara değerler AĞDA taşınmaz, her makinede lokal hesaplanır (GDD 12.2).")]
        [SerializeField] private float _stretchLerpSpeed = 6f;

        [Header("Geçiş")]
        [Tooltip("Tutma/bırakma anında IK ağırlığının 0↔1 geçiş hızı. Yüksek = sert snap, düşük = " +
                 "eller yumuşakça tutamağa gider. Bırakınca kollar animasyona geri döner.")]
        [SerializeField] private float _blendSpeed = 8f;

        private PlayerGrabber _grabber;
        private float _weight; // 0 = saf animasyon, 1 = tam IK
        private Vector3 _leftTarget;
        private Vector3 _rightTarget;
        private Quaternion _frameRotation = Quaternion.identity;
        private bool _hasCachedTarget;
        private float _stretchCurrent = 1f; // kademeye doğru yumuşatılan anlık uzama çarpanı
        private bool _hadTarget;            // geçen karede tutuyor muydu
        private ushort _lastCarryGeneration; // geçen karenin tutuş nesli — asıl "yeni tutuş" ölçütü

        private void Awake()
        {
            _grabber = GetComponent<PlayerGrabber>();
        }

        private void LateUpdate()
        {
            // Hedef var mı? (owner/server lokal kaydından, spectator SyncVar'dan — ikisi de burada aynı)
            // NOT: grabPoint ÖNCEDEN atanır — && kısa devre yaparsa out parametresi yazılmamış sayılır (CS0165).
            Transform grabPoint = null;
            Transform coffinRoot = null;
            bool hasTarget = _grabber != null && _grabber.TryGetCarryGrabPoint(out grabPoint, out coffinRoot);

            // Hedefler tabuta göre tanımlı bir çerçevede ofsetlenir → tabut dönünce eller onunla döner.
            // Son geçerli hedef saklanır: bırakma anında hedef anında kaybolur ama ağırlık hâlâ
            // sönüyordur; saklamazsak eller o birkaç karede sıçrardı.
            if (hasTarget)
            {
                BuildOffsetFrame(grabPoint, coffinRoot, out Vector3 outward, out Vector3 up, out Vector3 along);
                _leftTarget = OffsetTarget(grabPoint, _leftArm.HandLocalOffset, outward, up, along);
                _rightTarget = OffsetTarget(grabPoint, _rightArm.HandLocalOffset, outward, up, along);

                // El yöneliminin TABANI: tabutun kendi çerçevesi. Böylece tabut dönünce eller onunla
                // döner ve iki el birbirine göre tutarlı kalır. Her kolun kendi düzeltme açısı bunun
                // üstüne biner (rig'in el ekseni ve sol/sağ aynası için).
                _frameRotation = Quaternion.LookRotation(along, up);
                _hasCachedTarget = true;
            }

            // Uzama hedefi kopma uyarısı kademesinden gelir (GDD 4.3). Kademe basamaklı geldiği için
            // burada yumuşatılır — ara değerler ağda taşınmaz, her makinede lokal hesaplanır.
            // Tutmuyorken hedef 1 (kol normal boyuna toplanır).
            float stretchTarget = 1f;
            if (hasTarget && _grabber != null && _stretchByWarnLevel != null && _stretchByWarnLevel.Length > 0)
            {
                int level = Mathf.Clamp(_grabber.GripWarningLevel, 0, _stretchByWarnLevel.Length - 1);
                stretchTarget = Mathf.Max(1f, _stretchByWarnLevel[level]);
            }
            // YENİ TUTUŞ kenarı: uzama SIFIRDAN başlar. E ile bırakıp hemen yeniden tutmak
            // ~0.17 sn'lik sönme penceresinin içine düşer; sıfırlamazsak taze tutuş, bir öncekinden
            // kalan "kopmak üzere" uzamasıyla açılır — yanlış alarm. Kopma anında kademe zaten 0'a
            // döndüğü için burada 1'e snap etmek doğru semantik: her tutuş normal kolla başlar.
            //
            // Kenar ölçütü boolean DEĞİL tutuş NESLİ: `hasTarget` render karesinde örneklenir, tutuş
            // kararı ise fizik adımında/ağ turunda verilir. Gözlemcide bırak+yeniden-tut iki LateUpdate
            // arasına sıkışırsa `hasTarget` iki karede de true kalır ve boolean kenar HİÇ görünmez.
            // Nesil server'da her tutuşta arttığı için aynı tabutun aynı köşesine regrab bile ayırt
            // edilir (PlayerGrabber.CarryGeneration).
            ushort generation = _grabber != null ? _grabber.CarryGeneration : (ushort)0;
            if (hasTarget && (!_hadTarget || generation != _lastCarryGeneration))
                _stretchCurrent = 1f;
            _hadTarget = hasTarget;
            _lastCarryGeneration = generation;

            _stretchCurrent = Mathf.MoveTowards(_stretchCurrent, stretchTarget,
                                                Mathf.Max(0.01f, _stretchLerpSpeed) * Time.deltaTime);

            // Ağırlık yumuşak geçer — tutma/bırakma anında eller snap etmesin.
            float previousWeight = _weight;
            _weight = Mathf.MoveTowards(_weight, hasTarget ? 1f : 0f, _blendSpeed * Time.deltaTime);

            if (_weight <= 0f || !_hasCachedTarget)
            {
                // Sıfıra YENİ indiysek son karenin uzaması kemiklerde asılı kalmasın: bir kez geri yaz.
                if (previousWeight > 0f)
                {
                    RestoreRestScale(_leftArm);
                    RestoreRestScale(_rightArm);
                }
                return; // saf animasyon; kemiklere dokunma
            }

            SolveArm(_leftArm, _leftTarget);
            SolveArm(_rightArm, _rightTarget);
        }

        /// <summary>
        /// El ofsetinin uygulanacağı çerçeveyi kurar. "Dışa" yönü grab point'in tabut merkezine göre
        /// YATAY konumundan türetilir — grab point'ler dört köşede ve hepsi identity rotasyonlu olduğu
        /// için lokal eksenleri "dışarı"yı göstermiyor; sabit lokal ofset iki köşede dışarı, ikisinde
        /// içeri iterdi. Dejenere durumda (grab point tam merkez ekseninde) tabutun kendi yan eksenine
        /// düşülür.
        /// </summary>
        private static void BuildOffsetFrame(Transform grabPoint, Transform coffinRoot,
                                             out Vector3 outward, out Vector3 up, out Vector3 along)
        {
            if (coffinRoot == null)
            {
                outward = grabPoint.right;
                up = grabPoint.up;
                along = grabPoint.forward;
                return;
            }

            up = coffinRoot.up;
            along = coffinRoot.forward;

            // Merkezden grab point'e giden vektörün, tabutun BOY eksenine dik bileşeni = "dışa".
            Vector3 fromCenter = grabPoint.position - coffinRoot.position;
            outward = Vector3.ProjectOnPlane(fromCenter, along);
            outward = Vector3.ProjectOnPlane(outward, up);

            outward = outward.sqrMagnitude > 0.000001f ? outward.normalized : coffinRoot.right;
        }

        private static Vector3 OffsetTarget(Transform grabPoint, Vector3 offset,
                                            Vector3 outward, Vector3 up, Vector3 along)
        {
            return grabPoint.position + outward * offset.x + up * offset.y + along * offset.z;
        }

        /// <summary>
        /// İki kemikli IK + uzama. Sıra önemli: ÖNCE uzama scale'i (kemik dünya boyları değişir),
        /// SONRA açı çözümü — böylece açılar gerçek (uzamış) boylarla hesaplanır.
        /// </summary>
        private void SolveArm(ArmChain arm, Vector3 target)
        {
            if (arm == null || arm.UpperArm == null || arm.ForeArm == null || arm.Hand == null)
                return;

            CaptureRestLengths(arm);
            if (arm.RestUpperLength <= 0.0001f || arm.RestForeLength <= 0.0001f)
                return; // dejenere rig — sessizce çık, animasyonu bozma

            // --- YALNIZ UZAMA MODU ---
            // IK kapalıysa poz animasyondan gelir; burada tek iş kolu uzatmak. Pole/hedef geometrisi
            // hiç gerekmez, o yüzden aşağıdaki kontrollerin ÖNÜNDE çıkılır.
            if (!_solveArmIK)
            {
                ApplyStretchScale(arm, Mathf.Lerp(1f, _stretchCurrent, _weight));
                return;
            }

            // Pole zorunlu (fail-closed): dirsek yönü bilinmiyorsa kola HİÇ dokunma. Yanlış tarafa
            // kırılmış dirsek, dokunulmamış animasyondan daha kötüdür.
            if (arm.ElbowPole == null)
            {
                if (!arm.MissingPoleReported)
                {
                    arm.MissingPoleReported = true;
                    Debug.LogError($"{name}: PlayerArmStretchIK — kol zincirine Elbow Pole atanmadı, " +
                                   "o kolun IK'sı devre dışı. Rig'deki Pole_Elbow_L/R'yi ata.");
                }
                return;
            }

            Vector3 shoulder = arm.UpperArm.position;

            // --- 0. GEÇERLİLİK ---
            // Dejenere geometri kontrolleri scale YAZILMADAN ÖNCE: eskiden uzama uygulanıp
            // sonra buradan çıkılıyordu ve kol, açısı çözülmemiş halde ölçekli kalıyordu — "geçersiz
            // pole'da kola HİÇ dokunma" fail-closed sözü o karelerde tutulmuyordu. Bu iki değer
            // uzamaya bağlı değil (scale kemiğin kendi konumunu değiştirmez), erken hesaplanabilir.
            Vector3 toTarget = target - shoulder;
            if (toTarget.sqrMagnitude < 0.0001f)
                return; // hedef omuzun üstünde

            Vector3 poleDir = arm.ElbowPole.position - shoulder;
            if (poleDir.sqrMagnitude < 0.000001f)
                return; // pole omuzla çakışık

            toTarget.Normalize();

            float distance = Vector3.Distance(shoulder, target);
            float naturalReach = arm.RestUpperLength + arm.RestForeLength;

            // --- 1. UZAMA ---
            // KAYNAK: kopma gerilimi (ekip kararı 2026-08) — mesafe DEĞİL.
            //
            // Eskiden uzama `mesafe / doğal kol boyu`ydu, yani bir GEOMETRİ AÇIĞINI kapatıyordu:
            // kol yetişmiyordu, biz esnetiyorduk. Oyuncuya hiçbir şey anlatmıyor, sürekli gerili
            // duruyordu. Artık uzama bir BİLGİ KANALI: kol yalnız kopmaya yaklaşınca gerilir ve
            // "elimden kayıyor" der (GDD 4.3 kopma uyarısı, %90 okunabilir kaos ilkesi).
            //
            // Bunun ön şartı: `CarryHeight` kolun DOĞAL erişimi içinde olmalı. Değilse kol taban
            // durumda da yetişemez ve uzama yine anlamını yitirir — profil tooltip'inde yazılı.
            //
            // Yetişemezse kol hedefe doğru bakıp orada kalır (aşağıdaki reach kelepçesi), uzayarak
            // ZORLAMAZ. Bu bilinçli: uzama gerilimin göstergesi, mesafenin çözümü değil.
            float stretch = _stretchCurrent;

            // Ağırlıkla harmanla — bırakırken uzama da geri toplanır.
            float appliedStretch = Mathf.Lerp(1f, stretch, _weight);

            // Scale YALNIZ üst kola yazılır: ön kol onun child'ı olduğu için aynı çarpanı MİRAS alır
            // (twist bone'lar da öyle — Baran'a verilen parent kuralının sebebi bu). İkisine birden
            // yazsaydık çarpan karesi alınırdı.
            ApplyStretchScale(arm, appliedStretch);

            float upperLength = arm.RestUpperLength * appliedStretch;
            float foreLength = arm.RestForeLength * appliedStretch;

            // --- 2. AÇI ÇÖZÜMÜ (kosinüs teoremi) ---
            // Üçgen dejenerasyonuna karşı kelepçe: hedef ne tam erişimin ötesinde ne de kemiklerin
            // farkından yakın olabilir (ikisi de acos'u patlatır).
            float reach = Mathf.Clamp(
                distance,
                Mathf.Abs(upperLength - foreLength) + 0.001f,
                (upperLength + foreLength) - 0.001f);

            // Bükülme düzlemi pole target'tan gelir — rig'de yaşadığı için karakterle döner ve poz
            // değiştikçe doğru kalır. Animasyondaki dirsek konumundan TAHMİN ETMİYORUZ: kol yukarı
            // uzanıp düzleştiğinde o tahmin belirsizleşip dirseği rastgele tarafa kırıyordu.
            Vector3 bendAxis = Vector3.Cross(toTarget, poleDir.normalized);
            if (bendAxis.sqrMagnitude < 0.000001f)
            {
                // Dejenere: pole hedef doğrultusuna paralel. Son çare hedefe DİK olmalı — aksi halde
                // bükülme ekseni geçersiz olur. UpperArm.up bunu garanti etmiyordu.
                bendAxis = Vector3.Cross(toTarget, Vector3.up);
                if (bendAxis.sqrMagnitude < 0.000001f)
                    bendAxis = Vector3.Cross(toTarget, Vector3.right);
            }
            bendAxis.Normalize();

            // Omuzdaki iç açı: üst kol ile hedef doğrultusu arasındaki sapma.
            float cosShoulder = (upperLength * upperLength + reach * reach - foreLength * foreLength) /
                                (2f * upperLength * reach);
            float shoulderAngle = Mathf.Acos(Mathf.Clamp(cosShoulder, -1f, 1f)) * Mathf.Rad2Deg;

            // Üst kolu hedef doğrultusundan POLE'A DOĞRU bükülme açısı kadar çevir.
            // İşaret POZİTİF: bendAxis = Cross(toTarget, poleDir) ve Unity'de AngleAxis(+θ, ekseni)
            // toTarget'ı poleDir'e doğru döndürür (AngleAxis(+90, +Z) * +X = +Y). Negatif verildiğinde
            // dirsek pole'un TERS tarafına kırılıyordu — poleları takmanın etkisi tersine dönüyordu.
            Vector3 desiredUpperDir = Quaternion.AngleAxis(shoulderAngle, bendAxis) * toTarget;
            ApplyBoneAim(arm.UpperArm, arm.ForeArm.position - shoulder, desiredUpperDir);

            // Ön kolu (dirsek yeni yerine oturdu) doğrudan hedefe doğrult.
            ApplyBoneAim(arm.ForeArm, arm.Hand.position - arm.ForeArm.position, target - arm.ForeArm.position);

            // --- 3. EL YÖNELİMİ ---
            // ApplyBoneAim minimum rotasyon uygular ve kemiğin KENDİ EKSENİ etrafındaki dönüşünü (roll)
            // kontrol etmez; el o kontrolsüz roll'u miras alıp çarpık duruyordu. Yönelimi burada açıkça
            // yazıyoruz. Sıra önemli: ön kol döndükten SONRA — dünya rotasyonu yazmak mirası ezer.
            Quaternion handTarget = _frameRotation * Quaternion.Euler(arm.HandRotationOffset);
            arm.Hand.rotation = Quaternion.Slerp(arm.Hand.rotation, handTarget, _weight);
        }

        /// <summary>Kemiği, ucunu hedef doğrultuya bakacak şekilde döndürür; IK ağırlığıyla harmanlanır.</summary>
        private void ApplyBoneAim(Transform bone, Vector3 currentDir, Vector3 desiredDir)
        {
            if (currentDir.sqrMagnitude < 0.000001f || desiredDir.sqrMagnitude < 0.000001f)
                return;

            Quaternion delta = Quaternion.FromToRotation(currentDir, desiredDir);
            bone.rotation = Quaternion.Slerp(bone.rotation, delta * bone.rotation, _weight);
        }

        /// <summary>
        /// Uzamayı kemiklere yazar. Yalnız uzunluk ekseninde — kol uzar, kalınlaşmaz. Değerler bind
        /// scale'in ÜSTÜNE çarpılır (mirror'lı rigler korunur).
        ///
        /// EL uzamayı miras ALMAMALI: üst kolu scale'leyince ön kol (istenen) ve el (istenmeyen) ikisi
        /// de mirası alır — el şişip deforme oluyordu. Elde mirası ters scale ile iptal ediyoruz. Kollar
        /// yalnız GERİLİYKEN uzadığı için zincir o anda düzdür, eksenler hizalıdır ve iptal doğru çalışır.
        /// </summary>
        private static void ApplyStretchScale(ArmChain arm, float stretch)
        {
            int axis = arm.LengthAxisIndex;

            Vector3 upper = arm.RestUpperLocalScale;
            upper[axis] = arm.RestUpperLocalScale[axis] * stretch;

            Vector3 hand = arm.RestHandLocalScale;
            hand[axis] = arm.RestHandLocalScale[axis] / Mathf.Max(0.0001f, stretch);

            // Gereksiz yazımı elerken karşılaştırma HEDEF ile KEMİĞİN GERÇEK scale'i arasında yapılır,
            // "son yazdığım değer" cache'iyle DEĞİL. Sebep: FBX import'unda
            // `removeConstantScaleCurves: 0` — yani Animator kemik scale'ini HER KARE klip değerine
            // geri yazıyor. Cache'e güvenen sürüm ilk kareden sonra "zaten 2.0 yazmıştım" deyip erken
            // dönüyordu; Animator'ın geri yazdığı 1.0 kalıcı oluyor, uzama tek kare görünüp kayboluyordu.
            // Gerçek scale'e bakınca hem Animator'a karşı her kare yeniden kazanıyoruz, hem de kol
            // 1.0'dayken (ömrünün çoğu) hiç yazmıyoruz — asıl istenen tasarruf zaten oydu.
            if (arm.UpperArm.localScale != upper)
                arm.UpperArm.localScale = upper;
            if (arm.Hand.localScale != hand)
                arm.Hand.localScale = hand;
        }

        /// <summary>Kemik ölçeklerini bind pose'a geri yazar — IK bırakıldığında/kapatıldığında
        /// deforme kemik kalmasın (ağırlık 0'da erken dönüş son kareyi geri almıyordu).</summary>
        private static void RestoreRestScale(ArmChain arm)
        {
            if (arm == null || !arm.RestCaptured)
                return;
            if (arm.UpperArm != null)
                arm.UpperArm.localScale = arm.RestUpperLocalScale;
            if (arm.Hand != null)
                arm.Hand.localScale = arm.RestHandLocalScale;
        }

        private void OnDisable()
        {
            // Taşırken component kapatılırsa/obje despawn olursa kemikler uzamış kalmasın.
            RestoreRestScale(_leftArm);
            RestoreRestScale(_rightArm);
            _weight = 0f;

            // Uzama da sıfırlanır: pooled/yeniden etkinleşen instance, uyarı kademesi 0 olsa
            // bile yüksek _stretchCurrent ile başlayıp yeni tutuşun ilk karelerinde SAHTE uzama
            // gösteriyordu. Hedef cache'i de geçersizleşir ki bayat çerçeveye çözülmesin.
            _stretchCurrent = 1f;
            _hasCachedTarget = false;
            _hadTarget = false;
            _lastCarryGeneration = 0;
        }

        /// <summary>
        /// Bind pose ölçümleri: kemik boyları + üst kolun UZUNLUK EKSENİ. Bir kez, henüz hiç scale
        /// yazmadan alınır. Awake'te değil ilk kullanımda: Awake anında Animator henüz pozlamamış
        /// olabiliyor ve yanlış boy ölçülürdü.
        ///
        /// Eksen elle GİRİLMEZ, ölçülür: kemikten child'ına giden yön kendi lokal uzayında hangi
        /// eksene en yakınsa o. Rig konvansiyonu (Blender Y, bazı pipeline'lar X) fark etmez, Baran'ın
        /// eksen cevabını beklemeye de gerek kalmaz.
        /// </summary>
        private static void CaptureRestLengths(ArmChain arm)
        {
            if (arm.RestCaptured)
                return;

            // Bind scale'ler SAKLANIR, sıfırlanmaz: mirror'lı riglerde kol kemiği -1 ölçekli olabilir;
            // Vector3.one yazmak o kolu ters çevirip rig'i bozardı. Uzama bu değerin ÜSTÜNE çarpılır.
            arm.RestUpperLocalScale = arm.UpperArm.localScale;
            arm.RestHandLocalScale = arm.Hand.localScale;

            arm.RestUpperLength = Vector3.Distance(arm.UpperArm.position, arm.ForeArm.position);
            arm.RestForeLength = Vector3.Distance(arm.ForeArm.position, arm.Hand.position);

            Vector3 localToChild = arm.UpperArm.InverseTransformDirection(
                arm.ForeArm.position - arm.UpperArm.position);
            arm.LengthAxisIndex = DominantAxis(localToChild);
            arm.RestCaptured = true;
        }

        /// <summary>Vektörün en büyük bileşeninin ekseni (0=X, 1=Y, 2=Z).</summary>
        private static int DominantAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az)
                return 0;
            return ay >= az ? 1 : 2;
        }
    }
}
