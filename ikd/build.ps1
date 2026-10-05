#Requires -Version 5.1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "== restore / build =="
dotnet build ikd.slnx -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== tests =="
dotnet run --project tests\Ikd.Tests\Ikd.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== publish ikd.exe =="
$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish src\Ikd.Cli\Ikd.Cli.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$ikd = Join-Path $dist "ikd.exe"
if (-not (Test-Path $ikd)) { Write-Error "未生成 $ikd"; exit 1 }

# geometry.ikd 是纯库（无 main），不参与直接运行
$appExamples = @(
    "hello.ikd", "shapes.ikd", "generics.ikd", "oo.ikd",
    "app.ikd", "closures.ikd", "match.ikd", "errors.ikd"
)

Write-Host "== ikd version =="
& $ikd version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== examples: check =="
Get-ChildItem (Join-Path $PSScriptRoot "examples") -Filter *.ikd | ForEach-Object {
    Write-Host "-- check $($_.Name)"
    & $ikd check $_.FullName
    if ($LASTEXITCODE -ne 0) { Write-Error "检查失败: $($_.Name)"; exit $LASTEXITCODE }
}

Write-Host "== examples: run =="
foreach ($e in $appExamples) {
    Write-Host "-- run $e"
    & $ikd run (Join-Path "examples" $e)
    if ($LASTEXITCODE -ne 0) { Write-Error "示例失败: $e"; exit $LASTEXITCODE }
}

Write-Host "== examples: build to exe + smoke =="
$exeDir = Join-Path $dist "examples"
New-Item -ItemType Directory -Path $exeDir -Force | Out-Null
foreach ($e in $appExamples) {
    $out = Join-Path $exeDir ($e -replace '\.ikd$', '.exe')
    Write-Host "-- build $e -> $($e -replace '\.ikd$', '.exe')"
    & $ikd build (Join-Path "examples" $e) -o $out
    if ($LASTEXITCODE -ne 0) { Write-Error "打包失败: $e"; exit $LASTEXITCODE }
    & $out
    if ($LASTEXITCODE -ne 0) { Write-Error "打包出的 exe 运行失败: $e"; exit $LASTEXITCODE }
}

Write-Host "== 零依赖冒烟（把 ikd.exe 拷到临时目录） =="
$smoke = Join-Path $env:TEMP ("ikd-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $smoke -Force | Out-Null
try {
    Copy-Item $ikd (Join-Path $smoke "ikd.exe")
    $helloSrc = Join-Path $PSScriptRoot "examples\hello.ikd"
    & (Join-Path $smoke "ikd.exe") run $helloSrc
    if ($LASTEXITCODE -ne 0) { Write-Error "零依赖 run 失败"; exit $LASTEXITCODE }
    Push-Location $smoke
    try {
        & .\ikd.exe build $helloSrc -o smoke.exe
        if ($LASTEXITCODE -ne 0) { Write-Error "零依赖 build 失败"; exit $LASTEXITCODE }
        & .\smoke.exe
        if ($LASTEXITCODE -ne 0) { Write-Error "零依赖 smoke.exe 失败"; exit $LASTEXITCODE }
    }
    finally { Pop-Location }
}
finally {
    if (Test-Path $smoke) { Remove-Item $smoke -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "完成。产物:"
Write-Host "  $ikd"
Write-Host "  $exeDir"
exit 0
