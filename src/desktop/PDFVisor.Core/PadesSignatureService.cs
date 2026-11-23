using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PDFVisor.Interop;

namespace PDFVisor.Core;

public sealed record PdfSignatureVerificationResult(
    string SignerName,
    string SubFilter,
    string Reason,
    string SigningTime,
    bool IsCryptographicallyValid,
    bool CoversWholeDocument,
    bool IsChainTrusted,
    bool HasTimestampToken,
    string StatusSummary);

public static class PadesSignatureService
{
    public const int DefaultReservedBytes = 12288;
    private static readonly Oid TimeStampTokenOid = new("1.2.840.1.113549.1.9.16.2.14");

    /// <summary>
    /// Lists signing-capable certificates with private keys from the Windows CurrentUser Personal store (CNG / Smart Cards),
    /// filtering out expired certificates and certificates whose KeyUsage explicitly forbids digital signatures.
    /// </summary>
    public static IReadOnlyList<X509Certificate2> ListWindowsSigningCertificates(bool includeExpired = false)
    {
        var result = new List<X509Certificate2>();
        DateTime now = DateTime.Now;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        foreach (var cert in store.Certificates)
        {
            if (!cert.HasPrivateKey) continue;
            if (!includeExpired && (now < cert.NotBefore || now > cert.NotAfter)) continue;

            var ku = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (ku is not null &&
                (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
            {
                continue;
            }

            result.Add(cert);
        }
        return result;
    }

    /// <summary>
    /// Loads a PKCS#12 (.pfx / .p12) signing certificate with private key from disk.
    /// </summary>
    public static X509Certificate2 LoadCertificateFromPfx(string pfxFilePath, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pfxFilePath);
        var cert = X509CertificateLoader.LoadPkcs12FromFile(
            pfxFilePath,
            password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException("El archivo PFX/P12 no contiene una clave privada de firma.");
        return cert;
    }

    /// <summary>
    /// Signs a PDF with a detached CMS/PKCS#7 PAdES signature (ETSI.CAdES.detached, SHA-256)
    /// using QPDF for ISO 32000 structure preparation and Windows CNG / SignedCms for cryptography.
    /// </summary>
    public static void SignPdfPades(
        string sourcePdfPath,
        string destinationPdfPath,
        X509Certificate2 certificate,
        int pageIndex,
        PvRect widgetRect,
        string? reason = "Firmado digitalmente con PDFVisor",
        string? location = null,
        string? sourcePassword = null,
        string? tsaUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPdfPath);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("El certificado seleccionado no contiene clave privada.");

        string signerLabel = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrWhiteSpace(signerLabel)) signerLabel = certificate.Subject;

        // 1. Prepare PDF with /Sig dictionary, visible /AP /N appearance, /ByteRange and <0000...> hex placeholder
        QpdfTools.PreparePadesSignature(
            sourcePdfPath,
            destinationPdfPath,
            pageIndex,
            widgetRect,
            signerLabel,
            reason,
            location,
            DefaultReservedBytes,
            sourcePassword);

        // 2. Read prepared file and locate exact <0000...> placeholder bounds (last occurrence for incremental co-signatures)
        byte[] fileBytes = File.ReadAllBytes(destinationPdfPath);
        string hexZeros = "<" + new string('0', DefaultReservedBytes * 2) + ">";
        byte[] pattern = Encoding.ASCII.GetBytes(hexZeros);
        int contentsStart = FindLastSubsequence(fileBytes, pattern);
        if (contentsStart < 0)
            throw new InvalidOperationException("No se encontró el marcador de firma /Contents en el PDF preparado.");

        int contentsEnd = contentsStart + pattern.Length;
        int part2Len = fileBytes.Length - contentsEnd;

        // 3. Concatenate exact ByteRange [0..contentsStart] + [contentsEnd..EOF]
        byte[] signedData = new byte[contentsStart + part2Len];
        Buffer.BlockCopy(fileBytes, 0, signedData, 0, contentsStart);
        Buffer.BlockCopy(fileBytes, contentsEnd, signedData, contentsStart, part2Len);

        // 4. Compute detached CMS/PKCS#7 signature (SHA-256 + Pkcs9SigningTime)
        var contentInfo = new ContentInfo(signedData);
        var signedCms = new SignedCms(contentInfo, detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.WholeChain
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        signedCms.ComputeSignature(signer, silent: false);

        // 4b. Optional RFC 3161 Timestamping (PAdES-B-T)
        if (!string.IsNullOrWhiteSpace(tsaUrl) && signedCms.SignerInfos.Count > 0)
        {
            byte[] sigValue = signedCms.SignerInfos[0].GetSignature();
            byte[] tsaTokenDer = RequestRfc3161TimestampToken(tsaUrl, sigValue);
            signedCms.SignerInfos[0].AddUnsignedAttribute(new AsnEncodedData(TimeStampTokenOid, tsaTokenDer));
        }

        byte[] derSignature = signedCms.Encode();
        if (derSignature.Length > DefaultReservedBytes)
            throw new InvalidOperationException($"La firma CMS ({derSignature.Length} B) excede el espacio reservado ({DefaultReservedBytes} B).");

        // 5. Write hex-encoded DER signature into the <0000...> slot (preserving exact byte length)
        string hexSig = Convert.ToHexString(derSignature);
        byte[] hexBytes = Encoding.ASCII.GetBytes(hexSig);
        Buffer.BlockCopy(hexBytes, 0, fileBytes, contentsStart + 1, hexBytes.Length);

        File.WriteAllBytes(destinationPdfPath, fileBytes);
    }

    /// <summary>
    /// Verifies all digital signatures present in an open PdfDocument against the exact ByteRange on disk.
    /// </summary>
    public static IReadOnlyList<PdfSignatureVerificationResult> VerifySignatures(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rawSigs = document.GetSignatures();
        if (rawSigs.Count == 0) return Array.Empty<PdfSignatureVerificationResult>();

        string backingPath = document.ActiveSourcePath;
        if (string.IsNullOrWhiteSpace(backingPath) || !File.Exists(backingPath))
            return Array.Empty<PdfSignatureVerificationResult>();

        byte[] fileBytes = File.ReadAllBytes(backingPath);
        var results = new List<PdfSignatureVerificationResult>(rawSigs.Count);

        foreach (var sig in rawSigs)
        {
            if (sig.ByteRange.Length < 4 || sig.ContentsDer.Length == 0)
            {
                results.Add(new PdfSignatureVerificationResult(
                    "Desconocido", sig.SubFilter, sig.Reason, sig.Time, false, false, false, false, "Firma incompleta o sin ByteRange"));
                continue;
            }

            int off1 = sig.ByteRange[0];
            int len1 = sig.ByteRange[1];
            int off2 = sig.ByteRange[2];
            int len2 = sig.ByteRange[3];

            bool rangeValid = off1 >= 0 && len1 >= 0 && off2 >= 0 && len2 >= 0 &&
                              off1 + len1 <= fileBytes.Length &&
                              off2 + len2 <= fileBytes.Length;

            if (!rangeValid)
            {
                results.Add(new PdfSignatureVerificationResult(
                    "Desconocido", sig.SubFilter, sig.Reason, sig.Time, false, false, false, false, "ByteRange fuera de límites"));
                continue;
            }

            bool coversWhole = (off1 == 0) && (off2 + len2 == fileBytes.Length);
            byte[] signedData = new byte[len1 + len2];
            Buffer.BlockCopy(fileBytes, off1, signedData, 0, len1);
            Buffer.BlockCopy(fileBytes, off2, signedData, len1, len2);

            byte[] der = TrimDerTrailingZeros(sig.ContentsDer);

            string signerName = "Desconocido";
            bool cryptoValid = false;
            bool chainTrusted = false;
            bool hasTimestamp = false;
            string summary;

            try
            {
                var signedCms = new SignedCms(new ContentInfo(signedData), detached: true);
                signedCms.Decode(der);

                if (signedCms.SignerInfos.Count > 0)
                {
                    var si = signedCms.SignerInfos[0];
                    if (si.Certificate is X509Certificate2 cert)
                    {
                        signerName = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                        if (string.IsNullOrWhiteSpace(signerName)) signerName = cert.Subject;

                        using var chain = new X509Chain();
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(3);
                        chainTrusted = chain.Build(cert);
                    }

                    foreach (CryptographicAttributeObject attr in si.UnsignedAttributes)
                    {
                        if (attr.Oid?.Value == TimeStampTokenOid.Value)
                        {
                            hasTimestamp = true;
                            break;
                        }
                    }
                }

                signedCms.CheckSignature(verifySignatureOnly: true);
                cryptoValid = true;
                string level = hasTimestamp ? "PAdES-B-T" : "PAdES-B-B";
                string trust = chainTrusted ? "cadena confiable" : "certificado no confiable localmente";
                summary = coversWhole
                    ? $"Válida ({level}, documento íntegro, {trust})"
                    : $"Válida ({level}, con modificaciones posteriores, {trust})";
            }
            catch (Exception ex)
            {
                summary = $"Firma inválida o documento alterado: {ex.Message}";
            }

            results.Add(new PdfSignatureVerificationResult(
                signerName,
                sig.SubFilter,
                sig.Reason,
                sig.Time,
                cryptoValid,
                coversWhole,
                chainTrusted,
                hasTimestamp,
                summary));
        }

        return results;
    }

    private static byte[] RequestRfc3161TimestampToken(string tsaUrl, byte[] signatureBytes)
    {
        byte[] hash = SHA256.HashData(signatureBytes);
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        var req = Rfc3161TimestampRequest.CreateFromHash(hash, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: nonce);
        byte[] reqDer = req.Encode();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var content = new ByteArrayContent(reqDer);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var resp = http.PostAsync(tsaUrl, content).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        byte[] respDer = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var token = req.ProcessResponse(respDer, out _);
        return token.AsSignedCms().Encode();
    }

    private static int FindLastSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = haystack.Length - needle.Length; i >= 0; i--)
        {
            if (haystack[i] != needle[0]) continue;
            bool match = true;
            for (int j = 1; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static byte[] TrimDerTrailingZeros(byte[] raw)
    {
        // ASN.1 SEQUENCE (0x30) length parsing to strip trailing zero padding cleanly
        if (raw.Length > 2 && raw[0] == 0x30)
        {
            int lenByte = raw[1];
            int totalLen = -1;
            if ((lenByte & 0x80) == 0)
            {
                totalLen = 2 + lenByte;
            }
            else
            {
                int numLenBytes = lenByte & 0x7F;
                if (numLenBytes >= 1 && numLenBytes <= 4 && raw.Length >= 2 + numLenBytes)
                {
                    int payloadLen = 0;
                    for (int i = 0; i < numLenBytes; i++)
                    {
                        payloadLen = (payloadLen << 8) | raw[2 + i];
                    }
                    totalLen = 2 + numLenBytes + payloadLen;
                }
            }
            if (totalLen > 0 && totalLen <= raw.Length)
            {
                byte[] trimmed = new byte[totalLen];
                Buffer.BlockCopy(raw, 0, trimmed, 0, totalLen);
                return trimmed;
            }
        }
        return raw;
    }
}
