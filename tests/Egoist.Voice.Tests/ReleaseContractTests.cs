using System.Text.Json;
using System.Text.RegularExpressions;

namespace Egoist.Voice.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void InstallerScripts_DeriveTheirDefaultVersionFromTheProject()
    {
        var root = RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "Egoist.Voice.csproj"));
        var version = Regex.Match(project, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(version));

        foreach (var relativePath in new[]
                 {
                     "scripts/test-installer.ps1"
                 })
        {
            var script = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.Contains("[string]$Version = \"\"", script, StringComparison.Ordinal);
            Assert.Contains("<Version>([^<]+)</Version>", script, StringComparison.Ordinal);
            Assert.DoesNotContain("[string]$Version = \"2.0.0\"", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VoiceDeliveryIsOneIntegrityCheckedExeWithABoundedBootstrapWindow()
    {
        var root = RepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(root, "installer", "EgoistVoiceBootstrap.cs"));
        var packer = File.ReadAllText(Path.Combine(root, "scripts", "New-EgoistVoiceSingleFile.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "scripts", "Test-EgoistVoiceSingleFile.ps1"));

        Assert.Contains("EGOISTVOICEPKG01", bootstrap, StringComparison.Ordinal);
        Assert.Contains("VerifyPayload", bootstrap, StringComparison.Ordinal);
        Assert.Contains("ExtractPayload", bootstrap, StringComparison.Ordinal);
        Assert.Contains("AvailableFreeSpace", bootstrap, StringComparison.Ordinal);
        Assert.Contains("FormBorderStyle.FixedDialog", bootstrap, StringComparison.Ordinal);
        Assert.Contains("FormStartPosition.CenterScreen", bootstrap, StringComparison.Ordinal);
        Assert.Contains("MaximizeBox = false", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("FormWindowState.Maximized", bootstrap, StringComparison.Ordinal);
        Assert.Contains("EGOISTVOICEPKG01", packer, StringComparison.Ordinal);
        Assert.Contains("TransformBlock", packer, StringComparison.Ordinal);
        Assert.Contains("Embedded payload checksum mismatch", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void CorpusSchemasAndRunner_KeepTheOfflinePrivacyContract()
    {
        var root = RepositoryRoot();
        foreach (var name in new[]
                 {
                     "corpus-script-v2.schema.json",
                     "corpus-reference-v2.schema.json",
                     "corpus-profile-v1.schema.json",
                     "benchmark-report-v2.schema.json",
                     "benchmark-report-v3.schema.json"
                 })
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "corpus", name)));
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.True(schema.RootElement.TryGetProperty("$schema", out _));
            Assert.True(schema.RootElement.TryGetProperty("$id", out _));
        }

        using (var reportSchema = JsonDocument.Parse(File.ReadAllText(
                   Path.Combine(root, "tests", "corpus", "benchmark-report-v3.schema.json"))))
        {
            Assert.False(reportSchema.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.False(reportSchema.RootElement
                .GetProperty("properties")
                .GetProperty("entries")
                .GetProperty("items")
                .GetProperty("additionalProperties")
                .GetBoolean());
            var schemaText = reportSchema.RootElement.GetRawText();
            Assert.Contains("transcript", schemaText, StringComparison.Ordinal);
            Assert.Contains("audioPath", schemaText, StringComparison.Ordinal);
            Assert.Contains("audioContent", schemaText, StringComparison.Ordinal);
            Assert.Contains("targetApplication", schemaText, StringComparison.Ordinal);
        }

        var schemaPrivacyTest = File.ReadAllText(
            Path.Combine(root, "tests", "corpus", "Test-BenchmarkReportV3Schema.ps1"));
        Assert.Contains("Test-Json", schemaPrivacyTest, StringComparison.Ordinal);
        Assert.Contains("$forbiddenProperties", schemaPrivacyTest, StringComparison.Ordinal);
        Assert.Contains("$objectPaths", schemaPrivacyTest, StringComparison.Ordinal);

        var runner = File.ReadAllText(Path.Combine(root, "scripts", "run-corpus-baseline.ps1"));
        Assert.Contains("[switch]$Record", runner, StringComparison.Ordinal);
        Assert.Contains("--corpus-record", runner, StringComparison.Ordinal);
        Assert.Contains("--corpus-benchmark", runner, StringComparison.Ordinal);
        Assert.Contains("$DecoderMode", runner, StringComparison.Ordinal);
        Assert.Contains("$WhisperRuntime", runner, StringComparison.Ordinal);
        Assert.Contains("$Profile", runner, StringComparison.Ordinal);
        Assert.Contains("$ProfilePath", runner, StringComparison.Ordinal);
        Assert.Contains("quality-challenge-v1.json", runner, StringComparison.Ordinal);
        Assert.Contains(".candidate", runner, StringComparison.Ordinal);
        Assert.Contains("Start-Process", runner, StringComparison.Ordinal);
        Assert.Contains("-Wait", runner, StringComparison.Ordinal);
        Assert.Contains("$BenchmarkProcess.ExitCode", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("& $Executable --corpus-benchmark", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item", runner, StringComparison.OrdinalIgnoreCase);

        var app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        Assert.Contains("allowModelDownload: false", app, StringComparison.Ordinal);
        Assert.Contains("AppLog.SuppressSensitiveData()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicProxyCorpus_PinsProvenanceAndStaysOutsideGit()
    {
        var root = RepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "tests", "corpus", "public-proxy-v1.sources.json")));
        var descriptor = document.RootElement;

        Assert.Equal("egoist.voice.public-proxy-sources/v1", descriptor.GetProperty("schema").GetString());
        Assert.Equal("local-only-no-transcripts-in-git", descriptor.GetProperty("privacy").GetString());
        Assert.Equal(0.25, descriptor.GetProperty("selection").GetProperty("minDurationSeconds").GetDouble());
        var sources = descriptor.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(4, sources.Length);
        Assert.Equal(3025, sources.Sum(source => source.GetProperty("targetCount").GetInt32()));
        Assert.Equal(250, descriptor.GetProperty("derivedSets")[0].GetProperty("count").GetInt32());

        foreach (var source in sources)
        {
            Assert.Matches("^[0-9a-f]{40}$", source.GetProperty("revision").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("license").GetString()));
            foreach (var file in source.GetProperty("files").EnumerateArray())
            {
                Assert.True(file.GetProperty("bytes").GetInt64() > 0);
                Assert.Matches("^[0-9a-f]{64}$", file.GetProperty("sha256").GetString()!);
            }
        }

        var wrapper = File.ReadAllText(Path.Combine(root, "scripts", "acquire-public-proxy-corpus.ps1"));
        var importer = File.ReadAllText(Path.Combine(root, "scripts", "acquire-public-proxy-corpus.py"));
        var gitIgnore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert.Contains("artifacts\\corpora\\public-proxy-v1", wrapper, StringComparison.Ordinal);
        Assert.Contains("revision=source[\"revision\"]", importer, StringComparison.Ordinal);
        Assert.Contains("sha256_file", importer, StringComparison.Ordinal);
        Assert.Contains("valid_normalized_audio", importer, StringComparison.Ordinal);
        Assert.Contains("len(payload) > 44", importer, StringComparison.Ordinal);
        Assert.Contains("previous_source_rows", importer, StringComparison.Ordinal);
        Assert.Contains("Public proxy evidence only; no user-voice accuracy claim.", importer, StringComparison.Ordinal);
        Assert.Contains("artifacts/", gitIgnore, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Egoist.Voice.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Egoist.Voice.sln not found");
    }
}
