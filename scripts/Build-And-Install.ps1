param(
    # PaperTodo 仓库根目录，构建脚本会安装到 <PaperTodoRoot>\plugins\<插件 id>\
    # 留空时使用与本仓库同级的 PaperTodo-dev。
    [string]$PaperTodoRoot = "",

    # 可选：PaperTodo 若从发布目录运行，把产物同步到该目录的 plugins 根。
    # 例如 "<PaperTodoRoot>\输出\PaperTodo-v4.0.0-beta2\plugins"
    [string]$AdditionalPluginsRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PaperTodoRoot)) {
    # 仓库放在 <tool>\PaperTodo.Plugin.DATAIO，宿主通常在 <tool>\PaperTodo-dev。
    $PaperTodoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\PaperTodo-dev"))
}

if (-not (Test-Path -LiteralPath $PaperTodoRoot -PathType Container)) {
    throw "未找到 PaperTodo 源码目录：$PaperTodoRoot。请用 -PaperTodoRoot 指定。"
}

# 复用 PaperTodo 提供的共享构建/安装脚本，保证产物清理和部署规则与官方一致。
$sharedScript = Join-Path $PaperTodoRoot "plugin-samples\Build-And-Install-NativePlugin.ps1"
if (-not (Test-Path -LiteralPath $sharedScript -PathType Leaf)) {
    throw "未找到 PaperTodo 构建脚本：$sharedScript"
}

$projectPath = Join-Path $PSScriptRoot "..\PaperTodo.Plugin.DATAIO.csproj"

# 让 csproj 的 ProjectReference 跟随同一个宿主目录（环境变量会作为 MSBuild 属性传入）。
$env:PaperTodoRoot = $PaperTodoRoot

& $sharedScript -ProjectPath $projectPath -PaperTodoRoot $PaperTodoRoot

# PaperTodo 运行时从自身目录的 plugins/ 加载插件；发布目录不会自动同步仓库 plugins/。
if (-not [string]::IsNullOrWhiteSpace($AdditionalPluginsRoot)) {
    $manifestPath = Join-Path $PSScriptRoot "..\plugin.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $pluginId = [string]$manifest.id

    $source = Join-Path $PaperTodoRoot (Join-Path "plugins" $pluginId)
    $target = Join-Path $AdditionalPluginsRoot $pluginId
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "共享脚本未生成插件目录：$source"
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    # 保留插件自己的 .runtime/（例如 WebView2 用户数据）。
    Get-ChildItem -LiteralPath $target -Force |
        Where-Object { $_.Name -ne ".runtime" } |
        Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $source -Force |
        Copy-Item -Destination $target -Recurse -Force

    Write-Host "已同步 $pluginId 到 $target"
}
