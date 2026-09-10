# Signing documents with GroupDocs.Signature for .NET inside a Linux container.
#
# The point of this file is the font layer. Build this image and Dockerfile.nofonts and compare:
# without it the sample cannot sign at all. GroupDocs.Signature does not substitute a missing
# family - it raises "Sign document error: Font <name> was not found" - and dropping SignatureFont
# does not rescue you either, because it then asks for its own default (Times New Roman) and fails
# identically. On a fontless base image every text signature fails, so the font layer is required,
# not an optimisation.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY DockerFontsDemo.csproj ./
RUN dotnet restore DockerFontsDemo.csproj

COPY . ./
RUN dotnet publish DockerFontsDemo.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/runtime:8.0

# Fonts. The base image ships none, so a text signature that names a family gets whatever the
# platform substitutes - usually nothing, and CJK renders as empty boxes.
#   fonts-dejavu-core     Latin/Greek/Cyrillic workhorse; the sample asks for "DejaVu Sans"
#   fonts-liberation      metric-compatible stand-ins for Arial / Times New Roman / Courier New,
#                         which is what documents authored on Windows actually reference
#   fonts-noto-cjk        Chinese, Japanese and Korean; the sample asks for "Noto Sans CJK JP"
#   fontconfig            the resolver itself, plus fc-cache / fc-list for debugging
RUN apt-get update && apt-get install -y --no-install-recommends \
        fontconfig \
        fonts-dejavu-core \
        fonts-liberation \
        fonts-noto-cjk \
    && fc-cache -f \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# documents/ and Result/ are resolved relative to the working directory.
# Mount a volume over /app/Result to keep the signed file after the container exits:
#   docker run --rm -v "$PWD/Result:/app/Result" groupdocs-signature-fonts-net
ENTRYPOINT ["dotnet", "DockerFontsDemo.dll"]
