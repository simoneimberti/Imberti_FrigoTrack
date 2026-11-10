using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// File usato sia dal Server sia dal Client (collegato nei .csproj).
// Contiene Diffie-Hellman e la cifratura AES-GCM.
//
//   client: sceglie a  ->  A = g^a mod p  ->  invia A
//   server: sceglie b  ->  B = g^b mod p  ->  invia B
//   client: K = B^a mod p      server: K = A^b mod p      (stesso numero!)
// a, b e K non viaggiano mai in rete.
static class Crypto
{
    // Gruppo 14 dell'RFC 3526: p da 2048 bit, g = 2
    public static readonly BigInteger P = FromHex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD1" +
        "29024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3D" +
        "C2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D" +
        "670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
        "DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AACAA68FFFFFFFFFFFFFFFF");

    public static readonly BigInteger G = 2;

    // ---------- Diffie-Hellman ----------

    // Numero segreto casuale (a nel client, b nel server): 32 byte = 256 bit
    public static BigInteger NewSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return new BigInteger(bytes, isUnsigned: true);
    }

    // Chiave pubblica: A = g^a mod p (oppure B = g^b mod p)
    public static BigInteger PublicKey(BigInteger secret)
    {
        return BigInteger.ModPow(G, secret, P);
    }

    // Segreto condiviso K = B^a mod p (uguale a A^b mod p).
    // K e' lungo 2048 bit, ma AES-256 vuole una chiave di 32 byte: uso SHA-256(K).
    public static byte[] SharedKey(BigInteger otherPublicKey, BigInteger mySecret)
    {
        BigInteger K = BigInteger.ModPow(otherPublicKey, mySecret, P);
        return SHA256.HashData(K.ToByteArray());
    }

    // I numeri grandi viaggiano nel JSON come testo esadecimale
    public static string ToHex(BigInteger number)
    {
        return number.ToString("X").TrimStart('0');
    }

    public static BigInteger FromHex(string hex)
    {
        // lo "0" davanti serve perche' altrimenti il numero potrebbe essere letto come negativo
        return BigInteger.Parse("0" + hex, NumberStyles.HexNumber);
    }

    // ---------- AES-GCM ----------

    // Il nonce (12 byte casuali, nuovo a ogni messaggio) viene messo davanti al testo cifrato.
    // Il tag (16 byte) serve a scoprire se qualcuno ha modificato il messaggio.
    public static void Encrypt(byte[] key, string text, out string ciphertext, out string tag)
    {
        byte[] plain = Encoding.UTF8.GetBytes(text);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tagBytes = new byte[16];

        using (AesGcm aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tagBytes);
        }

        // nonce + testo cifrato, uno dopo l'altro, in un unico array
        byte[] all = new byte[12 + cipher.Length];
        Array.Copy(nonce, 0, all, 0, 12);
        Array.Copy(cipher, 0, all, 12, cipher.Length);

        ciphertext = Convert.ToBase64String(all);
        tag = Convert.ToBase64String(tagBytes);
    }

    // Restituisce il testo in chiaro, oppure null se qualcosa non va
    // (tag sbagliato = messaggio manomesso, chiave sbagliata, Base64 non valido).
    public static string Decrypt(byte[] key, string ciphertext, string tag)
    {
        try
        {
            byte[] all = Convert.FromBase64String(ciphertext);
            byte[] tagBytes = Convert.FromBase64String(tag);

            // i primi 12 byte sono il nonce, il resto e' il testo cifrato
            byte[] nonce = new byte[12];
            byte[] cipher = new byte[all.Length - 12];
            Array.Copy(all, 0, nonce, 0, 12);
            Array.Copy(all, 12, cipher, 0, cipher.Length);

            byte[] plain = new byte[cipher.Length];
            using (AesGcm aes = new AesGcm(key, 16))
            {
                aes.Decrypt(nonce, cipher, tagBytes, plain);   // se il tag non torna lancia un'eccezione
            }
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    // Cifra un JSON e lo mette nella busta {"session_id", "ciphertext", "tag"}
    public static string Seal(string sessionId, byte[] key, string json)
    {
        Encrypt(key, json, out string ciphertext, out string tag);

        JsonObject envelope = new JsonObject();
        envelope["session_id"] = sessionId;
        envelope["ciphertext"] = ciphertext;
        envelope["tag"] = tag;
        return envelope.ToJsonString();
    }
}
