namespace PDFVisor.Core;

/// <summary>
/// Lightweight, zero-allocation Spanish / English localization dictionary for PDFVisor UI.
/// </summary>
public static class LocalizationService
{
    private static string _currentLanguage = "es";

    public static event Action? LanguageChanged;

    public static string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            string norm = string.Equals(value, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "es";
            if (_currentLanguage != norm)
            {
                _currentLanguage = norm;
                LanguageChanged?.Invoke();
            }
        }
    }

    private static readonly Dictionary<string, (string Es, string En)> Strings = new(StringComparer.Ordinal)
    {
        ["Menu.File"] = ("_Archivo", "_File"),
        ["Menu.Edit"] = ("_Editar", "_Edit"),
        ["Menu.View"] = ("_Ver", "_View"),
        ["Menu.Pages"] = ("_Páginas", "_Pages"),
        ["Menu.Tools"] = ("_Herramientas", "_Tools"),
        ["Menu.Security"] = ("_Seguridad", "_Security"),
        ["Menu.Help"] = ("A_yuda", "_Help"),
        ["Menu.Open"] = ("_Abrir...", "_Open..."),
        ["Menu.Save"] = ("_Guardar", "_Save"),
        ["Menu.SaveAs"] = ("Guardar _como...", "Save _As..."),
        ["Menu.Print"] = ("_Imprimir...", "_Print..."),
        ["Menu.CloseTab"] = ("Cerrar _pestaña", "_Close Tab"),
        ["Menu.Exit"] = ("_Salir", "E_xit"),
        ["Menu.Language"] = ("_Idioma", "_Language"),
        ["Welcome.Subtitle"] = ("Abre un documento o arrastra archivos PDF aquí para empezar", "Open a document or drag & drop PDF files here to begin"),
        ["Welcome.OpenButton"] = ("Abrir documento PDF", "Open PDF Document"),
        ["Welcome.Recent"] = ("Documentos recientes", "Recent Documents"),
        ["Welcome.Clear"] = ("Limpiar", "Clear"),
        ["Status.Ready"] = ("Listo", "Ready"),
        ["Panel.Thumbnails"] = ("Miniaturas", "Thumbnails"),
        ["Panel.Bookmarks"] = ("Marcadores", "Bookmarks"),
        ["Panel.Comments"] = ("Comentarios y anotaciones", "Comments & Annotations"),
        ["Panel.Forms"] = ("Formularios", "Forms"),
        ["Panel.Signatures"] = ("Firmas digitales", "Digital Signatures"),
        ["Panel.Attachments"] = ("Archivos adjuntos", "Attachments"),
        ["Panel.Objects"] = ("Objetos de página", "Page Objects")
    };

    public static string Get(string key)
    {
        if (Strings.TryGetValue(key, out var pair))
        {
            return _currentLanguage == "en" ? pair.En : pair.Es;
        }
        return key;
    }
}
