using System.Globalization;
using System.Text.Json.Nodes;

class Program
{
    // Indirizzo del server. Se lo cambi, cambialo anche nel Server (Server/Program.cs).
    public const string Url = "http://localhost:5000";

    // Quante misure invia ogni sensore e ogni quanti millisecondi
    const int MeasuresPerSensor = 10;
    const int IntervalMs = 2000;

    static void Main()
    {
        // i numeri decimali vogliono sempre il punto (anche con Windows in italiano)
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("===== FrigoTrack - client dei sensori =====");
            Console.WriteLine("1) Avvia i 3 sensori");
            Console.WriteLine("2) Demo: una misura valida e una non valida");
            Console.WriteLine("3) Esegui i test (il server deve essere acceso)");
            Console.WriteLine("0) Esci");
            Console.Write("Scelta: ");

            string choice = Console.ReadLine();
            if (choice == null || choice == "0") return;

            if (choice == "1") StartSensors();
            else if (choice == "2") Demo();
            else if (choice == "3") Tests.RunAll();
            else Console.WriteLine("Scelta non valida");
        }
    }

    // ===================================================================
    //  1) Tre sensori che lavorano insieme, ognuno in un thread con la sua sessione
    // ===================================================================
    static void StartSensors()
    {
        // identificativo, tipo di merce, temperatura "normale" del sensore
        Thread t1 = new Thread(() => RunSensor("CELLA-BG-001", "pharma", 5));
        Thread t2 = new Thread(() => RunSensor("CAMION-MI-002", "food", 2));
        Thread t3 = new Thread(() => RunSensor("CELLA-RM-003", "frozen", -20));

        t1.Start();
        t2.Start();
        t3.Start();

        // aspetto che tutti e tre finiscano
        t1.Join();
        t2.Join();
        t3.Join();
        Console.WriteLine("Tutti i sensori hanno finito.");
    }

    static void RunSensor(string sensorId, string cargo, double normalTemperature)
    {
        try
        {
            // 1) handshake: prima di inviare qualsiasi dato
            Connection connection = new Connection();
            connection.Handshake();
            Log(sensorId, "handshake fatto, sessione " + connection.SessionId);

            // 2) invio le misure a intervalli regolari
            Random random = new Random();
            for (int n = 1; n <= MeasuresPerSensor; n++)
            {
                // temperatura normale +/- 0,5 gradi; una misura ogni 4 e' fuori intervallo (cosi' scatta l'allarme)
                double temperature = normalTemperature + random.NextDouble() - 0.5;
                if (n % 4 == 0) temperature = temperature + 6;
                temperature = Math.Round(temperature, 1);

                JsonObject measure = NewMeasure(sensorId, cargo, temperature, random.Next(40, 70), 100 - n);
                JsonNode ack = connection.Send("/api/telemetry", measure.ToJsonString(), "telemetry");

                if (connection.Status != 200)
                {
                    Log(sensorId, "misura " + n + ": errore HTTP " + connection.Status + " " + ack["error"]);
                    return;
                }

                if ((bool)ack["alarm"])
                    Log(sensorId, "misura " + n + ": " + temperature + " C -> ALLARME: " + ack["alarm_message"]);
                else
                    Log(sensorId, "misura " + n + ": " + temperature + " C -> ok");

                Thread.Sleep(IntervalMs);
            }

            // 3) chiedo al server le misure salvate per questo sensore
            JsonObject request = new JsonObject();
            request["sensor_id"] = sensorId;
            JsonNode list = connection.Send("/api/readings", request.ToJsonString(), "readings-request");
            Log(sensorId, "il server ha salvato " + list["count"] + " misure di questo sensore");

            // 4) chiudo la sessione: il server cancella la chiave
            JsonObject close = new JsonObject();
            close["action"] = "close";
            connection.Send("/api/close", close.ToJsonString(), "close-request");
            Log(sensorId, "sessione chiusa (HTTP " + connection.Status + ")");
        }
        catch (HttpRequestException)
        {
            Log(sensorId, "ERRORE: server non raggiungibile. Hai avviato il Server?");
        }
        catch (Exception ex)
        {
            Log(sensorId, "ERRORE: " + ex.Message);
        }
    }

    // ===================================================================
    //  2) Demo per l'interrogazione: una misura valida e una non valida
    // ===================================================================
    static void Demo()
    {
        try
        {
            Connection connection = new Connection();
            connection.ShowTraffic = true;   // stampa cio' che viaggia davvero in rete

            Console.WriteLine();
            Console.WriteLine("--- Handshake ---");
            connection.Handshake();
            Console.WriteLine("   session_id = " + connection.SessionId);
            Console.WriteLine("   chiave AES (NON viaggia in rete): " + Convert.ToHexString(connection.Key));

            Console.WriteLine();
            Console.WriteLine("--- Misura VALIDA ---");
            JsonObject good = NewMeasure("CELLA-BG-007", "pharma", 4.6, 55, 87);
            Console.WriteLine("   in chiaro (solo qui)  " + good.ToJsonString());
            JsonNode ack = connection.Send("/api/telemetry", good.ToJsonString(), "telemetry");
            Console.WriteLine("   risposta decifrata: HTTP " + connection.Status + " " + ack.ToJsonString());

            Console.WriteLine();
            Console.WriteLine("--- Misura NON VALIDA (99 gradi, il massimo e' 40) ---");
            JsonObject bad = NewMeasure("CELLA-BG-007", "pharma", 99, 55, 87);
            Console.WriteLine("   in chiaro (solo qui)  " + bad.ToJsonString());

            SchemaError error = SchemaValidator.Validate(bad.ToJsonString(), "telemetry");
            Console.WriteLine("   il client la rifiuta: campo '" + error.Field + "' -> " + error.Message);
            Console.WriteLine("   la invio lo stesso, senza validarla, per vedere cosa risponde il server:");
            JsonNode answer = connection.SendWithoutValidation("/api/telemetry", bad.ToJsonString());
            Console.WriteLine("   risposta decifrata: HTTP " + connection.Status + "  errore: " + answer["error"] + "  campo: " + answer["field"]);

            // chiudo la sessione
            connection.ShowTraffic = false;
            JsonObject close = new JsonObject();
            close["action"] = "close";
            connection.Send("/api/close", close.ToJsonString(), "close-request");
            Console.WriteLine();
            Console.WriteLine("Sessione chiusa (HTTP " + connection.Status + ").");
        }
        catch (HttpRequestException)
        {
            Console.WriteLine("ERRORE: server non raggiungibile. Hai avviato il Server?");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERRORE: " + ex.Message);
        }
    }

    // ===================================================================
    //  Funzioni di supporto
    // ===================================================================

    // Crea il messaggio di una misura (stesso formato dell'esempio della consegna)
    public static JsonObject NewMeasure(string sensorId, string cargo, double temperature, int humidity, int battery)
    {
        JsonObject measure = new JsonObject();
        measure["sensor_id"] = sensorId;
        measure["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        measure["cargo_type"] = cargo;
        measure["temperature_c"] = temperature;
        measure["humidity"] = humidity;
        measure["battery"] = battery;
        return measure;
    }

    static void Log(string sensorId, string text)
    {
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + sensorId + "] " + text);
    }
}
