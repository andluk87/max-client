#!/bin/sh
set -eu
# A successful publish is required before claiming an EXE exists.
dotnet publish /project/native/MaxClient.csproj \
  --no-restore -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false -o /project/dist
test -s /project/dist/MAX-Native-Client.exe
cp /project/notices/THIRD_PARTY_NOTICES.md /project/dist/THIRD_PARTY_NOTICES.md
printf '\nГотово: dist/MAX-Native-Client.exe\n'
