[CmdletBinding()]
param(
    [string]$GameDir
)

$ErrorActionPreference = "Stop"
$RepoDir = $PSScriptRoot
$LibDir = Join-Path $RepoDir "lib"
$Project = Join-Path $RepoDir "src\Sts2Headless\Sts2Headless.csproj"
$RequiredDlls = @(
    "sts2.dll",
    "SmartFormat.dll",
    "SmartFormat.ZString.dll",
    "Sentry.dll",
    "Steamworks.NET.dll",
    "MonoMod.Backports.dll",
    "MonoMod.ILHelpers.dll",
    "0Harmony.dll",
    "System.IO.Hashing.dll"
)

function Get-SteamRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    $defaultRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Steam"),
        (Join-Path $env:ProgramFiles "Steam"),
        "C:\Steam",
        "D:\SteamLibrary",
        "E:\SteamLibrary"
    )
    foreach ($root in $defaultRoots) {
        if ($root -and (Test-Path $root)) { $roots.Add($root) }
    }

    foreach ($registryPath in @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\Software\WOW6432Node\Valve\Steam"
    )) {
        try {
            $steam = Get-ItemProperty -Path $registryPath -ErrorAction Stop
            $root = $steam.SteamPath
            if (-not $root) { $root = $steam.InstallPath }
            if ($root -and (Test-Path $root)) { $roots.Add($root) }
        } catch {
        }
    }

    foreach ($root in @($roots)) {
        $libraryFile = Join-Path $root "steamapps\libraryfolders.vdf"
        if (-not (Test-Path $libraryFile)) { continue }
        $content = Get-Content -Raw -Path $libraryFile
        foreach ($match in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
            $library = $match.Groups[1].Value -replace '\\\\', '\'
            if (Test-Path $library) { $roots.Add($library) }
        }
    }

    return $roots | Select-Object -Unique
}

function Find-GameDirectory {
    if ($GameDir) {
        if (-not (Test-Path $GameDir)) {
            throw "Game directory does not exist: $GameDir"
        }
        return (Resolve-Path $GameDir).Path
    }

    foreach ($steamRoot in Get-SteamRoots) {
        $gameRoot = Join-Path $steamRoot "steamapps\common\Slay the Spire 2"
        if (-not (Test-Path $gameRoot)) { continue }
        $dataDir = Join-Path $gameRoot "data_sts2_windows_x86_64"
        if (Test-Path (Join-Path $dataDir "sts2.dll")) { return $dataDir }
        if (Test-Path (Join-Path $gameRoot "sts2.dll")) { return $gameRoot }
        $found = Get-ChildItem -Path $gameRoot -Filter "sts2.dll" -File -Recurse |
            Select-Object -First 1
        if ($found) { return $found.Directory.FullName }
    }

    throw "Slay the Spire 2 was not found. Pass -GameDir with the folder containing sts2.dll."
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    throw ".NET 9 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/9.0"
}
$DotNet = $dotnetCommand.Source
$sdks = & $DotNet --list-sdks
if (-not ($sdks -match '^9\.')) {
    throw ".NET 9 SDK is required. Installed SDKs: $($sdks -join ', ')"
}
$runtimes = & $DotNet --list-runtimes
if (-not ($runtimes -match '^Microsoft\.NETCore\.App 9\.')) {
    throw ".NET 9 runtime is required. Install it from https://dotnet.microsoft.com/download/dotnet/9.0"
}

$ResolvedGameDir = Find-GameDirectory
Write-Host "Game directory: $ResolvedGameDir"
New-Item -ItemType Directory -Path $LibDir -Force | Out-Null

$gameFiles = Get-ChildItem -Path $ResolvedGameDir -File -Recurse
foreach ($dll in $RequiredDlls) {
    $source = $gameFiles | Where-Object { $_.Name -ieq $dll } | Select-Object -First 1
    if (-not $source) { throw "Required game file was not found: $dll" }
    $destination = Join-Path $LibDir $dll
    Copy-Item -Path $source.FullName -Destination $destination -Force
    Write-Host "Copied $dll"
}

$sts2Dll = Join-Path $LibDir "sts2.dll"
Copy-Item -Path $sts2Dll -Destination (Join-Path $LibDir "sts2.dll.original") -Force

$patchDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sts2-tui-patcher-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $patchDir | Out-Null
try {
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Mono.Cecil" Version="0.11.6" />
  </ItemGroup>
</Project>
'@ | Set-Content -Path (Join-Path $patchDir "Patcher.csproj") -Encoding UTF8

    @'
using System;
using System.IO;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Cil;

var dllPath = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(dllPath)!);
var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters {
    AssemblyResolver = resolver,
    ReadingMode = ReadingMode.Deferred
});

var patches = 0;
foreach (var type in module.Types)
{
    foreach (var nested in type.NestedTypes)
    {
        foreach (var nested2 in nested.NestedTypes)
        {
            if (!nested2.Name.Contains("YieldAwaiter") && nested2.Name != "<>c") continue;
            foreach (var method in nested2.Methods)
            {
                if (method.Name != "get_IsCompleted" || method.Body == null) continue;
                var il = method.Body.GetILProcessor();
                il.Body.Instructions.Clear();
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                patches++;
            }
        }
    }
}

foreach (var type in module.Types)
{
    foreach (var method in type.Methods)
    {
        if (method.Name != "WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction" || method.Body == null)
            continue;
        var il = method.Body.GetILProcessor();
        il.Body.Instructions.Clear();
        var completedTask = module.ImportReference(
            typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!);
        il.Emit(OpCodes.Call, completedTask);
        il.Emit(OpCodes.Ret);
        patches++;
    }
}

var output = dllPath + ".patched";
module.Write(output);
module.Dispose();
File.Move(output, dllPath, true);
Console.WriteLine($"Applied {patches} IL patches");
'@ | Set-Content -Path (Join-Path $patchDir "Program.cs") -Encoding UTF8

    Write-Host "Applying headless IL patches..."
    & $DotNet run --project (Join-Path $patchDir "Patcher.csproj") --configuration Release -- $sts2Dll
    if ($LASTEXITCODE -ne 0) { throw "IL patching failed with exit code $LASTEXITCODE" }
} finally {
    Remove-Item -Path $patchDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Building the headless backend..."
& $DotNet build $Project --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Backend build failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Setup complete. Start the game with STS2-TUI.exe or STS2-TUI.cmd."
