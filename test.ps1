$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ObjDir = Join-Path $ProjectRoot 'obj'
New-Item -ItemType Directory -Force -Path $ObjDir | Out-Null

$FwDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$WpfDir = Join-Path $FwDir 'WPF'
$Csc = Join-Path $FwDir 'csc.exe'
$TestExe = Join-Path $ObjDir 'DriverPlusTests.exe'

$SrcDir = Join-Path $ProjectRoot 'src'
$Sources = @()
if (Test-Path $SrcDir) {
    $Sources = @(
        Get-ChildItem -Path $SrcDir -Recurse -Filter '*.cs' -File |
        Where-Object { $_.Name -ne 'Program.cs' } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName }
    )
}
$TestFiles = @(
    Get-ChildItem -Path (Join-Path $ProjectRoot 'tests') -Recurse -Filter '*.cs' -File |
    Sort-Object FullName |
    ForEach-Object { $_.FullName }
)

$AllFiles = @($Sources + $TestFiles)
Write-Host "[TDD] Compiling $($AllFiles.Count) source/test file(s) -> $TestExe"

& $Csc /nologo /target:exe /platform:x64 /optimize+ /highentropyva+ /langversion:5 `
    /r:"$FwDir\System.dll" `
    /r:"$FwDir\System.Core.dll" `
    /r:"$FwDir\Microsoft.CSharp.dll" `
    /r:"$FwDir\System.Drawing.dll" `
    /r:"$FwDir\System.Management.dll" `
    /r:"$FwDir\System.ServiceProcess.dll" `
    /r:"$FwDir\System.Windows.Forms.dll" `
    /r:"$FwDir\System.Web.Extensions.dll" `
    /r:"$FwDir\System.IO.Compression.dll" `
    /r:"$FwDir\System.IO.Compression.FileSystem.dll" `
    /r:"$FwDir\System.Net.Http.dll" `
    /r:"$FwDir\System.Xaml.dll" `
    /r:"$WpfDir\WindowsBase.dll" `
    /r:"$WpfDir\PresentationCore.dll" `
    /r:"$WpfDir\PresentationFramework.dll" `
    "/out:$TestExe" `
    @AllFiles

if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE"
}

Write-Host "[TDD] Running DriverPlusTests.exe..."
& $TestExe
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE"
}
