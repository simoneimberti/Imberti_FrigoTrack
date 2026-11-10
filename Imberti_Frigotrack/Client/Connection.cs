using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

// Collegamento cifrato con il server: prima l'handshake, poi i messaggi dentro la busta.
// Ogni sensore ha la sua Connection, quindi la sua sessione e la sua chiave.
class Connection
{
    static readonly HttpClient http = new HttpClient();   // ne basta uno per tutto il programma

    public string SessionId = "";
    public byte[] Key = new byte[32];
    public int Status = 0;               // codice HTTP dell'ultima risposta
    public bool ShowTraffic = false;     // se true stampa cio' che viaggia in rete (usato dalla demo)

    // ---------- Handshake Diffie-Hellman: va fatto PRIMA di inviare qualsiasi dato ----------
    public void Handshake()
    {
        BigInteger a = Crypto.NewSecret();     // segreto del client: non esce mai da qui
        BigInteger A = Crypto.PublicKey(a);    // A = g^a mod p

        JsonObject request = new JsonObject();
        request["p"] = Crypto.ToHex(Crypto.P);
        request["g"] = Crypto.ToHex(Crypto.G);
        request["A"] = Crypto.ToHex(A);

        string answer = Post("/api/handshake", request.ToJsonString());
        if (Status != 200)
            throw new Exception("handshake rifiutato (HTTP " + Status + "): " + answer);

        SchemaError error = SchemaValidator.Validate(answer, "handshake-response");
        if (error != null)
            throw new Exception("risposta dell'handshake non valida: campo '" + error.Field + "' " + error.Message);

        JsonNode response = JsonNode.Parse(answer);
        BigInteger B = Crypto.FromHex((string)response["B"]);
        SessionId = (string)response["session_id"];
        Key = Crypto.SharedKey(B, a);          // K = B^a mod p  ->  chiave AES
    }

    // ---------- Invio normale: PRIMA valida con lo schema, POI cifra e invia ----------
    public JsonNode Send(string path, string json, string schemaName)
    {
        SchemaError error = SchemaValidator.Validate(json, schemaName);
        if (error != null)
            throw new Exception("il client non invia il messaggio: campo '" + error.Field + "' " + error.Message);

        return SendWithoutValidation(path, json);
    }

    // ---------- Cifra e invia SENZA validare ----------
    // Serve solo alla demo e ai test, per far arrivare al server un messaggio sbagliato e vedere il suo 400.
    public JsonNode SendWithoutValidation(string path, string json)
    {
        string envelope = Crypto.Seal(SessionId, Key, json);
        if (ShowTraffic) Console.WriteLine("   in rete  -> " + envelope);

        string answer = Post(path, envelope);
        if (ShowTraffic) Console.WriteLine("   in rete  <- HTTP " + Status + " " + answer);

        return OpenAnswer(answer);
    }

    // La risposta e' una busta cifrata da aprire, oppure un errore in chiaro (per esempio sessione inesistente)
    public JsonNode OpenAnswer(string answer)
    {
        JsonNode node = JsonNode.Parse(answer);
        if (node["ciphertext"] == null) return node;   // errore in chiaro: non c'e' niente da decifrare

        string json = Crypto.Decrypt(Key, (string)node["ciphertext"], (string)node["tag"]);
        if (json == null) throw new Exception("la risposta del server non si decifra");
        return JsonNode.Parse(json);
    }

    // POST di un testo JSON al server: restituisce il corpo della risposta e salva il codice HTTP in Status
    public string Post(string path, string body)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, Program.Url + path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response = http.Send(request);
        Status = (int)response.StatusCode;
        return new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
    }
}
