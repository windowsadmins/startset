using System.CommandLine;
using StartSet.Infrastructure.Configuration;
using StartSet.Infrastructure.Security;

namespace StartSet.CLI.Commands;

/// <summary>
/// Commands to sign and verify script payloads and to make a signing keypair.
/// Match outset's --sign-script-file, --verify-script-file and --generate-keypair.
/// </summary>
public static class SigningCommand
{
    public const string DefaultKeyVariable = "STARTSET_SIGNING_KEY";

    /// <summary>The command names, which run without a session log.</summary>
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "sign", "verify", "generate-keypair" };

    /// <summary>
    /// startset sign &lt;files...&gt; [--key-env VAR | --key-file PATH]. The private key is never
    /// taken as a plain argument, which would leave it in shell history and process listings.
    /// </summary>
    public static Command CreateSign()
    {
        var command = new Command("sign", "Embed an Ed25519 signature in one or more scripts (.ps1, .cmd, .bat), in place");

        var filesArgument = new Argument<string[]>("files", "Scripts to sign") { Arity = ArgumentArity.OneOrMore };
        var keyEnvOption = new Option<string>(
            aliases: ["--key-env"],
            getDefaultValue: () => DefaultKeyVariable,
            description: "Environment variable holding the base64 private key");
        var keyFileOption = new Option<string?>(
            aliases: ["--key-file"],
            description: "File holding the base64 private key; used instead of --key-env");

        command.AddArgument(filesArgument);
        command.AddOption(keyEnvOption);
        command.AddOption(keyFileOption);

        command.SetHandler(context =>
        {
            var files = context.ParseResult.GetValueForArgument(filesArgument);
            var keyEnv = context.ParseResult.GetValueForOption(keyEnvOption)!;
            var keyFile = context.ParseResult.GetValueForOption(keyFileOption);
            string? encoded;
            try
            {
                encoded = keyFile is not null ? File.ReadAllText(keyFile) : Environment.GetEnvironmentVariable(keyEnv);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read the key file: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            var privateKey = ScriptSigning.ParsePrivateKey(encoded);
            if (privateKey is null)
            {
                Console.Error.WriteLine(keyFile is not null
                    ? $"{keyFile} does not hold a base64 Ed25519 private key"
                    : $"{keyEnv} is not set to a base64 Ed25519 private key");
                context.ExitCode = 1;
                return;
            }

            var failures = 0;
            foreach (var file in files)
            {
                try
                {
                    ScriptSigning.SignFile(file, privateKey);
                    Console.WriteLine($"Signed: {file}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not sign {file}: {ex.Message}");
                    failures++;
                }
            }

            context.ExitCode = failures > 0 ? 1 : 0;
        });

        return command;
    }

    /// <summary>startset verify &lt;files...&gt; [--public-key B64]. Exits 1 when any file fails.</summary>
    public static Command CreateVerify(PreferencesService preferencesService)
    {
        var command = new Command("verify", "Check the Ed25519 signature embedded in one or more scripts");

        var filesArgument = new Argument<string[]>("files", "Scripts to verify") { Arity = ArgumentArity.OneOrMore };
        var publicKeyOption = new Option<string?>(
            aliases: ["--public-key"],
            description: "Base64 public key to verify against. Default: the ManifestSigningKey policy value");

        command.AddArgument(filesArgument);
        command.AddOption(publicKeyOption);

        command.SetHandler(context =>
        {
            var files = context.ParseResult.GetValueForArgument(filesArgument);
            var publicKey = context.ParseResult.GetValueForOption(publicKeyOption);
            var encoded = publicKey ?? preferencesService.Preferences.ManifestSigningKey;
            if (encoded is null)
            {
                Console.Error.WriteLine("No public key: pass --public-key, or set ManifestSigningKey by policy");
                context.ExitCode = 1;
                return;
            }

            var key = ScriptSigning.ParsePublicKey(encoded);
            var failures = 0;
            foreach (var file in files)
            {
                var check = File.Exists(file)
                    ? ScriptSigning.VerifyFile(file, key)
                    : new SignatureCheck(SignatureFailure.Unreadable, "file not found");
                if (check.IsValid)
                {
                    Console.WriteLine($"Valid: {file}");
                }
                else
                {
                    Console.Error.WriteLine($"Invalid: {file}: {check.Describe()}");
                    failures++;
                }
            }

            context.ExitCode = failures > 0 ? 1 : 0;
        });

        return command;
    }

    /// <summary>startset generate-keypair. Prints a new public and private key.</summary>
    public static Command CreateGenerateKeypair()
    {
        var command = new Command("generate-keypair", "Generate an Ed25519 keypair for script signing");

        command.SetHandler(() =>
        {
            var (publicKey, privateKey) = ScriptSigning.GenerateKeypair();
            Console.WriteLine($"Public key (set as the ManifestSigningKey policy value): {publicKey}");
            Console.WriteLine($"Private key (keep secret; signs scripts): {privateKey}");
        });

        return command;
    }
}
