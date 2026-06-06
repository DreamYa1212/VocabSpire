# VocabSpire 打包脚本
# 用法: .\package.ps1
# 先构建，再打包 zip 到 publish/VocabSpire.zip

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = "$root/VocabSpire/.godot/mono/temp/bin/Release"
$dst = "$root/publish/VocabSpire"
$zip = "$root/publish/VocabSpire.zip"

# 构建
Write-Host "=== Building ===" -ForegroundColor Cyan
Push-Location "$root/VocabSpire"
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Pop-Location

# 打包
Write-Host "=== Packing ===" -ForegroundColor Cyan
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
New-Item -ItemType Directory -Path "$dst/wordbanks" -Force | Out-Null

Copy-Item "$src/VocabSpire.dll"               $dst/
Copy-Item "$src/VocabSpire.pdb"               $dst/
Copy-Item "$src/VocabSpire.json"              $dst/
Copy-Item "$src/VocabSpire.deps.json"         $dst/
Copy-Item "$src/VocabSpire.runtimeconfig.json" $dst/
Copy-Item "$src/GodotSharp.dll"               $dst/
Copy-Item "$src/ZstdSharp.dll"                $dst/
Copy-Item "$root/VocabSpire/Resources/vocab_icon.png"   $dst/
Copy-Item "$root/VocabSpire/Resources/wordbanks/*.json" "$dst/wordbanks/"
Copy-Item "$root/VocabSpire/Resources/wordbanks/*.csv"  "$dst/wordbanks/"

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$dst/*" -DestinationPath $zip
Remove-Item -Recurse -Force $dst

Write-Host "=== Package OK: $zip ===" -ForegroundColor Green
