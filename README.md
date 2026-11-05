# Saha360

**Bir Matasis uygulaması · https://saha360.matasis.com**

Personel havuzundan işlere ekip atama; işin hazırlanmasından şef onayına kadar tüm süreci izleme; bildirim, fotoğraf,
malzeme, not ve raporlama işlevlerini tek sistemde toplayan web uygulaması.

| | |
|---|---|
| Teknoloji | ASP.NET Core MVC (Razor Views), C#, Entity Framework Core, SQL Server, ASP.NET Core Identity |
| .NET sürümü | **.NET 10 (LTS)** — tüm projeler `net10.0`, paketler `10.0.13` |
| Veritabanı | SQL Server (yerelde `.\SQLEXPRESS`), şema EF Core Migration ile yönetilir; SSMS ile incelenebilir |
| Arayüz | Türkçe, Bootstrap 5 (yerel kopya), mobil uyumlu |
| Testler | xUnit · 82 test (birim + servis + HTTP) |

---

## 1. Hızlı başlangıç (yerel geliştirme)

1. VS Code'da `Saha360` klasörünü açın ve **F5**'e basın (yapılandırma: *Saha360*).
   Ya da terminalde:
   ```bash
   dotnet run --project src/Saha360.Web --launch-profile http
   ```
2. Tarayıcı `http://localhost:5231` adresinde açılır. İlk açılışta:
   - `Saha360` veritabanı SQLEXPRESS üzerinde **otomatik oluşturulur** (yalnız Development ortamında),
   - roller kurulur ve **yalnız ilk yönetici hesabı** oluşturulur (`admin`; parolası
     `src/Saha360.Web/appsettings.Development.json` → `Bootstrap:AdminPassword`). Başka veri yoktur.
3. `admin` ile girip **Toplu Yükleme (Excel)** ekranından şablonu indirin, birim, unvan, ekip, personel, kullanıcı ve
   malzemeleri doldurup yükleyin (ayrıntı: bölüm 2a). İsterseniz tek tek de ekleyebilirsiniz (Tanımlar, Personel, Kullanıcılar).

   `appsettings.Development.json` yayın paketine girmez ve `.gitignore`'dadır; depodan yeni indirildiyse
   `appsettings.Development.example.json` dosyasını bu adla kopyalayıp parolayı yazın. Deneme amaçlı örnek veri gerekirse
   bu dosyada `Seed:Demo` = `true` ve `Seed:Password` verip **boş** bir veritabanıyla başlatın.

Testler:
```bash
dotnet test
```

---

## 2a. Excel ile toplu yükleme

Menü › **Toplu Yükleme (Excel)** (`/toplu-yukleme`): tüm listeler tek dosyada (bir birimi baştan kurmak için).
Ayrıca her listenin kendi ekranında **Toplu … ekle (Excel)** düğmesi var (`/toplu-yukleme/kullanicilar`, `personel`, `malzemeler`,
`ekipler`, `birimler`, `unvanlar`, `kategoriler`, `bolgeler`): yalnız o listenin şablonu iner ve yalnız o sayfa işlenir; birim/ekip/unvan
önerileri sistemdeki kayıtlardan gelir. Yetki: tanımlar → sistem/birim yöneticisi, personel ve malzeme → planlamacı da, kullanıcılar → yalnız sistem yöneticisi.

- **Boş şablonu indir:** tek `.xlsx`; "Nasıl doldurulur" + Birimler, Unvanlar, Kategoriler, Bölgeler, Malzemeler, Ekipler,
  Personel, Kullanıcılar sayfaları. Zorunlu başlıklar turuncu, başlık üzerinde açıklama; Evet/Hayır açılır listeleri;
  Birim/Ekip/Unvan sütunlarında diğer sayfalardan öneri listesi. Tüm sütunlar metin biçiminde (sicil no `1001` bozulmaz).
- **Mevcut kayıtlarla indir:** sistemdeki kayıtlar dolu gelir; düzenleyip geri yükleyerek toplu güncelleme. Parola dışa aktarılmaz.
- **Yükleme kuralları:** sayfalar bağımlılık sırasıyla işlenir, aynı dosyada birbirine başvurabilir (ekip şefi ve bağlı şef
  personel yüklendikten sonra bağlanır). Anahtar eşleşirse güncellenir: birim/unvan/kategori/bölge → ad, malzeme → kod,
  ekip → birim+ad, personel → sicil no, kullanıcı → kullanıcı adı. **Hepsi ya da hiçbiri** (tek transaction); hatalar sayfa ve
  satır numarasıyla gösterilir. Unvanda "Herkes atanabilir" sütunu (eski şablonlardaki "Kısıtlamasız" başlığı da okunur) ekip ekranındaki atama kısıtını belirler.
- Sınırlar: 5 MB, sayfa başına 5.000 satır. Her yükleme denetim kaydına sayfa bazında yazılır.

## 2. Gereksinim özeti (Aşama 1 analizi)

- **Havuz:** Personelin müsaitliği hiçbir yerde sayaç olarak tutulmaz; her seferinde *aktiflik + izin dönemleri + çakışan aktif
  atamalardan* hesaplanır (`AvailabilityService`). Havuz sayısı bu yüzden kaymaz.
- **Atama:** Sürükle-bırak ve mobil "İşe ekle" yalnız ekrandaki **taslağı** değiştirir. "Taslağı kaydet / Atamaları kaydet /
  Kaydet ve personele gönder" tek istekte, sunucuda **atomik** kaydedilir; uygunluk ve çakışma orada yeniden denetlenir.
- **Durumlar** tek merkezden (`WorkflowRules` + `WorkflowService`) yönetilir; controller durum değiştiremez.
- **Sistem rolü ≠ iş içi görev:** Sistem rolleri (`SystemRoles`) yetkiyi; görev rolleri (Şef, İşçi, Kanal Personeli…) `JobRoles`
  tablosunda tanımlanabilir veridir ve havuz ekranındaki atama gruplarını oluşturur.
- **Kabul/ret atama bazındadır:** bir kişinin reddi işi reddetmez; ekipte kimse kalmazsa iş "Atama Bekliyor"a düşer.
- **Şef onayı işi kesinleştirir**, yönetici yalnız bilgilendirilir (ikinci onay yok).

### Varsayımlar ve kararlar (değiştirmek isterseniz söyleyin)

| Konu | Karar |
|---|---|
| Personel girişi | Hesap isteğe bağlı. Hesabı olan personel kendi işini kabul/ret eder; **hesabı olmayan personel işe gönderildiğinde planlamacı adına kabul edilmiş sayılır** (işlem geçmişine yazılır). |
| "Atandı / Gönderildi" durumu | "Kabul Bekliyor" ile birleştirildi: gönderim anında bildirim gider ve yanıt beklenir. |
| Çalışmaya başlama | Ekipteki herkes yanıt vermeden (kabul) iş başlatılamaz. |
| Ret sonrası | Ret gerekçesi zorunlu; atama satırı silinmez ("Reddetti" olarak kalır), şef + planlamacı + yönetici bilgilendirilir. |
| Havuza dönüş | Şef onayında tüm ekip serbest kalır. Başlamış ama süresi geçmiş iş, bitişi "şimdi"ye uzamış sayılır (personel kendiliğinden serbest kalmaz). |
| Onay yetkisi | İşin sorumlu şefi; istisna olarak birim yöneticisi ve sistem yöneticisi. |
| Unvan kısıtı | Personel yalnız kendi unvanının ekip alanına atanır (Kanal Personeli → Şef olamaz). **Herkes atanabilir** işaretli alanlara (varsayılan: Şoför) unvan şartı aranmadan herkes atanabilir. **Ekip lideri** işaretli rol (ör. Şef) ekip ekranında en üstte ve yeni işte hazır açık gelir; onay yetkisi vermez. Kural sunucuda da denetlenir; ayar Tanımlar › Görev rolleri ekranında. |
| İşe özel ekip alanları | Ekip ekranında yalnız o işe eklenen alanlar görünür; "+ Ekip alanı ekle" ile tüm unvanlardan seçilir, boş alan kaldırılabilir. Alan listesi boş olsa da iş ile saklanır. Yeni işte başlangıçta yalnız lider rolü (Şef) açıktır. |
| Taslak sayfa yenilenirse | Kaydedilmemiş taslak kaybolur; sayfadan çıkarken tarayıcı uyarır. |
| Eşzamanlı planlama | İşin ve ilgili personelin satırları kayıt sırasında kilitlenir (SQL Server `UPDLOCK`); sayfa açıldıktan sonra iş değiştiyse kayıt reddedilir. |
| Malzeme | Yalnız tüketim kaydı; stok tutulmaz. Aynı malzeme+birim tekrar eklenirse miktar birleştirilir. Katalog değişse de iş satırında ad/birim anlık görüntüsü kalır. |
| Notlar | Silinmez ve düzenlenmez; düzeltme için yeni not eklenir. |
| Dosyalar | JPG/PNG/WEBP/PDF, dosya başına 10 MB, içerik imzası kontrolü; web kökü dışında rastgele adla; silme "yumuşak" (kayıt ve dosya kalır, geçmişte görünür). |
| Bildirim | Yalnız uygulama içi. (alıcı, tekilleştirme anahtarı) benzersiz → aynı olay iki kez bildirim üretmez. |
| Rapor dışa aktarma | CSV (UTF-8, `;` ayraçlı — Türkçe Excel doğrudan açar), formül enjeksiyonu kaçışlanır. |
| Birim kapsamı | Birim yöneticisi / planlamacı / rapor görüntüleyici hesabına birim atanırsa yalnız o birimi görür; boşsa tümünü. |
| Oturum | 8 saat hareketsizlikte düşer; 5 hatalı denemede 5 dk kilit; giriş adresinde IP başına dakikada 10 deneme. |

---

## 3. Mimari ve klasör yapısı

Şartnamenin izin verdiği sade yapı: **Web + Domain + Infrastructure** (Application katmanı Infrastructure içindeki
servislerde; gereksiz soyutlama yok, iş kuralı controller'da yok).

```text
Saha360.slnx
src/
  Saha360.Domain/          Entity'ler, enum'lar, kurallar (bağımlılık yok)
    Entities/  Enums/  Rules/ (WorkflowRules, TimeRange, TrTime)  Exceptions/  Security/ (SystemRoles)
  Saha360.Infrastructure/  EF Core, Identity, dosya deposu, servisler
    Persistence/ (AppDbContext, Configurations, Migrations, DataSeeder)
    Identity/ (ApplicationUser)   FileStorage/ (LocalFileStorage, FileValidator)
    Services/  AccessService · AvailabilityService · AssignmentService · WorkflowService · WorkOrderService
               WorkContentService · WorkQueryService · NotificationService · AuditService · ReportService
               DashboardService · DefinitionService · EmployeeService · MaterialService · UserAdminService
  Saha360.Web/             MVC: Controllers, Views, ViewModels, wwwroot (site.js, pool.js, work-form.js)
tests/
  Saha360.Tests/           RuleTests · AssignmentTests · WorkflowTests · ContentAndReportTests · WebTests
```

**NuGet paketleri:** `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`,
`Microsoft.EntityFrameworkCore.Design` (migration aracı), `System.Security.Cryptography.Pkcs` (güvenlik açığı olan geçişli
sürümü güncellemek için). Testlerde: `xunit`, `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.AspNetCore.Mvc.Testing`.
Grafikler sunucu tarafında basit çubuklarla çizilir (ek kütüphane yok).

---

## 4. Veri modeli

| Tablo | Amaç / önemli kurallar |
|---|---|
| `AspNetUsers` (`ApplicationUser`) | Giriş hesabı; `IsActive`, birim kapsamı |
| `OrganizationUnits`, `Teams`, `JobRoles`, `WorkCategories`, `WorkLocations` | Tanımlar; silinmez, pasife alınır; adlar benzersiz |
| `Employees` | Personel; sicil no ve kullanıcı bağlantısı benzersiz (boş olabilir); `RowVersion` |
| `EmployeeAvailabilities` | İzin / rapor / müsait değil dönemleri |
| `WorkOrders` | İş; `WorkNumber` benzersiz (`IS-YYYY-00001`); `Status`, plan tarihleri, sorumlu birim/şef indeksli; `RowVersion` |
| `WorkTeamGroups` | İşe özel ekip alanları (iş, unvan, sıra); (İş, Unvan) benzersiz |
| `WorkAssignments` | Personel↔iş; satır silinmez, `ReleasedAtUtc` ile ayrılır. **(İş, Personel) için `ReleasedAtUtc IS NULL` filtreli benzersiz indeks** → aynı kişi aynı işe iki kez eklenemez |
| `WorkStatusHistories` | Her durum geçişi (kimden, kime, kim, gerekçe) |
| `WorkNotes`, `WorkAttachments`, `WorkMaterials` | İş kayıtları; malzeme miktarı `decimal(18,3)` |
| `MaterialCatalog` | Kod benzersiz; stok yok |
| `Notifications` | (Alıcı, `DeduplicationKey`) benzersiz |
| `AuditLogs` | Değiştirilemez denetim izi; iş geçmişi sekmesi buradan |

Tüm ilişkiler **Restrict** (cascade delete yok). Tarihler veritabanında **UTC**; ekranda Türkiye saati (`TrTime`).
Enum'lar sayı olarak saklanır: `WorkStatus` 0 Taslak, 1 Atama Bekliyor, 3 Kabul Bekliyor, 4 Planlandı, 5 Devam Ediyor,
6 Tamamlanma Onayı Bekliyor, 7 Şef Tarafından İade Edildi, 8 Tamamlandı, 9 İptal Edildi.

### Durum geçiş matrisi

| Şu anki durum | Gidebileceği durumlar | Tetikleyen |
|---|---|---|
| Taslak | Kabul Bekliyor, İptal | Planlamacı "personele gönder" / iptal |
| Atama Bekliyor | Kabul Bekliyor, İptal | Yeniden atama |
| Kabul Bekliyor | Planlandı, Atama Bekliyor, İptal | Herkes kabul etti / ekipte kimse kalmadı |
| Planlandı | Devam Ediyor, Kabul Bekliyor, Atama Bekliyor, İptal | Personel "çalışmaya başladım" / ekibe yeni kişi |
| Devam Ediyor | Tamamlanma Onayı Bekliyor, İptal | Personel "tamamlandı" (açıklama zorunlu) |
| Tamamlanma Onayı Bekliyor | Tamamlandı, Şef Tarafından İade | Şef onay / iade (gerekçe zorunlu). "Ek bilgi iste" durumu değiştirmez |
| Şef Tarafından İade | Devam Ediyor, Tamamlanma Onayı Bekliyor, İptal | Personel |
| Tamamlandı, İptal | — | Son durumlar |

Her geçişte: yetki → mevcut durum → zorunlu alan → durum + geçmiş + denetim **tek transaction** → bildirim.
Aynı istek ikinci kez gelirse işlem uygulanmaz ("zaten onaylanmış" bilgisi döner).

---

## 5. Ekran haritası

| Menü | Adres | Kim |
|---|---|---|
| Gösterge Paneli | `/` | Planlama, şef, yönetici, rapor (kartlar tıklanınca süzülmüş liste) |
| İşlerim (mobil öncelikli) | `/islerim` | Personeli olan her hesap |
| İş Programı / Personel Havuzu | `/havuz`, `/havuz?isId=…` | Planlama |
| Tüm İşler / Onay Bekleyen / Reddedilen-İade / Tamamlanan | `/isler?view=…` | Herkes (kendi kapsamı) |
| Yeni İş Oluştur | `/isler/yeni` | Planlama |
| İş detayı (Genel, Ekip, Malzemeler, Fotoğraflar, Notlar, İşlem Geçmişi) | `/isler/{id}` | Görme yetkisi olan |
| Şef onay ekranı | `/onay` | Şef, birim/sistem yöneticisi |
| Personel Listesi / Organizasyon Şeması / İzinler / Ekipler | `/personel`, `/organizasyon`, `/personel/izinler`, `/tanimlar/ekipler` | Görme: planlama + birim yöneticisi; ekleme/düzenleme, izin ve ekip üyeliği: yalnız birim yöneticisi ve sistem yöneticisi |
| Malzeme Kataloğu / İş Bazlı Kullanım | `/malzemeler`, `/malzemeler/kullanim` | Planlama |
| Raporlar (11 rapor, CSV) | `/raporlar` | Yönetici, planlama, rapor görüntüleyici |
| Bildirimler | `/bildirimler` | Herkes |
| Tanımlar | `/tanimlar` | Görüntüleme planlama; değişiklik yönetici |
| Kullanıcılar / Sistem ve Denetim | `/kullanicilar`, `/sistem` | Sistem yöneticisi (izinle devredilebilir) |
| Yetkiler (rol × izin tablosu) | `/yetkiler` | Yalnız sistem yöneticisi (devredilemez) |
| Sağlık kontrolü | `/saglik` | Herkese açık (yalnız "Healthy/Unhealthy") |

Menüyü gizlemek yalnız arayüz kolaylığıdır: her adres sunucuda politika ile, her kayıt `AccessService` kapsamıyla denetlenir.

### 5a. Yetki modeli (üç katman)

1. **İzin (ne yapabilir?):** `Permissions.cs` izin anahtarları; rol × izin tablosu `RolePermissions` tablosunda ve
   **Yetkiler** (`/yetkiler`) ekranından değiştirilir. Birden fazla rolü olan kişinin izinleri birleşir. Sistem yöneticisi
   her zaman tüm izinlere sahiptir. Yeni sürümde eklenen izinler açılışta varsayılan değerle tabloya eklenir.
2. **Birim kapsamı (hangi veri?):** İzinler yalnız kişinin biriminde geçerlidir. Birim = kullanıcı hesabındaki "Birim kapsamı",
   boşsa bağlı personel kaydının birimi. Birimi olmayan (ve personel kaydı olmayan) yönetici hiçbir şey görmez.
   "Tüm birimlerin verisini görme" izni veya kullanıcıya özel "Tüm birimleri görsün" ayarı (yalnız sistem yöneticisi verir) sınırı kaldırır.
3. **Kişisel ilişki (her zaman):** İzni olmasa da herkes kendisine atanan işi görür ve çalışır; işin sorumlu şefi o işi onaylar/iade eder.

Varsayılan tablo (Yetkiler ekranından değiştirilebilir):

| İzin | Birim yöneticisi | Planlamacı | Rapor görüntüleyici | Şef / Personel |
|---|:-:|:-:|:-:|:-:|
| Birimin tüm işlerini görme | ✓ | ✓ | ✓ | — |
| İş oluşturma, düzenleme, ekip atama, iptal, yeniden açma | ✓ | ✓ | — | — |
| Birimdeki her işi onaylama/iade | ✓ | — | — | (yalnız sorumlu şefi olduğu iş) |
| Personeli görme (liste, havuz, şema, ekipler) | ✓ | ✓ | — | — |
| Personel ekleme/düzenleme, izin/müsaitlik, ekip üyeliği | ✓ | — | — | — |
| Tanımlar (birim, unvan, kategori, bölge) | ✓ | — | — | — |
| Malzeme kataloğu | ✓ | ✓ | — | — |
| Raporlar ve dışa aktarma | ✓ | ✓ | ✓ | — |
| Kullanıcı hesapları, denetim kaydı, tüm birimler | — | — | — | — |

Yetki yükseltme koruması: kullanıcı yönetimi izni verilen kişi bile sistem yöneticisi rolü ya da "tüm birimler" veremez,
yönetici hesabına dokunamaz, kendi birimi dışına hesap açamaz/bağlayamaz. Yetki tablosu değişiklikleri denetim kaydına yazılır.

---

## 6. Veritabanı ve migration

Yerel bağlantı (`appsettings.json`): `Server=.\SQLEXPRESS;Database=Saha360;Trusted_Connection=True;TrustServerCertificate=True`.
SSMS'te sunucu adı olarak `.\SQLEXPRESS` yazıp Windows kimlik doğrulamasıyla bağlanabilirsiniz.

```bash
dotnet tool restore
dotnet ef migrations add <Ad> --project src/Saha360.Infrastructure --startup-project src/Saha360.Web --output-dir Persistence/Migrations
dotnet ef database update --project src/Saha360.Infrastructure --startup-project src/Saha360.Web
```

VS Code'da uygulama F5 ile çalışırken `bin` klasörü kilitlidir; migration eklerken komuta `--configuration Release` ekleyin.

---

## 7. Yayına alma (IIS / sunucu) — özet

> **Adım adım rehber: [docs/YAYINLAMA.md](docs/YAYINLAMA.md)**. Paket `yayinla.ps1` ile alınır, güncelleme `sunucu-guncelle.ps1` ile yapılır;
> bağlantı Matasis Flow ile aynı şekildedir (`.\SQLEXPRESS`, Windows kimliği, açılışta otomatik migration).
> Aşağıdaki maddeler daha sıkı (ortam değişkenli, elle migration) bir kurulum için alternatiftir.

1. Sunucuya .NET 10 **Hosting Bundle** ve SQL Server kurun. SQL'de uygulamaya özel, yalnız bu veritabanında `db_datareader`,
   `db_datawriter` (ve migration için geçici `db_ddladmin`) yetkili bir kullanıcı açın.
2. `dotnet publish src/Saha360.Web -c Release -o yayin`
3. Gizli bilgileri **ortam değişkeniyle** verin (dosyaya yazmayın), ör. IIS uygulama havuzunda:
   - `ConnectionStrings__Default` = üretim bağlantı cümlesi
   - `Storage__RootPath` = web kökü dışındaki dosya klasörü (ör. `D:\Saha360Dosyalar`; yalnız uygulama havuzu hesabına yazma izni)
   - `DataProtection__KeysPath` = oturum anahtarları klasörü
   - İlk kurulumda bir kez: `Bootstrap__AdminUserName`, `Bootstrap__AdminPassword` → uygulama ilk yöneticiyi oluşturur.
     Girişten sonra parolayı değiştirin ve bu iki değişkeni silin. (Kodda gömülü varsayılan yönetici yoktur.)
4. Migration'ı kontrollü uygulayın: `dotnet ef migrations script --idempotent -o guncelle.sql` ile betik üretip SSMS'te çalıştırın
   (veya bir kerelik `Database__MigrateOnStartup=true`). Üretimde açılışta otomatik migration varsayılan olarak **kapalı**.
5. HTTPS sertifikası bağlayın; uygulama üretimde HTTPS'e yönlendirir ve HSTS gönderir. Ayrıntılı hata sayfası gösterilmez.
6. `/saglik` adresini izleme aracına ekleyin.

### saha360.matasis.com alt alan adı

1. **DNS** (matasis.com'un yönetildiği panel): `saha360` için sunucunun IP'sine bir **A** kaydı
   (veya ana siteyle aynı sunucudaysa ana alan adına **CNAME**) ekleyin.
2. **IIS:** Site bağlamalarına (Bindings) `https`, ana bilgisayar adı `saha360.matasis.com`, port 443 ekleyin;
   80 portunu da aynı ad ile bağlayın (uygulama HTTPS'e yönlendirir).
3. **Sertifika:** `saha360.matasis.com` (ya da `*.matasis.com` joker) sertifikası; ücretsiz seçenek win-acme (Let's Encrypt).
4. Yalnız bu adresten yanıt vermesi için ortam değişkeni: `AllowedHosts` = `saha360.matasis.com`.

### Yedekleme
- SQL Server: günlük **full**, iş yüküne göre saatlik **log** yedeği (SQL Agent / bakım planı). Yedekler sunucudan ayrı,
  erişimi kısıtlı ve şifreli konumda tutulmalı. **Ayda bir geri yükleme testi** yapın.
- Fotoğraflar veritabanında değil `Storage:RootPath` klasöründedir: bu klasörü de aynı plana ekleyin.
- `DataProtection:KeysPath` klasörü kaybolursa kullanıcılar yeniden giriş yapar (veri kaybı olmaz).

---

## 8. Test kapsamı (şartname 14. bölüm)

`dotnet test` → **97 test, hepsi geçiyor** (yetki tablosu, birim kapsamı ve yetki yükseltme testleri dahil).

- **Atama:** taslağa ekleme, kayıtla havuzdan çıkma (yalnız o zaman aralığı), havuz sayısının türetilmesi, aynı kişinin iki kez
  eklenememesi, çakışan işe atamanın iş numarasıyla engellenmesi, atamayı kaldırınca serbest kalma, izinli personel,
  bayat sayfa sürümü, başka birimin planlamacısı.
- **İş akışı:** taslakta bildirim yok, gönderimde bildirim, hesapsız personelin otomatik kabulü, herkes kabul edince Planlandı,
  ret gerekçesi zorunlu ve geçmiş korunuyor, şef onayı + yönetici bildirimi, tekrarlanan onayın etkisiz kalması,
  personelin/diğer şefin onay verememesi, iade gerekçesi, ek bilgi isteme, iptal, eksiksiz durum geçmişi.
- **Malzeme / not / dosya:** negatif-sıfır-çok ondalıklı miktar reddi, miktar birleştirme, yetkisiz kullanıcının dosya kimliğini
  bilse de açamaması, yasak tür ve boyut reddi, dosya kaldırmanın denetim kaydına geçmesi.
- **Rapor ve yetki:** iş sayısı ≠ atama sayısı, raporların birim kapsamı, pasif personelin geçmişinin korunması,
  CSV formül kaçışı.
- **HTTP:** anonim kullanıcı girişe yönlenir, personel yönetim sayfalarını adres yazarak açamaz, onay POST'u reddedilir,
  taslak iş personele 404, CSRF jetonu olmayan POST 400, tüm ana sayfalar planlamacı/yönetici için açılıyor.

Ek olarak tarayıcıda uçtan uca denendi: planlamacı girişi → havuzdan sürükle-bırak ve "İşe ekle" ile ekip → personele gönder →
telefon boyutunda personel girişi → kabul → fotoğraf yükleme → şef girişi → onay → tamamlandı; yeni iş formunda malzeme paneli.

---

## 9. Bilinen eksikler / sonraki adımlar

- E-posta/SMS bildirimi yok (uygulama içi bildirim var; `NotificationDelivery` + arka plan gönderici eklenebilir).
- Parola sıfırlama e-postası yok; yönetici "Kullanıcılar" ekranından yeni parola verir.
- Excel (.xlsx) yerine CSV; Excel'de doğrudan açılır.
- Harita/koordinat, kontrol listesi, QR, çevrimdışı çalışma, stok entegrasyonu kapsam dışı (şartname 17. bölüm).
- Fotoğraflar küçültülmeden saklanır (10 MB sınırı); küçük önizleme tarayıcıda ölçeklenir.
- Not düzenleme yok (bilinçli: notlar değiştirilemez).
- Gecikme eşiği bildirimi (isteğe bağlı) yok; geciken işler panelde ve raporda görünür.
