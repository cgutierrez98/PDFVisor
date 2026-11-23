using PDFVisor.Interop;

namespace PDFVisor.Core;

public interface IDocumentCommand : IDisposable
{
    string Description { get; }
    void Execute();
    void Undo();
}

public sealed class CommandHistory : ObservableObject, IDisposable
{
    private readonly Stack<IDocumentCommand> _undoStack = new();
    private readonly Stack<IDocumentCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? NextUndoDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;
    public string? NextRedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : null;

    public void Execute(IDocumentCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        ClearStack(_redoStack);
        NotifyStateChanged();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
        NotifyStateChanged();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.Push(cmd);
        NotifyStateChanged();
    }

    public void Clear()
    {
        ClearStack(_undoStack);
        ClearStack(_redoStack);
        NotifyStateChanged();
    }

    private static void ClearStack(Stack<IDocumentCommand> stack)
    {
        while (stack.Count > 0)
        {
            stack.Pop().Dispose();
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(NextUndoDescription));
        OnPropertyChanged(nameof(NextRedoDescription));
    }

    public void Dispose()
    {
        Clear();
    }
}

public sealed class RotatePageCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly int _oldRotation;
    private readonly int _newRotation;

    public string Description => $"Rotar página {_pageIndex + 1}";

    public RotatePageCommand(PdfDocument doc, int pageIndex, int deltaQuarterTurns)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _oldRotation = doc.GetPageRotation(pageIndex);
        _newRotation = ((_oldRotation + deltaQuarterTurns) % 4 + 4) % 4;
    }

    public void Execute() => _doc.SetPageRotation(_pageIndex, _newRotation);
    public void Undo() => _doc.SetPageRotation(_pageIndex, _oldRotation);
    public void Dispose() { }
}

public sealed class MovePageCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public string Description => $"Mover página {_fromIndex + 1} a {_toIndex + 1}";

    public MovePageCommand(PdfDocument doc, int fromIndex, int toIndex)
    {
        _doc = doc;
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    public void Execute() => _doc.MovePage(_fromIndex, _toIndex);
    public void Undo() => _doc.MovePage(_toIndex, _fromIndex);
    public void Dispose() { }
}

public sealed class DeletePageCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private string? _backupPdfPath;

    public string Description => $"Eliminar página {_pageIndex + 1}";

    public DeletePageCommand(PdfDocument doc, int pageIndex)
    {
        _doc = doc;
        _pageIndex = pageIndex;
    }

    public void Execute()
    {
        if (_backupPdfPath is null)
        {
            _backupPdfPath = Path.Combine(Path.GetTempPath(), $"pdfvisor_undo_page_{Guid.NewGuid():N}.pdf");
            _doc.ExtractPages([_pageIndex], _backupPdfPath);
        }
        _doc.DeletePage(_pageIndex);
    }

    public void Undo()
    {
        if (_backupPdfPath is null || !File.Exists(_backupPdfPath))
            throw new InvalidOperationException("Backup page snapshot not found for Undo.");
        _doc.ImportPages(_backupPdfPath, _pageIndex);
    }

    public void Dispose()
    {
        if (_backupPdfPath is not null && File.Exists(_backupPdfPath))
        {
            try { File.Delete(_backupPdfPath); } catch { }
            _backupPdfPath = null;
        }
    }
}

public sealed class ImportPagesCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly string _sourcePath;
    private readonly int _insertAtIndex;
    private int _importedCount;

    public string Description => $"Combinar {Path.GetFileName(_sourcePath)}";

    public ImportPagesCommand(PdfDocument doc, string sourcePath, int insertAtIndex)
    {
        _doc = doc;
        _sourcePath = sourcePath;
        _insertAtIndex = insertAtIndex;
    }

    public void Execute()
    {
        _importedCount = _doc.ImportPages(_sourcePath, _insertAtIndex);
    }

    public void Undo()
    {
        for (int i = 0; i < _importedCount; i++)
        {
            _doc.DeletePage(_insertAtIndex);
        }
    }

    public void Dispose() { }
}

public sealed class AddHighlightCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly PvRect _rect;
    private readonly uint _rgba;
    private int _annotIndex = -1;

    public string Description => $"Resaltar en página {_pageIndex + 1}";

    public AddHighlightCommand(PdfDocument doc, int pageIndex, PvRect rect, uint rgba = 0xFFEA0080)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _rect = rect;
        _rgba = rgba;
    }

    public void Execute() => _annotIndex = _doc.AddHighlightAnnotation(_pageIndex, _rect, _rgba);
    public void Undo() { if (_annotIndex >= 0) _doc.RemoveAnnotation(_pageIndex, _annotIndex); }
    public void Dispose() { }
}

public sealed class AddSquareCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly PvRect _rect;
    private readonly uint _strokeRgba;
    private readonly uint _fillRgba;
    private int _annotIndex = -1;

    public string Description => $"Rectángulo en página {_pageIndex + 1}";

    public AddSquareCommand(PdfDocument doc, int pageIndex, PvRect rect, uint strokeRgba = 0xE53935FF, uint fillRgba = 0x00000000)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _rect = rect;
        _strokeRgba = strokeRgba;
        _fillRgba = fillRgba;
    }

    public void Execute() => _annotIndex = _doc.AddSquareAnnotation(_pageIndex, _rect, _strokeRgba, _fillRgba);
    public void Undo() { if (_annotIndex >= 0) _doc.RemoveAnnotation(_pageIndex, _annotIndex); }
    public void Dispose() { }
}

public sealed class AddNoteCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly PvRect _rect;
    private readonly string _contents;
    private readonly uint _rgba;
    private int _annotIndex = -1;

    public string Description => $"Nota en página {_pageIndex + 1}";

    public AddNoteCommand(PdfDocument doc, int pageIndex, PvRect rect, string contents, uint rgba = 0xFFC107FF)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _rect = rect;
        _contents = contents;
        _rgba = rgba;
    }

    public void Execute() => _annotIndex = _doc.AddNoteAnnotation(_pageIndex, _rect, _contents, _rgba);
    public void Undo() { if (_annotIndex >= 0) _doc.RemoveAnnotation(_pageIndex, _annotIndex); }
    public void Dispose() { }
}

public sealed class AddInkCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly PvPoint[] _points;
    private readonly uint _rgba;
    private int _annotIndex = -1;

    public string Description => $"Dibujo en página {_pageIndex + 1}";

    public AddInkCommand(PdfDocument doc, int pageIndex, PvPoint[] points, uint rgba = 0x1E88E5FF)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _points = points;
        _rgba = rgba;
    }

    public void Execute() => _annotIndex = _doc.AddInkAnnotation(_pageIndex, _points, _rgba);
    public void Undo() { if (_annotIndex >= 0) _doc.RemoveAnnotation(_pageIndex, _annotIndex); }
    public void Dispose() { }
}

public sealed class EditTextObjectCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly int _objIndex;
    private readonly string _oldText;
    private readonly string _newText;

    public string Description => $"Editar texto en página {_pageIndex + 1}";

    public EditTextObjectCommand(PdfDocument doc, int pageIndex, int objIndex, string oldText, string newText)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _objIndex = objIndex;
        _oldText = oldText;
        _newText = newText;
    }

    public void Execute() => _doc.EditTextObject(_pageIndex, _objIndex, _newText);
    public void Undo() => _doc.EditTextObject(_pageIndex, _objIndex, _oldText);
    public void Dispose() { }
}

public sealed class MovePageObjectCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly int _objIndex;
    private readonly double _dx;
    private readonly double _dy;

    public string Description => $"Mover objeto en página {_pageIndex + 1}";

    public MovePageObjectCommand(PdfDocument doc, int pageIndex, int objIndex, double dx, double dy)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _objIndex = objIndex;
        _dx = dx;
        _dy = dy;
    }

    public void Execute() => _doc.MovePageObject(_pageIndex, _objIndex, _dx, _dy);
    public void Undo() => _doc.MovePageObject(_pageIndex, _objIndex, -_dx, -_dy);
    public void Dispose() { }
}

public sealed class AddTextObjectCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly string _text;
    private readonly string _fontName;
    private readonly float _fontSize;
    private readonly double _xPt;
    private readonly double _yPt;
    private readonly uint _rgba;
    private int _objIndex = -1;

    public string Description => $"Añadir texto en página {_pageIndex + 1}";

    public AddTextObjectCommand(PdfDocument doc, int pageIndex, string text, string fontName, float fontSize, double xPt, double yPt, uint rgba = 0x111111FF)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _text = text;
        _fontName = fontName;
        _fontSize = fontSize;
        _xPt = xPt;
        _yPt = yPt;
        _rgba = rgba;
    }

    public void Execute() => _objIndex = _doc.AddTextObject(_pageIndex, _text, _fontName, _fontSize, _xPt, _yPt, _rgba);
    public void Undo() { if (_objIndex >= 0) _doc.RemovePageObject(_pageIndex, _objIndex); }
    public void Dispose() { }
}

public sealed class AddImageObjectCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly byte[] _bgra;
    private readonly int _widthPx;
    private readonly int _heightPx;
    private readonly PvRect _destRect;
    private int _objIndex = -1;

    public string Description => $"Insertar imagen en página {_pageIndex + 1}";

    public AddImageObjectCommand(PdfDocument doc, int pageIndex, byte[] bgra, int widthPx, int heightPx, PvRect destRect)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _bgra = bgra;
        _widthPx = widthPx;
        _heightPx = heightPx;
        _destRect = destRect;
    }

    public void Execute() => _objIndex = _doc.AddImageBgra(_pageIndex, _bgra, _widthPx, _heightPx, _destRect);
    public void Undo() { if (_objIndex >= 0) _doc.RemovePageObject(_pageIndex, _objIndex); }
    public void Dispose() { }
}

public sealed class AddUnicodeTextObjectCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly int _pageIndex;
    private readonly string _text;
    private readonly string _ttfPath;
    private readonly float _fontSize;
    private readonly double _xPt;
    private readonly double _yPt;
    private readonly uint _rgba;
    private int _objIndex = -1;

    public string Description => $"Añadir texto Unicode en página {_pageIndex + 1}";

    public AddUnicodeTextObjectCommand(PdfDocument doc, int pageIndex, string text, string ttfPath, float fontSize, double xPt, double yPt, uint rgba = 0x111111FF)
    {
        _doc = doc;
        _pageIndex = pageIndex;
        _text = text;
        _ttfPath = ttfPath;
        _fontSize = fontSize;
        _xPt = xPt;
        _yPt = yPt;
        _rgba = rgba;
    }

    public void Execute() => _objIndex = _doc.AddUnicodeTextObject(_pageIndex, _text, _ttfPath, _fontSize, _xPt, _yPt, _rgba);
    public void Undo() { if (_objIndex >= 0) _doc.RemovePageObject(_pageIndex, _objIndex); }
    public void Dispose() { }
}

public sealed class BatchGeneratedObjectsCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly Func<PdfDocument, List<(int PageIndex, int FirstObjIndex, int Count)>> _generator;
    private List<(int PageIndex, int FirstObjIndex, int Count)> _inserted = [];

    public string Description { get; }

    public BatchGeneratedObjectsCommand(
        PdfDocument doc,
        string description,
        Func<PdfDocument, List<(int PageIndex, int FirstObjIndex, int Count)>> generator)
    {
        _doc = doc;
        Description = description;
        _generator = generator;
    }

    public void Execute()
    {
        _inserted = _generator(_doc);
    }

    public void Undo()
    {
        for (int g = _inserted.Count - 1; g >= 0; g--)
        {
            var (pageIndex, firstObjIndex, count) = _inserted[g];
            if (firstObjIndex < 0 || count <= 0) continue;
            for (int k = count - 1; k >= 0; k--)
            {
                _doc.RemovePageObject(pageIndex, firstObjIndex + k);
            }
        }
    }

    public void Dispose() { }
}

public sealed class SnapshotPdfMutationCommand : IDocumentCommand
{
    private readonly PdfDocument _doc;
    private readonly Action<string, string> _qpdfTransform;
    private string? _beforeSnapshot;
    private string? _afterSnapshot;

    public string Description { get; }

    public SnapshotPdfMutationCommand(PdfDocument doc, string description, Action<string, string> qpdfTransform)
    {
        _doc = doc;
        Description = description;
        _qpdfTransform = qpdfTransform;
    }

    public void Execute()
    {
        if (_beforeSnapshot is null)
        {
            _beforeSnapshot = Path.Combine(Path.GetTempPath(), $"pdfvisor_snap_before_{Guid.NewGuid():N}.pdf");
            _doc.SaveAs(_beforeSnapshot, incremental: false);
        }

        if (_afterSnapshot is null || !File.Exists(_afterSnapshot))
        {
            _afterSnapshot = Path.Combine(Path.GetTempPath(), $"pdfvisor_snap_after_{Guid.NewGuid():N}.pdf");
            _qpdfTransform(_beforeSnapshot, _afterSnapshot);
        }

        _doc.LoadWorkingCopy(_afterSnapshot, workingPassword: null);
    }

    public void Undo()
    {
        if (_beforeSnapshot is null || !File.Exists(_beforeSnapshot))
            throw new InvalidOperationException("Snapshot not available for Undo.");
        _doc.LoadWorkingCopy(_beforeSnapshot, workingPassword: null);
    }

    public void Dispose()
    {
        if (_beforeSnapshot is not null && File.Exists(_beforeSnapshot))
        {
            try { File.Delete(_beforeSnapshot); } catch { }
            _beforeSnapshot = null;
        }
        if (_afterSnapshot is not null && File.Exists(_afterSnapshot))
        {
            try { File.Delete(_afterSnapshot); } catch { }
            _afterSnapshot = null;
        }
    }
}
