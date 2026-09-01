using System.Collections.Generic;
using UnityEngine;

namespace SunsetExpress.UI
{
    /// <summary>
    /// İmlecin TEK sahibi. Hiçbir bileşen `Cursor.lockState`'e doğrudan yazmaz; yalnızca
    /// <see cref="Request"/> / <see cref="Release"/> ile niyetini bildirir, uygulamayı
    /// <see cref="Enforce"/> yapar.
    ///
    /// Neden merkezî: imlece yazan üç yer vardı (oyun içi menü, ilan panosu paneli, kamera) ve
    /// birbirlerini eziyorlardı. Kamera her sahne geçişinde yeniden doğduğu için (oyuncular
    /// despawn/respawn oluyor) açık bir paneli tıklanamaz hale getirebiliyordu.
    ///
    /// SİMETRİ ŞART: hakem yalnız "talep varsa kilitleme" deseydi, sonradan doğan bir
    /// bileşen imleci kilitleyip serbest bırakan olmadığı için paneli ölü bırakırdı. Bu yüzden
    /// <see cref="Enforce"/> İKİ YÖNÜ de her kare dayatır: talep varsa serbest, yoksa kilitli.
    ///
    /// Sayaç değil KÜME: aynı sahip iki kez Request çağırsa bile tek kayıt olur, bir Release yeter.
    /// Sayaçta kaçırılan bir Release imleci sonsuza dek serbest bırakırdı. Sahipler
    /// `OnDisable`/`OnDestroy`'da Release çağırmalıdır — `object` parametresi Unity'nin fake-null'ına
    /// dönüşmediği için kümeden çıkarma atlanmaz. Yeni sahip eklersen bu disiplini koru;
    /// <see cref="PurgeDeadOwners"/> yalnızca SİGORTADIR, doğru yol Release çağırmaktır.
    /// </summary>
    public static class CursorArbiter
    {
        private static readonly HashSet<object> Owners = new();

        /// <summary>
        /// YOK EDİLMİŞ sahipleri kümeden atar. FAIL-SAFE, normal yol değil.
        ///
        /// Neden şart: hakem artık SİMETRİK — sahip varken imleç her kare serbest bırakılıyor — ve
        /// `OrbitCamera` de buna saygı duyuyor, yani yeni doğan bir kamera imleci artık zorla geri
        /// ALMIYOR. Eskiden o davranış kazara bir kurtarma yoluydu; bilinçli olarak kapatıldı.
        /// Sonuç: TEK BİR kaçırılmış `Release`, imleci kalıcı serbest ve kamerayı kalıcı ölü bırakır
        /// ve oyunun çıkışı yoktur. Bugünkü iki sahip de `OnDisable`/`OnDestroy`'da bırakıyor, ama
        /// bu sınıf "bir gün biri unutur"a karşı sigorta olmalı — oyunu oynanamaz kılan bir riski
        /// disipline emanet etmeyiz.
        ///
        /// Yalnız `UnityEngine.Object` sahipleri denetlenir: fake-null kontrolü ancak onlarda
        /// anlamlıdır. Saf C# nesnesi sahip olursa (bugün yok) kendi Release'inden sorumludur.
        /// </summary>
        private static void PurgeDeadOwners()
        {
            if (Owners.Count == 0)
                return;

            Owners.RemoveWhere(o => o is Object unityOwner && unityOwner == null);
        }

        /// <summary>Herhangi bir UI şu an imleci istiyor mu.</summary>
        public static bool AnyoneWantsCursor
        {
            get
            {
                PurgeDeadOwners();
                return Owners.Count > 0;
            }
        }

        /// <summary>
        /// BAŞKA biri imleci istiyor mu. Bir UI'ın "üstümde açık başka pencere var mı" sorusunu
        /// sorabilmesi için: ESC menüsü, ilan panosu açıkken ESC'ye tepki vermemeli — iki panel
        /// üst üste binerse okunamaz hale geliyor.
        /// </summary>
        public static bool AnyoneElseWantsCursor(object self)
        {
            PurgeDeadOwners();

            foreach (object owner in Owners)
            {
                if (!ReferenceEquals(owner, self))
                    return true;
            }
            return false;
        }

        /// <summary>İmleci talep et (panel açılırken).</summary>
        public static void Request(object owner)
        {
            if (owner != null)
                Owners.Add(owner);
        }

        /// <summary>Talebi bırak (panel kapanırken, OnDisable/OnDestroy dahil).</summary>
        public static void Release(object owner)
        {
            if (owner != null)
                Owners.Remove(owner);
        }

        /// <summary>
        /// İmleç durumunu dayatır — her kare, iki yönde de.
        ///
        /// Serbest bırakma KOŞULSUZDUR: bir panel açıksa imleç ona ait, oturum durumu fark etmez.
        /// Kilitleme ise yalnız oyun içindeyken anlamlı — ana menüde imleç serbest kalmalı.
        ///
        /// `Application.isFocused` guard'ı yalnız KİLİTLEME yönünde: play mode koşarken editöre
        /// alt-tab yapıldığında imleci geri kapmayalım (geliştirme konforu). Serbest bırakma
        /// odaktan bağımsızdır, çünkü onu kaçırmak paneli tıklanamaz bırakır.
        ///
        /// Not: Unity EDİTÖRÜ play mode'da ESC'ye basılınca kilidi kendisi kırar ve Game view'a
        /// tıklanana kadar geri vermez — bu döngü o pencerede kilidi geri isteyip duracaktır ama
        /// editör vermeyecektir. Build'de böyle bir davranış yok.
        /// </summary>
        public static void Enforce(bool sessionActive)
        {
            if (AnyoneWantsCursor)
            {
                if (Cursor.lockState != CursorLockMode.None)
                    Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible)
                    Cursor.visible = true;
                return;
            }

            if (!sessionActive || !Application.isFocused)
                return;

            if (Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = CursorLockMode.Locked;
            if (Cursor.visible)
                Cursor.visible = false;
        }
    }
}
