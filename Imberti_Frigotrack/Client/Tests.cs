using System.Text.Json.Nodes;

// Test automatici (il server deve essere acceso).
// Ogni test e' una funzione: se una condizione e' falsa lancia un'eccezione e il test FALLISCE.
static class Tests
{
    static int passed = 0;
    static int failed = 0;

    public static void RunAll()
    {
        passed = 0;
        failed = 0;
        Console.WriteLine();

        Run("1. Handshake riuscito", HandshakeOk);
        Run("2. Misura valida -> 200", ValidMeasure);
        Run("3. Misura non valida -> 400 con il campo sbagliato", InvalidMeasure);
        Run("4. Busta manomessa -> 400", TamperedEnvelope);
        Run("5. Sessione inesistente -> 404", UnknownSession);
        Run("6. Temperatura fuori intervallo -> allarme", Alarm);
        Run("7. /api/readings restituisce le misure salvate", ReadingsList);

        Console.WriteLine();
        Console.WriteLine("Risultato: " + passed + " passati, " + failed + " falliti");
    }

    // Esegue un test e stampa OK o FALLITO
    static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("[OK]       " + name);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("[FALLITO]  " + name + "  ->  " + ex.Message);
        }
    }

    // Se la condizione e' falsa il test fallisce con questo messaggio
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // ===================================================================

    static void HandshakeOk()
    {
        Connection connection = new Connection();
        connection.Handshake();
        Check(connection.SessionId.Length == 16, "session_id mancante");

        // se le due chiavi fossero diverse il server non riuscirebbe a decifrare e risponderebbe 400
        JsonObject measure = Program.NewMeasure("CELLA-BG-001", "pharma", 5, 50, 90);
        connection.Send("/api/telemetry", measure.ToJsonString(), "telemetry");
        Check(connection.Status == 200, "atteso 200, ricevuto " + connection.Status);
    }

    static void ValidMeasure()
    {
        Connection connection = new Connection();
        connection.Handshake();

        JsonObject measure = Program.NewMeasure("CELLA-BG-002", "pharma", 4.6, 55, 87);
        JsonNode ack = connection.Send("/api/telemetry", measure.ToJsonString(), "telemetry");

        Check(connection.Status == 200, "atteso 200, ricevuto " + connection.Status);
        Check((string)ack["status"] == "ok", "status diverso da ok");
        Check((bool)ack["alarm"] == false, "allarme non previsto");
    }

    static void InvalidMeasure()
    {
        Connection connection = new Connection();
        connection.Handshake();

        // temperatura 99: oltre il massimo (40). La mando senza validarla nel client.
        JsonObject hot = Program.NewMeasure("CELLA-BG-003", "pharma", 99, 55, 87);
        JsonNode error = connection.SendWithoutValidation("/api/telemetry", hot.ToJsonString());
        Check(connection.Status == 400, "atteso 400, ricevuto " + connection.Status);
        Check((string)error["field"] == "temperature_c", "campo indicato: " + error["field"]);

        // tipo di merce che non esiste
        JsonObject wrongCargo = Program.NewMeasure("CELLA-BG-003", "gelato", 3, 55, 87);
        error = connection.SendWithoutValidation("/api/telemetry", wrongCargo.ToJsonString());
        Check(connection.Status == 400, "atteso 400, ricevuto " + connection.Status);
        Check((string)error["field"] == "cargo_type", "campo indicato: " + error["field"]);

        // il client, invece, non la invia nemmeno: Send lancia un'eccezione
        bool blocked = false;
        try { connection.Send("/api/telemetry", hot.ToJsonString(), "telemetry"); }
        catch (Exception) { blocked = true; }
        Check(blocked, "il client ha inviato una misura non valida");
    }

    static void TamperedEnvelope()
    {
        Connection connection = new Connection();
        connection.Handshake();

        // costruisco una busta corretta, poi cambio un bit del testo cifrato (come farebbe un attaccante)
        JsonObject measure = Program.NewMeasure("CELLA-BG-004", "pharma", 5, 55, 87);
        JsonNode envelope = JsonNode.Parse(Crypto.Seal(connection.SessionId, connection.Key, measure.ToJsonString()));
        byte[] data = Convert.FromBase64String((string)envelope["ciphertext"]);
        data[data.Length - 1] = (byte)(data[data.Length - 1] ^ 1);
        envelope["ciphertext"] = Convert.ToBase64String(data);

        connection.Post("/api/telemetry", envelope.ToJsonString());
        Check(connection.Status == 400, "atteso 400, ricevuto " + connection.Status);
    }

    static void UnknownSession()
    {
        Connection connection = new Connection();
        connection.SessionId = "0000000000000000";   // il server non l'ha mai creata (niente handshake)

        JsonObject measure = Program.NewMeasure("CELLA-BG-005", "pharma", 5, 55, 87);
        connection.Send("/api/telemetry", measure.ToJsonString(), "telemetry");
        Check(connection.Status == 404, "atteso 404, ricevuto " + connection.Status);
    }

    static void Alarm()
    {
        Connection connection = new Connection();
        connection.Handshake();

        // farmaci a 12 gradi: fuori dall'intervallo 2..8
        JsonObject measure = Program.NewMeasure("CELLA-BG-006", "pharma", 12, 55, 87);
        JsonNode ack = connection.Send("/api/telemetry", measure.ToJsonString(), "telemetry");

        Check(connection.Status == 200, "atteso 200, ricevuto " + connection.Status);
        Check((bool)ack["alarm"] == true, "l'allarme non e' scattato");
    }

    static void ReadingsList()
    {
        Connection connection = new Connection();
        connection.Handshake();

        // invio una misura...
        JsonObject measure = Program.NewMeasure("CELLA-BG-007", "food", 3, 55, 87);
        connection.Send("/api/telemetry", measure.ToJsonString(), "telemetry");

        // ...e la richiedo
        JsonObject request = new JsonObject();
        request["sensor_id"] = "CELLA-BG-007";
        JsonNode list = connection.Send("/api/readings", request.ToJsonString(), "readings-request");
        Check(connection.Status == 200, "atteso 200, ricevuto " + connection.Status);
        Check((int)list["count"] >= 1, "nessuna misura restituita");

        // con un intervallo nel futuro non ci sono misure
        request["from"] = "2099-01-01T00:00:00Z";
        list = connection.Send("/api/readings", request.ToJsonString(), "readings-request");
        Check((int)list["count"] == 0, "il filtro 'from' non funziona");
    }
}
