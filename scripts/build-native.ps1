# Copyright (c) 2026 Maël Kobura
# SPDX-License-Identifier: MIT
#
# Builds the native library nat_vorbis for win-x64 and copies it to
# artifacts/native/win-x64/, where the package project takes it.
# Run from any folder; CMake and a C compiler for x64 must be available.

param(
    [string]$Generator = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root "artifacts/native/build"
$output = Join-Path $root "artifacts/native/win-x64"

# The source folder is given as an argument from the build folder, which every version of CMake accepts.
$arguments = @((Join-Path $root "native"), "-A", "x64")
if ($Generator -ne "") {
    $arguments += @("-G", $Generator)
}

New-Item -ItemType Directory -Force $build | Out-Null
Push-Location $build
try {
    & cmake @arguments
    if ($LASTEXITCODE -ne 0) { throw "CMake configuration failed." }
}
finally {
    Pop-Location
}

& cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "The native build failed." }

New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item (Join-Path $build "$Configuration/nat_vorbis.dll") $output -Force
Write-Host "nat_vorbis.dll -> $output"
