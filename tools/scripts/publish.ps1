<#
 .SYNOPSIS
    Builds csproj files with container support into docker
    images. Linux docker images will be Alpine which we are
    officially supporting due to their attack surface. This
    is compared to the default images when publishing, which
    are debian.

 .PARAMETER Registry
    The name of the container registry to push to (optional)
 .PARAMETER ImageNamespace
    The namespace to use for the image inside the registry.

 .PARAMETER Os
    Operating system to build for. Defaults to Linux
 .PARAMETER Arch
    Architecture to build. Defaults to x64
 .PARAMETER ImageTag
    Tag to publish under. Defaults "latest"

 .PARAMETER NoBuid
    Whether to not build before publishing.
 .PARAMETER Debug
    Whether to build Release or Debug - default to Release.
#>

Param(
    [string] $Registry = $null,
    [string] $ImageNamespace = $null,
    [string] $Os = "linux",
    [string] $Arch = "x64",
    [string] $ImageTag = "latest",
    [switch] $NoBuild,
    [switch] $Debug
)

$ErrorActionPreference = "Stop"

$Path = & (Join-Path $PSScriptRoot "get-root.ps1") -fileName "Industrial-IoT.sln"

$configuration = "Release"
if ($script:Debug.IsPresent) {
    $configuration = "Debug"
}

$env:SDK_CONTAINER_REGISTRY_CHUNKED_UPLOAD = $true
$env:SDK_CONTAINER_REGISTRY_CHUNKED_UPLOAD_SIZE_BYTES = 131072
$env:SDK_CONTAINER_REGISTRY_PARALLEL_UPLOAD = $false

# Find all container projects, publish them and then push to container registry
Get-ChildItem $Path -Filter *.csproj -Recurse | ForEach-Object {
    $projFile = $_
    $properties = ([xml] (Get-Content -Path $projFile.FullName)).Project.PropertyGroup `
        | Where-Object { ![string]::IsNullOrWhiteSpace($_.ContainerRepository) } `
        | Select-Object -First 1
    if ($properties) {
        $fullName = ""
        $extra = @()
        if ($script:Registry) {
            $extra += "/p:ContainerRegistry=$($script:Registry)"
        }
        if ($script:ImageNamespace) {
            $fullName = "$($fullName)$($script:ImageNamespace)/"
        }
        $fullName = "$($fullName)$($properties.ContainerRepository)"

        $fullTag = "$($script:ImageTag)-$($script:Os)-$($script:Arch)"
        if ($script:Debug.IsPresent) {
            $fullTag = "$($fullTag)-debug"
        }

        Write-Host "Publish $($projFile.FullName) as $($fullName):$($fullTag)..."

        if ($script:NoBuild) {
            $extra += "--no-build"
        }

        dotnet publish $projFile.FullName -c $configuration --self-contained false `
            /p:TargetLatestRuntimePatch=true `
            /p:ContainerRepository=$fullName `
            /p:ContainerImageTag=$fullTag `
            /t:PublishContainer $extra
        if ($LastExitCode -ne 0) {
            throw "Failed to publish container."
        }

        Write-Host "$($fullName):$($fullTag) published."
    }
}
