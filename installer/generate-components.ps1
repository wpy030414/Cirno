param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$Out
)

# 从发布目录生成 WiX 组件清单：一个文件一个组件（升级/修复时粒度最细），
# 组件 Id 由相对路径哈希得到，保证多次构建稳定，MajorUpgrade 才能正确比对。

$root = (Resolve-Path $PublishDir).Path.TrimEnd('\')
$files = Get-ChildItem -Path $root -Recurse -File |
    Where-Object { $_.Extension -notin @('.pdb') } |
    Sort-Object FullName

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!-- 由 installer/generate-components.ps1 生成，请勿手工修改 -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">')

$sha1 = [System.Security.Cryptography.SHA1]::Create()

foreach ($file in $files) {
    $relative = $file.FullName.Substring($root.Length + 1)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant())
    $hash = $sha1.ComputeHash($bytes)
    $id = 'cmp_' + (($hash[0..3] | ForEach-Object { $_.ToString('x2') }) -join '')

    $subdir = [System.IO.Path]::GetDirectoryName($relative)
    $subAttribute = ''
    if (-not [string]::IsNullOrEmpty($subdir)) {
        $escapedSub = [System.Security.SecurityElement]::Escape($subdir)
        $subAttribute = " Subdirectory=`"$escapedSub`""
    }

    $escaped = [System.Security.SecurityElement]::Escape($relative)
    [void]$sb.AppendLine("      <Component Id=`"$id`" Guid=`"*`"$subAttribute>")
    [void]$sb.AppendLine("        <File Source=`"`$(var.PublishDir)\$escaped`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine('      </Component>')
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$sha1.Dispose()
$directory = Split-Path $Out -Parent
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

"generated $Out with $($files.Count) components"
