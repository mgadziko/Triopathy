param(
    [switch]$Publish,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish')
)
$ErrorActionPreference = 'Stop'
$appProject = Join-Path $PSScriptRoot 'Triopathy.Windows\Triopathy.Windows.csproj'
$testProject = Join-Path $PSScriptRoot 'Triopathy.Tests\Triopathy.Tests.csproj'
& dotnet run --project $testProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Behavior tests failed.' }
& dotnet build $appProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
if ($Publish) {
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Write-Host "Published Triopathy to $OutputDirectory"
}
