using System.Security.Cryptography;

// The add-on index signer. ECDSA P-256 with SHA-256, the signature in IEEE P1363 form (r and s, 64 bytes), base64.
//
//   dotnet run --project tools/addon-sign -- keygen <private key file>   make a key pair; prints the PUBLIC key to build in
//   dotnet run --project tools/addon-sign -- sign <private key file> <file>   writes <file>.sig
//   dotnet run --project tools/addon-sign -- public <private key file>   prints the public key again
//
// The private key file is PKCS#8, base64. It lives outside every repository and is never printed.
if (args.Length < 2) {
	Console.Error.WriteLine("usage: keygen <key> | sign <key> <file> | public <key>");

	return 2;
}

switch (args[0]) {
	case "keygen": {
		if (File.Exists(args[1])) {
			Console.Error.WriteLine($"{args[1]} already exists - not overwriting a signing key");

			return 1;
		}

		using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
		File.WriteAllText(args[1], Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
		Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

		return 0;
	}

	case "public": {
		using ECDsa key = Load(args[1]);
		Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

		return 0;
	}

	case "sign" when args.Length >= 3: {
		using ECDsa key = Load(args[1]);
		byte[] data = File.ReadAllBytes(args[2]);
		byte[] sig = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

		// Checked straight back with the public half, so a broken signature never reaches a release.
		using ECDsa pub = ECDsa.Create();
		pub.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);

		if (!pub.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) {
			Console.Error.WriteLine("the signature didn't verify");

			return 1;
		}

		File.WriteAllText(args[2] + ".sig", Convert.ToBase64String(sig));
		Console.WriteLine($"signed {Path.GetFileName(args[2])}");

		return 0;
	}

	default:
		Console.Error.WriteLine("usage: keygen <key> | sign <key> <file> | public <key>");

		return 2;
}

static ECDsa Load(string file) {
	ECDsa key = ECDsa.Create();
	key.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(file).Trim()), out _);

	return key;
}
