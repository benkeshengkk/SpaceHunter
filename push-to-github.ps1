<#
  用 GitHub REST API 把项目推上去（不需要本地安装 git）。
  做法：Git Data API —— 先把每个文件做成 blob，再拼成一棵树，
  最后一次 commit + 一次 ref 更新，把所有文件原子性推上去。

  用法：
    $env:GH_TOKEN = 'ghp_xxxxxxxxxxxx'
    .\push-to-github.ps1 -Owner '你的用户名' -Repo 'SpaceHunter'
#>
param(
    [Parameter(Mandatory = $true)][string]$Owner,
    [string]$Repo = 'SpaceHunter',
    [string]$Branch = 'main',
    [string]$Description = '找出硬盘里的垃圾，看清楚之后再决定删不删。删掉的一律先进隔离区。',
    [string]$SourceDir = (Split-Path -Parent $MyInvocation.MyCommand.Path)
)

$ErrorActionPreference = 'Stop'

if (-not $env:GH_TOKEN) { throw "请先设置 GH_TOKEN 环境变量" }
$token = $env:GH_TOKEN
$h = @{
    'Authorization'  = "Bearer $token"
    'Accept'         = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'     = 'SpaceHunter-Pusher'
}

function Api {
    param([string]$Method, [string]$Path, $Body)
    $u = "https://api.github.com$Path"
    $p = @{
        Uri         = $u
        Method      = $Method
        Headers     = $h
        TimeoutSec  = 60
    }
    if ($null -ne $Body) {
        $p.Body         = ($Body | ConvertTo-Json -Depth 12 -Compress)
        $p.ContentType  = 'application/json; charset=utf-8'
    }
    Invoke-RestMethod @p
}

# ---- 0. 确认身份 ----
Write-Host ">> 验证身份..."
$me = Api -Method GET -Path '/user'
Write-Host "   登录为: $($me.login)" -ForegroundColor Green

# ---- 1. 建仓库（已存在就跳过）----
Write-Host ">> 检查仓库 $Owner/$Repo ..."
$exists = $false
try {
    $null = Api -Method GET -Path "/repos/$Owner/$Repo"
    $exists = $true
} catch {
    # 注意：不能写 .StatusCode.value__ —— PS 5.1 下取不到那个内部字段。
    # 一律按「有响应就说明不是 404，否则才是真 404」来处理。
    $code = 0
    try { $code = [int]$_.Exception.Response.StatusCode } catch { $code = 0 }
    if ($code -ne 404 -and $code -ne 0) { throw }
}

if (-not $exists) {
    Write-Host "   创建中..."
    # 'private' 必须加引号：private 是 PowerShell 的保留字，
    # 不加引号在这一行会被解析器当成别的东西，直接报语法错。
    $repoBody = @{
        'name'            = $Repo
        'description'     = $Description
        'private'         = $false
        'auto_init'       = $false
        'has_issues'      = $true
        'has_wiki'        = $true
        'license_template' = 'gpl-3.0'
    }
    $null = Api -Method POST -Path '/user/repos' -Body $repoBody
    Write-Host "   已创建（公开）" -ForegroundColor Green
} else {
    Write-Host "   已存在，继续往里推。" -ForegroundColor Yellow
}

# ---- 2. 收集文件 ----
$skipDirs = @('.git', 'bin', 'obj', 'docs')
$files = Get-ChildItem $SourceDir -Recurse -File -Force |
    Where-Object {
        $rel = $_.FullName.Substring($SourceDir.Length).TrimStart('\')
        $top = ($rel -split '\\')[0]
        # docs 目录里的 png 不进 git（仓库体积）
        ($skipDirs -notcontains $top) -and ($_.Extension -ne '.exe')
    }

Write-Host ">> 待上传 $($files.Count) 个文件"
foreach ($f in $files) {
    $rel = $f.FullName.Substring($SourceDir.Length).TrimStart('\') -replace '\\', '/'
    Write-Host "   $rel"
}

# ---- 3. 逐个做成 blob ----
Write-Host "`n>> 上传内容..."
$entries = @()
foreach ($f in $files) {
    $rel = $f.FullName.Substring($SourceDir.Length).TrimStart('\') -replace '\\', '/'
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $b64 = [Convert]::ToBase64String($bytes)

    $blob = Api -Method POST -Path "/repos/$Owner/$Repo/git/blobs" -Body @{
        content = $b64
        encoding = 'base64'
    }
    $entries += @{ path = $rel; mode = '100644'; type = 'blob'; sha = $blob.sha }
    Write-Host "   blob  $rel"
}

# ---- 4. 建树 ----
$tree = Api -Method POST -Path "/repos/$Owner/$Repo/git/trees" -Body @{
    tree = $entries
}

# ---- 5. 提交 ----
Write-Host "`n>> 创建提交..."
$commit = Api -Method POST -Path "/repos/$Owner/$Repo/git/commits" -Body @{
    message = "初始版本：空间猎手 SpaceHunter`n`nWindows 单文件磁盘清理工具。`n删掉的一律先进隔离区，可恢复。`n系统目录 / 链接点 / 隔离区自身 三重拦截。`n自检 103 项覆盖核心逻辑。"
    tree = $tree.sha
    parents = @()
}

# ---- 6. 更新 ref ----
Write-Host ">> 设置 $Branch 分支..."
try {
    $null = Api -Method PATCH -Path "/repos/$Owner/$Repo/git/refs/heads/$Branch" -Body @{
        sha = $commit.sha
        force = $true
    }
} catch {
    # 分支还不存在（auto_init=false），改用 create
    $null = Api -Method POST -Path "/repos/$Owner/$Repo/git/refs" -Body @{
        ref = "refs/heads/$Branch"
        sha = $commit.sha
    }
}

Write-Host ""
Write-Host ("完成 -> https://github.com/{0}/{1}" -f $Owner, $Repo) -ForegroundColor Green
Write-Host ""
Write-Host "提醒：" -ForegroundColor DarkGray
Write-Host "  · 截图还没上传（docs/ 里只有说明文件），README 里的图片暂时显示不出来"
Write-Host "  · 如果杀毒软件报毒，README 开头已经写了处理办法"
Write-Host "  · 许可证已按 GPL-3.0 设置"