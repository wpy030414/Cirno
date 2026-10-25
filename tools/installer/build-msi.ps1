param(
    [string]$Version,
    [string]$Configuration = "Release"
)

# 构建可安装的 MSI：
#   1. 自包含发布（.NET + WinAppSDK 都打进产物，用户机器不需要装运行时）
#   2. 生成组件清单
#   3. WiX 打包
$ErrorActionPreference = "Stop"

# 仓库根：tools/installer 上溯两级
$repo = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

# 版本权威：Directory.Build.props 的 <Version>（CalVer 点分四段 YY.MM.DD.HH）。
# 显示形态 YY.MM.DD-HH 用于文件名/关于信息；MSI ProductVersion 只接受 1-3 段数字。
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $repo "Directory.Build.props")
    $raw = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } |
            Where-Object { $_ } | Select-Object -First 1)
    if (-not $raw) { throw "Directory.Build.props 缺少 <Version>" }
    $parts = $raw.Split('.')
    $Version = if ($parts.Count -ge 4) {
        ($parts[0..2] -join '.') + '-' + ($parts[3..($parts.Count - 1)] -join '.')
    } else {
        $raw
    }
}
$msiVersion = ($Version -split '-')[0]
$segments = $msiVersion.Split('.')
if ($segments.Count -gt 3) { $msiVersion = ($segments[0..2] -join '.') }
"version: $Version (MSI ProductVersion: $msiVersion)"

# 本机 SDK 可能不在 PATH：优先用用户目录安装的 dotnet（Ceobe 同样记录此位置）
$dotnetCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),
    (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")
)
$dotnet = $dotnetCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dotnet) { $dotnet = "dotnet" }

$publishDir = Join-Path $repo "artifacts\publish"
$msiPath = Join-Path $repo "artifacts\Cirno-$Version-x64.msi"
$components = Join-Path $PSScriptRoot "Components.generated.wxs"
$wix = Join-Path $repo ".workbuddy\tools\wix.exe"

"== publish =="
& $dotnet publish (Join-Path $repo "src\Cirno.App\Cirno.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:Platform=x64 -p:WindowsAppSDKSelfContained=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

# 自包含的 .NET 不带 VC++ 运行库（vcruntime140 / msvcp140），而 WinAppSDK 的原生 DLL 依赖它。
# 直接把编译器自带的可再分发 DLL 放到应用目录（app-local 部署，微软允许），
# 这样目标机器不需要预装 VC++ 2015-2022 运行库。
$vcCrt = Get-ChildItem "C:/Program Files/Microsoft Visual Studio/2022/*/VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT" -Directory -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($vcCrt) {
    Copy-Item (Join-Path $vcCrt.FullName "*.dll") $publishDir -Force
    "bundled VC runtime from $($vcCrt.FullName)"
} else {
    Write-Warning "未找到 VC++ 可再分发目录，安装包将依赖目标机器已装 VC++ 运行库"
}

"== components =="
& (Join-Path $PSScriptRoot "generate-components.ps1") -PublishDir $publishDir -Out $components
if ($LASTEXITCODE -ne 0) { throw "组件清单生成失败" }

"== msi =="
if (-not (Test-Path $wix)) { throw "未找到 wix.exe，请先安装：dotnet tool install --tool-path .workbuddy/tools wix" }

New-Item -ItemType Directory -Force -Path (Split-Path $msiPath -Parent) | Out-Null

# Util 扩展提供 util:CloseApplication：升级前先关掉正在运行的实例。
# 扩展按 WiX 主版本配套（wix 5.x → WixToolset.Util 5.x），缺失时先装。
$utilExtension = "WixToolset.Util.wixext"
& $wix extension list 2>&1 | Out-String | ForEach-Object { $installed = $_ }
if ($installed -notmatch [regex]::Escape($utilExtension)) {
    $wixVersion = (& $wix --version).Trim()
    $major = ($wixVersion -split "\+")[0]
    & $wix extension add "$utilExtension/$major"
    if ($LASTEXITCODE -ne 0) { throw "WiX Util 扩展安装失败" }
}

& $wix build (Join-Path $PSScriptRoot "Package.wxs") $components `
    -ext WixToolset.Util.wixext `
    -d "ProductVersion=$msiVersion" `
    -d "PublishDir=$publishDir" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "WiX 打包失败" }

$size = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
"MSI: $msiPath ($size MB)"
