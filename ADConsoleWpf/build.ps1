param([switch]$Rebuild)
# ============================================================
#  ADConsole build helper (PowerShell)
#  Ensures UTF-8 BOM on all sources, then builds with the
#  .NET Framework 4.8 MSBuild that ships with Windows.
# ============================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$enc = New-Object System.Text.UTF8Encoding($true)

# csc/XAML compiler both mis-handle non-BOM UTF-8 for files containing
# Chinese, so force a BOM on every source file before compiling.
Get-ChildItem $root -Recurse -Include *.cs,*.xaml -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object {
        $b = [System.IO.File]::ReadAllBytes($_.FullName)
        if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) {
            $t = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
            [System.IO.File]::WriteAllText($_.FullName, $t, $enc)
            Write-Host "  BOM: $($_.Name)"
        }
    }

$msb = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if (-not (Test-Path $msb)) { throw "MSBuild not found: $msb" }

$target = if ($Rebuild) { 'Rebuild' } else { 'Build' }
& $msb "$root\ADConsoleWpf.csproj" /t:$target /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo

if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "`nOK: $root\bin\Release\ADConsole.exe"
