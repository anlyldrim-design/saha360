# Saha360 - SUNUCUDA calistirilir: yeni surumu (zip) IIS sitesine kurar.
#
# Ne yapar:
#   1. Mevcut site klasorunun yedegini alir (App_Data haric)  -> <Site>_yedek_<tarih>
#   2. app_offline.htm koyar (IIS uygulamayi durdurur, dosyalar serbest kalir)
#   3. Zip icindeki yeni dosyalari kopyalar; appsettings.Production.json ve App_Data'ya DOKUNMAZ
#   4. app_offline.htm'yi siler (uygulama yeni surumle acilir, veritabani guncellemesi otomatik yapilir)
#
# Kullanim (sunucuda, yonetici PowerShell):
#   powershell -ExecutionPolicy Bypass -File .\sunucu-guncelle.ps1 -Zip .\Saha360-guncelleme.zip -Site C:\inetpub\Saha360
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [string]$Site = 'C:\inetpub\Saha360',
    [switch]$NoBackup
)
$ErrorActionPreference = 'Stop'

$Zip = (Resolve-Path $Zip).Path
if (-not (Test-Path (Join-Path $Site 'Saha360.Web.dll'))) {
    throw "Site klasorunde Saha360.Web.dll yok: $Site  (-Site ile dogru klasoru verin; ilk kurulumsa docs\YAYINLAMA.md'ye bakin)"
}
if (-not (Test-Path (Join-Path $Site 'appsettings.Production.json'))) {
    Write-Host 'UYARI: Sitede appsettings.Production.json yok. Veritabani ve yonetici ayarlarini kontrol edin.' -ForegroundColor Yellow
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$temp = Join-Path $env:TEMP "saha360-$stamp"
Expand-Archive -Path $Zip -DestinationPath $temp -Force
if (-not (Test-Path (Join-Path $temp 'Saha360.Web.dll'))) { throw 'Zip beklenen dosyalari icermiyor (Saha360.Web.dll yok).' }

if (-not $NoBackup) {
    $backup = "$Site`_yedek_$stamp"
    Write-Host "Yedek aliniyor: $backup"
    robocopy $Site $backup /E /XD (Join-Path $Site 'App_Data') /NFL /NDL /NJH /NJS /NP | Out-Null
}

$offline = Join-Path $Site 'app_offline.htm'
Set-Content -Path $offline -Encoding UTF8 -Value '<!doctype html><meta charset="utf-8"><title>Bakim</title><body style="font-family:sans-serif;text-align:center;padding-top:15vh"><h1>Saha360 guncelleniyor</h1><p>Birkac dakika icinde tekrar deneyin.</p></body>'
Write-Host 'Uygulama durduruldu (app_offline.htm). Dosyalar kopyalaniyor...'
Start-Sleep -Seconds 3

try {
    # /XF: sunucuya ozel ayar dosyasi ve bakim sayfasi ezilmez; /XD: yuklenen fotograflar ve oturum anahtarlari korunur.
    robocopy $temp $Site /E /XF appsettings.Production.json app_offline.htm /XD App_Data /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Kopyalama basarisiz (robocopy kodu $LASTEXITCODE)." }
}
finally {
    Remove-Item $offline -Force -ErrorAction SilentlyContinue
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Tamamlandi. Siteyi tarayicida acin; ilk acilis veritabani guncellemesi nedeniyle birkac saniye surebilir." -ForegroundColor Green
if (-not $NoBackup) { Write-Host "Sorun olursa geri donmek icin yedek: $backup" }
