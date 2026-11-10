using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Una sessione = un client che ha fatto l'handshake. Ogni sessione ha la sua chiave.
class Session
{
    public string Id = "";
    public byte[] Key;
    public DateTime LastUse = DateTime.Now;   // serve per la scadenza (401)
}

class Program
{
    // Indirizzo del server (se lo cambi, cambialo anche nel Client)
    const string Url = "http://localhost:5000/";

    // Una sessione scade dopo 10 minuti senza messaggi
    static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(10);

    // Dati usati da tutte le richieste. Ogni richiesta gira in un suo thread,
    // quindi si toccano sempre dentro lock(locker) per evitare conflitti.
    static readonly object locker = new object();
    static Dictionary<string, Session> sessions = new Dictionary<string, Session>();
    static JsonArray readings = new JsonArray();   // le misure ricevute
    static string dataFile = "";                   // file JSON dove si salvano

    static void Main()
    {
        // i numeri decimali vogliono sempre il punto (anche con Windows in italiano)
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // le misure stanno in data/readings.json, accanto alla cartella schemas
        dataFile = Path.GetFullPath(Path.Combine(SchemaValidator.SchemasFolder, "..", "data", "readings.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(dataFile));
        LoadReadings();

        HttpListener listener = new HttpListener();
        listener.Prefixes.Add(Url);
        listener.Start();

        Console.WriteLine("Server FrigoTrack in ascolto su " + Url);
        Console.WriteLine("Misure salvate in " + dataFile + " (" + readings.Count + " gia' presenti)");
        Console.WriteLine("Per fermarlo: Ctrl+C");
        Console.WriteLine();

        while (true)
        {
            // aspetta la prossima richiesta...
            HttpListenerContext context = listener.GetContext();

            // ...e la gestisce in un nuovo thread, cosi' il server resta libero per gli altri client
            Thread thread = new Thread(() => HandleRequest(context));
            thread.Start();
        }
    }

    // Se il file esiste, ricarica le misure salvate nei giri precedenti
    static void LoadReadings()
    {
        if (File.Exists(dataFile))
        {
            readings = JsonNode.Parse(File.ReadAllText(dataFile)).AsArray();
        }
    }

    // ---------- Smistamento: ogni richiesta passa da qui ----------
    static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url.AbsolutePath;
            string body = new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEnd();

            if (context.Request.HttpMethod != "POST")
                Send(context, 404, ErrorJson("endpoint non trovato", ""));
            else if (path == "/api/handshake")
                Handshake(context, body);
            else if (path == "/api/telemetry" || path == "/api/readings" || path == "/api/close")
                EncryptedRequest(context, path, body);
            else
                Send(context, 404, ErrorJson("endpoint non trovato", ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERRORE: " + ex.Message);
            try { Send(context, 500, ErrorJson("errore interno del server", "")); } catch { }
        }
    }

    // ---------- POST /api/handshake (in chiaro: la chiave non c'e' ancora) ----------
    static void Handshake(HttpListenerContext context, string body)
    {
        // 1) lo schema controlla che p e g siano quelli del gruppo 14 e che A sia un numero esadecimale
        SchemaError error = SchemaValidator.Validate(body, "handshake-request");
        if (error != null)
        {
            Send(context, 400, ErrorJson(error.Message, error.Field));
            return;
        }

        JsonNode request = JsonNode.Parse(body);
        BigInteger A = Crypto.FromHex((string)request["A"]);

        // 2) Diffie-Hellman lato server
        BigInteger b = Crypto.NewSecret();     // segreto del server: non esce mai da qui
        BigInteger B = Crypto.PublicKey(b);    // B = g^b mod p
        byte[] key = Crypto.SharedKey(A, b);   // K = A^b mod p  ->  chiave AES

        // 3) nuova sessione con un identificativo casuale (8 byte -> 16 caratteri esadecimali)
        Session session = new Session();
        session.Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLower();
        session.Key = key;
        lock (locker)
        {
            sessions[session.Id] = session;
        }
        Log("handshake: nuova sessione " + session.Id);

        // 4) rispondo (in chiaro) con session_id e B
        JsonObject response = new JsonObject();
        response["session_id"] = session.Id;
        response["B"] = Crypto.ToHex(B);
        Send(context, 200, response.ToJsonString());
    }

    // ---------- Richieste cifrate: /api/telemetry, /api/readings, /api/close ----------
    static void EncryptedRequest(HttpListenerContext context, string path, string body)
    {
        // 1) la busta ha la forma giusta?
        SchemaError error = SchemaValidator.Validate(body, "envelope");
        if (error != null)
        {
            Send(context, 400, ErrorJson(error.Message, error.Field));
            return;
        }
        JsonNode envelope = JsonNode.Parse(body);
        string sessionId = (string)envelope["session_id"];

        // 2) la sessione esiste? e' scaduta?
        Session session = null;
        lock (locker)
        {
            if (sessions.ContainsKey(sessionId))
                session = sessions[sessionId];
        }
        if (session == null)
        {
            Send(context, 404, ErrorJson("sessione inesistente: serve un nuovo handshake", ""));
            return;
        }
        if (DateTime.Now - session.LastUse > SessionTimeout)
        {
            lock (locker)
            {
                sessions.Remove(sessionId);
            }
            Send(context, 401, ErrorJson("sessione scaduta: serve un nuovo handshake", ""));
            return;
        }

        // 3) PRIMA decifro. Se il tag non torna rispondo 400 e non elaboro nulla.
        string json = Crypto.Decrypt(session.Key, (string)envelope["ciphertext"], (string)envelope["tag"]);
        if (json == null)
        {
            Log("busta manomessa o non decifrabile (sessione " + sessionId + ")");
            Send(context, 400, ErrorJson("busta non decifrabile o manomessa", ""));
            return;
        }
        session.LastUse = DateTime.Now;   // messaggio autentico: la sessione resta viva

        // 4) POI valido il messaggio decifrato con il suo schema
        string schemaName = "";
        if (path == "/api/telemetry") schemaName = "telemetry";
        if (path == "/api/readings") schemaName = "readings-request";
        if (path == "/api/close") schemaName = "close-request";

        error = SchemaValidator.Validate(json, schemaName);
        if (error != null)
        {
            Log("messaggio rifiutato: campo '" + error.Field + "' -> " + error.Message);
            SendEncrypted(context, 400, session, ErrorJson(error.Message, error.Field));
            return;
        }

        // 5) il messaggio e' valido: lo elaboro
        JsonObject message = JsonNode.Parse(json).AsObject();
        if (path == "/api/telemetry") Telemetry(context, session, message);
        if (path == "/api/readings") Readings(context, session, message);
        if (path == "/api/close") Close(context, session);
    }

    // ---------- /api/telemetry: controlla l'allarme e salva la misura ----------
    static void Telemetry(HttpListenerContext context, Session session, JsonObject measure)
    {
        string sensorId = (string)measure["sensor_id"];
        string cargo = (string)measure["cargo_type"];
        double temperature = (double)measure["temperature_c"];

        // l'allarme scatta se la temperatura esce dall'intervallo di sicurezza della merce
        SafeRange(cargo, out double min, out double max);
        bool alarm = temperature < min || temperature > max;
        string alarmMessage = "temperatura " + temperature + " C fuori intervallo " + min + ".." + max + " C (" + cargo + ")";

        string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        // salvo la misura (piu' ora di arrivo e allarme) e riscrivo il file
        measure["received_at"] = now;
        measure["alarm"] = alarm;
        lock (locker)
        {
            readings.Add(measure);
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            File.WriteAllText(dataFile, readings.ToJsonString(options));
        }

        // risposta (ack)
        JsonObject ack = new JsonObject();
        ack["status"] = "ok";
        ack["sensor_id"] = sensorId;
        ack["received_at"] = now;
        ack["alarm"] = alarm;
        if (alarm)
            ack["alarm_message"] = alarmMessage;

        Log(sensorId + ": " + temperature + " C -> salvata" + (alarm ? "   *** ALLARME ***" : ""));
        SendEncrypted(context, 200, session, ack.ToJsonString());
    }

    // Intervallo di sicurezza (in gradi C) per ogni tipo di merce
    static void SafeRange(string cargo, out double min, out double max)
    {
        if (cargo == "food") { min = 0; max = 4; }
        else if (cargo == "pharma") { min = 2; max = 8; }
        else { min = -25; max = -18; }   // "frozen" (lo schema ammette solo questi tre tipi)
    }

    // ---------- /api/readings: restituisce le misure di un sensore ----------
    static void Readings(HttpListenerContext context, Session session, JsonObject request)
    {
        string sensorId = (string)request["sensor_id"];

        // "from" e "to" sono facoltativi: se mancano non c'e' limite
        DateTimeOffset from = DateTimeOffset.MinValue;
        DateTimeOffset to = DateTimeOffset.MaxValue;
        if (request["from"] != null) from = DateTimeOffset.Parse((string)request["from"]);
        if (request["to"] != null) to = DateTimeOffset.Parse((string)request["to"]);

        JsonArray found = new JsonArray();
        lock (locker)
        {
            foreach (JsonNode reading in readings)
            {
                DateTimeOffset time = DateTimeOffset.Parse((string)reading["timestamp"]);
                if ((string)reading["sensor_id"] == sensorId && time >= from && time <= to)
                    found.Add(reading.DeepClone());   // serve una copia: un nodo JSON puo' stare in un solo array
            }
        }

        JsonObject response = new JsonObject();
        response["sensor_id"] = sensorId;
        response["count"] = found.Count;
        response["readings"] = found;

        Log("readings: " + found.Count + " misure di " + sensorId);
        SendEncrypted(context, 200, session, response.ToJsonString());
    }

    // ---------- /api/close: chiude la sessione e cancella la chiave ----------
    static void Close(HttpListenerContext context, Session session)
    {
        lock (locker)
        {
            sessions.Remove(session.Id);   // da ora la sessione non esiste piu' (404)
        }

        JsonObject response = new JsonObject();
        response["status"] = "closed";
        SendEncrypted(context, 200, session, response.ToJsonString());   // cifro PRIMA di cancellare la chiave

        Array.Clear(session.Key);   // metto a zero la chiave in memoria
        Log("sessione " + session.Id + " chiusa, chiave cancellata");
    }

    // ---------- Invio delle risposte ----------

    // In chiaro (quando non c'e' una chiave con cui cifrare)
    static void Send(HttpListenerContext context, int status, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    // Cifrata: il JSON viene messo nella busta
    static void SendEncrypted(HttpListenerContext context, int status, Session session, string json)
    {
        Send(context, status, Crypto.Seal(session.Id, session.Key, json));
    }

    // Messaggio di errore: {"error": "...", "field": "..."} (field solo se si sa qual e' il campo sbagliato)
    static string ErrorJson(string message, string field)
    {
        JsonObject error = new JsonObject();
        error["error"] = message;
        if (field != "") error["field"] = field;
        return error.ToJsonString();
    }

    static void Log(string text)
    {
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text);
    }
}
