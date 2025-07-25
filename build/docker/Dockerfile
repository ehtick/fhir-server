# --platform tells docker to always use the host platform for the build not the target platform. Runtime container will use target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.302-azurelinux3.0 AS build
ARG TARGETARCH
ARG FHIR_VERSION
ARG ASSEMBLY_VER

WORKDIR /repo

COPY .editorconfig \
     CustomAnalysisRules.ruleset \
     CustomAnalysisRules.Test.ruleset \
     Directory.Build.props \
     Directory.Packages.props \
     global.json \
     nuget.config \
     stylecop.json \
     THIRDPARTYNOTICES.md \
     ./

# Copy csproj and projitem files first, then do a dotnet restore. These layers are only invalidated
# when these project files change.

COPY ./src/Microsoft.Health.Fhir.ValueSets/Microsoft.Health.Fhir.ValueSets.csproj \
     ./src/Microsoft.Health.Fhir.ValueSets/Microsoft.Health.Fhir.ValueSets.csproj

COPY ./src/Microsoft.Health.Fhir.Core/Microsoft.Health.Fhir.Core.csproj \
     ./src/Microsoft.Health.Fhir.Core/Microsoft.Health.Fhir.Core.csproj

COPY ./src/Microsoft.Health.Fhir.Azure/Microsoft.Health.Fhir.Azure.csproj \
     ./src/Microsoft.Health.Fhir.Azure/Microsoft.Health.Fhir.Azure.csproj

COPY ./src/Microsoft.Health.TaskManagement/Microsoft.Health.TaskManagement.csproj \
     ./src/Microsoft.Health.TaskManagement/Microsoft.Health.TaskManagement.csproj

COPY ./src/Microsoft.Health.Fhir.SqlServer/Microsoft.Health.Fhir.SqlServer.csproj \
     ./src/Microsoft.Health.Fhir.SqlServer/Microsoft.Health.Fhir.SqlServer.csproj

COPY ./src/Microsoft.Health.Fhir.Api.OpenIddict/Microsoft.Health.Fhir.Api.OpenIddict.csproj \
     ./src/Microsoft.Health.Fhir.Api.OpenIddict/Microsoft.Health.Fhir.Api.OpenIddict.csproj

COPY ./src/Microsoft.Health.Fhir.Api/Microsoft.Health.Fhir.Api.csproj \
     ./src/Microsoft.Health.Fhir.Api/Microsoft.Health.Fhir.Api.csproj

COPY ./src/Microsoft.Health.Fhir.CosmosDb/Microsoft.Health.Fhir.CosmosDb.csproj \
     ./src/Microsoft.Health.Fhir.CosmosDb/Microsoft.Health.Fhir.CosmosDb.csproj

COPY ./src/Microsoft.Health.Fhir.CosmosDb.Core/Microsoft.Health.Fhir.CosmosDb.Core.csproj \
     ./src/Microsoft.Health.Fhir.CosmosDb.Core/Microsoft.Health.Fhir.CosmosDb.Core.csproj

COPY ./src/Microsoft.Health.Fhir.CosmosDb.Initialization/Microsoft.Health.Fhir.CosmosDb.Initialization.csproj \
     ./src/Microsoft.Health.Fhir.CosmosDb.Initialization/Microsoft.Health.Fhir.CosmosDb.Initialization.csproj

COPY ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Core/Microsoft.Health.Fhir.${FHIR_VERSION}.Core.csproj \
     ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Core/Microsoft.Health.Fhir.${FHIR_VERSION}.Core.csproj

COPY ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Api/Microsoft.Health.Fhir.${FHIR_VERSION}.Api.csproj \
     ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Api/Microsoft.Health.Fhir.${FHIR_VERSION}.Api.csproj

COPY ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Web/Microsoft.Health.Fhir.${FHIR_VERSION}.Web.csproj \
     ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Web/Microsoft.Health.Fhir.${FHIR_VERSION}.Web.csproj

COPY ./src/Microsoft.Health.Fhir.Shared.Web/Microsoft.Health.Fhir.Shared.Web.projitems \
     ./src/Microsoft.Health.Fhir.Shared.Web/Microsoft.Health.Fhir.Shared.Web.projitems

COPY ./src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems \
     ./src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems

COPY ./src/Microsoft.Health.Fhir.Shared.Client/Microsoft.Health.Fhir.Shared.Client.projitems \
     ./src/Microsoft.Health.Fhir.Shared.Client/Microsoft.Health.Fhir.Shared.Client.projitems

COPY ./src/Microsoft.Health.Fhir.Shared.Api/Microsoft.Health.Fhir.Shared.Api.projitems \
     ./src/Microsoft.Health.Fhir.Shared.Api/Microsoft.Health.Fhir.Shared.Api.projitems

RUN dotnet restore ./src/Microsoft.Health.Fhir.${FHIR_VERSION}.Web/Microsoft.Health.Fhir.${FHIR_VERSION}.Web.csproj -a $TARGETARCH

# now copy over everything else and publish

COPY . .

RUN dotnet publish /repo/src/Microsoft.Health.Fhir.${FHIR_VERSION}.Web/Microsoft.Health.Fhir.${FHIR_VERSION}.Web.csproj -o "/build" --no-restore -p:AssemblyVersion="${ASSEMBLY_VER}" -p:FileVersion="${ASSEMBLY_VER}" -p:Version="${ASSEMBLY_VER}" -f net9.0 -a $TARGETARCH

# Implicitly uses the target platform for the runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:9.0.7-azurelinux3.0 AS runtime

ARG FHIR_VERSION

# tdnf clean all - cleans all the repos used to obtain packages and reduces the size of our image.
RUN tdnf clean all && tdnf repolist --refresh && tdnf update -y && tdnf clean all

# See https://github.com/dotnet/SqlClient/issues/220
RUN tdnf install icu -y && \
  tdnf clean all

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080

WORKDIR /app
COPY --from=build /build .

RUN ln -s "Microsoft.Health.Fhir.${FHIR_VERSION}.Web.dll" "Microsoft.Health.Fhir.Web.dll"

USER nonroot
EXPOSE 8080
ENTRYPOINT ["dotnet", "Microsoft.Health.Fhir.Web.dll"]
