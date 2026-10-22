using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Evaluation.Core;

public sealed class PrivateFiles
{
    public const long MaximumBytes = 20 * 1024 * 1024;
    public const int MaximumText = 1_000_000;
    private readonly string root;
    private readonly BlobContainerClient? container;
    public PrivateFiles(IConfiguration config)
    {
        var provider = config["Storage:Provider"] ?? "Local";
        if (provider == "AzureBlob")
        {
            if (!Uri.TryCreate(config["Storage:ContainerUri"], UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Query.Length != 0 || uri.UserInfo.Length != 0 ||
                uri.Fragment.Length != 0 || uri.AbsolutePath.Trim('/').Split('/').Length != 1 ||
                string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
                throw new ArgumentException("Storage:ContainerUri must be an existing HTTPS container URI without credentials or query parameters.");
            // DefaultAzureCredential uses the user-assigned managed identity named by AZURE_CLIENT_ID when set.
            var clientId = config["AZURE_CLIENT_ID"];
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            { ManagedIdentityClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId, ExcludeInteractiveBrowserCredential = true });
            container = new BlobContainerClient(uri, credential, new BlobClientOptions
            { Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(30) } });
            // Never create a container or change public access/RBAC from application startup.
        }
        else if (provider != "Local") throw new ArgumentException("Storage:Provider must be Local or AzureBlob.");
        root = Path.GetFullPath(config["Storage:Root"] ?? "private-data");
        // Blob storage never touches the local disk; app containers run as non-root with a read-only /app.
        if (container is null) Directory.CreateDirectory(root);
    }
    private string PathFor(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new InvalidDataException("Invalid private object key");
        return Path.Combine(root, key);
    }
    public Stream Open(string key)
    {
        _ = PathFor(key); // Validate opaque keys for both providers.
        return container is null ? new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read) :
            container.GetBlobClient(key).OpenRead(new BlobOpenReadOptions(allowModifications: false));
    }
    public async Task<(string Key, string Hash, long Size)> Store(Stream source, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N");
        try
        {
            await using Stream target = container is null ?
                new FileStream(PathFor(key), FileMode.CreateNew, FileAccess.Write, FileShare.None) : new MemoryStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) != 0)
            {
                length += read;
                if (length > MaximumBytes) throw new InvalidDataException("File exceeds 20 MB limit.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (length == 0) throw new InvalidDataException("Empty file.");
            if (container is not null)
            {
                target.Position = 0;
                await container.GetBlobClient(key).UploadAsync(target, overwrite: false, cancellationToken: ct);
            }
            return (key, Convert.ToHexString(hash.GetHashAndReset()), length);
        }
        catch
        {
            // Cloud upload timeouts can leave an unreferenced object. Operator orphan
            // reconciliation must remove it; do not mask the original failure with another network call.
            if (container is null) Delete(key);
            throw;
        }
    }
    public void Delete(string key)
    {
        _ = PathFor(key);
        if (container is null) File.Delete(PathFor(key));
        else container.GetBlobClient(key).DeleteIfExists();
    }
    public async Task<string> StoreText(string text, CancellationToken ct)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return (await Store(stream, ct)).Key;
    }
    public async Task<string> ReadText(string key, CancellationToken ct)
    {
        await using var stream = Open(key);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var text = await reader.ReadToEndAsync(ct);
        if (text.Length > MaximumText) throw new InvalidDataException("Extracted text exceeds the stored limit.");
        return text;
    }
    public static async Task<string> Extract(Stream stream, string extension, CancellationToken ct)
    {
        if (extension is not ".pdf" and not ".docx" and not ".md" and not ".txt")
            throw new InvalidDataException("Unsupported document format.");
        string text;
        if (extension == ".pdf")
        {
            var signature = new byte[5];
            if (await stream.ReadAsync(signature, ct) != 5 || Encoding.ASCII.GetString(signature) != "%PDF-")
                throw new InvalidDataException("Invalid PDF signature.");
            stream.Position = 0;
            using var pdf = PdfDocument.Open(stream);
            if (pdf.IsEncrypted || pdf.NumberOfPages is < 1 or > 500)
                throw new InvalidDataException("Encrypted PDFs or more than 500 pages are unsupported.");
            var result = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var pageText = ContentOrderTextExtractor.GetText(page);
                if ((long)result.Length + pageText.Length + 1 > MaximumText)
                    throw new InvalidDataException("PDF text limit exceeded.");
                result.AppendLine(pageText);
            }
            text = result.ToString();
        }
        else if (extension == ".docx")
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
            if (archive.Entries.Count > 1024 || archive.Entries.Sum(x => x.Length) > 40 * 1024 * 1024 ||
                archive.Entries.Any(e => e.Length > 20 * 1024 * 1024 ||
                    (e.Length > 1024 * 1024 && e.Length / Math.Max(1, e.CompressedLength) > 100)))
                throw new InvalidDataException("Archive expansion limit exceeded.");
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry("word/vbaProject.bin") is not null)
                throw new InvalidDataException("Unsupported DOCX package.");
            var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("Missing DOCX document.");
            using var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            var xml = XDocument.Load(reader);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            text = string.Join("\n", xml.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
        }
        else
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var buffer = new char[MaximumText + 1];
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            text = new string(buffer, 0, length).TrimStart('\uFEFF');
            if (text.Any(c => c == '\0' || (char.IsControl(c) && c is not '\r' and not '\n' and not '\t')))
                throw new InvalidDataException("Invalid UTF-8 text content.");
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumText)
            throw new InvalidDataException("Empty or oversized extracted text.");
        return text;
    }
}

// ClamAV INSTREAM is an operator-controlled malware service, never a document URL.
public sealed class MalwareScanner(IConfiguration config)
{
    public bool Configured => !string.IsNullOrWhiteSpace(config["Malware:ClamAvHost"]);
    private int Port => int.TryParse(config["Malware:ClamAvPort"], out var port) ? port : 3310;

    // ClamAV needs minutes to load signatures after container start; only claim uploads once it answers PING.
    public async Task<bool> Ping(CancellationToken cancellationToken)
    {
        if (!Configured) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(config["Malware:ClamAvHost"]!, Port, timeout.Token);
            await using var network = client.GetStream();
            await network.WriteAsync(Encoding.ASCII.GetBytes("zPING\0"), timeout.Token);
            return await ReadReply(network, timeout.Token) == "PONG";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    public async Task Scan(Stream content, CancellationToken cancellationToken)
    {
        if (!Configured) throw new InvalidOperationException("Malware scanner is not configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        using var client = new TcpClient();
        try { await client.ConnectAsync(config["Malware:ClamAvHost"]!, Port, ct); }
        catch (Exception e) when (e is SocketException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        { throw new ScannerUnavailableException(); }
        await using var network = client.GetStream();
        await network.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), ct);
        var buffer = new byte[65536];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
        {
            var size = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size, read);
            await network.WriteAsync(size, ct);
            await network.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await network.WriteAsync(new byte[4], ct);
        if (await ReadReply(network, ct) != "stream: OK")
            throw new InvalidDataException("Malware scan rejected content or did not return a clean verdict.");
    }

    private static async Task<string> ReadReply(Stream network, CancellationToken ct)
    {
        var response = new List<byte>();
        var one = new byte[1];
        while (response.Count < 1024 && await network.ReadAsync(one, ct) != 0 && one[0] != 0) response.Add(one[0]);
        return Encoding.ASCII.GetString(response.ToArray());
    }
}
public sealed class ScannerUnavailableException() : Exception("Malware scanner is not reachable.") { }
