param(
    # PaperTodo 源码根目录（需要其中的 PaperTodo.Plugin.Abstractions 工程）。
    # 留空时使用与本仓库同级的 PaperTodo-dev。
    [string]$PaperTodoRoot = "",

    # 产物输出目录，默认 <仓库>\dist。
    [string]$OutputRoot = "",

    # 目标运行时；PaperTodo 目前只发布 win-x64。
    [string]$Runtime = "win-x64",

    # 是否把 .NET 运行时打进插件目录。宿主自带运行时，默认不打包。
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($PaperTodoRoot)) {
    $PaperTodoRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "..\PaperTodo-dev"))
}

if (-not (Test-Path -LiteralPath $PaperTodoRoot -PathType Container)) {
    throw "未找到 PaperTodo 源码目录：$PaperTodoRoot。请用 -PaperTodoRoot 指定。"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "dist"
}

$projectPath = Join-Path $repoRoot "PaperTodo.Plugin.DATAIO.csproj"
$manifestPath = Join-Path $repoRoot "plugin.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pluginId = [string]$manifest.id
$version = [string]$manifest.version

if ([string]::IsNullOrWhiteSpace($pluginId) -or [string]::IsNullOrWhiteSpace($version)) {
    throw "plugin.json 缺少 id 或 version。"
}

# csproj 的版本必须与 plugin.json 一致，否则用户看到的是错误版本。
$csproj = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$csprojVersion = [regex]::Match($csproj, "<Version>([^<]+)</Version>").Groups[1].Value
if ($csprojVersion -ne $version) {
    throw "版本不一致：plugin.json=$version，csproj=$csprojVersion。请先统一版本号。"
}

# CHANGELOG 必须包含当前版本小节，避免发布说明漏更。
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"
if (Test-Path -LiteralPath $changelogPath -PathType Leaf) {
    $changelog = Get-Content -LiteralPath $changelogPath -Raw -Encoding UTF8
    if ($changelog -notmatch ("(?m)^##\s+" + [regex]::Escape($version) + "(\s|$)")) {
        throw "CHANGELOG.md 缺少 $version 的更新小节，请先补充发布说明。"
    }
}

$publishDir = Join-Path $OutputRoot "publish"
$pluginDir = Join-Path $OutputRoot $pluginId
$zipPath = Join-Path $OutputRoot "$pluginId-$version-$Runtime.zip"

Write-Host "清理旧产物…"
foreach ($path in @($publishDir, $pluginDir, $zipPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

# 真正的干净构建：仅删除 dist 不足以防止增量编译复用旧的对象文件。
Write-Host "清理 Release 中间产物…"
& dotnet clean $projectPath -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    throw "dotnet clean 失败（退出码 $LASTEXITCODE）。"
}

Write-Host "发布 $pluginId $version ($Runtime)…"
& dotnet publish $projectPath `
    -c Release `
    -r $Runtime `
    --self-contained:$($SelfContained.IsPresent) `
    -o $publishDir `
    -p:PaperTodoRoot=$PaperTodoRoot
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）。"
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Get-ChildItem -LiteralPath $publishDir -Force |
    Copy-Item -Destination $pluginDir -Recurse -Force

# 宿主提供契约 DLL：包里不能带自己的副本，否则会与宿主版本冲突。
$sharedDll = Join-Path $pluginDir "PaperTodo.Plugin.Abstractions.dll"
if (Test-Path -LiteralPath $sharedDll) {
    Remove-Item -LiteralPath $sharedDll -Force
}

# 同时也清掉调试符号与调试配置，减小 Release 包体积。
Get-ChildItem -Path (Join-Path $pluginDir "*") -Force -Include *.pdb, *.xml -File |
    Remove-Item -Force -ErrorAction SilentlyContinue

Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $pluginDir "plugin.json") -Force

$dllPath = Join-Path $pluginDir "PaperTodo.Plugin.DATAIO.dll"
$depsPath = Join-Path $pluginDir "PaperTodo.Plugin.DATAIO.deps.json"
foreach ($required in @($dllPath, $depsPath, (Join-Path $pluginDir "plugin.json"))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "成品目录缺少必需文件：$required"
    }
}

# 用程序集版本再校验一次，防止 publish 缓存导致产物与源码版本不符。
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString(3)
if ($assemblyVersion -ne $version) {
    throw "程序集版本 $assemblyVersion 与 plugin.json 的 $version 不一致。"
}

Write-Host "生成 Release 压缩包…"
Compress-Archive -LiteralPath $pluginDir -DestinationPath $zipPath -Force

# 部分受限环境缺少 Get-FileHash，直接用 .NET 计算 SHA256。
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $stream = [System.IO.File]::OpenRead($zipPath)
    try {
        $hashBytes = $sha.ComputeHash($stream)
    }
    finally {
        $stream.Dispose()
    }
}
finally {
    $sha.Dispose()
}
$hash = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })

# 校验值必须落盘：下载页与自动化发布都需要直接消费，而不是从控制台复制。
# 使用 .NET 写入不带 BOM 的 ASCII，保持 sha256 文件格式通用。
$shaPath = "$zipPath.sha256"
[System.IO.File]::WriteAllText($shaPath, "$hash  $([System.IO.Path]::GetFileName($zipPath))`n")

# 中间 publish 目录含宿主契约 DLL，与成品目录同处 dist 容易被误发布，打包完成后清除。
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

Write-Host ""
Write-Host "成品目录：$pluginDir"
Write-Host "Release 包：$zipPath"
Write-Host "SHA256 文件：$shaPath"
Write-Host "SHA256：$hash"
