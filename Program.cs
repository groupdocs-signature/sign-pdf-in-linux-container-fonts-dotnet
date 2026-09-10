// Topic: Signing documents inside a Linux container - font provisioning, resolving a family that exists, and a working Dockerfile.
// Most container base images ship zero fonts, and GroupDocs.Signature does NOT substitute a missing one: naming a family that is
// not installed throws "Sign document error: Font <name> was not found" and produces no document. Leaving the font unset does not
// help either - GroupDocs then requests its own default (Times New Roman) and fails identically - so a fontless image cannot apply
// a text signature at all. This sample inventories the fonts on disk, asks the library which candidate families it can actually
// use, signs a PDF with Latin and CJK text signatures, reads both back, and shows the missing-font exception inside a catch.
// Run it against Dockerfile (fonts installed) and Dockerfile.nofonts (none) to see both outcomes.

using System.Text;
using GroupDocs.Signature;
using GroupDocs.Signature.Domain;
using GroupDocs.Signature.Options;

namespace Demo.DockerFonts;

internal static class Program
{
    private const string DocsFolder = "documents";
    private const string ResultFolder = "Result";

    private static readonly string SourcePdf = Path.Combine(DocsFolder, "sample.pdf");
    private static readonly string SignedPdf = Path.Combine(ResultFolder, "signed.pdf");

    // Preference order, most portable first. The container installs the first entry of each list;
    // the trailing entries are what a Windows or macOS developer box is likely to have instead.
    private static readonly string[] LatinCandidates = { "DejaVu Sans", "Liberation Sans", "Arial", "Verdana" };
    private static readonly string[] CjkCandidates =
    {
        "Noto Sans CJK JP", "Noto Sans CJK SC", "Noto Sans CJK", "Noto Sans JP",
        "MS Gothic", "Yu Gothic", "SimSun", "Malgun Gothic",
    };

    // A family that exists nowhere, used to show the failure mode on purpose.
    private const string AbsentFamily = "No Such Font Family";

    private const string LatinText = "Approved by GroupDocs";

    // Japanese for "approved". Kept as escapes so this file stays ASCII, and never written to
    // stdout - the Windows console codepage here cannot encode it and would crash the sample.
    private const string CjkText = "\u627F\u8A8D\u6E08\u307F";

    private static int Main()
    {
        Directory.CreateDirectory(DocsFolder);
        Directory.CreateDirectory(ResultFolder);
        ApplyLicense();

        Console.WriteLine("=== GroupDocs.Signature - signing in a container: font report ===");
        Console.WriteLine($"[env] os        : {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim()}");
        Console.WriteLine($"[env] container : {(IsContainer() ? "yes" : "no")}");

        IReadOnlyList<string> fontFiles = FontInventory.FindFontFiles();
        Console.WriteLine($"[fonts] font files on disk: {fontFiles.Count}");
        Console.WriteLine($"[fonts] sample: {FontInventory.Summarise(fontFiles, 6)}");

        if (!File.Exists(SourcePdf))
        {
            Console.Error.WriteLine($"Missing source document: {Path.GetFullPath(SourcePdf)}");
            return 1;
        }

        string? latinFamily = ResolveUsableFamily(SourcePdf, LatinCandidates);
        string? cjkFamily = ResolveUsableFamily(SourcePdf, CjkCandidates);
        Console.WriteLine($"[fonts] latin family resolved: {latinFamily ?? "(none - falling back to the platform default)"}");
        Console.WriteLine($"[fonts] cjk family resolved  : {cjkFamily ?? "(none - CJK signature will be skipped)"}");

        // The teaching moment: what actually happens when the image has no fonts.
        Console.WriteLine($"[demo] signing with '{AbsentFamily}' on purpose...");
        Console.WriteLine($"[demo] -> {DescribeMissingFontFailure(SourcePdf)}");

        int applied;
        try
        {
            applied = SignWithResolvedFonts(SourcePdf, SignedPdf, latinFamily, cjkFamily);
        }
        catch (GroupDocsSignatureException ex)
        {
            // Reached when the image has no usable font at all. Omitting SignatureFont does not help:
            // GroupDocs then asks for its own default family (Times New Roman) and fails the same way.
            Console.Error.WriteLine($"[sign] FAILED: {ex.Message}");
            Console.Error.WriteLine("[sign] this image cannot render text signatures - it has no usable font.");
            Console.Error.WriteLine("[sign] there is no code-level workaround: install at least one font in the image.");
            Console.Error.WriteLine("[sign] minimum fix: apt-get install -y fonts-dejavu-core (add fonts-noto-cjk for CJK).");
            return 3;
        }

        Console.WriteLine($"[sign] text signatures applied: {applied}");

        IReadOnlyList<string> recovered = SearchTextSignatures(SignedPdf);
        Console.WriteLine($"[search] text signatures found: {recovered.Count}");
        Console.WriteLine($"[search] latin text recovered : {(recovered.Contains(LatinText) ? "yes" : "no")}");
        Console.WriteLine($"[search] cjk text recovered   : {(recovered.Contains(CjkText) ? "yes" : "no")}");
        if (cjkFamily is null)
        {
            Console.WriteLine("[warn] no CJK font in this image - install fonts-noto-cjk (see Dockerfile) to sign CJK text.");
        }

        Console.WriteLine($"[result] {Path.GetFullPath(SignedPdf)}");
        return recovered.Count > 0 ? 0 : 2;
    }

    private static void ApplyLicense()
    {
        // Point this at your .lic file to remove evaluation limits.
        // Get a free temporary licence: https://purchase.groupdocs.com/temporary-license
        const string licensePath = "REPLACE_WITH_YOUR_LICENSE_PATH";

        // In a container the path above is baked in at build time, which is rarely what you want.
        // LIC_PATH lets the licence be mounted and named at run time instead:
        //   docker run --rm -v "/path/to/licences:/lic:ro" -e LIC_PATH=/lic/GroupDocs.Total.lic <image>
        string? fromEnv = Environment.GetEnvironmentVariable("LIC_PATH");

        string? resolved = File.Exists(licensePath) ? licensePath
            : !string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv) ? fromEnv
            : null;

        if (resolved is not null)
        {
            new License().SetLicense(resolved);
            Console.WriteLine("[license] applied");
        }
        else
        {
            // Evaluation mode still signs, but it adds its own trial text to the page - which the
            // search below will report alongside (or instead of) yours. Licence it for a clean run.
            Console.WriteLine("[license] no licence set - running in evaluation mode");
        }
    }

    /// <summary>
    /// Detects whether the process is running inside a container.
    /// </summary>
    /// <remarks>
    /// Checks for the Docker marker file, then for a container runtime named in PID 1's cgroup entry,
    /// which also catches containerd, podman and Kubernetes. Always false on Windows.
    /// </remarks>
    private static bool IsContainer()
    {
        if (File.Exists("/.dockerenv"))
        {
            return true;
        }

        const string cgroup = "/proc/1/cgroup";
        if (!File.Exists(cgroup))
        {
            return false;
        }

        try
        {
            string text = File.ReadAllText(cgroup);
            return text.Contains("docker") || text.Contains("containerd") || text.Contains("kubepods");
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Signs the document with a text signature per resolved family, skipping CJK when no CJK font exists.
    /// </summary>
    /// <remarks>
    /// Passing <c>null</c> for a family omits <see cref="SignatureFont"/> entirely so GroupDocs uses the
    /// platform default rather than a name it cannot resolve. Both signatures go through a single
    /// <c>Sign</c> call. Returns the number of signatures written.
    /// </remarks>
    private static int SignWithResolvedFonts(string sourcePath, string outputPath, string? latinFamily, string? cjkFamily)
    {
        using var signature = new Signature(sourcePath);

        var options = new List<SignOptions>
        {
            BuildTextOptions(LatinText, latinFamily, top: 50),
        };

        // Without a CJK-capable font the glyphs cannot be embedded at all, so skip rather than throw.
        if (cjkFamily is not null)
        {
            options.Add(BuildTextOptions(CjkText, cjkFamily, top: 120));
        }

        SignResult result = signature.Sign(outputPath, options);
        return result.Succeeded.Count;
    }

    /// <summary>
    /// Builds a text signature option set, attaching a font only when a family was resolved.
    /// </summary>
    /// <remarks>
    /// <see cref="SignatureFont"/> is left unset when <paramref name="familyName"/> is <c>null</c>;
    /// naming a family that is not installed is what raises the "Font ... was not found" exception.
    /// </remarks>
    private static TextSignOptions BuildTextOptions(string text, string? familyName, int top)
    {
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
    }

    /// <summary>
    /// Attempts a signature with a font family that does not exist and returns the resulting error text.
    /// </summary>
    /// <remarks>
    /// This is the exact failure a fontless base image produces. The attempt writes to a temporary file
    /// that is deleted afterwards, so it never touches <c>Result/</c>. The exception is caught on purpose -
    /// the sample is demonstrating the message, not crashing on it.
    /// </remarks>
    private static string DescribeMissingFontFailure(string sourcePath)
    {
        (bool ok, string message) = TryFamily(sourcePath, AbsentFamily);
        return ok ? "no exception - this platform substituted a font instead of failing" : message;
    }

    /// <summary>
    /// Returns the first candidate family GroupDocs can actually use, or <c>null</c> if none work.
    /// </summary>
    /// <remarks>
    /// Resolution asks the library rather than guessing from file names. Font files rarely carry the
    /// family string a caller must pass - the Debian <c>fonts-noto-cjk</c> package installs
    /// <c>NotoSansCJK-Regular.ttc</c>, whose family is "Noto Sans CJK JP" - so a filename match both
    /// misses real fonts and claims fonts that will not resolve.
    /// </remarks>
    private static string? ResolveUsableFamily(string sourcePath, IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (TryFamily(sourcePath, candidate).Ok)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts a throwaway signature with one font family and reports whether it succeeded.
    /// </summary>
    /// <remarks>
    /// Writes to a temporary file that is always deleted, so probing never touches <c>Result/</c>.
    /// A missing family surfaces as <see cref="GroupDocsSignatureException"/> carrying
    /// "Font ... was not found", which is returned rather than thrown.
    /// </remarks>
    private static (bool Ok, string Message) TryFamily(string sourcePath, string familyName)
    {
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
    }

    /// <summary>
    /// Reads every text signature back out of the signed document.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="TextSearchOptions"/> across all pages and returns the recovered strings, which is
    /// how the sample proves the CJK text survived the round-trip rather than merely appearing to.
    /// </remarks>
    private static IReadOnlyList<string> SearchTextSignatures(string signedPath)
    {
        using var signature = new Signature(signedPath);

        var options = new TextSearchOptions { AllPages = true };
        List<TextSignature> found = signature.Search<TextSignature>(options);

        var texts = new List<string>();
        foreach (TextSignature item in found)
        {
            texts.Add(item.Text);
        }

        return texts;
    }
}

/// <summary>
/// A filesystem-level inventory of the fonts a container or host actually provides.
/// </summary>
/// <remarks>
/// Deliberately does not use <c>System.Drawing</c>: <c>System.Drawing.Common</c> is Windows-only from
/// .NET 7 onward and throws on Linux, which is itself a common container failure. Scanning the font
/// directories works identically on every platform and needs no native dependency.
/// </remarks>
internal static class FontInventory
{
    private static readonly string[] Extensions = { ".ttf", ".otf", ".ttc", ".pfb" };

    /// <summary>
    /// Returns the font files visible in the standard system and per-user font directories.
    /// </summary>
    /// <remarks>
    /// Probes the Linux, Windows and macOS locations in one pass and ignores directories that do not
    /// exist, so the same call is meaningful on a developer laptop and inside a slim base image.
    /// </remarks>
    internal static IReadOnlyList<string> FindFontFiles()
    {
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

        var files = new List<string>();
        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
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
    }

    /// <summary>
    /// Builds a short, ASCII-safe sample of the font files found, for logging.
    /// </summary>
    /// <remarks>
    /// Prints distinct file stems rather than resolved family names, and says how many were not shown,
    /// so a container with two fonts and a laptop with four hundred both produce one readable line.
    /// </remarks>
    internal static string Summarise(IReadOnlyList<string> fontFiles, int max)
    {
        if (fontFiles.Count == 0)
        {
            return "(none - this image has no fonts installed)";
        }

        var names = new List<string>();
        foreach (string file in fontFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (!names.Contains(stem))
            {
                names.Add(stem);
            }

            if (names.Count == max)
            {
                break;
            }
        }

        var builder = new StringBuilder(string.Join(", ", names));
        int remaining = fontFiles.Count - names.Count;
        if (remaining > 0)
        {
            builder.Append($" (+{remaining} more)");
        }

        return builder.ToString();
    }
}
