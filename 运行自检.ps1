# 运行自检：只在临时目录里造测试数据，验证隔离区、预检查、跨盘复制等核心逻辑。
# 不会碰到你自己的任何文件。
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

Get-Process SpaceHunter -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$src = Get-ChildItem (Join-Path $here 'src\*.cs') | Sort-Object Name | ForEach-Object { $_.FullName }

Write-Host "编译自检程序..."
& $csc /nologo /target:exe /platform:anycpu /debug- /main:SpaceHunter.SelfTest `
    /out:_selftest.exe `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src (Join-Path $here 'test\_selftest.cs')

if ($LASTEXITCODE -ne 0) { throw "自检程序编译失败" }

Write-Host ""
& (Join-Path $here '_selftest.exe')
$code = $LASTEXITCODE
Remove-Item (Join-Path $here '_selftest.exe') -Force -ErrorAction SilentlyContinue
exit $code