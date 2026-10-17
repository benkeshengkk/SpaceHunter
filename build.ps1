# 用 Windows 自带的 .NET Framework 编译器重新编译。
# 不需要装 Visual Studio，也不需要 NuGet。
#
# 源码在 src\ 下，按 1~8 顺序编号（就是依赖顺序）。
# 用带 BOM 的 UTF-8 保存这个文件 —— PowerShell 5.1 只有看到 BOM
# 才会按 UTF-8 解析，否则按系统 ANSI 读，中文全成乱码。

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "找不到 csc.exe" }

# ------------------------------------------------------------------
#  关掉正在运行的实例
#
#  这里有个坑：Stop-Process **杀不掉管理员权限的进程**。
#  如果用户以管理员身份运行过一次（弹过 UAC 的那个子进程，
#  或者右键「以管理员身份运行」），普通权限下 Get-Process|Stop-Process
#  会静默失败，然后编译报 error CS0016「无法写入输出文件」。
#
#  所以：先试普通杀，不行就用 taskkill 提权杀，还不行就编译到临时
#  文件名，最后才放弃 —— 至少保证用户能拿到一个能用的新版本。
# ------------------------------------------------------------------
function Stop-Instances {
    param([switch]$Quiet)

    $left = @(Get-Process SpaceHunter -ErrorAction SilentlyContinue)
    if ($left.Count -eq 0) { return 0 }

    $left | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $left = @(Get-Process SpaceHunter -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        if (-not $Quiet) {
            Write-Host "还有 $($left.Count) 个实例没关掉，试提权结束..." -ForegroundColor DarkYellow
        }
        foreach ($p in $left) {
            # taskkill 同样受权限限制，所以要 -Verb RunAs（会弹 UAC）
            Start-Process taskkill -ArgumentList "/PID $($p.Id) /F" `
                -Verb RunAs -WindowStyle Hidden -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 2
    }

    $left = @(Get-Process SpaceHunter -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0 -and -not $Quiet) {
        Write-Host ""
        Write-Host "!! 还有实例在运行，PID: $($left.Id -join ', ')" -ForegroundColor Red
        Write-Host ""
        Write-Host "   非管理员权限杀不掉管理员进程。请任选一种办法：" -ForegroundColor Red
        Write-Host "     1) 右键「任务管理器」→ 结束任务"
        Write-Host "     2) 右键任务管理器里的进程 → 打开文件所在位置，检查是不是管理员启动的"
        Write-Host "     3) 右键本文件 →「以管理员身份运行」"
        Write-Host ""
        Write-Host "   （程序本身已加单实例保护，正常不会有两个一起跑）" -ForegroundColor DarkGray
        Write-Host ""
    }
    return $left.Count
}

$still = Stop-Instances

# ------------------------------------------------------------------
#  编译
# ------------------------------------------------------------------
$src = Get-ChildItem (Join-Path $here 'src\*.cs') |
       Sort-Object Name |
       ForEach-Object { $_.FullName }
if ($src.Count -eq 0) { throw "src\ 目录下没有 .cs 文件" }

$target = Join-Path $here 'SpaceHunter.exe'
$temp   = Join-Path $here 'SpaceHunter.new.exe'

# 先编到临时名。这样即使旧 exe 正被占用（管理员实例/杀软扫描），
# 也照样能编出一个新的，再去尝试替换 —— 用户至少拿得到能用的版本。
Write-Host "编译 $($src.Count) 个源文件..."
$out = if ($still -gt 0) { $temp } else { $target }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /debug- `
    /out:$out `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src

if ($LASTEXITCODE -ne 0) { throw "编译失败" }

# 换回正式文件名
if ($out -eq $temp) {
    try {
        Move-Item $temp $target -Force -ErrorAction Stop
        $out = $target
    } catch {
        Write-Host ""
        Write-Host "新版本已经编好，但替换旧的失败（旧的还被占用）。" -ForegroundColor Yellow
        Write-Host "请关掉占用它的程序后，手动把 SpaceHunter.new.exe 改名成 SpaceHunter.exe。" -ForegroundColor Yellow
        Write-Host ""
        $out = $temp
    }
}

$exe = Get-Item $out
Write-Host ""
if ($out -eq $target) {
    Write-Host ("完成 -> {0}  ({1:N0} 字节)" -f $exe.FullName, $exe.Length) -ForegroundColor Green
    Write-Host ""
    Write-Host "可以直接双击运行，或者右键发送到桌面快捷方式。" -ForegroundColor DarkGray
} else {
    Write-Host ("完成 -> {0}  ({1:N0} 字节)" -f $exe.FullName, $exe.Length) -ForegroundColor Yellow
}