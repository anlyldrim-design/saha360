# Saha360'ı sunucuda yayınlama (Windows Server + IIS)

Kurulum Matasis Flow ile aynıdır: aynı sunucudaki `.\SQLEXPRESS`, Windows kimliğiyle bağlantı (`Trusted_Connection`),
veritabanını uygulama ilk açılışta kendisi oluşturur.

## 1. Yayın paketini al (kendi bilgisayarında)

Proje klasöründe (`Saha360`):

```bash
powershell -ExecutionPolicy Bypass -File .\yayinla.ps1
```

Çıktı:

| Dosya | Ne için |
|---|---|
| `yayin\Saha360\` | İlk kurulumda sunucuya kopyalanacak klasörün tamamı |
| `yayin\Saha360-guncelleme.zip` + `yayin\sunucu-guncelle.ps1` | Sonraki güncellemeler için |

Geliştirme ayarları pakete girmez. `yayin\Saha360\appsettings.Production.json` sunucu ayarlarıdır; ilk çalıştırmada oluşturulur,
sonraki çalıştırmalarda korunur.

## 2. Sunucuya kurulacaklar

Matasis Flow zaten çalışıyorsa bunlar kuruludur, bu adımı atlayın.

- **.NET 10 Hosting Bundle** (kurulumdan sonra `iisreset`)
- **IIS**
- **SQL Server Express**

## 3. Ayarlar: `appsettings.Production.json`

```json
{
  "ConnectionStrings": { "Default": "Server=.\\SQLEXPRESS;Database=Saha360;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true" },
  "Database": { "MigrateOnStartup": true },
  "Bootstrap": { "AdminUserName": "admin", "AdminPassword": "(rastgele üretildi)", "AdminFullName": "Sistem Yöneticisi" },
  "Seed": { "Demo": false },
  "AllowedHosts": "*"
}
```

- **ConnectionStrings:Default:** Matasis Flow'daki bağlantının aynısı, yalnız veritabanı adı `Saha360`. SQL başka sunucudaysa
  `Server=SQLSUNUCU\\SQLEXPRESS` yazın. SQL kullanıcısıyla bağlanmak için `Trusted_Connection=True` yerine `User Id=…;Password=…` kullanın.
- **Database:MigrateOnStartup: true:** Veritabanı ve tablo güncellemeleri açılışta otomatik yapılır.
- **Bootstrap:** Sistemde hiç yönetici yokken ilk yönetici bu bilgilerle oluşturulur. İlk girişten sonra parolanızı
  değiştirin, ardından `AdminPassword` değerini `""` yapın. Yönetici varken bu alan hiçbir şeyi değiştirmez.
- **AllowedHosts:** İsterseniz `saha360.matasis.com` yapın; uygulama yalnız bu adresten yanıt verir.

## 4. Dosyaları kopyala

`yayin\Saha360` klasörünün içeriğini sunucuda `C:\inetpub\Saha360` klasörüne kopyalayın.

## 5. IIS'te siteyi oluştur

1. **Uygulama Havuzları** → *Ekle*. Ad: `Saha360`. **.NET CLR sürümü: Yönetilen kod yok**.
2. **Siteler** → *Web Sitesi Ekle*:
   - Ad: `Saha360`
   - Havuz: `Saha360`
   - Fiziksel yol: `C:\inetpub\Saha360`
   - Bağlama: `http`, port `80`, ana bilgisayar adı `saha360.matasis.com`
3. Fotoğraf ve oturum anahtarı klasörüne yazma izni verin (yönetici PowerShell):

```bash
New-Item -ItemType Directory -Force C:\inetpub\Saha360\App_Data; icacls C:\inetpub\Saha360\App_Data /grant "IIS APPPOOL\Saha360:(OI)(CI)M"
```

## 6. Veritabanı izni (SSMS'te)

Matasis Flow'da yaptığınızın aynısı, havuz adı `Saha360`. İlk açılışta veritabanını uygulama oluşturur:

```sql
CREATE LOGIN [IIS APPPOOL\Saha360] FROM WINDOWS;
ALTER SERVER ROLE dbcreator ADD MEMBER [IIS APPPOOL\Saha360];
```

İlk açılıştan sonra yetkiyi yalnızca bu veritabanıyla sınırlayın (daha güvenli):

```sql
USE Saha360; CREATE USER [IIS APPPOOL\Saha360] FOR LOGIN [IIS APPPOOL\Saha360]; ALTER ROLE db_owner ADD MEMBER [IIS APPPOOL\Saha360];
USE master; ALTER SERVER ROLE dbcreator DROP MEMBER [IIS APPPOOL\Saha360];
```

## 7. İlk açılış

1. `http://saha360.matasis.com` adresini açın. İlk açılış, veritabanı kurulduğu için birkaç saniye sürer.
2. Kullanıcı adı `admin`, parola `appsettings.Production.json` içindeki `AdminPassword` ile giriş yapın.
3. Sağ üstten parolanızı değiştirin ve dosyadaki `AdminPassword` değerini silin.
4. Sırayla şunları girin: Tanımlar (birimler, unvanlar) → Kullanıcılar ve personel (Excel toplu yükleme de olur) →
   Yetkiler ekranında rollerin izinlerini kontrol edin.

## 8. Alan adı ve HTTPS

1. **DNS** (matasis.com paneli): `saha360` için sunucu IP'sine **A** kaydı ekleyin.
2. **Sertifika:** win-acme ile ücretsiz Let's Encrypt sertifikası alın. win-acme IIS'teki `saha360.matasis.com` bağlamasını
   bulur ve `https` bağlamasını kendisi ekler.
3. HTTPS bağlaması olunca uygulama http isteklerini https'e yönlendirir.

## 9. Güncelleme

1. Kendi bilgisayarınızda `yayinla.ps1` çalıştırın.
2. `yayin\Saha360-guncelleme.zip` ve `yayin\sunucu-guncelle.ps1` dosyalarını sunucuya taşıyın.
3. Sunucuda, bu dosyaların olduğu klasörde yönetici PowerShell ile çalıştırın:

```bash
powershell -ExecutionPolicy Bypass -File .\sunucu-guncelle.ps1 -Zip .\Saha360-guncelleme.zip -Site C:\inetpub\Saha360
```

Betik önce siteyi yedekler, sonra uygulamayı kısa süre durdurup yeni dosyaları kopyalar. `appsettings.Production.json` ve
`App_Data` (fotoğraflar, oturum anahtarları) korunur. Veritabanı güncellemesi açılışta otomatik yapılır.

**Yedek:** SQL Server'daki `Saha360` veritabanını ve `C:\inetpub\Saha360\App_Data\uploads` klasörünü düzenli yedekleyin.

## Sorun giderme

- **500.19 / 500.31:** Hosting Bundle eksik ya da kurulumdan sonra `iisreset` yapılmamış.
- **Nedeni belli olmayan açılmama:**
  1. `web.config` içinde `stdoutLogEnabled="true"` yapın.
  2. `logs` klasörünü oluşturup havuza yazma izni verin.
  3. Hatayı `logs\stdout_*.log` dosyasında okuyun. İş bitince ayarı geri `false` yapın.
- **Veritabanına bağlanamıyor:** 6. adımdaki yetkiyi ya da bağlantı cümlesindeki sunucu adını kontrol edin.
- **Giriş yapamıyorum:** Yönetici zaten oluşmuşsa `Bootstrap` ayarı bir şey değiştirmez. Parolayı unuttuysanız bana yazın.
