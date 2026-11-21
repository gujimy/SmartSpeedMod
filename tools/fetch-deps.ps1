<#
.SYNOPSIS
    拉取编译所需的依赖：游戏引用程序集（refs/）与 CesiumLoader SDK（refs/sdk/）。

.DESCRIPTION
    依赖不提交进 Git 仓库，本地首次构建或 CI 构建前运行本脚本即可。
    来源：
      1) 游戏引用程序集 —— CesiumLoader 公开仓库的 refs/game/
      2) CesiumLoader SDK —— CesiumLoader 最新 release 的 cesium-sdk-tools.zip

    对照 sources.json 里记录的 SHA256 校验每个程序集，缺文件或哈希不符会报错退出。

.PARAMETER Force
    即使目标文件已存在也重新下载。

.PARAMETER SdkVersion
    指定 SDK 版本（如 3.8.7），默认取最新 release。

.PARAMETER Proxy
    GitHub 代理前缀，用于直连 GitHub 不通的环境。
    例如 https://v4.gh-proxy.org/ 会把 https://raw.githubusercontent.com/... 
    拼成 https://v4.gh-proxy.org/https://raw.githubusercontent.com/... 。
    本脚本默认直连失败时自动尝试该代理。

.EXAMPLE
    pwsh -NoProfile -File tools/fetch-deps.ps1
    pwsh -NoProfile -File tools/fetch-deps.ps1 -SdkVersion 3.8.7 -Force
    pwsh -NoProfile -File tools/fetch-deps.ps1 -Proxy https://v4.gh-proxy.org/
#>
[CmdletBinding()]
param(
    [switch] $Force,
    [string] $SdkVersion,
    [string] $Proxy = 'https://v4.gh-proxy.org/'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$refsDir = Join-Path $repo 'refs'
$sdkDir = Join-Path $refsDir 'sdk'
$manifestPath = Join-Path $PSScriptRoot 'game-refs.json'
$cesiumRepo = 'higashitaniyume/CesiumLoader'

# ---- 解析系统代理（Windows Internet Settings / 环境变量）----
# curl.exe 默认不读 Windows 注册表的 IE 代理，所以这里显式取出来传给 --proxy。
function Get-SystemProxy {
    # 1) 环境变量优先（标准 http_proxy/https_proxy/all_proxy）
    foreach ($name in @('HTTPS_PROXY','https_proxy','HTTP_PROXY','http_proxy','ALL_PROXY','all_proxy')) {
        $v = [Environment]::GetEnvironmentVariable($name)
        if ($v) { return $v }
    }
    # 2) Windows 注册表 IE 代理（Clash 等通常写这里）
    try {
        $ie = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
        if ($ie -and $ie.ProxyEnable -eq 1 -and $ie.ProxyServer) {
            $server = $ie.ProxyServer
            # 多协议形式 "http=host:port;https=host:port" 时取 http 部分
            if ($server -match '=') {
                $m = [regex]::Match($server, '(?:^|;)\s*http\s*=\s*([^;]+)')
                if ($m.Success) { $server = $m.Groups[1].Value }
                else {
                    $m2 = [regex]::Match($server, '(?:^|;)\s*https?\s*=\s*([^;]+)')
                    if ($m2.Success) { $server = $m2.Groups[1].Value }
                }
            }
            if ($server -notmatch '^https?://') { $server = "http://$server" }
            return $server
        }
    }
    catch { }
    return $null
}

$systemProxy = Get-SystemProxy
if ($systemProxy) { Write-Host "检测到系统代理: $systemProxy" }
else { Write-Host "未检测到系统代理，将使用直连 / gh-proxy" }

# 下载辅助：用 curl.exe（带重试，能应对代理下的大文件 EOF）。
# 策略：系统代理 -> 直连 -> gh-proxy 代理前缀。每个环节都校验返回码。
function Get-Url {
    param([string] $Url, [string] $OutFile, [int] $TimeoutSec = 120)

    function Invoke-Curl {
        param([string] $U, [string] $O, [string] $Px)
        $args = @('-f','-L','--retry','6','--retry-delay','2','--retry-all-errors',
                  '--connect-timeout','20','--max-time',$TimeoutSec)
        if ($Px) { $args += @('--proxy', $Px) }
        $args += @('-o', $O, $U)
        & curl.exe @args 2>$null
        return $LASTEXITCODE
    }

    # 1) 系统代理
    if ($systemProxy) {
        Write-Host ("  [GET via system proxy] {0}" -f $Url)
        if ((Invoke-Curl -U $Url -O $OutFile -Px $systemProxy) -eq 0) { return }
    }

    # 2) 直连
    Write-Host ("  [GET direct] {0}" -f $Url)
    if ((Invoke-Curl -U $Url -O $OutFile -Px $null) -eq 0) { return }

    # 3) gh-proxy 代理前缀
    if (-not $Proxy) { throw "下载失败: $Url" }
    $proxied = $Proxy.TrimEnd('/') + '/' + $Url
    Write-Host ("  [GET via gh-proxy] {0}" -f $proxied)
    if ((Invoke-Curl -U $proxied -O $OutFile -Px $null) -ne 0) {
        throw "所有下载方式均失败: $Url"
    }
}

New-Item -ItemType Directory -Path $refsDir -Force | Out-Null
New-Item -ItemType Directory -Path $sdkDir -Force | Out-Null

# ---------- 1) 游戏引用程序集 ----------
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "缺少依赖清单: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$rawBase = "https://raw.githubusercontent.com/$($cesiumRepo)/$($manifest.ref)/refs/game"

Write-Host "== 游戏引用程序集 (基线 $($manifest.baseline), ref $($manifest.ref)) =="
foreach ($asm in $manifest.assemblies.PSObject.Properties) {
    $name = $asm.Name
    $expect = $asm.Value
    $target = Join-Path $refsDir $name
    $need = $Force -or -not (Test-Path -LiteralPath $target -PathType Leaf)
    if (-not $need) {
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -eq $expect) {
            Write-Host ("  [OK]      {0}" -f $name)
            continue
        }
        Write-Host ("  [MISMATCH] {0} 重新下载" -f $name)
        $need = $true
    }
    $url = "$rawBase/$name"
    Write-Host ("  [GET]     {0}" -f $url)
    Get-Url -Url $url -OutFile $target
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expect) {
        throw "SHA256 校验失败: $name (期望 $expect, 实际 $actual)"
    }
    Write-Host ("  [VERIFIED] {0}" -f $name)
}

# ---------- 2) CesiumLoader SDK ----------
$sdkDll = Join-Path $sdkDir 'CesiumLoader.SDK.dll'
if (($Force -or -not (Test-Path -LiteralPath $sdkDll -PathType Leaf))) {
    if (-not $SdkVersion) {
        Write-Host "== 查询 CesiumLoader 最新版本 =="
        $apiUrl = "https://api.github.com/repos/$cesiumRepo/releases/latest"
        try {
            $latest = Invoke-RestMethod -Uri $apiUrl -UseBasicParsing
        }
        catch {
            $tmp = Join-Path $refsDir "latest.json"
            Get-Url -Url $apiUrl -OutFile $tmp
            $latest = Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
        $tag = $latest.tag_name                       # 形如 modloader-3.8.7
        $SdkVersion = $tag -replace '^modloader-', ''
        Write-Host "  最新版本: $SdkVersion ($tag)"
    }
    $assetUrl = "https://github.com/$cesiumRepo/releases/download/modloader-$SdkVersion/cesium-sdk-tools.zip"
    $zip = Join-Path $refsDir "cesium-sdk-tools-$SdkVersion.zip"
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) {
        Write-Host "== 下载 SDK 工具包: $assetUrl =="
        Get-Url -Url $assetUrl -OutFile $zip -TimeoutSec 300
    }
    else {
        Write-Host "== SDK 工具包已存在，直接解压: $zip =="
    }

    # SDK 工具包里找出 CesiumLoader.SDK.dll（可能在任意子目录）。
    # 优先用 python 的 zipfile 解压（避免 Windows 上 ZipFile 的文件锁问题），
    # 没有 python 时回退到 .NET ZipFile。
    $extracted = $false
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py) {
        $pyScript = @"
import zipfile, sys, os
z, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(z) as f:
    matches = [n for n in f.namelist() if n.endswith('CesiumLoader.SDK.dll')]
    if not matches:
        sys.exit('SDK 工具包里没有 CesiumLoader.SDK.dll')
    data = f.read(matches[0])
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, 'wb').write(data)
    print('  [OK] CesiumLoader.SDK.dll (%d bytes)' % len(data))
"@
        $tmpPy = Join-Path $refsDir "_extract_sdk.py"
        Set-Content -LiteralPath $tmpPy -Value $pyScript -Encoding utf8
        & python $tmpPy $zip $sdkDll
        if ($LASTEXITCODE -eq 0) { $extracted = $true }
        Remove-Item -LiteralPath $tmpPy -Force -ErrorAction SilentlyContinue
    }
    if (-not $extracted) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.Name -eq 'CesiumLoader.SDK.dll' } | Select-Object -First 1
            if (-not $entry) { throw "SDK 工具包里没有 CesiumLoader.SDK.dll" }
            $tmpSdk = Join-Path $sdkDir 'CesiumLoader.SDK.dll.tmp'
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmpSdk, $true)
            $archive.Dispose()
            Move-Item -LiteralPath $tmpSdk -Destination $sdkDll -Force
            Write-Host "  [OK] CesiumLoader.SDK.dll"
        }
        finally {
            $archive.Dispose()
        }
    }
    # 解压成功后删除 zip 以节省空间；删除失败不影响构建
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "== SDK 已存在: $sdkDll =="
}

Write-Host "依赖就绪。现在可运行: dotnet build -c Release"
