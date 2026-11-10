using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

// File condiviso: lo usano Server e Client (vedi i .csproj).

// Un errore di validazione: quale campo e perche'
class SchemaError
{
    public string Field = "";
    public string Message = "";
}

// Validazione con JSON Schema. Il controllo lo fa la libreria JsonSchema.Net:
// qui non ci sono controlli sui singoli campi, le regole stanno nei file di schemas/.
static class SchemaValidator
{
    // Cartella schemas/ (la cerco risalendo dalla cartella del programma)
    public static readonly string SchemasFolder = FindSchemasFolder();

    static string FindSchemasFolder()
    {
        string folder = AppContext.BaseDirectory;
        while (folder != null)
        {
            string candidate = Path.Combine(folder, "schemas");
            if (Directory.Exists(candidate)) return candidate;
            folder = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        return "schemas";   // non trovata: il programma lo segnala all'avvio
    }

    // Valida un testo JSON con schemas/<schemaName>.schema.json.
    // Restituisce null se e' valido, altrimenti il primo errore trovato.
    public static SchemaError Validate(string json, string schemaName)
    {
        // 1) carico lo schema dal file (ogni volta: se lo modifico vale subito)
        JsonSchema schema = JsonSchema.FromFile(Path.Combine(SchemasFolder, schemaName + ".schema.json"));

        // 2) leggo il JSON da controllare
        JsonNode document;
        try
        {
            document = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return NewError("", "il testo non e' un JSON valido");
        }

        // 3) la libreria confronta il JSON con lo schema
        EvaluationOptions options = new EvaluationOptions();
        options.OutputFormat = OutputFormat.List;             // risultati in un unico elenco
        options.RequireFormatValidation = true;              // controlla anche "format" (date-time)
        options.Culture = CultureInfo.GetCultureInfo("it");   // messaggi in italiano

        EvaluationResults result = schema.Evaluate(document, options);
        if (result.IsValid) return null;

        // 4) primo errore dell'elenco: InstanceLocation = DOVE (es. "/temperature_c"), Errors = PERCHE'
        foreach (EvaluationResults detail in result.Details)
        {
            if (!detail.HasErrors) continue;

            string field = detail.InstanceLocation.ToString().TrimStart('/').Replace("/", ".");
            foreach (KeyValuePair<string, string> error in detail.Errors)
            {
                return NewError(field, error.Value);
            }
        }
        return NewError("", "messaggio non valido");
    }

    static SchemaError NewError(string field, string message)
    {
        SchemaError error = new SchemaError();
        error.Field = field;
        error.Message = message;
        return error;
    }
}
