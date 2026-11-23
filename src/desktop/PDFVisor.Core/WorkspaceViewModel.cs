using System.Collections.ObjectModel;
using PDFVisor.Interop;

namespace PDFVisor.Core;

public sealed class WorkspaceViewModel : ObservableObject, IDisposable
{
    private DocumentSession? _activeSession;

    public ObservableCollection<DocumentSession> OpenSessions { get; } = [];

    public DocumentSession? ActiveSession
    {
        get => _activeSession;
        set => SetProperty(ref _activeSession, value);
    }

    public DocumentSession OpenDocument(string filePath, string? password = null)
    {
        // Reuse already-open tab if same file
        var existing = OpenSessions.FirstOrDefault(s =>
            string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveSession = existing;
            return existing;
        }

        var doc = PdfDocument.Open(filePath, password);
        var session = new DocumentSession(doc);
        OpenSessions.Add(session);
        ActiveSession = session;
        return session;
    }

    public void CloseSession(DocumentSession session)
    {
        int idx = OpenSessions.IndexOf(session);
        if (idx < 0) return;

        OpenSessions.RemoveAt(idx);
        session.Dispose();

        if (ReferenceEquals(ActiveSession, session))
        {
            ActiveSession = OpenSessions.Count > 0
                ? OpenSessions[Math.Clamp(idx, 0, OpenSessions.Count - 1)]
                : null;
        }
    }

    public void Dispose()
    {
        foreach (var session in OpenSessions)
        {
            session.Dispose();
        }
        OpenSessions.Clear();
        ActiveSession = null;
    }
}
