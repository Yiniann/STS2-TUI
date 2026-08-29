[CmdletBinding()]
param(
    [string]$Python = "python",
    [string]$OutputDirectory = "artifacts"
)

$ErrorActionPreference = "Stop"
$RepoDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$OutputDirectory = Join-Path $RepoDir $OutputDirectory
$BuildDirectory = Join-Path $RepoDir "build\windows"
$DistDirectory = Join-Path $RepoDir "dist\windows"
$StageDirectory = Join-Path $BuildDirectory "STS2-TUI-Windows-x64"
$ZipPath = Join-Path $OutputDirectory "STS2-TUI-Windows-x64.zip"

Remove-Item $BuildDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $DistDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $DistDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

& $Python -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot "requirements-build.txt")
if ($LASTEXITCODE -ne 0) { throw "Could not install Windows build dependencies" }

& $Python -m PyInstaller `
    --noconfirm `
    --clean `
    --onefile `
    --console `
    --name "STS2-TUI" `
    --paths (Join-Path $RepoDir "python") `
    --hidden-import "tui" `
    --hidden-import "curses" `
    --workpath (Join-Path $BuildDirectory "pyinstaller") `
    --specpath (Join-Path $BuildDirectory "spec") `
    --distpath $DistDirectory `
    (Join-Path $RepoDir "python\play.py")
if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Path $StageDirectory -Force | Out-Null
Copy-Item (Join-Path $DistDirectory "STS2-TUI.exe") $StageDirectory
Copy-Item (Join-Path $RepoDir "STS2-TUI.cmd") $StageDirectory
Copy-Item (Join-Path $RepoDir "setup.ps1") $StageDirectory
Copy-Item (Join-Path $RepoDir "README.md") $StageDirectory
Copy-Item (Join-Path $RepoDir "LICENSE") $StageDirectory
Copy-Item (Join-Path $RepoDir "localization_eng") $StageDirectory -Recurse
Copy-Item (Join-Path $RepoDir "localization_zhs") $StageDirectory -Recurse

$sourceTarget = Join-Path $StageDirectory "src"
New-Item -ItemType Directory -Path $sourceTarget -Force | Out-Null
& robocopy (Join-Path $RepoDir "src") $sourceTarget /E /XD bin obj | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Could not copy backend source (robocopy exit $LASTEXITCODE)" }

Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $StageDirectory -DestinationPath $ZipPath -CompressionLevel Optimal
Write-Host "Created $ZipPath"
