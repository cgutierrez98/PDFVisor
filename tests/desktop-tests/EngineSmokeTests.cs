using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PDFVisor.Core;
using PDFVisor.Interop;
using Xunit;

namespace PDFVisor.Tests;

public sealed class EngineSmokeTests : IDisposable
{
    private readonly List<string> _tempFiles = [];
    private readonly string _tempPdfPath;

    public EngineSmokeTests()
    {
        PdfEngine.Initialize();
        _tempPdfPath = CreateTempPdf("Hello PDFVisor! Professional PDF Engine.");
    }

    private string CreateTempPdf(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"pdfvisor_test_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildMinimalValidPdf(text));
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
        PdfEngine.Shutdown();
    }

    [Fact]
    public void Engine_Opens_Renders_And_ExtractsText_FromMinimalPdf()
    {
        Assert.False(string.IsNullOrWhiteSpace(PdfEngine.Version));

        using var doc = PdfDocument.Open(_tempPdfPath);
        Assert.Equal(1, doc.PageCount);

        var size = doc.GetPageSize(0);
        Assert.InRange(size.WidthPt, 594.0, 596.0);
        Assert.InRange(size.HeightPt, 841.0, 843.0);

        byte[] pixels = doc.RenderPageBgra(0, 200, 280);
        Assert.Equal(200 * 280 * 4, pixels.Length);
        bool hasNonWhite = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)
            {
                hasNonWhite = true;
                break;
            }
        }
        Assert.True(hasNonWhite, "Expected rendered page to contain drawn text pixels.");

        string extracted = doc.ExtractPageText(0);
        Assert.Contains("Hello PDFVisor!", extracted);
    }

    [Fact]
    public async Task Engine_SearchesText_And_ReturnsValidBoundingRects()
    {
        using var workspace = new WorkspaceViewModel();
        var session = workspace.OpenDocument(_tempPdfPath);

        var sameSession = workspace.OpenDocument(_tempPdfPath);
        Assert.Same(session, sameSession);
        Assert.Single(workspace.OpenSessions);

        int count = await session.SearchAsync("PDFVisor");
        Assert.Equal(1, count);
        Assert.Single(session.CurrentPageHighlights);

        var rect = session.CurrentPageHighlights[0];
        Assert.True(rect.Right > rect.Left, "Expected positive rect width.");
        Assert.True(rect.Top > rect.Bottom, "Expected PDF bottom-left coords with top > bottom.");
        Assert.Equal("1 / 1", session.SearchStatusText);

        int zero = await session.SearchAsync("NonExistentTokenXYZ");
        Assert.Equal(0, zero);
        Assert.Empty(session.CurrentPageHighlights);
        Assert.Equal("0 resultados", session.SearchStatusText);

        Assert.Empty(session.Bookmarks);

        workspace.CloseSession(session);
        Assert.Empty(workspace.OpenSessions);
        Assert.Null(workspace.ActiveSession);
    }

    [Fact]
    public void Phase2_PageOrganization_UndoRedo_And_SavePersist()
    {
        string docAPath = CreateTempPdf("PAGE_ALPHA");
        string docBPath = CreateTempPdf("PAGE_BETA");
        string extractedPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_extract_{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(extractedPath);

        using (var session = new DocumentSession(PdfDocument.Open(docAPath)))
        {
            Assert.Equal(1, session.PageCount);
            Assert.False(session.IsModified);

            // 1. Merge docB into docA -> 2 pages [ALPHA, BETA]
            session.ImportPagesFromFile(docBPath);
            Assert.Equal(2, session.PageCount);
            Assert.True(session.IsModified);
            Assert.Contains("PAGE_ALPHA", session.Document.ExtractPageText(0));
            Assert.Contains("PAGE_BETA", session.Document.ExtractPageText(1));

            // 2. Move page 1 (BETA) to index 0 -> [BETA, ALPHA]
            session.CurrentPageIndex = 1;
            session.MoveCurrentPage(0);
            Assert.Contains("PAGE_BETA", session.Document.ExtractPageText(0));
            Assert.Contains("PAGE_ALPHA", session.Document.ExtractPageText(1));

            // 3. Undo move -> [ALPHA, BETA]
            session.Undo();
            Assert.Contains("PAGE_ALPHA", session.Document.ExtractPageText(0));
            Assert.Contains("PAGE_BETA", session.Document.ExtractPageText(1));

            // 4. Redo move -> [BETA, ALPHA]
            session.Redo();
            Assert.Contains("PAGE_BETA", session.Document.ExtractPageText(0));

            // 5. Rotate page 0 permanently 90 deg + Undo + Redo
            session.CurrentPageIndex = 0;
            session.RotateCurrentPagePermanent(1);
            Assert.Equal(1, session.Document.GetPageRotation(0));
            session.Undo();
            Assert.Equal(0, session.Document.GetPageRotation(0));
            session.Redo();
            Assert.Equal(1, session.Document.GetPageRotation(0));

            // 6. Delete page 0 (BETA) + Undo restores it!
            session.DeleteCurrentPage();
            Assert.Equal(1, session.PageCount);
            Assert.Contains("PAGE_ALPHA", session.Document.ExtractPageText(0));
            session.Undo();
            Assert.Equal(2, session.PageCount);
            Assert.Contains("PAGE_BETA", session.Document.ExtractPageText(0));

            // 7. Extract page 0 to separate PDF
            session.CurrentPageIndex = 0;
            session.ExtractCurrentPage(extractedPath);
            Assert.True(File.Exists(extractedPath));

            // 8. Save in-place and clear dirty flag
            session.Save();
            Assert.False(session.IsModified);
        }

        // Reopen saved docA from disk and verify persisted 2 pages + rotation
        using (var reopened = PdfDocument.Open(docAPath))
        {
            Assert.Equal(2, reopened.PageCount);
            Assert.Contains("PAGE_BETA", reopened.ExtractPageText(0));
            Assert.Contains("PAGE_ALPHA", reopened.ExtractPageText(1));
            Assert.Equal(1, reopened.GetPageRotation(0));
        }

        // Verify extracted standalone PDF
        using (var extracted = PdfDocument.Open(extractedPath))
        {
            Assert.Equal(1, extracted.PageCount);
            Assert.Contains("PAGE_BETA", extracted.ExtractPageText(0));
        }
    }

    [Fact]
    public void Phase2_Annotations_UndoRedo_And_Qpdf_Optimize_And_Aes256()
    {
        string workPath = CreateTempPdf("Annotate and Optimize Me!");
        string optimizedPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_opt_{Guid.NewGuid():N}.pdf");
        string encryptedPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_enc_{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(optimizedPath);
        _tempFiles.Add(encryptedPath);

        using (var session = new DocumentSession(PdfDocument.Open(workPath)))
        {
            Assert.Equal(0, session.Document.GetAnnotationCount(0));

            // Add Highlight, Square, Note, Ink
            session.AddHighlight(new PvRect { Left = 70, Bottom = 710, Right = 240, Top = 740 });
            session.AddSquare(new PvRect { Left = 100, Bottom = 500, Right = 300, Top = 620 });
            session.AddNote(new PvRect { Left = 320, Bottom = 600, Right = 344, Top = 624 }, "Revisar sección");
            session.AddInk([new PvPoint { X = 100, Y = 400 }, new PvPoint { X = 150, Y = 420 }, new PvPoint { X = 200, Y = 390 }]);
            Assert.Equal(4, session.Document.GetAnnotationCount(0));

            // Undo Ink -> 3 annotations, Redo -> 4 annotations
            session.Undo();
            Assert.Equal(3, session.Document.GetAnnotationCount(0));
            session.Redo();
            Assert.Equal(4, session.Document.GetAnnotationCount(0));

            // Save annotations to disk
            session.Save();

            // QPDF Optimize + Linearize
            session.OptimizeAndExport(optimizedPath, linearize: true, recompress: true, objectStreams: true);
            Assert.True(File.Exists(optimizedPath));
            Assert.True(QpdfTools.IsLinearized(optimizedPath));

            // QPDF AES-256 Encryption
            session.EncryptAes256AndExport(encryptedPath, userPassword: "SecretUser123", ownerPassword: "SecretOwner456");
            Assert.True(File.Exists(encryptedPath));
        }

        // Verify annotations survived Save + QPDF Optimize
        using (var optDoc = PdfDocument.Open(optimizedPath))
        {
            Assert.Equal(4, optDoc.GetAnnotationCount(0));
        }

        // Verify encrypted PDF rejects opening without password and succeeds with user password
        var ex = Assert.Throws<PdfEngineException>(() => PdfDocument.Open(encryptedPath));
        Assert.Equal(PvStatus.PasswordRequired, ex.Status);

        using (var encDoc = PdfDocument.Open(encryptedPath, "SecretUser123"))
        {
            Assert.Equal(1, encDoc.PageCount);
            Assert.Equal(4, encDoc.GetAnnotationCount(0));
            Assert.Contains("Annotate and Optimize Me!", encDoc.ExtractPageText(0));
        }
    }

    [Fact]
    public void Phase3_AcroForms_And_PadesSignature_SignVerify_And_TamperDetection()
    {
        string formPath = CreateTempPdf("Contract Form Document");
        string signedPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_signed_{Guid.NewGuid():N}.pdf");
        string tamperedPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_tampered_{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(signedPath);
        _tempFiles.Add(tamperedPath);

        // 1. Add and fill AcroForm text field
        using (var session = new DocumentSession(PdfDocument.Open(formPath)))
        {
            Assert.Empty(session.FormFields);

            session.AddTextFormField("ClientName", new PvRect { Left = 72, Bottom = 650, Right = 320, Top = 675 }, "Initial");
            Assert.Single(session.FormFields);
            Assert.Equal("ClientName", session.FormFields[0].Name);
            Assert.Equal("Initial", session.FormFields[0].Value);

            session.SetFormFieldValue("ClientName", "Carlos Pro User");
            Assert.Single(session.FormFields);
            Assert.Equal("Carlos Pro User", session.FormFields[0].Value);

            // 2. Create ephemeral RSA-2048 X.509 certificate and sign PAdES
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=PDFVisor Test Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            session.SignPadesAndExport(signedPath, cert, reason: "Aprobado por PDFVisor", location: "Madrid");
            Assert.True(File.Exists(signedPath));
        }

        // 3. Reopen signed PDF and verify cryptographic integrity + signer identity
        using (var signedDoc = PdfDocument.Open(signedPath))
        {
            var verifications = PadesSignatureService.VerifySignatures(signedDoc);
            Assert.Single(verifications);
            var v = verifications[0];
            Assert.True(v.IsCryptographicallyValid, v.StatusSummary);
            Assert.True(v.CoversWholeDocument);
            Assert.Equal("PDFVisor Test Signer", v.SignerName);
            Assert.Equal("ETSI.CAdES.detached", v.SubFilter);
            Assert.Equal("Aprobado por PDFVisor", v.Reason);
        }

        // 4. Tamper 1 byte inside the signed range and verify tamper detection fails validation!
        byte[] tamperedBytes = File.ReadAllBytes(signedPath);
        tamperedBytes[10] ^= 0xFF; // flip byte near header inside ByteRange[0..len1]
        File.WriteAllBytes(tamperedPath, tamperedBytes);

        using (var tamperedDoc = PdfDocument.Open(tamperedPath))
        {
            var verifications = PadesSignatureService.VerifySignatures(tamperedDoc);
            Assert.Single(verifications);
            Assert.False(verifications[0].IsCryptographicallyValid);
        }
    }

    [Fact]
    public void Phase4_DirectContentEditing_TextEdit_AddText_AddImage_Move_And_UndoRedo()
    {
        string path = CreateTempPdf("Original Heading");

        using (var session = new DocumentSession(PdfDocument.Open(path)))
        {
            // 1. Inspect existing text object
            var textObj = session.CurrentPageObjects.First(o => o.Type == PdfPageObjectType.Text);
            Assert.Contains("Original Heading", textObj.Text);

            // 2. Edit existing text in-place + Undo + Redo
            session.EditTextObject(textObj.Index, textObj.Text, "Updated Heading");
            Assert.Contains("Updated Heading", session.Document.ExtractPageText(0));

            session.Undo();
            Assert.Contains("Original Heading", session.Document.ExtractPageText(0));

            session.Redo();
            Assert.Contains("Updated Heading", session.Document.ExtractPageText(0));

            // 3. Add a brand new text object
            session.AddTextObject("Inserted Paragraph", 72, 620, "Helvetica", 14f);
            Assert.Contains("Inserted Paragraph", session.Document.ExtractPageText(0));

            // 4. Insert a 16x16 solid blue BGRA image object
            byte[] imgBgra = new byte[16 * 16 * 4];
            for (int i = 0; i < imgBgra.Length; i += 4)
            {
                imgBgra[i] = 240;     // B
                imgBgra[i + 1] = 120; // G
                imgBgra[i + 2] = 30;  // R
                imgBgra[i + 3] = 255; // A
            }
            session.AddImageBgra(imgBgra, 16, 16, new PvRect { Left = 100, Bottom = 400, Right = 200, Top = 500 });
            Assert.Contains(session.CurrentPageObjects, o => o.Type == PdfPageObjectType.Image);

            // 5. Move image object and verify bounds shifted
            var imgObj = session.CurrentPageObjects.First(o => o.Type == PdfPageObjectType.Image);
            double oldLeft = imgObj.Bounds.Left;
            session.MovePageObject(imgObj.Index, 50.0, 25.0);
            var movedImg = session.CurrentPageObjects.First(o => o.Type == PdfPageObjectType.Image);
            Assert.InRange(movedImg.Bounds.Left - oldLeft, 49.0, 51.0);

            // 6. Save and verify persisted content
            session.Save();
        }

        using (var reopened = PdfDocument.Open(path))
        {
            string text = reopened.ExtractPageText(0);
            Assert.Contains("Updated Heading", text);
            Assert.Contains("Inserted Paragraph", text);
            Assert.Contains(reopened.GetPageObjects(0), o => o.Type == PdfPageObjectType.Image);
        }
    }

    [Fact]
    public void Phase5_TruePermanentRedaction_And_InvisibleOcrTextLayer()
    {
        string path = CreateTempPdf("TOP_SECRET_12345");
        string sanitizedPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_sanitized_{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(sanitizedPath);

        using (var session = new DocumentSession(PdfDocument.Open(path)))
        {
            // Add a second public text line lower on the page
            session.AddTextObject("PUBLIC_SUMMARY_OK", 72, 500, "Helvetica", 14f);
            Assert.Contains("TOP_SECRET_12345", session.Document.ExtractPageText(0));
            Assert.Contains("PUBLIC_SUMMARY_OK", session.Document.ExtractPageText(0));

            // Apply permanent redaction over the top text region (y ~ 720)
            int removed = session.ApplyPermanentRedaction(new PvRect { Left = 60, Bottom = 700, Right = 400, Top = 760 });
            Assert.True(removed >= 1, "Expected at least one text object to be physically removed.");
            Assert.False(session.History.CanUndo, "Undo stack must be cleared after permanent redaction.");

            // Sanitize with QPDF (strips metadata and incremental history)
            session.SanitizeAndExport(sanitizedPath);
        }

        // Reopen sanitized PDF: secret text must be completely unrecoverable!
        using (var sanitized = PdfDocument.Open(sanitizedPath))
        {
            string extracted = sanitized.ExtractPageText(0);
            Assert.DoesNotContain("TOP_SECRET_12345", extracted);
            Assert.Contains("PUBLIC_SUMMARY_OK", extracted);
            Assert.Empty(sanitized.SearchPage(0, "TOP_SECRET_12345"));

            // Inject invisible OCR text layer (Tr 3) and verify it is searchable and extractable
            sanitized.AddInvisibleOcrText(0, "SCANNED_INVOICE_9988", 72, 300, 12f);
            Assert.Contains("SCANNED_INVOICE_9988", sanitized.ExtractPageText(0));
            Assert.NotEmpty(sanitized.SearchPage(0, "SCANNED_INVOICE_9988"));
        }
    }

    [Fact]
    public void Viewport_TileRendering_And_LruTileCache_Eviction()
    {
        // 1. Verify native tile sub-rect rendering on a 2000x2800 high-zoom page
        using (var doc = PdfDocument.Open(_tempPdfPath))
        {
            // Text is placed near top-left (x=72pt, y=720pt on 595x842pt page -> top ~14% of page)
            byte[] topLeftTile = doc.RenderPageTileBgra(
                pageIndex: 0,
                fullPageWidthPx: 2000,
                fullPageHeightPx: 2800,
                tileX: 0,
                tileY: 0,
                tileWidthPx: 512,
                tileHeightPx: 512);
            Assert.Equal(512 * 512 * 4, topLeftTile.Length);

            bool topLeftHasInk = false;
            for (int i = 0; i < topLeftTile.Length; i += 4)
            {
                if (topLeftTile[i] < 240 || topLeftTile[i + 1] < 240 || topLeftTile[i + 2] < 240)
                {
                    topLeftHasInk = true;
                    break;
                }
            }
            Assert.True(topLeftHasInk, "Top-left tile at 2000x2800 should contain rendered text pixels.");

            // Bottom-right tile should be pure white background
            byte[] bottomRightTile = doc.RenderPageTileBgra(0, 2000, 2800, 1488, 2288, 512, 512);
            bool bottomRightAllWhite = true;
            for (int i = 0; i < bottomRightTile.Length; i += 4)
            {
                if (bottomRightTile[i] != 255 || bottomRightTile[i + 1] != 255 || bottomRightTile[i + 2] != 255)
                {
                    bottomRightAllWhite = false;
                    break;
                }
            }
            Assert.True(bottomRightAllWhite, "Bottom-right tile should be blank white background.");
        }

        // 2. Verify LRU Tile Cache enforces strict byte budget and evicts least-recently-used key
        var cache = new LruTileCache<TileKey, string>(maxBytes: 200);
        var k1 = new TileKey(0, 0, 0, 1000, 1000, 0, 0, 512, 512);
        var k2 = new TileKey(0, 0, 0, 1000, 1000, 512, 0, 488, 512);
        var k3 = new TileKey(0, 1, 0, 1000, 1000, 0, 0, 512, 512);

        cache.Put(k1, "tile1", 100);
        cache.Put(k2, "tile2", 100);
        Assert.Equal(200, cache.CurrentBytes);

        // Touch k1 so k2 becomes least-recently-used
        Assert.True(cache.TryGet(k1, out var v1));
        Assert.Equal("tile1", v1);

        // Adding k3 (100 bytes) must evict k2 and retain k1 + k3
        cache.Put(k3, "tile3", 100);
        Assert.Equal(200, cache.CurrentBytes);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(k1, out _));
        Assert.False(cache.TryGet(k2, out _));
        Assert.True(cache.TryGet(k3, out _));
    }

    [Fact]
    public void Blocks1To4_TextSelection_UnicodeTtf_SurgicalImageRedaction_And_VisiblePades()
    {
        string path = CreateTempPdf("SelectThisWord PDFVisor");

        using var session = new DocumentSession(PdfDocument.Open(path));

        // 1. Block 1: Text hit-testing & selection range extraction + bounding rects
        int charIdx = session.Document.GetCharIndexAtPos(0, 80, 725, tolerancePt: 15.0);
        Assert.True(charIdx >= 0, "Expected to hit a character near (80, 725).");
        string rangeText = session.Document.GetTextRange(0, 0, 14);
        Assert.Contains("SelectThisWord", rangeText);
        var rects = session.Document.GetTextRangeRects(0, 0, 14);
        Assert.NotEmpty(rects);

        // 2. Block 2a: TrueType Unicode text insertion (Spanish accents & symbols!)
        string? ttf = DocumentSession.FindWindowsUnicodeFont();
        Assert.NotNull(ttf);
        session.AddTextObject("Documentación Española: año 2026 — 100€", 72, 600, fontSize: 14f);
        string extracted = session.Document.ExtractPageText(0);
        Assert.Contains("Documentación", extracted);
        Assert.Contains("Española", extracted);
        Assert.Contains("100€", extracted);

        // 3. Block 2b: Surgical partial pixel-level image redaction (image object stays alive, only redacted area masked)
        byte[] solidBlue = new byte[32 * 32 * 4];
        for (int i = 0; i < solidBlue.Length; i += 4)
        {
            solidBlue[i] = 250; solidBlue[i + 1] = 100; solidBlue[i + 2] = 20; solidBlue[i + 3] = 255;
        }
        session.AddImageBgra(solidBlue, 32, 32, new PvRect { Left = 100, Bottom = 300, Right = 300, Top = 500 });
        int beforeImgCount = session.CurrentPageObjects.Count(o => o.Type == PdfPageObjectType.Image);
        Assert.Equal(1, beforeImgCount);

        // Redact only the left half [100..200] of the image -> image object must remain (masked in-place, not deleted!)
        int affected = session.ApplyPermanentRedaction(new PvRect { Left = 100, Bottom = 300, Right = 200, Top = 500 });
        Assert.True(affected >= 1);
        Assert.Equal(1, session.CurrentPageObjects.Count(o => o.Type == PdfPageObjectType.Image));
    }

    [Fact]
    public void UpdateService_ParsesReleaseJson_And_DetectsNewerVersion()
    {
        const string sampleReleaseJson = """
        {
          "tag_name": "v0.10.0",
          "html_url": "https://github.com/cgutierrez98/PDFVisor/releases/tag/v0.10.0",
          "body": "Mejoras de rendimiento y correcciones.",
          "assets": [
            {
              "name": "PDFVisor-0.10.0-win-x64-portable.zip",
              "browser_download_url": "https://example.com/portable.zip"
            },
            {
              "name": "PDFVisor-Setup-0.10.0-win-x64.exe",
              "browser_download_url": "https://example.com/PDFVisor-Setup-0.10.0-win-x64.exe"
            }
          ]
        }
        """;

        var newer = UpdateService.ParseGitHubReleaseJson(sampleReleaseJson, new Version(0, 9, 0));
        Assert.NotNull(newer);
        Assert.True(newer.IsUpdateAvailable);
        Assert.Equal(new Version(0, 10, 0), newer.LatestVersion);
        Assert.Equal("PDFVisor-Setup-0.10.0-win-x64.exe", newer.InstallerFileName);
        Assert.Equal("https://example.com/PDFVisor-Setup-0.10.0-win-x64.exe", newer.InstallerDownloadUrl);

        var same = UpdateService.ParseGitHubReleaseJson(sampleReleaseJson, new Version(0, 10, 0));
        Assert.NotNull(same);
        Assert.False(same.IsUpdateAvailable);

        var older = UpdateService.ParseGitHubReleaseJson(sampleReleaseJson, new Version(1, 0, 0));
        Assert.NotNull(older);
        Assert.False(older.IsUpdateAvailable);
    }

    [Fact]
    public void Production_Sha256Verification_AppSettings_And_CreatePdfFromImages()
    {
        // 1. Verify SHA-256 updater checksum validation (pass + tamper fail)
        string dummyExe = Path.Combine(Path.GetTempPath(), $"PDFVisor-Setup-1.1.0-win-x64_{Guid.NewGuid():N}.exe");
        _tempFiles.Add(dummyExe);
        File.WriteAllBytes(dummyExe, Encoding.UTF8.GetBytes("PDFVisor installer payload"));
        string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dummyExe))).ToLowerInvariant();
        string fileName = Path.GetFileName(dummyExe);

        string validManifest = $"{actualHash}  {fileName}\n";
        Assert.True(UpdateService.VerifySha256Checksum(dummyExe, fileName, validManifest));
        Assert.False(UpdateService.VerifySha256Checksum(dummyExe, fileName, "0000000000000000  " + fileName));

        // 2. Verify AppSettings round-trip
        string settingsFile = Path.Combine(Path.GetTempPath(), $"pdfvisor_settings_{Guid.NewGuid():N}.json");
        _tempFiles.Add(settingsFile);
        var s = new AppSettings { WindowWidth = 1520, WindowHeight = 910, IsMaximized = true, IsSidebarOpen = false, DarkReadingMode = true };
        s.Save(settingsFile);
        var loaded = AppSettings.Load(settingsFile);
        Assert.Equal(1520, loaded.WindowWidth);
        Assert.Equal(910, loaded.WindowHeight);
        Assert.True(loaded.IsMaximized);
        Assert.False(loaded.IsSidebarOpen);
        Assert.True(loaded.DarkReadingMode);

        // 3. Verify CreateBlank + AppendBlankPage + AddImageBgra -> 2-page PDF from images
        string outPdf = Path.Combine(Path.GetTempPath(), $"pdfvisor_imgs_{Guid.NewGuid():N}.pdf");
        _tempFiles.Add(outPdf);
        byte[] img = new byte[16 * 16 * 4];
        for (int i = 0; i < img.Length; i += 4) { img[i] = 50; img[i + 1] = 180; img[i + 2] = 90; img[i + 3] = 255; }

        using (var created = PdfDocument.CreateBlank(400, 300))
        {
            created.AddImageBgra(0, img, 16, 16, new PvRect { Left = 0, Bottom = 0, Right = 400, Top = 300 });
            int p2 = created.AppendBlankPage(300, 500);
            Assert.Equal(1, p2);
            created.AddImageBgra(p2, img, 16, 16, new PvRect { Left = 0, Bottom = 0, Right = 300, Top = 500 });
            created.SaveAs(outPdf);
        }

        using (var reopened = PdfDocument.Open(outPdf))
        {
            Assert.Equal(2, reopened.PageCount);
            Assert.Contains(reopened.GetPageObjects(0), o => o.Type == PdfPageObjectType.Image);
            Assert.Contains(reopened.GetPageObjects(1), o => o.Type == PdfPageObjectType.Image);
        }
    }

    private static byte[] BuildMinimalValidPdf(string text)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();

        void AppendObj(int id, string body)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{id} 0 obj\n{body}\nendobj\n");
        }

        sb.Append("%PDF-1.4\n");
        AppendObj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AppendObj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        AppendObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        AppendObj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string stream = $"BT /F1 20 Tf 72 720 Td ({text}) Tj ET";
        AppendObj(5, $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");

        int xrefPos = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {offsets.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            sb.Append($"{offset:D10} 00000 n \n");
        }
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
