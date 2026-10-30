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

# 构建正式版并打包到兄弟目录 ../RimExodus.release（仓库外，不含 doc/.git/源码）。
# About.xml 去掉 Dev 标记（name 的 " Dev" 与 packageId 的 ".Dev"）后写入。
release version="1.7":
    powershell.exe -NoProfile -File build/Release.ps1 -Version {{version}}
