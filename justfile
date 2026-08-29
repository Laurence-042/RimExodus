default: build

build:
    dotnet build Source/RimExodus.csproj -c Debug

# 构建正式版并打包到兄弟目录 ../RimExodus.release（仓库外，不含 doc/.git/源码）。
# About.xml 去掉 Dev 标记（name 的 " Dev" 与 packageId 的 ".Dev"）后写入。
release:
    dotnet build Source/RimExodus.csproj -c Release
    rm -rf ../RimExodus.release
    mkdir -p ../RimExodus.release/About
    sed -e 's/Seamless World Dev</Seamless World</' -e 's/\.SeamlessWorld\.Dev</.SeamlessWorld</' About/About.xml > ../RimExodus.release/About/About.xml
    cp About/Preview.png About/PublishedFileId.txt ../RimExodus.release/About/
    cp -r 1.6 ../RimExodus.release/1.6
    rm -f ../RimExodus.release/1.6/Assemblies/*.pdb
    cp -r Textures ../RimExodus.release/Textures
    cp LICENSE ../RimExodus.release/
