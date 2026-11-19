$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$hdr = Join-Path $env:TEMP ("zwmenu-rel-" + [guid]::NewGuid().ToString("N") + ".txt")
$tokenPath = Join-Path $root ".github-token"
if (-not (Test-Path -LiteralPath $tokenPath)) { throw "Net fayla .github-token" }
$token = ([IO.File]::ReadAllText($tokenPath)).Trim()
if ($token.Length -lt 20) { throw "Fayl .github-token pustoy" }

$csproj = [IO.File]::ReadAllText((Join-Path $root "Zwmenu\Zwmenu.csproj"))
$versionMatch = [regex]::Match($csproj, "<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>")
if (-not $versionMatch.Success) { throw "V csproj net Version" }
$version = $versionMatch.Groups[1].Value
$iss = [IO.File]::ReadAllText((Join-Path $root "installer\Zwmenu.iss"))
$issMatch = [regex]::Match($iss, '#define MyAppVersion "([^"]+)"')
if (-not $issMatch.Success) { throw "V ustanovshike net versii" }
if ($issMatch.Groups[1].Value -ne $version) { throw "Versiya ustanovshika ne sovpadaet s $version" }
if ($iss -notmatch [regex]::Escape("VersionInfoVersion=$version.0")) { throw "VersionInfoVersion ne $version.0" }

Write-Output "Sobirayu programmu $version"
& dotnet publish (Join-Path $root "Zwmenu\Zwmenu.csproj") -c Release -r win-x64 --self-contained true "-p:DebugType=none" "-p:DebugSymbols=false" -o (Join-Path $root "publish\win-x64")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $root "publish\win-x64\Zwmenu.exe"
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($fileVersion -ne "${version}.0") { throw "V programme versiya $fileVersion, a v proekte $version" }

Write-Output "Sobirayu ustanovshik"
& (Join-Path $root "tools\innosetup\ISCC.exe") (Join-Path $root "installer\Zwmenu.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Join-Path $root "dist\Zwmenu-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Net dist\Zwmenu-Setup.exe" }
Copy-Item -LiteralPath $setup -Destination (Join-Path $env:USERPROFILE "Desktop\Zwmenu-Setup.exe") -Force
Write-Output "Kopiya na rabochem stole"

$out = Join-Path $env:TEMP "zwmenu-rel-out.json"
try {
  [IO.File]::WriteAllText($hdr, "Authorization: Bearer $token`r`nUser-Agent: Zwmenu`r`nAccept: application/vnd.github+json`r`n")

  function Gh([string]$method, [string]$url, [string]$bodyFile) {
    $curl = @("-sS", "-o", $out, "-w", "%{http_code}", "-X", $method, "-H", "@$hdr")
    if ($bodyFile) { $curl += @("-H", "Content-Type: application/json", "--data-binary", "@$bodyFile") }
    $curl += $url
    $code = & curl.exe @curl
    if ($LASTEXITCODE -ne 0) { throw "Set ne otvetila" }
    return "$code".Trim()
  }

  $code = Gh "GET" "https://api.github.com/repos/Z-oobastik-s/Zwmenu/releases/tags/v$version" ""
  if ($code -eq "404") {
    $meta = Join-Path $env:TEMP "zwmenu-rel-body.json"
    $json = @{ tag_name = "v$version"; name = $version; body = @"
Памятка у кубика в трее написана обычными словами: где кнопка и что она делает.
"@; draft = $false; prerelease = $false } | ConvertTo-Json
    [IO.File]::WriteAllText($meta, $json)
    $code = Gh "POST" "https://api.github.com/repos/Z-oobastik-s/Zwmenu/releases" $meta
    if ($code -ne "201") { throw "GitHub ne sozdal reliz ($code)" }
  } elseif ($code -ne "200") {
    throw "GitHub otvetil $code"
  }

  $rel = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
  $has = $false
  if ($rel.assets) {
    foreach ($asset in $rel.assets) {
      if ($asset.name -eq "Zwmenu-Setup.exe") { $has = $true }
    }
  }
  if ($has) {
    Write-Output "Reliz v${version} uzhe s ustanovshikom: https://github.com/Z-oobastik-s/Zwmenu/releases/tag/v${version}"
    exit 0
  }

  Write-Output "Zagruzhayu ustanovshik"
  $upload = "https://uploads.github.com/repos/Z-oobastik-s/Zwmenu/releases/$($rel.id)/assets?name=Zwmenu-Setup.exe"
  $code = & curl.exe --progress-bar -S -o $out -w "%{http_code}" -X POST -H "@$hdr" -H "Content-Type: application/octet-stream" --data-binary "@$setup" $upload
  if ($LASTEXITCODE -ne 0) { throw "Zagruzka oborvalas" }
  if ("$code".Trim() -ne "201") { throw "GitHub ne prinyal ustanovshik ($code)" }
  Write-Output "Reliz v${version}: https://github.com/Z-oobastik-s/Zwmenu/releases/tag/v${version}"
}
finally {
  if (Test-Path -LiteralPath $hdr) { Remove-Item -LiteralPath $hdr -Force }
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
}