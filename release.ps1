param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$Repo = "Gabix-lab/MvpLauncher"
$csprojPath = "MvpLauncher/MvpLauncher.csproj"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "HIBA: Rossz verzió formatum! Hasznalj: 1.0.0 (X.Y.Z)" -ForegroundColor Red
    Write-Host "Pelda: .\release.ps1 -Version 1.0.1" -ForegroundColor Yellow
    exit 1
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "HIBA: gh CLI nincs telepitve! Futtasd: winget install --id GitHub.cli" -ForegroundColor Red
    exit 1
}

$ghUser = (gh api user --jq .login 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $ghUser) {
    Write-Host "HIBA: gh nincs bejelentkezve! Futtasd: gh auth login" -ForegroundColor Red
    exit 1
}
Write-Host "Bejelentkezett GitHub fiók: $ghUser" -ForegroundColor Green

Write-Host ""
Write-Host "MVP Launcher v$Version kiadása indul..." -ForegroundColor Cyan
Write-Host ""

Write-Host "Verziószám frissítése a csproj-ban..." -ForegroundColor Yellow
$csprojContent = Get-Content $csprojPath -Raw
$csprojContent = $csprojContent -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>"
Set-Content $csprojPath -Value $csprojContent -NoNewline

Write-Host "Build..." -ForegroundColor Yellow
dotnet publish $csprojPath -c Release -r win-x64 --self-contained false -o publish

if ($LASTEXITCODE -ne 0) {
    Write-Host "HIBA: Build sikertelen!" -ForegroundColor Red
    exit 1
}

Write-Host "Pack (Velopack csomagolás)..." -ForegroundColor Yellow
vpk pack --packId MvpLauncher --packVersion $Version --packDir publish --mainExe MvpLauncher.exe --packTitle "MVP Launcher" --packAuthors "MVP Team" --icon MvpLauncher/icon.ico --runtime win-x64

if ($LASTEXITCODE -ne 0) {
    Write-Host "HIBA: Pack sikertelen!" -ForegroundColor Red
    exit 1
}

$releaseDir = "Releases"
if (-not (Test-Path $releaseDir)) {
    Write-Host "HIBA: A Releases mappa nem talalhato!" -ForegroundColor Red
    exit 1
}

$releaseFiles = Get-ChildItem $releaseDir | Where-Object { 
    $_.Name -like "*$Version*" -or 
    $_.Name -eq "RELEASES" -or 
    ($_.Name -like "*.json" -and $_.Name -ne "assets.win.json") -or 
    $_.Name -eq "MvpLauncher-win-Portable.zip" -or 
    $_.Name -eq "MvpLauncher-win-Setup.exe"
} | Select-Object -ExpandProperty FullName

if (-not $releaseFiles) {
    Write-Host "HIBA: Nem talalhatok a $Version verzióhoz tartozó fajlok a $releaseDir mappaban!" -ForegroundColor Red
    exit 1
}

Write-Host "Feltöltendő fájlok:" -ForegroundColor Cyan
$releaseFiles | ForEach-Object { Write-Host "  $_" }

Write-Host "Git commit + tag + push..." -ForegroundColor Yellow

$existingTag = git tag -l "v$Version"
if ($existingTag) {
    Write-Host "HIBA: A v$Version tag mar letezik!" -ForegroundColor Red
    Write-Host "Torles: git tag -d v$Version ; git push origin :refs/tags/v$Version" -ForegroundColor Yellow
    exit 1
}

git add $csprojPath
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    git commit -m "Release v$Version"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "HIBA: git commit sikertelen!" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Nincs valtozas, commit kihagyva." -ForegroundColor Yellow
}

git tag "v$Version"
git push
if ($LASTEXITCODE -ne 0) {
    Write-Host "HIBA: git push sikertelen!" -ForegroundColor Red
    exit 1
}
git push origin "v$Version"
if ($LASTEXITCODE -ne 0) {
    Write-Host "HIBA: tag push sikertelen!" -ForegroundColor Red
    exit 1
}

Write-Host "Feltoltes GitHub-ra a gh CLI-val..." -ForegroundColor Yellow
gh release create "v$Version" $releaseFiles `
    --repo $Repo `
    --title "MVP Launcher v$Version" `
    --notes "Release v$Version"

if ($LASTEXITCODE -ne 0) {
    Write-Host "HIBA: GitHub Release feltoltes sikertelen!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "KESZ! v$Version publikálva!" -ForegroundColor Green
Write-Host ""
Write-Host "Letoltesi link:" -ForegroundColor Cyan
Write-Host "https://github.com/$Repo/releases/latest" -ForegroundColor Cyan