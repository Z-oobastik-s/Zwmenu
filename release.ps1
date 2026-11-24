param([switch]$FullOnly)
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

New-Item -ItemType Directory -Force -Path (Join-Path $root "dist") | Out-Null
$pub = Join-Path $root "publish\win-x64"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security

function Get-RelPath([string]$baseDir, [string]$full) {
  $b = [IO.Path]::GetFullPath($baseDir).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
  $f = [IO.Path]::GetFullPath($full)
  if (-not $f.StartsWith($b, [StringComparison]::OrdinalIgnoreCase)) { throw "Fail vne publish: $full" }
  return $f.Substring($b.Length).Replace('\', '/')
}

function Get-Sha256([string]$path) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try {
    $stream = [IO.File]::OpenRead($path)
    try {
      return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
    } finally { $stream.Dispose() }
  } finally { $sha.Dispose() }
}

function Build-Manifest([string]$dir) {
  $files = @()
  Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = Get-RelPath $dir $_.FullName
    $files += [ordered]@{
      path = $rel
      sha256 = (Get-Sha256 $_.FullName)
      size = [int64]$_.Length
    }
  }
  return [ordered]@{ version = $version; files = $files }
}

Write-Output "Sobirayu manifest"
$manifestObj = Build-Manifest $pub
$manifestPath = Join-Path $root "dist\Zwmenu.manifest.json"
$manifestJson = ($manifestObj | ConvertTo-Json -Depth 6 -Compress)
[IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))
Write-Output ("Manifest: " + $manifestObj.files.Count + " faylov")

$out = Join-Path $env:TEMP "zwmenu-rel-out.json"
[IO.File]::WriteAllText($hdr, "Authorization: Bearer $token`r`nUser-Agent: Zwmenu`r`nAccept: application/vnd.github+json`r`n")

function Gh([string]$method, [string]$url, [string]$bodyFile) {
  $curl = @("-sS", "-o", $out, "-w", "%{http_code}", "-X", $method, "-H", "@$hdr")
  if ($bodyFile) { $curl += @("-H", "Content-Type: application/json", "--data-binary", "@$bodyFile") }
  $curl += $url
  $code = & curl.exe @curl
  if ($LASTEXITCODE -ne 0) { throw "Set ne otvetila" }
  return "$code".Trim()
}

function Find-PreviousManifest {
  $localPrev = Join-Path $root "dist\last.manifest.json"
  if (Test-Path -LiteralPath $localPrev) {
    $text = [IO.File]::ReadAllText($localPrev, [Text.Encoding]::UTF8).Trim()
    if ($text.StartsWith("{") -and $text.Contains('"files"')) {
      Write-Host "Beru lokalnyy last.manifest.json"
      return $text
    }
  }

  Write-Host "Ishchu predydushchiy manifest na GitHub"
  $code = Gh "GET" "https://api.github.com/repos/Z-oobastik-s/Zwmenu/releases?per_page=20" ""
  if ($code -ne "200") { return $null }
  $releases = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
  foreach ($rel in $releases) {
    if ($rel.tag_name -eq "v$version") { continue }
    if (-not $rel.assets) { continue }
    $asset = $rel.assets | Where-Object { $_.name -eq "Zwmenu.manifest.json" } | Select-Object -First 1
    if (-not $asset) { continue }
    Write-Host ("Nayden manifest " + $rel.tag_name)
    $tmp = Join-Path $env:TEMP ("zwmenu-prev-manifest-" + [guid]::NewGuid().ToString("N") + ".json")
    & curl.exe -sS -L -o $tmp -H "@$hdr" $asset.browser_download_url
    if ($LASTEXITCODE -ne 0) { continue }
    $text = [IO.File]::ReadAllText($tmp, [Text.Encoding]::UTF8).Trim()
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    if ($text.StartsWith("{") -and $text.Contains('"files"')) { return $text }
  }
  return $null
}

$patch = Join-Path $root "dist\Zwmenu-Patch.zip"
$patchReady = $false
$prevText = Find-PreviousManifest
$changed = New-Object System.Collections.Generic.List[object]
$prevMap = @{}
if ($null -ne $prevText -and "$prevText".Trim().StartsWith("{")) {
  $prev = "$prevText" | ConvertFrom-Json
  foreach ($f in $prev.files) {
    $prevMap[$f.path] = $f.sha256
  }
  foreach ($f in $manifestObj.files) {
    $old = $prevMap[$f.path]
    if (-not $old -or $old -ne $f.sha256) {
      $changed.Add($f)
    }
  }
} else {
  Write-Host "Predydushchego manifesta net: zaplatku ne sobirayu, tolko ustanovshik"
}

if (-not $FullOnly -and $changed.Count -gt 0) {
  $must = @("Zwmenu.exe", "Zwmenu.dll")
  foreach ($name in $must) {
    if (-not ($changed | Where-Object { $_.path -eq $name })) {
      $row = $manifestObj.files | Where-Object { $_.path -eq $name } | Select-Object -First 1
      if ($row) { $changed.Add($row) }
    }
  }

  $stage = Join-Path $env:TEMP ("zwmenu-patch-" + [guid]::NewGuid().ToString("N"))
  New-Item -ItemType Directory -Force -Path $stage | Out-Null
  try {
    $patchFiles = @()
    foreach ($f in $changed) {
      $src = Join-Path $pub ($f.path -replace '/', [IO.Path]::DirectorySeparatorChar)
      if (-not (Test-Path -LiteralPath $src)) { throw "Net fayla dlya zaplatki: $($f.path)" }
      $dest = Join-Path $stage ($f.path -replace '/', [IO.Path]::DirectorySeparatorChar)
      $destDir = Split-Path -Parent $dest
      if ($destDir) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
      Copy-Item -LiteralPath $src -Destination $dest -Force
      $patchFiles += [ordered]@{ path = $f.path; sha256 = $f.sha256; size = $f.size }
    }

    $patchIndex = [ordered]@{
      version = $version
      files = $patchFiles
    }
    $indexPath = Join-Path $stage "Zwmenu.patch.json"
    [IO.File]::WriteAllText($indexPath, ($patchIndex | ConvertTo-Json -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $patch) { Remove-Item -LiteralPath $patch -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $patch, [IO.Compression.CompressionLevel]::Optimal, $false)

    $patchSize = (Get-Item -LiteralPath $patch).Length
    if ($patchSize -lt 1000) { throw "Zaplatka slishkom malenka" }
    if ($patchSize -ge 40000000) {
      Write-Output ("Zaplatka " + $patchSize + " bait, eto slishkom mnogo. Gruzhu tolko ustanovshik.")
      Remove-Item -LiteralPath $patch -Force
    } else {
      $zip = [IO.Compression.ZipFile]::OpenRead($patch)
      try {
        $names = @{}
        foreach ($entry in $zip.Entries) {
          $name = $entry.FullName.Replace('\', '/').Trim('/')
          if ($name.Length -eq 0) { continue }
          $names[$name] = $true
        }
        if (-not $names.ContainsKey("Zwmenu.patch.json")) { throw "V zaplate net Zwmenu.patch.json" }
        if (-not $names.ContainsKey("Zwmenu.exe")) { throw "V zaplate net Zwmenu.exe" }
        if (-not $names.ContainsKey("Zwmenu.dll")) { throw "V zaplate net Zwmenu.dll" }
        foreach ($f in $patchFiles) {
          if (-not $names.ContainsKey($f.path)) { throw "V zaplate net $($f.path)" }
        }
      } finally {
        $zip.Dispose()
      }
      $patchReady = $true
      Write-Output ("Zaplatka gotova: " + $patchFiles.Count + " faylov, " + $patchSize + " bait")
    }
  } finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
  }
}

Write-Output "Sobirayu ustanovshik"
& (Join-Path $root "tools\innosetup\ISCC.exe") (Join-Path $root "installer\Zwmenu.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Join-Path $root "dist\Zwmenu-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Net dist\Zwmenu-Setup.exe" }
Copy-Item -LiteralPath $setup -Destination (Join-Path $env:USERPROFILE "Desktop\Zwmenu-Setup.exe") -Force
Write-Output "Kopiya na rabochem stole"

try {
  $code = Gh "GET" "https://api.github.com/repos/Z-oobastik-s/Zwmenu/releases/tags/v$version" ""
  if ($code -eq "404") {
    $meta = Join-Path $env:TEMP "zwmenu-rel-body.json"
    $note = [IO.File]::ReadAllText((Join-Path $root "installer\note.txt"), [Text.Encoding]::UTF8).Trim()
    $json = @{ tag_name = "v$version"; name = $version; body = $note; draft = $false; prerelease = $false } | ConvertTo-Json
    [IO.File]::WriteAllText($meta, $json, [Text.UTF8Encoding]::new($false))
    $code = Gh "POST" "https://api.github.com/repos/Z-oobastik-s/Zwmenu/releases" $meta
    if ($code -ne "201") { throw "GitHub ne sozdal reliz ($code)" }
  } elseif ($code -ne "200") {
    throw "GitHub otvetil $code"
  }

  $rel = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
  $hasSetup = $false
  $hasPatch = $false
  $hasManifest = $false
  if ($rel.assets) {
    foreach ($asset in $rel.assets) {
      if ($asset.name -eq "Zwmenu-Setup.exe") { $hasSetup = $true }
      if ($asset.name -eq "Zwmenu-Patch.zip") { $hasPatch = $true }
      if ($asset.name -eq "Zwmenu.manifest.json") { $hasManifest = $true }
    }
  }

  if (-not $hasSetup) {
    Write-Output "Zagruzhayu ustanovshik"
    $upload = "https://uploads.github.com/repos/Z-oobastik-s/Zwmenu/releases/$($rel.id)/assets?name=Zwmenu-Setup.exe"
    $code = & curl.exe --progress-bar -S -o $out -w "%{http_code}" -X POST -H "@$hdr" -H "Content-Type: application/octet-stream" --data-binary "@$setup" $upload
    if ($LASTEXITCODE -ne 0) { throw "Zagruzka oborvalas" }
    if ("$code".Trim() -ne "201") { throw "GitHub ne prinyal ustanovshik ($code)" }
  }

  if (-not $hasManifest) {
    Write-Output "Zagruzhayu manifest"
    $upload = "https://uploads.github.com/repos/Z-oobastik-s/Zwmenu/releases/$($rel.id)/assets?name=Zwmenu.manifest.json"
    $code = & curl.exe --progress-bar -S -o $out -w "%{http_code}" -X POST -H "@$hdr" -H "Content-Type: application/json" --data-binary "@$manifestPath" $upload
    if ($LASTEXITCODE -ne 0) { throw "Zagruzka manifesta oborvalas" }
    if ("$code".Trim() -ne "201") { throw "GitHub ne prinyal manifest ($code)" }
  }

  if ($patchReady -and -not $FullOnly -and -not $hasPatch) {
    Write-Output "Zagruzhayu zaplatku"
    $upload = "https://uploads.github.com/repos/Z-oobastik-s/Zwmenu/releases/$($rel.id)/assets?name=Zwmenu-Patch.zip"
    $code = & curl.exe --progress-bar -S -o $out -w "%{http_code}" -X POST -H "@$hdr" -H "Content-Type: application/zip" --data-binary "@$patch" $upload
    if ($LASTEXITCODE -ne 0) { throw "Zagruzka zaplatki oborvalas" }
    if ("$code".Trim() -ne "201") { throw "GitHub ne prinyal zaplatku ($code)" }
  }

  Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $root "dist\last.manifest.json") -Force
  Write-Output "Reliz v${version}: https://github.com/Z-oobastik-s/Zwmenu/releases/tag/v${version}"
}
finally {
  if (Test-Path -LiteralPath $hdr) { Remove-Item -LiteralPath $hdr -Force }
  if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
}
