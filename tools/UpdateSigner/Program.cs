using System.Security.Cryptography;

// Signature des mises à jour de Corral (ECDSA P-256 + SHA-256, format IEEE P1363 en base64).
//   keygen <fichier.pem>          crée une clé privée et affiche la clé publique à mettre dans Updater.PublicKey
//   sign <fichier> <sortie.sig>   signe avec la clé PEM lue dans la variable CORRAL_SIGNING_KEY
switch (args)
{
    case ["keygen", var output]:
    {
        if (File.Exists(output))
            return Fail($"{output} existe déjà : on ne remplace jamais une clé.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(output, key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine("Clé publique :");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case ["sign", var file, var output]:
    {
        var pem = Environment.GetEnvironmentVariable("CORRAL_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(pem))
            return Fail("Variable CORRAL_SIGNING_KEY absente.");
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        using var stream = File.OpenRead(file);
        File.WriteAllText(output, Convert.ToBase64String(key.SignData(stream, HashAlgorithmName.SHA256)));
        Console.WriteLine($"Signé : {output}");
        Console.WriteLine("PublicKey: " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    default:
        return Fail("Usage : keygen <fichier.pem> | sign <fichier> <sortie.sig>");
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
