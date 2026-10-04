[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$toolsPath = Join-Path $PSScriptRoot 'iPodManager.App\tools'
$licenseCache = Join-Path $PSScriptRoot 'artifacts\licenses'

function Install-Dependency {
    param(
        [string]$Name,
        [string]$Url,
        [string]$ArchiveHash,
        [string]$ArchivePrefix,
        [System.Collections.IDictionary]$Files,
        [string]$DestinationPath = $toolsPath,
        [System.Collections.IDictionary]$LicenseFiles = @{}
    )

    $complete = $true
    foreach ($file in $Files.Keys) {
        $destination = Join-Path $DestinationPath $file
        if (!(Test-Path -LiteralPath $destination -PathType Leaf) -or
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $Files[$file]) {
            $complete = $false
            break
        }
    }
    foreach ($file in $LicenseFiles.Keys) {
        $destination = Join-Path $licenseCache $file
        if (!(Test-Path -LiteralPath $destination -PathType Leaf) -or
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $LicenseFiles[$file][1]) {
            $complete = $false
            break
        }
    }
    if ($complete) {
        Write-Host "${Name}: OK"
        return
    }

    Write-Host "${Name}: downloading..."
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $temporaryPath = Join-Path $temporaryRoot ('MGS4-decoders-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryPath | Out-Null
    try {
        $zipPath = Join-Path $temporaryPath 'download.zip'
        Invoke-WebRequest -Uri $Url -OutFile $zipPath -UseBasicParsing -TimeoutSec 180
        if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $ArchiveHash) {
            throw "${Name}: downloaded ZIP checksum does not match the pinned archive."
        }

        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            foreach ($file in $LicenseFiles.Keys) {
                $entry = $archive.GetEntry($LicenseFiles[$file][0])
                if ($null -eq $entry) { throw "${Name}: archive is missing the license for $file." }
                $extractedPath = Join-Path $temporaryPath $file
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extractedPath)
                if ((Get-FileHash -LiteralPath $extractedPath -Algorithm SHA256).Hash -ne $LicenseFiles[$file][1]) {
                    throw "${Name}: extracted $file checksum does not match."
                }
            }
            foreach ($file in $Files.Keys) {
                $entry = $archive.GetEntry($ArchivePrefix + $file)
                if ($null -eq $entry) { throw "${Name}: archive is missing $file." }
                $extractedPath = Join-Path $temporaryPath $file
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extractedPath)
                if ((Get-FileHash -LiteralPath $extractedPath -Algorithm SHA256).Hash -ne $Files[$file]) {
                    throw "${Name}: extracted $file checksum does not match."
                }
            }
        }
        finally { $archive.Dispose() }

        # Verify the complete set before replacing any installed file.
        New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null
        foreach ($file in $Files.Keys) {
            Copy-Item -LiteralPath (Join-Path $temporaryPath $file) -Destination (Join-Path $DestinationPath $file) -Force
        }
        New-Item -ItemType Directory -Force -Path $licenseCache | Out-Null
        foreach ($file in $LicenseFiles.Keys) {
            Copy-Item -LiteralPath (Join-Path $temporaryPath $file) -Destination (Join-Path $licenseCache $file) -Force
        }
        Write-Host "${Name}: OK"
    }
    finally {
        $resolvedTemporaryPath = [IO.Path]::GetFullPath($temporaryPath)
        if ([IO.Path]::GetDirectoryName($resolvedTemporaryPath).TrimEnd('\') -eq $temporaryRoot.TrimEnd('\')) {
            try { Remove-Item -LiteralPath $resolvedTemporaryPath -Recurse -Force }
            catch { Write-Warning "Could not remove temporary directory: $resolvedTemporaryPath" }
        }
    }
}

function Install-License {
    param([string]$File, [string]$Url, [string]$Hash)

    $destination = Join-Path $licenseCache $File
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $Hash) { return }
    New-Item -ItemType Directory -Force -Path $licenseCache | Out-Null
    $temporaryFile = Join-Path $licenseCache ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Invoke-WebRequest -Uri $Url -OutFile $temporaryFile -UseBasicParsing -TimeoutSec 180
        if ((Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash -ne $Hash) {
            throw "$File checksum does not match the pinned upstream license."
        }
        Move-Item -LiteralPath $temporaryFile -Destination $destination -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}

Push-Location $PSScriptRoot
try {
    if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 10 SDK and ensure dotnet is on PATH.'
    }
    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 10) {
        throw 'Install a .NET SDK capable of targeting .NET 10.'
    }

    & cmd /d /v:on /c 'call "iPodManager.Plugin\setup_msvc.cmd" >nul && where cl >nul 2>nul && where link >nul 2>nul && if not exist "!WindowsSdkDir!Include\!WindowsSDKVersion!um\Windows.h" exit /b 1'
    if ($LASTEXITCODE -ne 0) {
        throw 'Install MSVC x64 Build Tools and a Windows SDK (Desktop development with C++).'
    }

    $buildPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build'))
    if ([IO.Path]::GetDirectoryName($buildPath) -ne [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')) {
        throw 'Build output must be directly inside the repository root.'
    }
    if (Test-Path -LiteralPath $buildPath) {
        if ((Get-Item -LiteralPath $buildPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to clean a build directory that is a link or junction.'
        }
        Remove-Item -LiteralPath $buildPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $buildPath | Out-Null

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Write-Host 'Checking dependencies...'
    Install-Dependency -Name 'FFmpeg' `
        -Url 'https://github.com/GyanD/codexffmpeg/releases/download/2026-02-09-git-9bfa1635ae/ffmpeg-2026-02-09-git-9bfa1635ae-full_build.zip' `
        -ArchiveHash '0594af02a72283b4d661abc28ba2bc576b77305d436e083dc746a030bba54d84' `
        -ArchivePrefix 'ffmpeg-2026-02-09-git-9bfa1635ae-full_build/bin/' `
        -Files @{ 'ffmpeg.exe' = 'bd6ebaf8c2d35e7bcabbcda85a13fe2dba02c30e4ebf7e9efe63803b026e4b8a' } `
        -LicenseFiles @{ 'FFmpeg-GPLv3.txt' = @(
            'ffmpeg-2026-02-09-git-9bfa1635ae-full_build/LICENSE',
            '8ceb4b9ee5adedde47b31e975c1d90c73ad27b6b165a1dcd80c7c545eb65b903') }

    Install-Dependency -Name 'vgmstream' `
        -Url 'https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-win64.zip' `
        -ArchiveHash '6c4a8a3813864fefed081bbd337dbc0ad93bf88e0b92f5db98d7ab258b22dc6c' `
        -ArchivePrefix '' `
        -Files ([ordered]@{
            'vgmstream-cli.exe' = '29df08c557ada8269c92a6abfe3886ad2f65849b0b26ba78a0819b363bbc5b85'
            'avcodec-vgmstream-59.dll' = '4d6cf54e7b3c26ef06e95bdb515a10d1e1bed3ec1ffd87ec3e5c4fbcd2a883f6'
            'avformat-vgmstream-59.dll' = '0dde66ea268cccb67b1aa219c1ce879602b839e561a79a00c8215e2688f1a4b2'
            'avutil-vgmstream-57.dll' = 'a999856c17cfdce3c22b92b6640a7ada6c4ab4c3c6841a863770b72fa0bc19b4'
            'libatrac9.dll' = 'fe82f1ae13a798337e03942c35351997c23caadb5eb0c6b7a2dc2a521ed8b02e'
            'libcelt-0061.dll' = '542254723d9100f91cbf18e41c4aff80287c22a9859e9e545c413d984df440ad'
            'libcelt-0110.dll' = 'bfcc51d865bb3b6a2793381bf535f23ec423feb722b46333fa8556ccdcafa61a'
            'libg719_decode.dll' = 'aad61f205ce1f1b61285f6b37b90a331ec8c6ae2f94f361714dc21a12c236960'
            'libmpg123-0.dll' = 'db9eb1d91b8eb9f47b0968201dbf6c51a9627e539a78afd9ad5f59c5d18fe4df'
            'libspeex-1.dll' = '04dbc85fcec6c54671535bf39937a70f2dd040a81ad670a0b84de6ca17b39bff'
            'libvorbis.dll' = '5f330a20b19b3c70dc43eedcc95891e65940fca04a77155c573efcdd4c2ca665'
        })

    $loaderPath = Join-Path $PSScriptRoot 'artifacts\asi-loader'
    Install-Dependency -Name 'Ultimate ASI Loader' `
        -Url 'https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/download/v9.7.4/Ultimate-ASI-Loader_x64.zip' `
        -ArchiveHash '8272d83b2692662098746f2d0ad0e2d85f3c8358ab1d63f75fbe835c2c8135fd' `
        -ArchivePrefix '' `
        -Files @{ 'dinput8.dll' = 'fa266e3513d02c08a1b808f28c10538a489eaffaa4b0707f7cc1066e71b5afd7' } `
        -DestinationPath $loaderPath

    # Byte-identical CELT notices cover both DLL versions. LGPL text is shared.
    Install-License -File 'LibAtrac9-MIT.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/LibAtrac9.LICENSE' `
        -Hash '06a7e63d1e48754e925339e0df0abc62f0ac828196c0783cb156ff69334fa737'
    Install-License -File 'CELT-COPYING.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/celt-0.6.1.COPYING' `
        -Hash '6913f9754c576d04e2c7452202aeff5910ba9b8dd29ac6949926a6d12f33c0b2'
    Install-License -File 'Ogg-COPYING.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/libogg-1.3.5.COPYING' `
        -Hash 'd2ab5758336489da61c12cc5bb757da5339c4ae9001f9bb0562b4370249af814'
    Install-License -File 'Vorbis-COPYING.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/libvorbis-1.3.7.COPYING' `
        -Hash 'ec1815db59fcd302846df949d7424876cb2e2dc5ed1606c5fb0b36787b1cf43a'
    Install-License -File 'mpg123-COPYING.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/mpg123-1.31.1.COPYING' `
        -Hash 'c22482728a634a8dfdb4ff72a96d4c1ed64cd8f3e79335c401751ac591609366'
    Install-License -File 'Opus-COPYING.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/opus-1.3.1.COPYING' `
        -Hash '8338ce8d922bb4416ce3dd1e5680173332435e3f0755007ac7801ccd674fe682'
    Install-License -File 'Speex-COPYING.txt' `
        -Url 'https://raw.githubusercontent.com/vgmstream/vgmstream/71e2361042531fe767fb98300cf8c1ee95e539a0/ext_libs/licenses/speex-1.2.1.COPYING' `
        -Hash '2654a4264b2bfe298dedc508748d140111840c315cc8eb646a3a68c13fa75b01'
    Install-License -File 'LGPL-2.1.txt' `
        -Url 'https://raw.githubusercontent.com/mono/taglib-sharp/b5ae84f2e84087bf160bb0471420200dd2b5d809/COPYING' `
        -Hash '6095e9ffa777dd22839f7801aa845b31c9ed07f3d6bf8a26dc5d2dec8ccc0ef3'
    Install-License -File 'Ultimate-ASI-Loader-MIT.txt' `
        -Url 'https://raw.githubusercontent.com/ThirteenAG/Ultimate-ASI-Loader/6b440669144c4a0bef5718ab155df160d231cd42/license' `
        -Hash 'ceca73c504e39084b0b80d2a437aea9988313d1915418d3c629c3e4a8313f5a1'
    Install-License -File 'Ultimate-ASI-Loader-miniz-MIT.txt' `
        -Url 'https://raw.githubusercontent.com/ThirteenAG/Ultimate-ASI-Loader/6b440669144c4a0bef5718ab155df160d231cd42/external/miniz/LICENSE' `
        -Hash '6f20fa7672b00e2e975c291df737cf227addf3ad32e36fef3fe0f416e4664d3d'
    Install-License -File 'Ultimate-ASI-Loader-FunctionHook-MIT.txt' `
        -Url 'https://raw.githubusercontent.com/ThirteenAG/injector/3a384e8d1b575c09383b0fab8bd92e34cb654949/utility/LICENSE.txt' `
        -Hash '7124c298a1fe76b169db561ee640101f88f573f21d1f269b34817b9538321f5f'
    Install-License -File 'Ultimate-ASI-Loader-MinHook-LICENSE.txt' `
        -Url 'https://raw.githubusercontent.com/TsudaKageyu/minhook/d94c64d32ea37bc4f5ee47d580709f70c6fb6080/LICENSE.txt' `
        -Hash 'd5c2224982b0b95a16b098a561335fa93f3eaf4d0aa964b7843edb986df78dc8'

    Write-Host 'Building application...'
    & dotnet build iPodManager.App\iPodManager.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Application Release build failed.' }

    Write-Host 'Building ASI...'
    & cmd /d /c iPodManager.Plugin\build_asi.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Native ASI build failed.' }
    Write-Host 'Assembling deployment...'
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'iPodManager.App\iPodManager.csproj')
    $framework = [string]$project.Project.PropertyGroup.TargetFramework
    $applicationOutput = Join-Path $PSScriptRoot "iPodManager.App\bin\Release\$framework"
    $deploymentPath = Join-Path $buildPath 'MGS4'
    $applicationPath = Join-Path $deploymentPath 'iPod'
    $deploymentTools = Join-Path $applicationPath 'tools'
    $scriptsPath = Join-Path $deploymentPath 'scripts'
    $deploymentLicenses = Join-Path $deploymentPath 'licenses'
    foreach ($directory in @($deploymentTools, $scriptsPath, $deploymentLicenses,
            (Join-Path $applicationPath 'content\custom'),
            (Join-Path $applicationPath 'content\podcasts'))) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    foreach ($file in @('iPodManager.exe', 'iPodManager.dll', 'iPodManager.deps.json',
            'iPodManager.runtimeconfig.json', 'TagLibSharp.dll', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $applicationOutput $file) -Destination $applicationPath
    }
    foreach ($file in @('ffmpeg.exe', 'vgmstream-cli.exe', 'avcodec-vgmstream-59.dll',
            'avformat-vgmstream-59.dll', 'avutil-vgmstream-57.dll', 'libatrac9.dll',
            'libcelt-0061.dll', 'libcelt-0110.dll', 'libg719_decode.dll', 'libmpg123-0.dll',
            'libspeex-1.dll', 'libvorbis.dll', 'ipod-dbm-template.dbm',
            'MGS4iPodStreaming.bank', 'COPYING')) {
        Copy-Item -LiteralPath (Join-Path $toolsPath $file) -Destination $deploymentTools
    }
    foreach ($file in @('FFmpeg-GPLv3.txt',
            'LibAtrac9-MIT.txt',
            'CELT-COPYING.txt',
            'Ogg-COPYING.txt',
            'Vorbis-COPYING.txt',
            'mpg123-COPYING.txt',
            'Opus-COPYING.txt',
            'Speex-COPYING.txt',
            'LGPL-2.1.txt',
            'Ultimate-ASI-Loader-MIT.txt',
            'Ultimate-ASI-Loader-miniz-MIT.txt',
            'Ultimate-ASI-Loader-FunctionHook-MIT.txt',
            'Ultimate-ASI-Loader-MinHook-LICENSE.txt')) {
        Copy-Item -LiteralPath (Join-Path $licenseCache $file) -Destination $deploymentLicenses
    }
    Copy-Item -LiteralPath (Join-Path $loaderPath 'dinput8.dll') -Destination $deploymentPath
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'iPodManager.Plugin\iPodManager.asi') -Destination $scriptsPath
    Write-Host 'Build complete: build\MGS4'
}
finally { Pop-Location }
