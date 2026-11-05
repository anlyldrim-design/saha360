# Saha360 yayin paketi: Release derler, yayin\Saha360 klasorune koyar.
# Onceden olusturulmus appsettings.Production.json (sunucu ayarlari) korunur; yoksa varsayilanla olusturulur.
# Kullanim:  powershell -ExecutionPolicy Bypass -File .\yayinla.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'yayin\Saha360'
$prod = Join-Path $out 'appsettings.Production.json'
$keep = $null
if (Test-Path $prod) { $keep = Get-Content $prod -Raw -Encoding UTF8 }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'src\Saha360.Web') -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'Derleme basarisiz.' }

$utf8 = New-Object System.Text.UTF8Encoding $false
if ($keep) {
    [System.IO.File]::WriteAllText($prod, $keep, $utf8)
    Write-Host 'appsettings.Production.json korundu.'
} else {
    # Ilk paket: Matasis Flow ile ayni baglanti sekli (.\SQLEXPRESS, Windows kimligi). Ilk yonetici parolasi rastgele uretilir.
    $chars = 'abcdefghjkmnpqrstuvwxyz23456789'.ToCharArray()
    $bytes = New-Object byte[] 14
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $pw = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    $json = @"
{
  "ConnectionStrings": {
    "Default": "Server=.\\SQLEXPRESS;Database=Saha360;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  },
  "Database": {
    "MigrateOnStartup": true
  },
  "Bootstrap": {
    "AdminUserName": "admin",
    "AdminPassword": "$pw",
    "AdminFullName": "Sistem Y\u00f6neticisi"
  },
  "Seed": {
    "Demo": false
  },
  "AllowedHosts": "*"
}
"@
    [System.IO.File]::WriteAllText($prod, $json, $utf8)
    Write-Host 'appsettings.Production.json olusturuldu (ilk yonetici: admin, parola dosyanin icinde).' -ForegroundColor Yellow
}
Write-Host "Hazir: $out" -ForegroundColor Green

# Guncelleme paketi: sunucuya tasinacak tek dosya. Sunucuya ozel ayar dosyasi icine konmaz (sunucudakini ezmesin).
$zip = Join-Path $root 'yayin\Saha360-guncelleme.zip'
$stage = Join-Path $env:TEMP ('s360-zip-' + [guid]::NewGuid().ToString('N'))
robocopy $out $stage /E /XF appsettings.Production.json /NFL /NDL /NJH /NJS /NP | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
Copy-Item (Join-Path $root 'sunucu-guncelle.ps1') (Join-Path $root 'yayin\sunucu-guncelle.ps1') -Force
Write-Host ("Guncelleme paketi: {0} ({1:N1} MB) + yayin\sunucu-guncelle.ps1" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
