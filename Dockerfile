FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /project
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY native/MaxClient.csproj native/
RUN dotnet restore native/MaxClient.csproj -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
COPY native/ native/
COPY THIRD_PARTY_NOTICES.md /project/notices/THIRD_PARTY_NOTICES.md
COPY docker/build-windows.sh /usr/local/bin/build-windows
RUN chmod +x /usr/local/bin/build-windows
CMD ["/usr/local/bin/build-windows"]
