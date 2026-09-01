namespace SunsetExpress.GameLoop
{
    /// <summary>
    /// Cenaze raporu — kontrat bitince ekibe gösterilen özet (GDD 3.1 "Gömme", 4.6).
    ///
    /// SUNUCUDA DOLDURULUR, client'ta yalnız gösterilir: hasar ve ceset durumu sunucu-otoriter
    /// verilerdir (<see cref="Coffins.CoffinDamage"/>, <see cref="Coffins.CorpseSlide"/>), client'ın
    /// kendi kopyasından türetmesi ekranlar arası tutarsızlık üretirdi.
    ///
    /// ⚠ EKSİK OLAN BİLİNÇLİDİR: ücret, bonuslar, suçluluk istatistikleri ve "Ayın Elemanı"
    /// (GDD 3.1 "Ödeme ve Hesaplaşma", GDD 9/10) burada YOK. O dosyalar `kismen-acik` — ekonomi
    /// formülü ve istatistik seti ekip kararı bekliyor ve varsayım yapmak yasak. Buradaki üç alan
    /// mevcut sunucu verisinden doğrudan okunur, hiçbir açık soruya dayanmaz. Karar çıkınca alanlar
    /// bu struct'a eklenir; panel zaten satır tabanlı çiziyor.
    /// </summary>
    public struct ContractReport
    {
        /// <summary>Kontrat adı — tanımsızsa sahne adı kullanılır.</summary>
        public string ContractName;

        /// <summary>
        /// Merhumun künyesi — <see cref="ContractDefinition.brief"/>'ten olduğu gibi gelir
        /// (ölüm sebebi, boy, mevki…). Kara mizah metni GDD 3.1'de zaten akışın parçası ve
        /// kontrat asset'inde tasarımcı tarafından yazılıyor; rapor onu tekrar üretmez, gösterir.
        ///
        /// Ceset PROFİLİ (<see cref="Profiles.CorpseProfile"/>) burada kullanılmıyor: o fizik
        /// varyantını tanımlar (kütle, kayma, mühürlü kapak) ve adı "Standart Merhum" gibi teknik
        /// bir etiket. Oyuncunun okuduğu künye kontratta yazılı olan.
        /// </summary>
        public string Brief;

        /// <summary>Level'a girişten teslime kadar geçen süre (sn).</summary>
        public float Duration;

        /// <summary>Tabut hasarı 0-1 (GDD 4.6).</summary>
        public float CoffinDamage01;

        /// <summary>
        /// Ceset tabutla birlikte teslim edildi mi. False = yolda düştü ve KALICI olarak kayboldu
        /// (GDD 3.4 pazarlıksız). Rapora utanç satırı olarak yazılır.
        /// </summary>
        public bool CorpseDelivered;
    }
}
