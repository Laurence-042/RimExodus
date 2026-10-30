set windows-shell := ["powershell.exe", "-NoProfile", "-Command"]

default:
    @just --list

# Build REF first, fetch the reference DLL, then build RimExodus.
build version="1.7":
    powershell.exe -NoProfile -File build/Build.ps1 -Version {{version}}

test-ref:
    just --justfile ../RimExodusFramework/justfile test

# Actual 1.7 Harmony binding plus absent/late/drift compatibility fixtures; no game launch.
test-compat:
    powershell.exe -NoProfile -File build/TestCompat.ps1

# Unmodified SS reference algorithms with host fixtures; no game launch.
test-ss:
    powershell.exe -NoProfile -File build/TestSurveillance.ps1

# 构建 1.6 和 1.7 正式版并打包到兄弟目录 ../RimExodus.release（仓库外，不含 doc/.git/源码）。
# About.xml 去掉 (Dev) 标记，并将自身 ID 与 REF 依赖都转为正式 ID。
release:
    powershell.exe -NoProfile -File build/Release.ps1
