# 空间猎手 —— 排障工具
#
# 什么时候用：提示「exe 仍在启动中、无法删除」，或者程序关不干净。
# 右键 →「以管理员身份运行」（很重要：非管理员杀不掉管理员进程）。
#
# 它做三件事：
#   1. 列出所有 SpaceHunter 进程（含提权子进程），告诉你哪些是残留
#   2. 把残留的全部结束掉
#   3. 报告 exe 到底能不能删，并说明原因

$ErrorActionPreference = 'SilentlyContinue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Is-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host ""
Write-Host "===== 空间猎手 排障 =====" -ForegroundColor Cyan
Write-Host ("当前权限: " + $(if (Is-Admin) { "管理员" } else { "普通用户（杀不掉管理员进程）" }))
Write-Host ("程序位置: " + $here)
Write-Host ""

# ---- 1. 找进程 ----
$procs = @(Get-Process SpaceHunter -ErrorAction SilentlyContinue)
Write-Host ("[1] 正在运行的 SpaceHunter 进程: " + $procs.Count)
if ($procs.Count -gt 0) {
    foreach ($p in $procs) {
        $cmd = ""
        try { $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine } catch {}
        $isElev = "正常实例"
        if ($cmd -and $cmd -like '*--elevate*') { $isElev = "提权子进程（后台干活的那种）" }
        Write-Host ("    PID {0,-7} {1}" -f $p.Id, $isElev) -ForegroundColor Yellow
    }
} else {
    Write-Host "    没有。"
}
Write-Host ""

# ---- 2. 结束进程 ----
if ($procs.Count -gt 0) {
    Write-Host "[2] 正在结束..." -NoNewline
    foreach ($p in $procs) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {
            # 非管理员杀不掉管理员的 -> 提权重试
            if (-not (Is-Admin)) {
                Write-Host ""
                Write-Host "    PID $($p.Id) 杀不掉（多半是管理员权限）。请重新「以管理员身份运行」本脚本。" -ForegroundColor Red
                Write-Host ""
                Read-Host "按回车关闭"
                exit 1
            }
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {}
        }
    }
    Start-Sleep -Seconds 2
    $left = @(Get-Process SpaceHunter -ErrorAction SilentlyContinue)
    if ($left.Count -eq 0) {
        Write-Host " 全部结束。" -ForegroundColor Green
    } else {
        Write-Host "还有 $($left.Count) 个残留。" -ForegroundColor Red
    }
} else {
    Write-Host "[2] 无需处理"
}
Write-Host ""

# ---- 3. 清理临时文件 ----
$tmp = Join-Path $env:TEMP 'SpaceHunter_elev'
if (Test-Path $tmp) {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[3] 清理临时目录: $(if (Test-Path $tmp) { '失败' } else { '完成' })"
} else {
    Write-Host "[3] 没有临时目录"
}
Write-Host ""

# ---- 4. 报告 exe 状态 ----
$exe = Join-Path $here 'SpaceHunter.exe'
Write-Host "[4] exe 状态"
if (-not (Test-Path $exe)) {
    Write-Host "    exe 不在（可能已被移动到别处）" -ForegroundColor Yellow
} else {
    try {
        # 试着独占打开：成功就说明没人占用
        $fs = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')
        $fs.Close()
        Write-Host "    exe 可以正常访问，现在可以覆盖/删除/改名了。" -ForegroundColor Green
    } catch {
        Write-Host "    exe 仍被占用: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host ""
        Write-Host "    如果确认没有 SpaceHunter 进程了，那占用它的是别的程序：" -ForegroundColor Red
        Write-Host "      · 杀毒软件正在扫描它 —— 等几十秒再试，或把它加进信任区"
        Write-Host "      · 资源管理器正开着它的文件夹"
        Write-Host "      · 远程桌面/同步盘（如 OneDrive）在传它"
        Write-Host "    查是谁：任务管理器 →「详细信息」→ 右键表头 → 勾选「映像路径」" -ForegroundColor DarkGray
    }
}
Write-Host ""
Read-Host "按回车关闭"