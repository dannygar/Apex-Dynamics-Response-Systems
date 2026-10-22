using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Evaluation.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Evaluation.Tests;

public class IngestionTests
{
    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    public async Task Utf8Extraction(string extension)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Grounded source"));
        Assert.Equal("Grounded source", await PrivateFiles.Extract(stream, extension, default));
    }
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("binary\0data")]
    public async Task InvalidTextRejected(string value)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value));
        await Assert.ThrowsAsync<InvalidDataException>(() => PrivateFiles.Extract(stream, ".txt", default));
    }
    [Fact] public void AzureBlobProviderDoesNotCreateLocalDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "apex-blob-" + Guid.NewGuid().ToString("N"));
        _ = new PrivateFiles(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "AzureBlob", ["Storage:ContainerUri"] = "https://acct.blob.core.windows.net/private",
            ["Storage:Root"] = root
        }).Build());
        Assert.False(Directory.Exists(root));
    }

    [Fact] public async Task InvalidUtf8Rejected()
    {
        using var stream = new MemoryStream(new byte[] { 0xff, 0xff });
        await Assert.ThrowsAsync<DecoderFallbackException>(() => PrivateFiles.Extract(stream, ".txt", default));
    }
    [Fact] public async Task OversizeTextRejected()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', PrivateFiles.MaximumText + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PrivateFiles.Extract(stream, ".txt", default));
    }
    [Fact] public async Task InvalidPdfRejected()
    {
        using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => PrivateFiles.Extract(stream, ".pdf", default));
    }
    [Fact] public async Task PdfTextExtraction()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Grounded PDF evidence", 12,
            new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
        using var stream = new MemoryStream(builder.Build());
        Assert.Contains("Grounded PDF evidence", await PrivateFiles.Extract(stream, ".pdf", default));
    }
    [Fact] public async Task ImageOnlyPdfRejected()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        using var stream = new MemoryStream(builder.Build());
        await Assert.ThrowsAsync<InvalidDataException>(() => PrivateFiles.Extract(stream, ".pdf", default));
    }
    [Fact] public async Task DocxExtraction()
    {
        using var stream = Docx("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Source evidence</w:t></w:r></w:p></w:body></w:document>""");
        Assert.Equal("Source evidence", await PrivateFiles.Extract(stream, ".docx", default));
    }
    [Fact] public async Task XmlExternalEntitiesRejected()
    {
        using var stream = Docx("""<!DOCTYPE x [<!ENTITY x SYSTEM "file:///C:/secret">]><x>&x;</x>""");
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => PrivateFiles.Extract(stream, ".docx", default));
    }
    [Fact] public async Task ScannerMissingCannotClearQuarantine()
    {
        var scanner = new MalwareScanner(new ConfigurationBuilder().Build());
        Assert.False(scanner.Configured);
        Assert.False(await scanner.Ping(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.Scan(new MemoryStream(), default));
    }
    private static MalwareScanner Scanner(int port) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Malware:ClamAvHost"] = "127.0.0.1", ["Malware:ClamAvPort"] = port.ToString() }).Build());
    private static async Task<(TcpListener Listener, Task<string> Received)> FakeClamAv(string reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[64]; var read = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
            return Encoding.ASCII.GetString(buffer, 0, read);
        });
        await Task.Yield();
        return (listener, received);
    }
    [Fact] public async Task PingRequiresPong()
    {
        var (listener, received) = await FakeClamAv("PONG");
        try
        {
            Assert.True(await Scanner(((IPEndPoint)listener.LocalEndpoint).Port).Ping(default));
            Assert.Equal("zPING\0", await received);
        }
        finally { listener.Stop(); }
        (listener, _) = await FakeClamAv("LOADING");
        try { Assert.False(await Scanner(((IPEndPoint)listener.LocalEndpoint).Port).Ping(default)); }
        finally { listener.Stop(); }
    }
    [Fact] public async Task ClosedScannerPortIsUnavailableNotRejected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.False(await Scanner(port).Ping(default));
        await Assert.ThrowsAsync<ScannerUnavailableException>(() => Scanner(port).Scan(new MemoryStream("x"u8.ToArray()), default));
    }
    [Theory]
    [InlineData("method-01-scope/stakeholder-map.md", "method-01-scope/stakeholder-map.md")]
    [InlineData("dt\\method-01-scope\\map.MD", "dt/method-01-scope/map.MD")]
    [InlineData("coaching-state.md", "coaching-state.md")]
    public void RelativePathsNormalize(string path, string expected) => Assert.Equal(expected, Artifacts.NormalizePath(path, true));
    [Theory]
    [InlineData("../up.md")]
    [InlineData("a/../b.md")]
    [InlineData("/root.md")]
    [InlineData("\\\\server\\share.md")]
    [InlineData("C:\\drive.md")]
    [InlineData("a/./b.md")]
    [InlineData("a//b.md")]
    [InlineData(" padded/a.md")]
    [InlineData("a\tb.md")]
    [InlineData("notes.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsafeRelativePathsRejected(string? path) => Assert.Throws<ArgumentException>(() => Artifacts.NormalizePath(path, true));
    [Fact] public void OverlongPathRejected() =>
        Assert.Throws<ArgumentException>(() => Artifacts.NormalizePath(new string('a', 258) + ".md", true));
    [Fact] public void NonMarkdownAllowedOnlyWhenNotDt() => Assert.Equal("brd.pdf", Artifacts.NormalizePath("brd.pdf", false));

    private static InputSnapshot Input(DocumentRole role, Guid? bundle = null, int length = 10, string path = "a.md")
    { var id = Guid.NewGuid(); return new(id, role, "h", length, bundle ?? id, path); }
    [Fact] public void GroupsOneArtifactPerRole()
    {
        var bundle = Guid.NewGuid();
        var dt = new[] { Input(DocumentRole.DT, bundle, path: "b.md"), Input(DocumentRole.DT, bundle, path: "a.md") };
        var brd = Input(DocumentRole.BRD); var prd = Input(DocumentRole.PRD);
        var artifacts = Artifacts.Group([prd, .. dt, brd], dt.Select(d => (d.VersionId, bundle)).ToArray());
        Assert.Equal([DocumentRole.DT, DocumentRole.BRD, DocumentRole.PRD], artifacts.Select(a => a.Role));
        Assert.Equal([dt[1].VersionId, dt[0].VersionId], artifacts[0].VersionIds); // Ordered by relative path.
    }
    [Fact] public void DtArtifactMustBeOneCompleteBundle()
    {
        var bundle = Guid.NewGuid(); var other = Guid.NewGuid();
        var a = Input(DocumentRole.DT, bundle); var b = Input(DocumentRole.DT, bundle); var c = Input(DocumentRole.DT, other);
        var members = new[] { (a.VersionId, bundle), (b.VersionId, bundle), (c.VersionId, other) };
        Assert.Single(Artifacts.Group([a, b], members));
        Assert.Throws<ArgumentException>(() => Artifacts.Group([a], members));
        Assert.Throws<ArgumentException>(() => Artifacts.Group([a, b, c], members));
    }
    [Fact] public void SelectionRulesRejectInvalidTargets()
    {
        Assert.Throws<ArgumentException>(() => Artifacts.Group([], []));
        Assert.Throws<ArgumentException>(() => Artifacts.Group([Input(DocumentRole.BRD), Input(DocumentRole.BRD)], []));
        Assert.Throws<ArgumentException>(() => Artifacts.Group([Input(DocumentRole.PRD), Input(DocumentRole.PRD)], []));
        Assert.Throws<ArgumentException>(() => Artifacts.Group([Input(DocumentRole.Source)], []));
        Assert.Throws<ArgumentException>(() => Artifacts.Group([Input(DocumentRole.PRD, length: Artifacts.MaximumArtifactCharacters + 1)], []));
    }
    [Theory]
    [InlineData("https://account.blob.core.windows.net/private?sig=not-a-real-secret")]
    [InlineData("http://account.blob.core.windows.net/private")]
    [InlineData("https://account.blob.core.windows.net/")]
    [InlineData("https://account.blob.core.windows.net/private/file")]
    public void CloudStorageRejectsSignedOrInvalidContainerConfiguration(string uri)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:Provider"] = "AzureBlob", ["Storage:ContainerUri"] = uri }).Build();
        Assert.Throws<ArgumentException>(() => new PrivateFiles(config));
    }
    private static MemoryStream Docx(string xml)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open())) writer.Write("<Types/>");
            using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open())) writer.Write(xml);
        }
        stream.Position = 0; return stream;
    }
}
