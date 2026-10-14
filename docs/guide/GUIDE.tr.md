# SHENT: eşler arası bir topluluk elektronik nakit sistemi

**Diller:** [Español](GUIDE.es.md) · [English](GUIDE.en.md) · [Português](GUIDE.pt.md) · [Français](GUIDE.fr.md) · [Deutsch](GUIDE.de.md) · [Italiano](GUIDE.it.md) · [Русский](GUIDE.ru.md) · [中文](GUIDE.zh.md) · [日本語](GUIDE.ja.md) · [Türkçe](GUIDE.tr.md)

---

## Özet

Elektronik nakdin tamamen eşler arası bir sürümü, ödemelerin hiçbir kurumdan geçmeden bir kişiden diğerine
gönderilmesini mümkün kılardı. Dijital imzalar sorunun bir kısmını çözer; ancak aynı coinin iki kez
harcanmasını önlemek için hâlâ güvenilir bir üçüncü tarafa ihtiyaç duyuluyorsa, temel faydalar kaybolur.
SHENT çifte harcama sorununu eşler arası bir ağ ile çözer. Ağ, işlemleri bir blok zincirine kaydeder ve her
blok bir iş kanıtıyla mühürlenir. Ortaya çıkan kayıt, bu iş yeniden yapılmadan değiştirilemez. Blok sayısıyla
değil birikmiş işle ölçülen en uzun zincir, olayların sırasının kanıtı ve en büyük hesaplama gücünden çıktığının
kanıtı olarak hizmet eder. Bu gücün büyük kısmı ağa saldırmak için iş birliği yapmayan düğümlerin elinde
olduğu sürece, bu düğümler en uzun zinciri üretir ve her saldırganı geride bırakır. Ağın kendisi asgari bir
yapı gerektirir. Mesajlar en iyi çaba esasıyla yayılır ve düğümler istedikleri zaman ağdan ayrılıp geri
dönebilir; döndüklerinde, yokken olanların kanıtı olarak en çok işe sahip zinciri kabul ederler.

SHENT bir topluluk parasıdır. Genesis bloğu bunu kendi metninde söyler:
`SHENT 13/10/2026 - moneda comunitaria sin valor monetario` (“SHENT 13.10.2026 - parasal değeri olmayan
topluluk parası”). Hiçbir piyasada işlem görmez, uygulama hiçbir şey alıp satmaz ve kimse onun bir gün bir
değeri olacağını vaat etmez.

---

## 1. Giriş

Her gün kullandığımız elektronik para neredeyse her zaman ödemeleri işleyen aracılara bağlıdır. Sistem
çalışır, ancak güvene dayalı bir modelin kendine özgü zayıflıklarını taşır: işlemler geri alınabilir,
aracılık maliyetleri küçük ödemeleri sınırlar ve her taraf gerekenden fazla bilgi vermek zorunda kalır.

Gereken şey, güven yerine kriptografik kanıta dayanan ve herhangi iki kişinin birbiriyle doğrudan işlem
yapmasını sağlayan bir ödeme sistemidir. Geri alınması hesaplama açısından imkânsız olan işlemler alıcıyı
korur. İşlemlerin sırasını kaydeden ve bu sırayı işle mühürleyen ağ, çifte harcamaya karşı korur.

SHENT bu fikrin bir uygulamasıdır: sıradan bir kişisel bilgisayarda çalışan; cüzdan, madencilik ve P2P ağı
içeren tam bir düğüm.

## 2. Hesaplar ve işlemler

SHENT bir hesap modeli kullanır. Her hesap bir **adres** ile tanımlanır. Adres bir Ed25519 açık anahtarından
elde edilir: anahtarın SHA-256 özetinin ilk 20 baytı, ağın önekiyle Bech32m olarak kodlanır (ana ağda
`shent1…`, test ağında `tshent1…`, yerel ağda `rshent1…`). Adres, hesap ilk işlemini gönderene kadar açık
anahtarı ortaya çıkarmaz.

Her hesabın bir **bakiyesi** ve bir **nonce** değeri, yani gönderilen işlemlerin sayacı vardır. Bir transfer
şunları içerir:

- gönderenin açık anahtarı,
- hedef adres,
- tutar ve ücret,
- hesabın bir sonraki nonce değeri,
- isteğe bağlı bir son geçerlilik yüksekliği,
- en fazla 80 baytlık isteğe bağlı bir not,
- ve yukarıdakilerin tümü ile ağ kimliği üzerinde bir Ed25519 imzası.

Nonce, diğer tasarımlarda önceki çıktılara yapılan atfın gördüğü işlevi görür: bir hesabın her işlemi yalnızca
bir kez ve sırayla kullanılabilir. Aynı nonce değerine sahip iki işlem bir çifte harcama girişimidir ve ağ
bunlardan en fazla birini kabul eder. İmza ağ kimliğini içerdiği için, test ağında geçerli olan bir işlem ana
ağda işe yaramaz.

Tutarlar tam sayı olarak saklanır: 1 SHENT 100.000.000 birimdir. Para için kayan noktalı sayılar kullanılmaz.

## 3. Bloklar ve zincir

Her blok bir başlık ve bir işlem listesi içerir. Başlık; önceki bloğun hash değerini, işlemlerin Merkle
kökünü, zaman damgasını, zorluk hedefini ve iş kanıtının nonce değerini içerir. Her blok bir öncekinin hash
değerini içerdiği için bloklar bir zincir oluşturur. Bir bloğu değiştirmek için o bloğun ve ondan sonra gelen
tüm blokların işini yeniden yapmak gerekirdi.

Bir blok yalnızca şu koşullar sağlandığında geçerlidir:

- Zaman damgası önceki 11 bloğun medyanından büyüktür ve gelecekte 5 dakikadan daha ileri bir zamanı
  göstermez.
- Blok en fazla 1.000.000 bayttır.
- İlk işlem madencinin ödülüdür (coinbase) ve tam olarak hak edilen miktarı öder.
- Diğer tüm işlemlerin imzaları geçerli, nonce değerleri sıralı ve bakiyeleri yeterlidir.

## 4. İş kanıtı

Bir bloğu mühürlemek için, başlığın hash değerini zorluk hedefinin altına düşüren bir nonce bulmak gerekir.
SHENT, iş kanıtı için hash fonksiyonu olarak deneme başına 2 MiB bellekle **Argon2id** kullanır. Bu
"memory-hard" bir fonksiyondur: her deneme hesaplamanın yanı sıra bellek de gerektirir; bu da kişisel bir
CPU'nun performansını özel donanımlarınkine yaklaştırır. Özel donanımları imkânsız kılmaz: yalnızca
avantajlarını azaltır.

Bu nonce değerini bulmanın kestirme bir yolu yoktur: her deneme bağımsızdır ve başarı olasılığı her denemede
aynıdır. Doğrulamak ise yalnızca tek bir hash hesaplamasına mal olur.

Zorluk, her blokta son 60 bloğa bakılarak yeniden hesaplanır; amaç, madenci az da olsa çok da olsa **ortalama
her 10 dakikada bir blok** çıkmasıdır. Güç eklenirse zorluk artar; güç çekilirse azalır.

## 5. Ağ

Ağın izlediği adımlar şunlardır:

1. Yeni işlemler tüm düğümlere yayılır.
2. Her düğüm yeni işlemleri bir blokta toplar.
3. Madencilik yapan her düğüm kendi bloğu için iş kanıtını arar.
4. Bir düğüm bunu bulduğunda bloğu diğer tüm düğümlere yayar.
5. Diğer düğümler bloğu yalnızca içindeki tüm işlemler geçerliyse ve daha önce harcanmamışsa kabul eder.
6. Düğümler kabullerini, kabul edilen bloğun hash değerini önceki blok olarak kullanıp bir sonraki blok
   üzerinde çalışarak ifade eder.

Bir düğüm başka hiçbir düğüme güvenmez. Her blok ve her işlem, bilinen bir düğümden gelse bile, konsensüs
kurallarıyla yerel olarak doğrulanır. Ağdan gelen bir mesaj tek başına hiçbir hesaba coin yazamaz: bakiyeleri
yalnızca en çok işe sahip zincirdeki geçerli bir blok değiştirir.

Düğümler birbirlerini bulmak için merkezi bir sunucuya bağlı değildir. Aynı anda üç yol kullanırlar:

- her düğümün kendi portunu, ağdan ve ağın genesis bloğundan türetilen bir anahtar altında yayımladığı,
  BitTorrent'in herkese açık DHT'si;
- yerel ağda keşif;
- eşler arasında adres alışverişi.

Bulunan her adres, iki tarafın da rastgele bir sınamayı kendi Ed25519 kimliğiyle imzaladığı bir el sıkışmadan
geçer. Ağın çalışması için en az bir düğümün gelen bağlantıları kabul etmesi yeterlidir (ana ağda TCP 37373
portu açık). Açık portu olmayan düğümler bağlantıları bir relay üzerinden alabilir.

## 6. Teşvik

Uzlaşım gereği, her bloğun ilk işlemi o bloğu çıkaran madenciye ait yeni coinler oluşturur. Bu, ağı ayakta
tutmanın teşvikidir ve coinlerin, onları çıkaran bir otorite olmadan dolaşıma girmesinin tek yoludur.

- Ödül **blok başına 50 SHENT** olarak belirlenmiştir.
- Ödül her **1.000.000 blokta** bir yarıya iner (10 dakikalık bloklarla yaklaşık 19 yıl).
- Toplam miktar hiçbir zaman **100.000.000 SHENT** sınırını aşmaz.
- Ayrıca madenci, bloğa dahil ettiği işlemlerin ücretlerini de alır.

Ödül bloğu bulana gider ve paylaştırılmaz. Çok sayıda madenci olduğunda, her biri toplam hesaplama gücündeki
payıyla orantılı bir sıklıkla blok bulur. Ortalamada sonuç bir paylaştırmayla aynıdır, ancak bölünmemiş ve
rastgele ödüller biçimindedir.

Teşvik, düğümlerin dürüst kalmasına da yardımcı olur. Diğer herkesin toplamından daha fazla hesaplama gücü
toplayan biri, bu gücü tarihi yeniden yazmak için mi yoksa kurallara uyarak yeni coin kazanmak için mi
kullanacağını seçmek zorunda kalırdı. Kendi servetinin bağlı olduğu sistemi baltalamaktansa kurallar içinde
oynamak onun için daha kârlıdır.

## 7. Rekabet eden zincirler

İki düğümün neredeyse aynı anda bir blok bulması ya da bir grup düğümün bağlantısız kalıp kendi başına
madencilik yapmış olması mümkündür. Bu durumda zincirin iki sürümü vardır. Düğümler ilk aldıkları sürüm
üzerinde çalışmaya devam eder, ancak diğerinin onu geçmesi ihtimaline karşı onu da saklar. Eşitlik bir sonraki
blok ortaya çıktığında bozulur: **daha fazla birikmiş işe** sahip dal tüm düğümlerde geçerli dal olur.

Zincirler birleştirilmez. Kaybeden daldaki bloklar ve onlarla birlikte ödülleri atılır. İçerdikleri işlemler
hâlâ geçerliyse yeniden bekleyen duruma geçer. Kurallara uygun olarak çıkarılmış bir blok, en çok işe sahip
zincirde kaldığı sürece ödülünü korur. Bu nedenle büyük bir ödemeyi kesinleşmiş saymadan önce birkaç onay
beklemek ve madenciliği ağa bağlıyken yapmak tavsiye edilir: yalıtılmış bir düğüm, sonradan ağın geri
kalanının zincirine karşı kaybedebilecek geçerli bloklar çıkarabilir.

İki koruma kuralı vardır:

- **Azami yeniden düzenleme derinliği:** bir düğüm, daha önce kabul edilmiş 288'den fazla bloğu değiştirmez
  (yaklaşık iki gün).
- **Kontrol noktaları:** yayımlanan program, geçmişin derinliklerine çoktan gömülmüş bazı blokların hash
  değerlerini içerir.

Her ikisi de, indirilen yazılıma biraz güvenmek karşılığında uzun bir saldırının zararını sınırlar. Bunlar
bilinçli ve bilinen bir tasarım kararıdır.

## 8. Gizlilik

Geleneksel bankacılık modeli gizliliği bilgiye erişimi kısıtlayarak sağlar. SHENT'te, herkese açık her kayıtta
olduğu gibi, tüm işlemler görünürdür: adresler, tutarlar ve zamanlar. Gizlilik başka bir şekilde korunur:
adreslerin üzerinde isim yoktur.

İçerdiği önlemler:

- **Ödeme almak için yeni adresler.** Cüzdan, "Ödeme al" her istendiğinde yeni bir adres üretir. Hepsi aynı
  yedekten türetilir, ancak dışarıdan bakıldığında birbirleriyle hiçbir ilişkileri yoktur… bir ödemede birlikte
  kullanılana kadar.
- **Dandelion++.** Kişinin kendi işlemleri önce rastgele bir düğüm yolundan geçer ve ancak ondan sonra yayılır;
  böylece hangi IP'den çıktıkları belli olmaz.
- **Tor.** Düğüm yalnızca bir SOCKS5 proxy üzerinden bağlanabilir. Bu modda bağlantı dinlemez ve DHT'yi, yerel
  ağı ya da UPnP'yi kullanmaz.

Şifreleme konusunda, yanlış anlaşılma olmasın diye:

- **Özel anahtarlar gerçekten şifrelidir**, yalnızca her kişinin kendi bilgisayarında.
- **Blok zinciri şifreli değildir**, bilerek. Her düğüm her işlemi okuyabilmeli ve doğrulayabilmelidir.
  Bloklar, transferler ve bakiyeler düğümler arasında neredeyse gerçek zamanlı olarak, açık hâlde paylaşılır.
  Taklit edilemezler; bunun nedeni gizli olmaları değil, imzalı ve işle mühürlenmiş olmalarıdır.
- **P2P protokolü de şifreli değildir**, tıpkı Bitcoin'inki gibi. Bağlantıyı gözlemleyen biri, bilgisayarda
  SHENT'in çalıştığını ve hangi açık verilerin geçtiğini görebilir. IP'yi gizlemek için Tor modu vardır.

## 9. Cüzdan ve yedek

Anahtarlar, işletim sisteminin rastgele sayı üreteciyle kişinin kendi bilgisayarında üretilir. Bir ana
parolayla şifrelenmiş bir `.shent` dosyasında saklanır (Argon2id + ChaCha20-Poly1305). Özel anahtar o
bilgisayardan asla çıkmaz ve ağ üzerinden asla gönderilmez.

- Dosya olmadan ve parola olmadan coinler kurtarılamaz. Hiçbir kurtarma mekanizması yoktur: kimse parolayı
  sıfırlayamaz.
- `.shent` dosyasının bir kopyası bilgisayarın dışında (bir USB bellekte, bir bulutta), parola ise güvenli bir
  yerde saklanmalıdır.
- Dosya, parola ve anahtar hiç kimseyle paylaşılmamalıdır. Hiçbir geliştirici ya da düğüm bunları hiçbir
  zaman istemeyecektir.

## 10. Güvenlik ve sınırlar

Ağın geri kalanından daha fazla hesaplama gücüne sahip bir saldırgan şunları yapabilirdi:

- son blokları yeniden yazmak,
- kendi işlemlerini geri almak (çifte harcama),
- başkalarının işlemlerinin onaylanmasını engellemek.

Buna karşılık şunları yapamazdı:

- yoktan coin yaratmak,
- anahtarına sahip olmadığı bir hesaptaki coinleri harcamak,
- kuralları değiştirmek.

Dürüst düğümler, nereden gelirse gelsin her geçersiz bloğu reddeder.

Güvenlik, ne kadar dürüst güç bulunduğuna bağlıdır. Az madencisi olan genç bir ağda tek bir güçlü bilgisayar
çoğunluğa sahip olabilir. Uygulamanın İstatistikler sayfası son günlerde kaç adresin madencilik yaptığını
gösterir ve tek bir adres blokların yarısından fazlasını topladığında uyarır.

Bu yazılım olduğu gibi, hiçbir garanti olmaksızın sunulur. Hiçbir sistem saldırılara karşı dokunulmaz
değildir. Kurallar, kod ve testler herkesin inceleyebilmesi için açıktadır.

## 11. Pratik kullanım

### Kurulum (Windows 10/11, 64 bit)

1. `SHENT-<version>-win-x64.zip` dosyası deponun *Releases* sayfasından indirilir.
2. Hash değeri `Get-FileHash .\SHENT-<version>-win-x64.zip -Algorithm SHA256` ile doğrulanır ve
   yayımlanan `.sha256` dosyasıyla karşılaştırılır.
3. Zip dosyasının tamamı açılır ve `SHENT.exe` çalıştırılır. Başka bir şey kurmak gerekmez.
4. Windows güvenlik duvarı sorarsa, diğer düğümlerin bağlanabilmesi için erişime izin verilir.

### İlk adımlar

- **Cüzdan → Cüzdan oluştur.** Bir ana parola seçilir ve `.shent` yedeği saklanır.
- **Madencilik.** İş parçacıkları ve yoğunluk seçilir ve reaktör çalıştırılır. Bağlı eş yoksa uygulama bunu
  bildirir: ağ olmadan çıkarılan bloklar atılabilir.
- **Gönder.** Hedef adres, tutar ve ücret yapıştırılır. İmzalamadan önce onay ekranı kontrol edilir.
- **Ödeme al.** Gösterilen adres paylaşılır. Adres herkese açıktır ve hiçbir şey harcamaya izin vermez.
- **Dil.** Dünya simgeli seçici, arayüzü 10 dil arasında değiştirir. Tutarlar, seçilen dilin ondalık
  ayırıcısıyla yazılır. Bir tutar belirsizse uygulama tahmin etmek yerine onu reddeder.

### Ağı güçlendirmek

Yönlendiricide **TCP 37373** portu bilgisayara yönlendirilerek açılır ya da UPnP etkinleştirilir. Bağlantı
kabul eden her düğüm, başkalarının katılmasını kolaylaştırır. Ekransız bir sunucu için konsol düğümü vardır:
`nodo-consola\shent-node.exe help`.

### Kaynak koddan derlemek

.NET 10 SDK gerektirir.

```
dotnet build Shent.sln
dotnet test tests/Shent.Core.Tests
dotnet run --project src/Shent.Desktop
```

Windows için yayın paketi `scripts/publish-windows.ps1` ile oluşturulur. Ağın teknik belgeleri
[`docs/P2P_INTERNET.md`](../P2P_INTERNET.md) dosyasındadır.

## 12. Sonuç

Güvene dayanmayan bir elektronik işlem sistemi önerdik. Dijital imzalar, coinlerin kontrolünü anahtara sahip
olana verir. Çifte harcamayı önlemek için ağ, işlemlerin herkese açık geçmişini bir iş kanıtı zincirine
kaydeder; dürüst düğümler hesaplama gücünün büyük kısmını elinde tuttuğu sürece bu zinciri değiştirmek bir
saldırgan için imkânsız hâle gelir. Ağ, sadeliği sayesinde sağlamdır: düğümler çok az koordinasyonla aynı anda
çalışır, kimliklerini belirtmeleri gerekmez ve istedikleri zaman ayrılıp geri dönebilirler; döndüklerinde olup
bitenlerin kanıtı olarak en çok işe sahip zinciri kabul ederler. Hesaplama güçleriyle oy verirler: geçerli
blokları üzerlerinde çalışarak kabul eder, geçersiz olanları üzerlerinde çalışmayı reddederek reddederler.
Gerekli tüm kurallar ve teşvikler bu tek konsensüs mekanizmasıyla uygulanır.

SHENT, bir topluluk onu kullansın, incelesin ve geliştirsin diye vardır.
