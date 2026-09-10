# Signing PDFs in Linux Containers with Fonts

[![Product Page](https://img.shields.io/badge/Product%20Page-2865E0?style=for-the-badge&logo=appveyor&logoColor=white)](https://github.com/groupdocs-signature/GroupDocs.Signature-Docs)
[![Docs](https://img.shields.io/badge/Docs-2865E0?style=for-the-badge&logo=Hugo&logoColor=white)](https://docs.groupdocs.com/signature/net/)
[![Blog](https://img.shields.io/badge/Blog-2865E0?style=for-the-badge&logo=WordPress&logoColor=white)](https://blog.groupdocs.com/categories/groupdocs.signature-product-family/)
[![Free Support](https://img.shields.io/badge/Free%20Support-2865E0?style=for-the-badge&logo=Discourse&logoColor=white)](https://forum.groupdocs.com/c/signature/13)
[![Temporary License](https://img.shields.io/badge/Temporary%20License-2865E0?style=for-the-badge&logo=rocket&logoColor=white)](https://purchase.groupdocs.com/temp-license/100124)

## Introduction

`sign-pdf-in-linux-container-fonts-dotnet` is a runnable .NET 8 console project that applies text signatures to a PDF inside a Linux container and reports the fonts it had to work with. It ships two Dockerfiles on purpose: `Dockerfile` installs a font layer, `Dockerfile.nofonts` does not. Build both, run both, and the difference is the whole lesson.

The finding behind the sample: **GroupDocs.Signature does not substitute a missing font.** Naming a family that is not installed raises `Sign document error: Font <name> was not found` and writes no document. Omitting the font does not rescue you either, because the library then asks for its own default, Times New Roman, and fails the same way. On `mcr.microsoft.com/dotnet/runtime:8.0`, which ships zero fonts, that means every text signature fails and there is no code-level workaround.

## Use Case Scenarios

The container that signs invoices in a background worker is the usual case: it works on the developer's Windows box, where 300 fonts are installed, and exits non-zero the first time it runs in the cluster. The second case is CJK: an image with DejaVu signs Latin text quietly and fails only when a Japanese or Chinese string arrives, so the bug ships. The third is a size optimisation that backfires, where someone sets `InvariantGlobalization=true` to trim ICU and `new Signature(...)` throws `CultureNotFoundException` before any font question is reached.

## The Problem

A base image is not a desktop. `mcr.microsoft.com/dotnet/runtime:8.0` and `python:3.11-slim` ship no fonts at all; `eclipse-temurin:17-jre` bundles 8 DejaVu files and `node:18-bookworm` bundles 6, which is why JVM and Node images sign Latin text quietly and still fail on CJK. Nobody gets CJK for free.

The usual reflex, picking a font by scanning `/usr/share/fonts` for a filename, does not work either. Debian's `fonts-noto-cjk` installs `NotoSansCJK-Regular.ttc`, whose family name is `Noto Sans CJK JP`. A filename match both misses fonts that are present and claims fonts that will not resolve when passed to `SignatureFont.FamilyName`.

### Common Challenges

- ❌ The failure surfaces as an exception at sign time, in the container, not at build time where it would be cheap to catch.
- ❌ Hard-coding `Arial` works on Windows and fails on every Debian-based image, which installs metric-compatible Liberation fonts under different family names.
- ❌ Font detection by filename claims families that `SignatureFont` cannot resolve.

## The Solution

The sample asks the library instead of guessing. For each candidate family it attempts a throwaway signature into a temporary file and keeps the first one that does not throw. That is portable: it works the same on a laptop with 400 fonts and in an image with two, and it never trusts a filename.

✅ **Resolve, do not assume** - `ResolveUsableFamily` probes a preference list, most portable first, and returns `null` when nothing resolves.
✅ **Degrade honestly** - a missing CJK family skips the CJK signature rather than throwing; a missing Latin family is fatal and says so.
✅ **Inventory without System.Drawing** - `System.Drawing.Common` is Windows-only from .NET 7 and throws on Linux, so the font scan reads the font directories directly.
✅ **Prove the round trip** - the signed file is re-opened and searched, so "the CJK text is in there" is a read-back rather than a claim.

## Implementation Workflow

1. **Inventory** - list the font files actually on disk, so the log distinguishes "no fonts" from "wrong family".
2. **Resolve** - probe Latin candidates (`DejaVu Sans`, `Liberation Sans`, `Arial`, `Verdana`) and CJK candidates (`Noto Sans CJK JP` first) against the real document.
3. **Sign** - build one `TextSignOptions` per resolved family and pass them to a single `Sign` call.
4. **Search** - re-open the output and read the text signatures back.
5. **Fail loudly** - catch `GroupDocsSignatureException`, print the minimum fix (`apt-get install -y fonts-dejavu-core`), and exit 3.

### Why does the font layer belong in the Dockerfile rather than in code?

Because there is nothing for code to fall back to. GroupDocs.Signature resolves a family through the platform; when the image has no font files, every family fails, including the library's own Times New Roman default. No try/catch, no embedded font path, and no `SignatureFont` omission changes that. The image must carry at least one font.

The layer this sample installs:

```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
        fontconfig \
        fonts-dejavu-core \
        fonts-liberation \
        fonts-noto-cjk \
    && fc-cache -f \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
```

`fontconfig` is the resolver plus `fc-list` for debugging, `fonts-dejavu-core` is the Latin/Greek/Cyrillic minimum, `fonts-liberation` supplies metric-compatible stand-ins for the Arial and Times New Roman that Windows-authored documents reference, and `fonts-noto-cjk` covers Chinese, Japanese and Korean.

## Requirements

- **.NET SDK 8.0** - the project targets `net8.0`; the runtime image is `mcr.microsoft.com/dotnet/runtime:8.0`.
- **GroupDocs.Signature 26.6.0** - pinned in `DockerFontsDemo.csproj`.
- **Docker** - optional for the host run, required to see the two-image comparison.
- **Licence (optional)** - set the path in `Program.cs`, or mount one and pass `LIC_PATH`; without it the run is in evaluation mode and adds trial text to the page.

## Project Structure

```
sign-pdf-in-linux-container-fonts-dotnet/
│
├── DockerFontsDemo.csproj
├── Dockerfile
├── Dockerfile.nofonts
├── .dockerignore
├── Program.cs
└── documents/
    └── sample.pdf
```

**File Organization:**
- **Program.cs** - the whole sample: inventory, resolution, signing, read-back, and the deliberate failure
- **DockerFontsDemo.csproj** - targets net8.0, pins GroupDocs.Signature, and carries a comment explaining why `InvariantGlobalization` must stay off
- **Dockerfile** - build plus runtime with the font layer
- **Dockerfile.nofonts** - the same image without fonts, so the failure can be reproduced on demand
- **documents/sample.pdf** - the input, copied to the output directory on build

## Practical Examples

### Use Case: Signs the document with a text signature per resolved family

Use it when the set of usable fonts is only known at run time, which in a container is always. Both signatures go through one `Sign` call, and passing `null` for a family omits `SignatureFont` entirely so the platform default is used rather than a name the library cannot resolve.

```csharp
using var signature = new Signature(sourcePath);

var options = new List<SignOptions>
{
    BuildTextOptions(LatinText, latinFamily, top: 50),
};

// Without a CJK-capable font the glyphs cannot be embedded, so skip rather than throw.
if (cjkFamily is not null)
{
    options.Add(BuildTextOptions(CjkText, cjkFamily, top: 120));
}

SignResult result = signature.Sign(outputPath, options);
return result.Succeeded.Count;
```

The return value is `result.Succeeded.Count`, so the caller logs how many signatures were actually written instead of assuming two. In practice: an image with DejaVu but no Noto returns 1, and the run still succeeds with a warning.

### Use Case: Builds a text signature option set, attaching a font only when a family was resolved

The conditional is the important line. `SignatureFont` is left unset when no family resolved, because naming an absent family is exactly what raises the "Font ... was not found" exception this sample is about.

```csharp
var options = new TextSignOptions(text)
{
    Left = 50,
    Top = top,
    Width = 280,
    Height = 40,
};

if (familyName is not null)
{
    options.Font = new SignatureFont { FamilyName = familyName, Size = 16 };
}

return options;
```

Note that omitting the font is not a fix on a fontless image - it moves the failure from your family name to Times New Roman. It is only useful when the platform has some font and you do not care which.

### Use Case: Returns the first candidate family GroupDocs can actually use

Run this before signing anything real. The candidate lists are ordered most-portable-first, so a container hits `DejaVu Sans` immediately while a Windows developer box falls through to `Arial`.

```csharp
foreach (string candidate in candidates)
{
    if (TryFamily(sourcePath, candidate).Ok)
    {
        return candidate;
    }
}

return null;
```

Returning `null` rather than throwing is what lets the caller treat a missing CJK font as "skip that signature" and a missing Latin font as "this image cannot sign".

### Use Case: Attempts a throwaway signature with one font family and reports whether it succeeded

This is the probe the resolution depends on. It signs into a temporary file so nothing lands in `Result/`, and it converts the exception into a return value:

```csharp
string scratch = Path.Combine(Path.GetTempPath(), $"gd-font-probe-{Guid.NewGuid():N}.pdf");
try
{
    using var signature = new Signature(sourcePath);
    var options = new TextSignOptions("probe")
    {
        Left = 10,
        Top = 10,
        Width = 60,
        Height = 20,
        Font = new SignatureFont { FamilyName = familyName, Size = 10 },
    };

    signature.Sign(scratch, options);
    return (true, "ok");
}
```

The `finally` is what keeps the probe clean, and the catch narrows to the library's own exception type rather than swallowing everything:

```csharp
catch (GroupDocsSignatureException ex)
{
    return (false, ex.Message);
}
finally
{
    if (File.Exists(scratch))
    {
        File.Delete(scratch);
    }
}
```

One probe per candidate is the cost. On a slim image with two fonts that is two attempts for Latin and eight for CJK, all against a one-page PDF, which is cheap enough to do at startup and cache.

### Use Case: Attempts a signature with a font family that does not exist and returns the resulting error text

The sample prints this on every run, in both images, so the exact message is visible next to the font inventory that explains it.

```csharp
(bool ok, string message) = TryFamily(sourcePath, AbsentFamily);
return ok ? "no exception - this platform substituted a font instead of failing" : message;
```

The `ok` branch exists because other platforms do substitute. If you ever see it fire, the environment is being more forgiving than GroupDocs.Signature is, and the code that depends on it is not portable.

### Use Case: Reads every text signature back out of the signed document

Signing without reading back is how a container ships broken CJK: the call succeeded, the glyphs are boxes. `TextSearchOptions` with `AllPages` returns what is actually in the file.

```csharp
using var signature = new Signature(signedPath);

var options = new TextSearchOptions { AllPages = true };
List<TextSignature> found = signature.Search<TextSignature>(options);

var texts = new List<string>();
foreach (TextSignature item in found)
{
    texts.Add(item.Text);
}

return texts;
```

The sample compares the recovered strings against what it signed and reports each one as recovered or not. In evaluation mode the trial text shows up here too, which is worth knowing before you read the output as a failure.

### Use Case: Returns the font files visible in the standard system and per-user font directories

The inventory runs before anything else so the log answers "what did this image actually have" without a shell in the container. It probes Linux, Windows and macOS locations in one pass:

```csharp
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string[] roots =
{
    "/usr/share/fonts",
    "/usr/local/share/fonts",
    Path.Combine(home, ".fonts"),
    Path.Combine(home, ".local/share/fonts"),
    Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
    "/System/Library/Fonts",
    "/Library/Fonts",
};
```

Directories that do not exist are skipped, and one that cannot be read is skipped too rather than ending the scan:

```csharp
var files = new List<string>();
foreach (string root in roots)
{
    if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
    {
        continue;
    }

    try
    {
        var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        foreach (string file in found)
        {
            if (Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                files.Add(file);
            }
        }
    }
    catch (UnauthorizedAccessException)
    {
        // A font directory we may not read tells us nothing; keep scanning the rest.
    }
}

return files;
```

`System.Drawing` is deliberately absent: `System.Drawing.Common` is Windows-only from .NET 7 onward and throws on Linux, which is its own popular container failure. Reading the directories needs no native dependency and behaves the same everywhere.

## Benefits

Font resolution stops being an assumption and becomes a startup check with a log line, so a broken image fails with "no usable font, install fonts-dejavu-core" instead of a stack trace at request time. The two Dockerfiles make the difference reproducible for anyone who doubts it, the read-back proves CJK survived rather than merely rendering, and the whole thing runs identically on a developer machine, where it simply resolves a different family. I built the probe after losing an afternoon to a filename match that confidently reported `NotoSansCJK-Regular` as available and then failed on every family name I derived from it.

## Related Use Cases and Resources

If you are running GroupDocs.Signature in containers or CI, these resources will help you:

* **Step-by-step use case guide in the documentation** - The container walkthrough: what each base image ships, the probe, and the fallback rules: [Read the article →](https://docs.groupdocs.com/signature/net/use-cases/signing-documents-linux-container-fonts/)

* **In-depth blog article about this project** - Why a fontless image cannot sign at all, and the four packages that fix it: [Read the article →](https://blog.groupdocs.com/signature/signing-documents-linux-container-fonts-net/)

* **Signing Documents in .NET with PKCS#11 USB Dongles, Smart Cards and HSMs** - The other deployment-shaped signing problem, where the constraint is hardware rather than fonts: [Read the article →](https://blog.groupdocs.com/signature/sign-documents-with-pkcs11-dotnet/)

* **GroupDocs.Signature for .NET 26.6 Release Highlights** - The release this sample pins, so you can see what changed around it: [Read the article →](https://blog.groupdocs.com/signature/groupdocs-signature-for-net-26-6/)

* **Signing Documents: the .NET Signing Reference** - The full option surface behind `TextSignOptions` and `SignatureFont`: [Read the article →](https://docs.groupdocs.com/signature/net/signing/)

* **System Requirements** - Supported platforms and runtimes, including the Linux notes: [Read the article →](https://docs.groupdocs.com/signature/net/system-requirements/)

## Keywords

`linux`, `sign`, `documents`, `pdf`, `docker`, `fonts`, `groupdocs signature`, `dotnet signing`, `text signature`, `container fonts`, `fontconfig`, `fonts-dejavu-core`, `fonts-noto-cjk`, `liberation fonts`, `cjk signature`, `SignatureFont`, `TextSignOptions`, `font not found`, `net8`, `dockerfile`, `font resolution`, `document signing in containers`, `invariant globalization`, `pdf signing linux`

**Ready to get started?** [View Documentation](https://docs.groupdocs.com/signature/net/) | [Get Support](https://forum.groupdocs.com/c/signature/13) | [Request License](https://purchase.groupdocs.com/temp-license/100124)
